using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tbus;

internal sealed record Registration(string Name, int Port, string User, string Password, string? Token = null, string? SocketPath = null);
internal sealed record RegistryRow(string Name, int Port, bool Up);

/// <summary>The router API on the control host, called directly (the Access headers go on the request, not on any command line).</summary>
internal sealed class BusClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _bus;

    public BusClient(string bus, string adminToken, IReadOnlyDictionary<string, string> accessHeaders, HttpMessageHandler? handler = null)
    {
        _bus = bus.TrimEnd('/');
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        foreach (var (k, v) in accessHeaders) _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
    }

    public async Task<Registration> RegisterAsync(string name, CancellationToken ct, ShareMeta? meta = null)
    {
        var fields = new Dictionary<string, object?> { ["name"] = name };
        if (meta != null) foreach (var (k, v) in meta.ToFields()) fields[k] = v;
        using var body = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_bus + "/_api/register", body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Check(resp, text, "register " + name);
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        // the pipe bus answers with a per-name token and the socket path; the chisel router with a port, user and password
        var reg = r.TryGetProperty("token", out var tok)
            ? new Registration(name, 0, "", "", tok.GetString()!, r.TryGetProperty("path", out var pp) ? pp.GetString() : "/_bus/ws/" + name)
            : new Registration(name, r.GetProperty("port").GetInt32(), r.GetProperty("user").GetString()!, r.GetProperty("password").GetString()!);
        Log.Register(reg.Token ?? reg.Password);
        return reg;
    }

    /// <summary>Changes metadata without re-registering (PATCH /_api/register/{name}). False if the name is not registered.</summary>
    public async Task<bool> UpdateAsync(string name, ShareMeta meta, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, _bus + "/_api/register/" + Uri.EscapeDataString(name))
        { Content = new StringContent(JsonSerializer.Serialize(meta.ToFields()), Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        Check(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "update " + name);
        return true;
    }

    public async Task<List<RegistryRow>> ListAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_bus + "/_api/registry", ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Check(resp, text, "list");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.EnumerateArray()
            .Select(e => new RegistryRow(e.GetProperty("name").GetString()!, e.GetProperty("port").GetInt32(), e.TryGetProperty("up", out var u) && u.GetBoolean()))
            .ToList();
    }

    /// <summary>True if removed, false if it was not registered.</summary>
    public async Task<bool> UnregisterAsync(string name, CancellationToken ct)
    {
        using var resp = await _http.DeleteAsync(_bus + "/_api/register/" + Uri.EscapeDataString(name), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        Check(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "unregister " + name);
        return true;
    }

    private static void Check(HttpResponseMessage resp, string text, string what)
    {
        if (resp.IsSuccessStatusCode) return;
        var hint = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Found =>
                " (Cloudflare Access or the admin token refused the call: check the service token with 'tbus login --service-token' / CF_ACCESS_CLIENT_ID+SECRET and the admin token)",
            _ => "",
        };
        var snippet = text.Length > 200 ? text[..200] : text;
        throw new BusException($"{what}: HTTP {(int)resp.StatusCode}{hint} {Log.Redact(snippet)}".TrimEnd(), resp.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class BusException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
