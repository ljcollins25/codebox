using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace R2Pipe;

internal sealed record PutUrl(int N, string Url);
internal sealed record CreateResult(string Id, string Mode, long PartSize, string? Name, long? Size, List<PutUrl>? Urls = null);
internal sealed record UrlResult(string Url, string Method, long Size = 0, string? Sha256 = null, bool ViaWorker = false, long Offset = 0);
internal sealed record PartInfo(int N, long Offset, long Size, string Sha256, string Etag, string State, string? Url = null);
internal sealed record MetaInfo(string Id, string Name, long? Size, long PartSize, string Mode, string Status, int? TotalParts, long? TotalSize, long InlineSize, string? Sha256, long Version, long CreatedAt, long ExpiresAt);
internal sealed record StateResult(MetaInfo Meta, List<PartInfo> Parts);
internal sealed record ListEntry(string Id, string? Name, long? Size, long? TotalSize, string Status, long CreatedAt, string? Mode);

/// <summary>An error answer from the Worker. 4xx (except 408 and 429) are final: retrying will not help.</summary>
internal sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public bool Fatal => (int)Status is >= 400 and < 500 && Status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
}

/// <summary>Signaling calls (small JSON) to the Worker.</summary>
internal interface IPipeApi
{
    Task<CreateResult> CreateAsync(string? name, long? size, long partSize, string? mode, CancellationToken ct);
    Task<UrlResult> PutUrlAsync(string id, int n, CancellationToken ct);
    /// <summary>PUT URLs for parts from..from+count-1 in one round trip.</summary>
    Task<List<PutUrl>> PutUrlsAsync(string id, int from, int count, CancellationToken ct);
    Task<UrlResult> GetUrlAsync(string id, int n, CancellationToken ct);
    Task DoneAsync(string id, int n, long offset, long size, string sha256, CancellationToken ct);
    Task AckAsync(string id, int n, CancellationToken ct);
    Task CompleteAsync(string id, int parts, long size, long inline, string sha256, CancellationToken ct);
    Task AbortAsync(string id, CancellationToken ct);
    /// <summary>The snapshot; with wait &gt; 0 it is held until its version moves past <paramref name="since"/>.</summary>
    Task<StateResult> StateAsync(string id, long since, int waitSeconds, CancellationToken ct);
    Task<List<ListEntry>> ListAsync(CancellationToken ct);
    /// <summary>Live updates for a receiver: state pushes and inline bytes (WebSocket), or polling as a fallback.</summary>
    Task<IReceiveFeed> OpenFeedAsync(string id, int pollSeconds, CancellationToken ct);
    /// <summary>The sender's inline channel, or null when it cannot be opened (then everything goes through R2 parts).</summary>
    Task<IInlineSender?> OpenInlineSenderAsync(string id, CancellationToken ct);
}

internal abstract record FeedItem;
internal sealed record StateItem(StateResult State) : FeedItem;
internal sealed record InlineItem(long Offset, byte[] Data) : FeedItem;

internal interface IReceiveFeed : IAsyncDisposable
{
    /// <summary>The next update; null when the feed ended.</summary>
    Task<FeedItem?> NextAsync(CancellationToken ct);
}

internal interface IInlineSender : IAsyncDisposable
{
    Task SendAsync(long offset, byte[] data, int length, CancellationToken ct);
    /// <summary>Waits until the Durable Object has stored the inline bytes up to <paramref name="total"/>.</summary>
    Task FlushAsync(long total, CancellationToken ct);
}

/// <summary>The data plane: bytes to and from the URL the Worker handed out (R2 directly, or the Worker in binding mode).</summary>
internal interface IBlobs
{
    Task PutAsync(UrlResult url, byte[] data, int length, CancellationToken ct);
    /// <summary>Reads exactly <paramref name="expected"/> bytes into <paramref name="buffer"/>.</summary>
    Task GetAsync(UrlResult url, byte[] buffer, int expected, CancellationToken ct);
}

internal sealed class HttpPipeApi : IPipeApi, IBlobs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public HttpPipeApi(string baseUrl, IReadOnlyDictionary<string, string> authHeaders, HttpClient? http = null)
    {
        _base = baseUrl.TrimEnd('/');
        _headers = authHeaders;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal string Base => _base;
    internal IReadOnlyDictionary<string, string> AuthHeaders => _headers;

    public async Task<IReceiveFeed> OpenFeedAsync(string id, int pollSeconds, CancellationToken ct)
    {
        try { return await WsFeed.ConnectAsync(this, id, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException) { return new PollFeed(this, id, pollSeconds); }
    }

    public async Task<IInlineSender?> OpenInlineSenderAsync(string id, CancellationToken ct)
    {
        try { return await WsInlineSender.ConnectAsync(this, id, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException) { return null; }
    }

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        // Access headers go to the Worker only, never to R2 on a presigned URL
        if (url.StartsWith(_base, StringComparison.OrdinalIgnoreCase)) foreach (var (k, v) in _headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private async Task<T> Send<T>(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        // no request may hang for ever: a long poll (?wait=S) gets S + 20 s, everything else 30 s; a timeout is retried by the callers
        int wait = path.Contains("wait=") && int.TryParse(path[(path.IndexOf("wait=") + 5)..].Split('&')[0], out var w) ? w : 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30 + wait));
        try
        {
            using var req = Req(m, _base + path);
            if (body != null) req.Content = JsonContent.Create(body, options: Json);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            await Check(res).ConfigureAwait(false);
            return (await res.Content.ReadFromJsonAsync<T>(Json, cts.Token).ConfigureAwait(false))!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException($"request to {path.Split('?')[0]} timed out"); }
    }

    internal static async Task Check(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        string msg = text;
        try { using var d = JsonDocument.Parse(text); if (d.RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? text; } catch (JsonException) { }
        if (msg.Length > 300) msg = msg[..300];
        throw new ApiException(res.StatusCode, $"{(int)res.StatusCode} {msg}");
    }

    public Task<CreateResult> CreateAsync(string? name, long? size, long partSize, string? mode, CancellationToken ct) =>
        Send<CreateResult>(HttpMethod.Post, "/t", new { name, size, partSize, mode }, ct);
    public Task<UrlResult> PutUrlAsync(string id, int n, CancellationToken ct) => Send<UrlResult>(HttpMethod.Post, $"/t/{id}/parts/{n}/put-url", null, ct);
    public Task<UrlResult> GetUrlAsync(string id, int n, CancellationToken ct) => Send<UrlResult>(HttpMethod.Post, $"/t/{id}/parts/{n}/get-url", null, ct);
    public async Task<List<PutUrl>> PutUrlsAsync(string id, int from, int count, CancellationToken ct) =>
        (await Send<PutUrlsResult>(HttpMethod.Post, $"/t/{id}/put-urls", new { from, count }, ct).ConfigureAwait(false)).Urls;
    private sealed record PutUrlsResult(List<PutUrl> Urls);
    public Task DoneAsync(string id, int n, long offset, long size, string sha256, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/parts/{n}/done", new { offset, size, sha256 }, ct);
    public Task AckAsync(string id, int n, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/parts/{n}/ack", null, ct);
    public Task CompleteAsync(string id, int parts, long size, long inline, string sha256, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/complete", new { parts, size, inline, sha256 }, ct);
    public Task AbortAsync(string id, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/abort", null, ct);
    public Task<StateResult> StateAsync(string id, long since, int waitSeconds, CancellationToken ct) => Send<StateResult>(HttpMethod.Get, $"/t/{id}/state?since={since}&wait={waitSeconds}", null, ct);
    public async Task<List<ListEntry>> ListAsync(CancellationToken ct)
    {
        var r = await Send<JsonElement>(HttpMethod.Get, "/t", null, ct).ConfigureAwait(false);
        return r.GetProperty("transfers").Deserialize<List<ListEntry>>(Json) ?? new();
    }

    /// <summary>Longest a transfer of one part may sit without progress before it is abandoned (then retried with a fresh URL).</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public async Task PutAsync(UrlResult url, byte[] data, int length, CancellationToken ct)
    {
        // a PUT has no progress signal: allow the stall time plus one second per MB (a slow link still finishes)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StallTimeout + TimeSpan.FromSeconds(length / 1_000_000.0));
        try
        {
            using var req = Req(HttpMethod.Put, url.Url);
            req.Content = new ByteArrayContent(data, 0, length);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            await Check(res).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("upload stalled (timed out)"); }
    }

    public async Task GetAsync(UrlResult url, byte[] buffer, int expected, CancellationToken ct)
    {
        // inactivity timeout: reset after every read, so a slow but moving download is fine and a stalled one is retried
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StallTimeout);
        try
        {
            using var req = Req(HttpMethod.Get, url.Url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await Check(res).ConfigureAwait(false);
            await using var s = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            int got = 0;
            while (got < expected)
            {
                int n = await s.ReadAsync(buffer.AsMemory(got, expected - got), cts.Token).ConfigureAwait(false);
                if (n == 0) throw new IOException($"part ended after {got} of {expected} bytes");
                got += n;
                cts.CancelAfter(StallTimeout);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("download stalled (timed out)"); }
    }
}
