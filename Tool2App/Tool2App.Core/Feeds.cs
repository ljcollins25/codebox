using System.Xml.Linq;
using System.Text.Json;

namespace Tool2App;

/// <summary>A NuGet package source: list versions of an id and download one .nupkg.</summary>
public interface IPackageFeed
{
    string Description { get; }
    Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct = default);
    Task DownloadAsync(string id, string version, string destinationFile, CancellationToken ct = default);
}

public static class FeedHttp
{
    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("tool2app/1.0");
        return http;
    }

    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await http.GetAsync(url, option, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode >= 500) { last = new HttpRequestException($"{(int)resp.StatusCode} from {url}", null, resp.StatusCode); resp.Dispose(); }
                else return resp;
            }
            catch (HttpRequestException ex) { last = ex; }
            await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
        }
        throw last!;
    }

    public static async Task SaveAsync(HttpClient http, string url, string destinationFile, CancellationToken ct)
    {
        using var resp = await GetAsync(http, url, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url}", null, resp.StatusCode);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFile))!);
        var tmp = destinationFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            File.Move(tmp, destinationFile, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

/// <summary>NuGet v2 (OData/Atom): works where the v3 service index is blocked. Base is e.g. https://www.nuget.org/api/v2</summary>
public sealed class V2Feed : IPackageFeed
{
    private readonly string _base;
    private readonly HttpClient _http;

    public V2Feed(string baseUrl, HttpClient? http = null)
    {
        _base = baseUrl.TrimEnd('/');
        _http = http ?? FeedHttp.CreateClient();
    }

    public string Description => "v2 " + _base;

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct = default)
    {
        var versions = new List<string>();
        string? url = $"{_base}/FindPackagesById()?id='{Uri.EscapeDataString(id)}'";
        var seen = new HashSet<string>();
        while (url is not null && seen.Add(url))
        {
            using var resp = await FeedHttp.GetAsync(_http, url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) break;
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url}", null, resp.StatusCode);
            var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "entry"))
            {
                var props = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "properties");
                var v = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                if (!string.IsNullOrWhiteSpace(v)) versions.Add(v.Trim());
            }
            var next = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && (string?)e.Attribute("rel") == "next");
            url = next is null ? null : new Uri(new Uri(url), (string?)next.Attribute("href")).ToString();
        }
        return versions;
    }

    public Task DownloadAsync(string id, string version, string destinationFile, CancellationToken ct = default) =>
        FeedHttp.SaveAsync(_http, $"{_base}/package/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}", destinationFile, ct);
}

/// <summary>NuGet v3: service index, then the PackageBaseAddress (flat container) resource.</summary>
public sealed class V3Feed : IPackageFeed
{
    private readonly string _index;
    private readonly HttpClient _http;
    private string? _flat;

    public V3Feed(string indexUrl, HttpClient? http = null)
    {
        _index = indexUrl;
        _http = http ?? FeedHttp.CreateClient();
    }

    public string Description => "v3 " + _index;

    private async Task<string> FlatAsync(CancellationToken ct)
    {
        if (_flat is not null) return _flat;
        using var resp = await FeedHttp.GetAsync(_http, _index, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {_index}", null, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var r in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = r.TryGetProperty("@type", out var t) ? t.GetString() : null;
            if (type is not null && type.StartsWith("PackageBaseAddress", StringComparison.Ordinal))
                return _flat = r.GetProperty("@id").GetString()!.TrimEnd('/') + "/";
        }
        throw new ToolException($"The v3 index {_index} has no PackageBaseAddress resource; try --feed-kind v2.");
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct = default)
    {
        var url = $"{await FlatAsync(ct).ConfigureAwait(false)}{id.ToLowerInvariant()}/index.json";
        using var resp = await FeedHttp.GetAsync(_http, url, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return Array.Empty<string>();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url}", null, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("versions").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    public async Task DownloadAsync(string id, string version, string destinationFile, CancellationToken ct = default)
    {
        var lid = id.ToLowerInvariant();
        var lv = version.ToLowerInvariant();
        await FeedHttp.SaveAsync(_http, $"{await FlatAsync(ct).ConfigureAwait(false)}{lid}/{lv}/{lid}.{lv}.nupkg", destinationFile, ct).ConfigureAwait(false);
    }
}

/// <summary>Tries each feed in order; moves on to the next only when one fails (network error, blocked API), not when a package is simply absent.</summary>
public sealed class FallbackFeed : IPackageFeed
{
    private readonly IReadOnlyList<IPackageFeed> _feeds;
    private int _preferred;
    public Action<string>? Log { get; set; }

    public FallbackFeed(params IPackageFeed[] feeds) { _feeds = feeds; }

    public string Description => string.Join(" -> ", _feeds.Select(f => f.Description));

    private async Task<T> RunAsync<T>(Func<IPackageFeed, Task<T>> action)
    {
        Exception? last = null;
        bool allNotFound = true;
        for (int i = 0; i < _feeds.Count; i++)
        {
            var idx = (_preferred + i) % _feeds.Count;
            try { var r = await action(_feeds[idx]).ConfigureAwait(false); _preferred = idx; return r; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or System.Xml.XmlException or ToolException)
            {
                last = ex;
                allNotFound &= ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound };
                Log?.Invoke($"feed {_feeds[idx].Description} failed: {ex.Message}");
            }
        }
        // every feed said "no such package/version": let the caller report that plainly instead of a feed outage
        if (allNotFound && last is HttpRequestException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(last).Throw();
        throw new ToolException($"No feed could serve the request ({Description}): {last?.Message}", last!);
    }

    public Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct = default) =>
        RunAsync(f => f.GetVersionsAsync(id, ct));

    public Task DownloadAsync(string id, string version, string destinationFile, CancellationToken ct = default) =>
        RunAsync(async f => { await f.DownloadAsync(id, version, destinationFile, ct).ConfigureAwait(false); return 0; });
}

public static class Feeds
{
    public const string NuGetV3 = "https://api.nuget.org/v3/index.json";
    public const string NuGetV2 = "https://www.nuget.org/api/v2";

    /// <summary>
    /// No url: nuget.org, v3 first with v2 as fallback. With a url the kind is auto (.json means v3, anything else v2),
    /// or forced with "v2" / "v3"; there is no fallback for an explicit feed.
    /// </summary>
    public static IPackageFeed Create(string? url, string? kind = null, HttpClient? http = null, Action<string>? log = null)
    {
        http ??= FeedHttp.CreateClient();
        if (string.IsNullOrWhiteSpace(url))
        {
            var k = kind?.ToLowerInvariant();
            if (k == "v2") return new V2Feed(NuGetV2, http);
            if (k == "v3") return new V3Feed(NuGetV3, http);
            return new FallbackFeed(new V3Feed(NuGetV3, http), new V2Feed(NuGetV2, http)) { Log = log };
        }
        var resolved = string.IsNullOrWhiteSpace(kind) || kind.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? (url.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "v3" : "v2")
            : kind.ToLowerInvariant();
        return resolved switch
        {
            "v3" => new V3Feed(url, http),
            "v2" => new V2Feed(url, http),
            _ => throw new ToolException($"Unknown feed kind '{kind}' (use v2, v3 or auto)."),
        };
    }
}
