using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace R2Pipe;

internal sealed record CreateResult(string Id, string Mode, long PartSize, string? Name, long? Size);
internal sealed record UrlResult(string Url, string Method, long Size = 0, string? Sha256 = null, bool ViaWorker = false);
internal sealed record PartInfo(int N, long Size, string Sha256, string Etag, string State);
internal sealed record MetaInfo(string Id, string Name, long? Size, long PartSize, string Mode, string Status, int? TotalParts, long? TotalSize, string? Sha256, long Version, long CreatedAt, long ExpiresAt);
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
    Task<UrlResult> GetUrlAsync(string id, int n, CancellationToken ct);
    Task DoneAsync(string id, int n, long size, string sha256, CancellationToken ct);
    Task AckAsync(string id, int n, CancellationToken ct);
    Task CompleteAsync(string id, int parts, long size, string sha256, CancellationToken ct);
    Task AbortAsync(string id, CancellationToken ct);
    /// <summary>The snapshot; with wait &gt; 0 it is held until its version moves past <paramref name="since"/>.</summary>
    Task<StateResult> StateAsync(string id, long since, int waitSeconds, CancellationToken ct);
    Task<List<ListEntry>> ListAsync(CancellationToken ct);
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

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        // Access headers go to the Worker only, never to R2 on a presigned URL
        if (url.StartsWith(_base, StringComparison.OrdinalIgnoreCase)) foreach (var (k, v) in _headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private async Task<T> Send<T>(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var req = Req(m, _base + path);
        if (body != null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await Check(res).ConfigureAwait(false);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
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
    public Task DoneAsync(string id, int n, long size, string sha256, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/parts/{n}/done", new { size, sha256 }, ct);
    public Task AckAsync(string id, int n, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/parts/{n}/ack", null, ct);
    public Task CompleteAsync(string id, int parts, long size, string sha256, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/complete", new { parts, size, sha256 }, ct);
    public Task AbortAsync(string id, CancellationToken ct) => Send<JsonElement>(HttpMethod.Post, $"/t/{id}/abort", null, ct);
    public Task<StateResult> StateAsync(string id, long since, int waitSeconds, CancellationToken ct) => Send<StateResult>(HttpMethod.Get, $"/t/{id}/state?since={since}&wait={waitSeconds}", null, ct);
    public async Task<List<ListEntry>> ListAsync(CancellationToken ct)
    {
        var r = await Send<JsonElement>(HttpMethod.Get, "/t", null, ct).ConfigureAwait(false);
        return r.GetProperty("transfers").Deserialize<List<ListEntry>>(Json) ?? new();
    }

    public async Task PutAsync(UrlResult url, byte[] data, int length, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Put, url.Url);
        req.Content = new ByteArrayContent(data, 0, length);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        await Check(res).ConfigureAwait(false);
    }

    public async Task GetAsync(UrlResult url, byte[] buffer, int expected, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, url.Url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await Check(res).ConfigureAwait(false);
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        int got = 0;
        while (got < expected)
        {
            int n = await s.ReadAsync(buffer.AsMemory(got, expected - got), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException($"part ended after {got} of {expected} bytes");
            got += n;
        }
    }
}
