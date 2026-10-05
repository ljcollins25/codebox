using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

namespace R2Pipe;

/// <summary>
/// r2pipe serve: keeps a WebSocket to the provider's Durable Object and answers the visitors' HTTP requests against a local
/// port. Request and response bodies travel inline (up to 1 MiB) over the WebSocket, the rest through R2 parts.
/// </summary>
internal sealed class ServeHost(HttpPipeApi api, string name, string target, int parallel, Action<string> log)
{
    private const int InlineMax = 1 << 20, InlineChunk = 256 * 1024;
    private const byte KReqBody = 1, KResBody = 3;
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "host", "connection", "keep-alive", "transfer-encoding", "upgrade", "content-length", "te", "trailer" };
    private readonly HttpClient _local = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Dictionary<uint, ReqState> _reqs = new();
    private readonly Dictionary<uint, TaskCompletionSource<List<PutUrl>>> _urlWaits = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    public int Served;

    private sealed class ReqState
    {
        public string Method = "GET", Path = "/"; public List<KeyValuePair<string, string>> Headers = new(); public bool HasBody;
        public MemoryStream Body = new(); public int PartsExpected = -1, PartsGot; public TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Task> Fetches = new(); public CancellationTokenSource Cancel = new();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                foreach (var (k, v) in api.AuthHeaders) ws.Options.SetRequestHeader(k, v);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _ws = ws;
                await ws.ConnectAsync(new Uri(api.Base.Replace("https://", "wss://").Replace("http://", "ws://") + "/_serve/" + name), ct).ConfigureAwait(false);
                log($"connected: requests to {api.Base}/p/{name}/ go to {target}");
                backoff = 1;
                await ReadLoop(ws, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException) { log("connection lost: " + e.Message); }
            if (ct.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(backoff, 15)), ct).ConfigureAwait(false);
            backoff *= 2;
        }
    }

    private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            var ms = new MemoryStream(); WebSocketReceiveResult r;
            do { r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
            var data = ms.ToArray();
            if (r.MessageType == WebSocketMessageType.Binary)
            {
                if (data.Length < 5 || data[0] != KReqBody) continue;
                uint rid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1));
                lock (_reqs) if (_reqs.TryGetValue(rid, out var st)) st.Body.Write(data, 5, data.Length - 5);
                continue;
            }
            using var doc = JsonDocument.Parse(data);
            var m = doc.RootElement;
            var t = m.GetProperty("t").GetString();
            uint id = m.TryGetProperty("rid", out var rr) ? rr.GetUInt32() : 0;
            switch (t)
            {
                case "req":
                    {
                        var st = new ReqState { Method = m.GetProperty("method").GetString()!, Path = m.GetProperty("path").GetString()!, HasBody = m.GetProperty("hasBody").GetBoolean() };
                        foreach (var h in m.GetProperty("headers").EnumerateArray()) st.Headers.Add(new(h[0].GetString()!, h[1].GetString()!));
                        lock (_reqs) _reqs[id] = st;
                        if (!st.HasBody) st.Ready.TrySetResult();
                        _ = Task.Run(() => Handle(id, st, ct), CancellationToken.None);
                        break;
                    }
                case "req-part":
                    {
                        ReqState? st; lock (_reqs) _reqs.TryGetValue(id, out st);
                        if (st == null) break;
                        int n = m.GetProperty("n").GetInt32(); int size = m.GetProperty("size").GetInt32(); var url = m.GetProperty("url").GetString()!;
                        var bytes = new byte[size];
                        st.Fetches.Add(Task.Run(async () => { await api.GetAsync(new UrlResult(url, "GET"), bytes, size, st.Cancel.Token).ConfigureAwait(false); return (n, bytes); }).ContinueWith(t => { lock (st.Body) { _partData[(id, t.Result.n)] = t.Result.bytes; } }, TaskContinuationOptions.OnlyOnRanToCompletion));
                        break;
                    }
                case "req-end":
                    {
                        ReqState? st; lock (_reqs) _reqs.TryGetValue(id, out st);
                        if (st == null) break;
                        st.PartsExpected = m.GetProperty("parts").GetInt32();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.WhenAll(st.Fetches.ToArray()).ConfigureAwait(false);
                                for (int n = 1; n <= st.PartsExpected; n++) { byte[] b; lock (st.Body) { b = _partData[(id, n)]; _partData.Remove((id, n)); } st.Body.Write(b); }
                                st.Ready.TrySetResult();
                            }
                            catch (Exception e) { st.Ready.TrySetException(e); }
                        });
                        break;
                    }
                case "urls":
                    {
                        TaskCompletionSource<List<PutUrl>>? w; lock (_urlWaits) _urlWaits.Remove(id, out w);
                        if (w == null) break;
                        var list = new List<PutUrl>();
                        if (m.TryGetProperty("urls", out var us)) foreach (var u in us.EnumerateArray()) list.Add(new PutUrl(u.GetProperty("n").GetInt32(), u.GetProperty("url").GetString()!));
                        if (list.Count == 0) w.TrySetException(new IOException(m.TryGetProperty("error", out var e) ? e.GetString()! : "no URLs"));
                        else w.TrySetResult(list);
                        break;
                    }
                case "abort":
                    lock (_reqs) if (_reqs.Remove(id, out var s)) s.Cancel.Cancel();
                    break;
            }
        }
    }

    private readonly Dictionary<(uint, int), byte[]> _partData = new();

    private async Task Handle(uint rid, ReqState st, CancellationToken ct)
    {
        try
        {
            await st.Ready.Task.ConfigureAwait(false);
            using var req = new HttpRequestMessage(new HttpMethod(st.Method), target.TrimEnd('/') + st.Path);
            if (st.HasBody) { st.Body.Position = 0; req.Content = new StreamContent(st.Body); }
            foreach (var h in st.Headers)
            {
                if (Skip.Contains(h.Key)) continue;
                if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value)) req.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            using var res = await _local.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, st.Cancel.Token).ConfigureAwait(false);
            var headers = new List<string[]>();
            foreach (var h in res.Headers) foreach (var v in h.Value) headers.Add(new[] { h.Key, v });
            foreach (var h in res.Content.Headers) foreach (var v in h.Value) headers.Add(new[] { h.Key, v });
            long? length = res.Content.Headers.ContentLength;
            await SendJson(new { t = "res", rid, status = (int)res.StatusCode, headers, length }, ct).ConfigureAwait(false);
            await using var body = await res.Content.ReadAsStreamAsync(st.Cancel.Token).ConfigureAwait(false);
            await PumpBody(rid, body, st.Cancel.Token, ct).ConfigureAwait(false);
            Interlocked.Increment(ref Served);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log($"request {rid} failed: {e.Message}");
            try { await SendJson(new { t = "error", rid, message = e.Message }, ct).ConfigureAwait(false); } catch { }
        }
        finally { lock (_reqs) _reqs.Remove(rid); }
    }

    /// <summary>The first 1 MiB inline, the rest as R2 parts (1, 2, 4... MiB up to 16 MiB), several uploading at once.</summary>
    private async Task PumpBody(uint rid, Stream body, CancellationToken rc, CancellationToken ct)
    {
        var chunk = new byte[InlineChunk];
        long inline = 0; bool eof = false;
        while (inline < InlineMax)
        {
            int r = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, InlineMax - inline)), rc).ConfigureAwait(false);
            if (r == 0) { eof = true; break; }
            var f = new byte[5 + r]; f[0] = KResBody; BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1), rid); Buffer.BlockCopy(chunk, 0, f, 5, r);
            await SendBinary(f, ct).ConfigureAwait(false);
            inline += r;
        }
        int n = 0;
        var urls = new Dictionary<int, string>();
        var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var uploads = new List<Task>();
        Exception? fail = null;
        while (!eof && fail == null)
        {
            n++;
            int want = (int)PartPlan.SizeOf(n, 1 << 20, 32 << 20);
            await gate.WaitAsync(rc).ConfigureAwait(false);
            var buf = new byte[want]; int len = await Sender.FillAsync(body, buf, want, rc).ConfigureAwait(false);
            if (len == 0) { gate.Release(); n--; break; }
            if (!urls.ContainsKey(n))
            {
                var tcs = new TaskCompletionSource<List<PutUrl>>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_urlWaits) _urlWaits[rid] = tcs;
                await SendJson(new { t = "urls", rid, from = n, count = 8 }, ct).ConfigureAwait(false);
                foreach (var u in await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), rc).ConfigureAwait(false)) urls[u.N] = u.Url;
            }
            int part = n; string url = urls[n];
            uploads.Add(Task.Run(async () =>
            {
                try
                {
                    await Retry.RunAsync(4, async _ => { await api.PutAsync(new UrlResult(url, "PUT"), buf, len, rc).ConfigureAwait(false); return 0; }, null, rc).ConfigureAwait(false);
                    await SendJson(new { t = "part", rid, n = part, size = len }, ct).ConfigureAwait(false);
                }
                catch (Exception e) { fail ??= e; }
                finally { gate.Release(); }
            }, CancellationToken.None));
            if (len < want) break;
        }
        await Task.WhenAll(uploads).ConfigureAwait(false);
        if (fail != null) throw fail;
        await SendJson(new { t = "end", rid, parts = n }, ct).ConfigureAwait(false);
    }

    private Task SendJson(object o, CancellationToken ct) => Send(JsonSerializer.SerializeToUtf8Bytes(o, Ws.Json), WebSocketMessageType.Text, ct);
    private Task SendBinary(byte[] b, CancellationToken ct) => Send(b, WebSocketMessageType.Binary, ct);
    private async Task Send(byte[] b, WebSocketMessageType t, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws!.SendAsync(b, t, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }
}
