using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace Tbus;

/// <summary>Refused by the Worker (bad or revoked token): registering again is the only way on.</summary>
internal sealed class PipeAuthException(string message) : Exception(message);

/// <summary>
/// The provider side of the pipe bus (no chisel): one WebSocket to the name's Durable Object, over which the Worker sends
/// the visitors' requests and web sockets. Bodies travel inline up to 1 MiB, past that through R2 parts (presigned URLs), both ways.
/// Streaming responses (text/event-stream) stay inline for as long as they last.
/// </summary>
internal sealed class PipeProvider
{
    public const int InlineMax = 1 << 20, InlineChunk = 256 * 1024, MaxPart = 16 << 20;
    private const byte KReqBody = 1, KResBody = 3, KWsIn = 4, KWsOut = 5;
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "host", "connection", "keep-alive", "transfer-encoding", "upgrade", "content-length", "te", "trailer" };

    private readonly string _wsUrl, _token, _targetHost; private readonly int _targetPort; private readonly Action<string> _log;
    private readonly HttpClient _local, _r2;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Dictionary<uint, Req> _reqs = new();
    private readonly Dictionary<uint, TaskCompletionSource<List<(int n, string url)>>> _urlWaits = new();
    private readonly Dictionary<uint, LocalWs> _ws = new();
    private ClientWebSocket? _sock;
    public int Served;

    public PipeProvider(string wsUrl, string token, string targetHost, int targetPort, Action<string> log, HttpMessageHandler? localHandler = null)
    {
        _wsUrl = wsUrl; _token = token; _targetHost = targetHost; _targetPort = targetPort; _log = log;
        _local = new HttpClient(localHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _r2 = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 32 }) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private string Target(string path) => $"http://{(_targetHost.Contains(':') ? "[" + _targetHost + "]" : _targetHost)}:{_targetPort}{path}";

    /// <summary>One connection: returns when it drops (null) or throws PipeAuthException when refused. onConnected runs once the socket is open.</summary>
    public async Task RunOnceAsync(Action onConnected, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        ws.Options.KeepAliveInterval = TimeSpan.Zero; // our own text ping: the Durable Object answers it without waking up
        try { await ws.ConnectAsync(new Uri(_wsUrl), ct).ConfigureAwait(false); }
        catch (WebSocketException e) when (e.Message.Contains("401") || e.Message.Contains("403")) { throw new PipeAuthException("the bus refused the provider token (revoked or name unregistered)"); }
        _sock = ws;
        onConnected();
        using var ping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = Task.Run(async () => { try { while (!ping.IsCancellationRequested) { await Task.Delay(25_000, ping.Token).ConfigureAwait(false); await Send("ping"u8.ToArray(), WebSocketMessageType.Text, ping.Token).ConfigureAwait(false); } } catch { } });
        try { await ReadLoop(ws, ct).ConfigureAwait(false); }
        finally
        {
            ping.Cancel();
            lock (_reqs) { foreach (var r in _reqs.Values) r.Cancel.Cancel(); _reqs.Clear(); }
            lock (_ws) { foreach (var w in _ws.Values) w.Dispose(); _ws.Clear(); }
        }
    }

    private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            var ms = new MemoryStream(); WebSocketReceiveResult r;
            do { r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false); if (r.MessageType == WebSocketMessageType.Close) { if (ws.CloseStatus == (WebSocketCloseStatus)4001) throw new PipeAuthException("the provider token was revoked"); return; } ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
            var data = ms.GetBuffer().AsMemory(0, (int)ms.Length);
            if (r.MessageType == WebSocketMessageType.Binary) { OnBinary(data); continue; }
            if (data.Length == 4 && data.Span.SequenceEqual("pong"u8)) continue;
            using var doc = JsonDocument.Parse(data);
            OnJson(doc.RootElement, ct);
        }
    }

    private void OnBinary(ReadOnlyMemory<byte> d)
    {
        if (d.Length < 5) return;
        var kind = d.Span[0]; var id = BinaryPrimitives.ReadUInt32BigEndian(d.Span[1..]);
        if (kind == KReqBody) { Req? r; lock (_reqs) _reqs.TryGetValue(id, out r); r?.Body.AddInline(d[5..].ToArray()); }
        else if (kind == KWsIn && d.Length >= 6) { LocalWs? w; lock (_ws) _ws.TryGetValue(id, out w); if (w != null) { var op = d.Span[5]; var bytes = d[6..].ToArray(); _ = w.FromViewer(op, bytes); } }
    }

    private void OnJson(JsonElement m, CancellationToken ct)
    {
        var t = m.GetProperty("t").GetString();
        uint id = m.TryGetProperty("rid", out var rr) ? rr.GetUInt32() : m.TryGetProperty("sid", out var ss) ? ss.GetUInt32() : 0;
        switch (t)
        {
            case "req":
                {
                    var req = new Req { Method = m.GetProperty("method").GetString()!, Path = m.GetProperty("path").GetString()!, HasBody = m.GetProperty("hasBody").GetBoolean() };
                    foreach (var h in m.GetProperty("headers").EnumerateArray()) req.Headers.Add(new(h[0].GetString()!, h[1].GetString()!));
                    req.Body = new BodyFeed(_r2);
                    lock (_reqs) _reqs[id] = req;
                    _ = Task.Run(() => Handle(id, req, ct), CancellationToken.None);
                    break;
                }
            case "req-part": { Req? r; lock (_reqs) _reqs.TryGetValue(id, out r); r?.Body.AddPart(m.GetProperty("url").GetString()!, m.GetProperty("size").GetInt32()); break; }
            case "req-end": { Req? r; lock (_reqs) _reqs.TryGetValue(id, out r); r?.Body.End(); break; }
            case "urls":
                {
                    TaskCompletionSource<List<(int, string)>>? w; lock (_urlWaits) _urlWaits.Remove(id, out w);
                    if (w == null) break;
                    var list = new List<(int, string)>();
                    if (m.TryGetProperty("urls", out var us)) foreach (var u in us.EnumerateArray()) list.Add((u.GetProperty("n").GetInt32(), u.GetProperty("url").GetString()!));
                    if (list.Count == 0) w.TrySetException(new IOException(m.TryGetProperty("error", out var e) ? e.GetString()! : "no URLs")); else w.TrySetResult(list);
                    break;
                }
            case "abort": lock (_reqs) if (_reqs.Remove(id, out var s)) { s.Cancel.Cancel(); s.Body.Fail(new IOException("aborted")); } break;
            case "ws-open": { var hs = m.GetProperty("headers").Clone(); var pr = m.TryGetProperty("protocols", out var p0) ? p0.Clone() : default; var wp = m.GetProperty("path").GetString()!; _ = Task.Run(() => OpenWs(id, wp, hs, pr, ct), CancellationToken.None); break; }
            case "ws-close": { LocalWs? w; lock (_ws) _ws.Remove(id, out w); w?.Dispose(); break; }
        }
    }

    private sealed class Req
    {
        public string Method = "GET", Path = "/"; public List<KeyValuePair<string, string>> Headers = new(); public bool HasBody;
        public BodyFeed Body = null!; public CancellationTokenSource Cancel = new();
    }

    private async Task Handle(uint rid, Req st, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(st.Method), Target(st.Path));
            if (st.HasBody) req.Content = new StreamContent(st.Body);
            foreach (var h in st.Headers)
            {
                if (Skip.Contains(h.Key)) continue;
                if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value)) req.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            using var res = await _local.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, st.Cancel.Token).ConfigureAwait(false);
            var headers = new List<string[]>();
            foreach (var h in res.Headers) foreach (var v in h.Value) headers.Add([h.Key, v]);
            foreach (var h in res.Content.Headers) foreach (var v in h.Value) headers.Add([h.Key, v]);
            var stream = res.Content.Headers.ContentType?.MediaType == "text/event-stream";
            await SendJson(new { t = "res", rid, status = (int)res.StatusCode, headers, length = res.Content.Headers.ContentLength }, ct).ConfigureAwait(false);
            await using var body = await res.Content.ReadAsStreamAsync(st.Cancel.Token).ConfigureAwait(false);
            await PumpBody(rid, body, stream, st.Cancel.Token, ct).ConfigureAwait(false);
            Interlocked.Increment(ref Served);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && !st.Cancel.IsCancellationRequested)
        {
            _log($"request {rid} failed: {e.Message}");
            try { await SendJson(new { t = "error", rid, message = e.Message }, ct).ConfigureAwait(false); } catch { }
        }
        finally { lock (_reqs) _reqs.Remove(rid); st.Cancel.Dispose(); }
    }

    private async Task PumpBody(uint rid, Stream body, bool streaming, CancellationToken rc, CancellationToken ct)
    {
        var chunk = new byte[InlineChunk];
        long inline = 0; bool eof = false;
        // inline: every read goes out as it arrives, so a stream is relayed live; a streaming response never leaves this loop
        while (streaming || inline < InlineMax)
        {
            int r = await body.ReadAsync(chunk.AsMemory(0, streaming ? chunk.Length : (int)Math.Min(chunk.Length, InlineMax - inline)), rc).ConfigureAwait(false);
            if (r == 0) { eof = true; break; }
            var f = new byte[5 + r]; f[0] = KResBody; BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1), rid); Buffer.BlockCopy(chunk, 0, f, 5, r);
            await Send(f, WebSocketMessageType.Binary, ct).ConfigureAwait(false);
            inline += r;
        }
        int n = 0;
        var urls = new Dictionary<int, string>();
        var gate = new SemaphoreSlim(8);
        var uploads = new List<Task>();
        Exception? fail = null;
        while (!eof && fail == null)
        {
            n++;
            int want = (int)Math.Min(1L << (19 + Math.Min(n, 6)), MaxPart); // 1, 2, 4 ... 16 MiB
            await gate.WaitAsync(rc).ConfigureAwait(false);
            var buf = new byte[want]; int len = await FillAsync(body, buf, rc).ConfigureAwait(false);
            if (len == 0) { gate.Release(); n--; break; }
            if (!urls.ContainsKey(n))
            {
                var tcs = new TaskCompletionSource<List<(int, string)>>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_urlWaits) _urlWaits[rid] = tcs;
                await SendJson(new { t = "urls", rid, from = n, count = 8 }, ct).ConfigureAwait(false);
                foreach (var (un, url) in await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), rc).ConfigureAwait(false)) urls[un] = url;
            }
            int part = n; string purl = urls[n];
            uploads.Add(Task.Run(async () =>
            {
                try
                {
                    for (int attempt = 1; ; attempt++)
                    {
                        try { using var c = new ByteArrayContent(buf, 0, len); using var resp = await _r2.PutAsync(purl, c, rc).ConfigureAwait(false); resp.EnsureSuccessStatusCode(); break; }
                        catch (Exception) when (attempt < 4 && !rc.IsCancellationRequested) { await Task.Delay(200 * attempt, rc).ConfigureAwait(false); }
                    }
                    await SendJson(new { t = "part", rid, n = part, size = len }, ct).ConfigureAwait(false);
                }
                catch (Exception e) { fail ??= e; }
                finally { gate.Release(); }
            }, CancellationToken.None));
            if (len < want && body.CanSeek) break;
        }
        await Task.WhenAll(uploads).ConfigureAwait(false);
        if (fail != null) throw fail;
        await SendJson(new { t = "end", rid, parts = n }, ct).ConfigureAwait(false);
    }

    /// <summary>Fills the buffer, but hands over what it has after 250 ms of silence, so a slow stream is not held back by the part size.</summary>
    private static async Task<int> FillAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (got > 0) idle.CancelAfter(250);
            int r;
            try { r = await s.ReadAsync(buf.AsMemory(got), idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            if (r == 0) break;
            got += r;
        }
        return got;
    }

    // ---- web sockets: logical streams on the provider socket
    private sealed class LocalWs(PipeProvider p, uint sid, ClientWebSocket ws) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        public ClientWebSocket Ws = ws; private int _unacked; private bool _disposed;
        public readonly TaskCompletionSource<bool> Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task FromViewer(byte op, byte[] data)
        {
            try { if (!await Connected.Task.ConfigureAwait(false)) return; } catch { return; }
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || Ws.State != WebSocketState.Open) return;
                await Ws.SendAsync(data, op == 1 ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
                _unacked += data.Length;
                if (_unacked >= 256 * 1024) { var n = _unacked; _unacked = 0; await p.SendJson(new { t = "ws-ack", sid, n }, CancellationToken.None).ConfigureAwait(false); }
            }
            catch { }
            finally { _lock.Release(); }
        }
        public void Dispose() { _disposed = true; try { Ws.Abort(); Ws.Dispose(); } catch { } }
    }

    private async Task OpenWs(uint sid, string path, JsonElement headers, JsonElement protocols, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        foreach (var h in headers.EnumerateArray())
        {
            var k = h[0].GetString()!; if (Skip.Contains(k) || k.StartsWith("sec-websocket", StringComparison.OrdinalIgnoreCase)) continue;
            try { ws.Options.SetRequestHeader(k, h[1].GetString()!); } catch { }
        }
        if (protocols.ValueKind == JsonValueKind.Array) foreach (var pr in protocols.EnumerateArray()) ws.Options.AddSubProtocol(pr.GetString()!);
        var lw = new LocalWs(this, sid, ws);
        lock (_ws) _ws[sid] = lw; // viewer messages that arrive while we connect wait on lw.Connected (in order is kept by the lock chain below)
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(10_000);
            await ws.ConnectAsync(new Uri(Target(path).Replace("http://", "ws://")), to.Token).ConfigureAwait(false);
        }
        catch (Exception e) { _log($"web socket {sid}: {e.Message}"); lw.Connected.TrySetResult(false); lock (_ws) _ws.Remove(sid); lw.Dispose(); try { await SendJson(new { t = "ws-close", sid, code = 1011 }, ct).ConfigureAwait(false); } catch { } return; }
        lw.Connected.TrySetResult(true);
        var buf = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var ms = new MemoryStream(); WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false); if (r.MessageType == WebSocketMessageType.Close) goto closed; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                var f = new byte[6 + ms.Length]; f[0] = KWsOut; BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1), sid); f[5] = (byte)(r.MessageType == WebSocketMessageType.Text ? 1 : 2);
                ms.GetBuffer().AsSpan(0, (int)ms.Length).CopyTo(f.AsSpan(6));
                await Send(f, WebSocketMessageType.Binary, ct).ConfigureAwait(false);
            }
        closed:;
        }
        catch (Exception) { }
        bool mine; lock (_ws) mine = _ws.Remove(sid);
        if (mine) { try { await SendJson(new { t = "ws-close", sid, code = ws.CloseStatus is { } c && (int)c is >= 1000 and < 5000 and not 1005 and not 1006 ? (int)c : 1000 }, ct).ConfigureAwait(false); } catch { } }
        lw.Dispose();
    }

    private Task SendJson(object o, CancellationToken ct) => Send(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, ct);
    private async Task Send(byte[] b, WebSocketMessageType t, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { var s = _sock; if (s is { State: WebSocketState.Open }) await s.SendAsync(b, t, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    /// <summary>The request body as a stream: the inline bytes, then R2 parts fetched (4 ahead) in order, so the local app starts at once.</summary>
    internal sealed class BodyFeed(HttpClient r2) : Stream
    {
        private readonly object _l = new();
        private readonly Queue<byte[]> _inline = new();
        private readonly List<(string url, int size)> _parts = new();
        private readonly Dictionary<int, Task<byte[]>> _ahead = new();
        private int _next; private bool _ended; private Exception? _fail;
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _cur = []; private int _pos;

        private void Wake() { TaskCompletionSource s; lock (_l) { s = _signal; _signal = new(TaskCreationOptions.RunContinuationsAsynchronously); } s.TrySetResult(); }
        public void AddInline(byte[] b) { lock (_l) _inline.Enqueue(b); Wake(); }
        public void AddPart(string url, int size) { lock (_l) _parts.Add((url, size)); Wake(); }
        public void End() { lock (_l) _ended = true; Wake(); }
        public void Fail(Exception e) { lock (_l) _fail = e; Wake(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default)
        {
            while (true)
            {
                if (_pos < _cur.Length) { int n = Math.Min(dest.Length, _cur.Length - _pos); _cur.AsMemory(_pos, n).CopyTo(dest); _pos += n; return n; }
                Task<byte[]>? head = null; Task wait;
                lock (_l)
                {
                    if (_fail != null) throw _fail;
                    if (_inline.Count > 0) { _cur = _inline.Dequeue(); _pos = 0; continue; }
                    for (int k = _next; k < _parts.Count && k < _next + 4; k++)
                        if (!_ahead.ContainsKey(k)) { var u = _parts[k].url; _ahead[k] = Task.Run(async () => { using var rs = await r2.GetAsync(u, ct).ConfigureAwait(false); rs.EnsureSuccessStatusCode(); return await rs.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false); }, CancellationToken.None); }
                    if (_ahead.TryGetValue(_next, out head)) { }
                    else if (_ended && _next >= _parts.Count) return 0;
                    wait = _signal.Task;
                }
                if (head != null) { _cur = await head.WaitAsync(ct).ConfigureAwait(false); _pos = 0; lock (_l) { _ahead.Remove(_next); _next++; } continue; }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
