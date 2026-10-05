using System.Net.WebSockets;
using System.Text.Json;

namespace R2Pipe;

internal static class Ws
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<ClientWebSocket> ConnectAsync(HttpPipeApi api, string id, string role, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        foreach (var (k, v) in api.AuthHeaders) ws.Options.SetRequestHeader(k, v);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = new Uri(api.Base.Replace("https://", "wss://").Replace("http://", "ws://") + $"/t/{id}/ws?role={role}");
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }

    /// <summary>One whole message (text or binary).</summary>
    public static async Task<(bool text, byte[] data)?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return (r.MessageType == WebSocketMessageType.Text, ms.ToArray());
        }
    }
}

/// <summary>WebSocket to the Durable Object: state pushes (with the GET URL of every ready part) and inline bytes.</summary>
internal sealed class WsFeed : IReceiveFeed
{
    private readonly ClientWebSocket _ws;
    private WsFeed(ClientWebSocket ws) => _ws = ws;
    public static async Task<WsFeed> ConnectAsync(HttpPipeApi api, string id, CancellationToken ct) => new(await Ws.ConnectAsync(api, id, "recv", ct).ConfigureAwait(false));

    public async Task<FeedItem?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            var m = await Ws.ReceiveAsync(_ws, ct).ConfigureAwait(false);
            if (m is null) return null;
            var (text, data) = m.Value;
            if (!text)
            {
                if (data.Length < 8) continue;
                long off = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data);
                return new InlineItem(off, data[8..]);
            }
            var s = System.Text.Encoding.UTF8.GetString(data);
            if (s == "pong") continue;
            using var doc = JsonDocument.Parse(s);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "state") return new StateItem(doc.RootElement.Deserialize<StateResult>(Ws.Json)!);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(1000).Token).ConfigureAwait(false); } catch { }
        _ws.Dispose();
    }
}

/// <summary>Fallback: long-poll the state. No inline bytes.</summary>
internal sealed class PollFeed(IPipeApi api, string id, int pollSeconds) : IReceiveFeed
{
    private long _since = -1;
    public async Task<FeedItem?> NextAsync(CancellationToken ct)
    {
        var st = await Retry.RunAsync(5, _ => api.StateAsync(id, _since, _since < 0 ? 0 : pollSeconds, ct), null, ct).ConfigureAwait(false);
        _since = st.Meta.Version;
        if (st.Meta.InlineSize > 0) throw new PipeException("this transfer has inline data, which needs the WebSocket, and the WebSocket could not be opened");
        return new StateItem(st);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class WsInlineSender : IInlineSender
{
    private readonly ClientWebSocket _ws;
    private readonly object _l = new();
    private long _acked; private string? _error;
    private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);

    private WsInlineSender(ClientWebSocket ws) { _ws = ws; _reader = Task.Run(ReadLoop); }
    public static async Task<WsInlineSender> ConnectAsync(HttpPipeApi api, string id, CancellationToken ct) => new(await Ws.ConnectAsync(api, id, "send", ct).ConfigureAwait(false));

    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var m = await Ws.ReceiveAsync(_ws, _stop.Token).ConfigureAwait(false);
                if (m is null) break;
                if (!m.Value.text) continue;
                var s = System.Text.Encoding.UTF8.GetString(m.Value.data);
                if (s == "pong") continue;
                using var doc = JsonDocument.Parse(s);
                var type = doc.RootElement.GetProperty("type").GetString();
                lock (_l)
                {
                    if (type == "inline-ack") _acked = Math.Max(_acked, doc.RootElement.GetProperty("size").GetInt64());
                    else if (type == "error") _error = doc.RootElement.GetProperty("message").GetString();
                    var o = _moved; _moved = new(TaskCreationOptions.RunContinuationsAsynchronously); o.TrySetResult();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException) { }
        lock (_l) { _error ??= "inline channel closed"; _moved.TrySetResult(); }
    }

    public async Task SendAsync(long offset, byte[] data, int length, CancellationToken ct)
    {
        var frame = new byte[8 + length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(frame, (ulong)offset);
        Buffer.BlockCopy(data, 0, frame, 8, length);
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    public async Task FlushAsync(long total, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            Task t;
            lock (_l)
            {
                if (_acked >= total) return;
                if (_error != null && _error != "inline channel closed") throw new PipeException("inline data refused: " + _error);
                if (_error != null) throw new IOException(_error);
                t = _moved.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new IOException("timed out waiting for the inline bytes to be stored");
            await t.WaitAsync(left, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(1000).Token).ConfigureAwait(false); } catch { }
        _stop.Cancel(); _ws.Dispose();
        try { await _reader.ConfigureAwait(false); } catch { }
    }
}
