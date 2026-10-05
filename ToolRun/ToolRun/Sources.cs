using System.Xml.Linq;
using Tool2App;

namespace ToolRun;

/// <summary>Several feeds as one: versions are the union, a download comes from a feed that lists the version. A feed that fails is skipped.</summary>
public sealed class MultiFeed : IPackageFeed
{
    private readonly IReadOnlyList<IPackageFeed> _feeds;
    private readonly Dictionary<string, List<IPackageFeed>> _owners = new(StringComparer.OrdinalIgnoreCase);
    public Action<string>? Log { get; set; }

    public MultiFeed(IReadOnlyList<IPackageFeed> feeds) { _feeds = feeds; }
    public string Description => string.Join(", ", _feeds.Select(f => f.Description));

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct = default)
    {
        var all = new List<string>();
        Exception? last = null;
        int answered = 0;
        foreach (var f in _feeds)
        {
            try
            {
                var vs = await f.GetVersionsAsync(id, ct).ConfigureAwait(false);
                answered++;
                foreach (var v in vs)
                {
                    if (!_owners.TryGetValue(id + "|" + v, out var owners)) _owners[id + "|" + v] = owners = new();
                    owners.Add(f);
                    if (!all.Contains(v, StringComparer.OrdinalIgnoreCase)) all.Add(v);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or ToolException or TaskCanceledException or IOException)
            {
                last = ex;
                Log?.Invoke($"source {f.Description} failed: {ex.Message}");
            }
        }
        if (answered == 0 && last is not null) throw new ToolException($"No source could be reached ({Description}): {last.Message}", last);
        return all;
    }

    public async Task DownloadAsync(string id, string version, string destinationFile, CancellationToken ct = default)
    {
        var order = new List<IPackageFeed>();
        if (_owners.TryGetValue(id + "|" + version, out var owners)) order.AddRange(owners);
        order.AddRange(_feeds.Where(f => !order.Contains(f)));
        Exception? last = null;
        foreach (var f in order)
        {
            try { await f.DownloadAsync(id, version, destinationFile, ct).ConfigureAwait(false); return; }
            catch (Exception ex) when (ex is HttpRequestException or ToolException or IOException)
            {
                last = ex;
                Log?.Invoke($"source {f.Description} could not serve {id} {version}: {ex.Message}");
            }
        }
        if (last is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } nf) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(nf).Throw();
        throw new ToolException($"No source could serve {id} {version}: {last?.Message}", last!);
    }
}

public static class FeedFactory
{
    /// <summary>No sources: nuget.org (v3, then v2). Otherwise one feed per url (".json" means v3, anything else v2); nuget.org's own v3 url keeps the v2 fallback.</summary>
    public static IPackageFeed Create(IReadOnlyList<string> sources, HttpClient? http = null, Action<string>? log = null)
    {
        http ??= FeedHttp.CreateClient();
        if (sources.Count == 0) return Feeds.Create(null, null, http, log);
        var feeds = new List<IPackageFeed>();
        foreach (var s in sources)
        {
            var url = s.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                throw new ToolException($"Source '{s}' is not an http(s) URL (local folder sources are not supported).");
            feeds.Add(url.TrimEnd('/').Equals(Feeds.NuGetV3, StringComparison.OrdinalIgnoreCase) ? Feeds.Create(null, null, http, log) : Feeds.Create(url, null, http, log));
        }
        return feeds.Count == 1 ? feeds[0] : new MultiFeed(feeds) { Log = log };
    }
}

public sealed record SourceSet(IReadOnlyList<string> Tool, IReadOnlyList<string> Runtime, string Origin);

public static class SourceResolver
{
    /// <summary>--source, then env TOOLRUN_SOURCE (; separated), then config.json, then the nearest nuget.config files, else nuget.org. Runtime and host packs use --runtime-source / config "runtimeSources" if given, else the same sources.</summary>
    public static SourceSet Resolve(CliOptions o, ToolRunHome home, string cwd, Func<string, string?> env, Action<string>? warn = null)
    {
        var cfg = ToolRunConfig.Load(o.ConfigFile ?? home.ConfigFile);
        IReadOnlyList<string> tool; string origin;
        var envSources = (env("TOOLRUN_SOURCE") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (o.Sources.Count > 0) { tool = o.Sources; origin = "--source"; }
        else if (envSources.Length > 0) { tool = envSources; origin = "TOOLRUN_SOURCE"; }
        else if (cfg.Sources.Count > 0) { tool = cfg.Sources; origin = "config.json"; }
        else
        {
            var fromNuGet = NuGetConfig.Sources(cwd, warn);
            tool = fromNuGet; origin = fromNuGet.Count > 0 ? "nuget.config" : "default (nuget.org)";
        }
        IReadOnlyList<string> runtime = o.RuntimeSources.Count > 0 ? o.RuntimeSources
            : (env("TOOLRUN_RUNTIME_SOURCE") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } e ? e
            : cfg.RuntimeSources.Count > 0 ? cfg.RuntimeSources : tool;
        return new SourceSet(tool, runtime, origin);
    }
}

/// <summary>The packageSources of the nuget.config files from the working directory up to the root (nearest wins, &lt;clear/&gt; honoured, disabled sources dropped). http(s) sources only.</summary>
public static class NuGetConfig
{
    private static readonly string[] Names = { "nuget.config", "NuGet.Config", "NuGet.config", "Nuget.Config" };

    public static IReadOnlyList<string> Sources(string startDir, Action<string>? warn = null)
    {
        var files = new List<string>();
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir is not null; dir = dir.Parent)
        {
            var hit = Names.Select(n => Path.Combine(dir.FullName, n)).FirstOrDefault(File.Exists);
            if (hit is not null) files.Add(hit);
        }
        files.Reverse();   // farthest first, so nearer files override
        return Merge(files.Select(f => { try { return File.ReadAllText(f); } catch (IOException) { return null; } }).Where(t => t is not null)!, warn);
    }

    public static IReadOnlyList<string> Merge(IEnumerable<string> configTexts, Action<string>? warn = null)
    {
        var list = new List<(string key, string value)>();
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in configTexts)
        {
            XDocument doc;
            try { doc = XDocument.Parse(text); } catch (System.Xml.XmlException) { continue; }
            var ps = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase));
            if (ps is not null)
                foreach (var e in ps.Elements())
                {
                    switch (e.Name.LocalName.ToLowerInvariant())
                    {
                        case "clear": list.Clear(); break;
                        case "add":
                            var key = (string?)e.Attribute("key"); var value = (string?)e.Attribute("value");
                            if (key is null || value is null) break;
                            list.RemoveAll(x => x.key.Equals(key, StringComparison.OrdinalIgnoreCase));
                            list.Add((key, value));
                            disabled.Remove(key);
                            break;
                        case "remove":
                            var rk = (string?)e.Attribute("key");
                            if (rk is not null) list.RemoveAll(x => x.key.Equals(rk, StringComparison.OrdinalIgnoreCase));
                            break;
                    }
                }
            var dis = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("disabledPackageSources", StringComparison.OrdinalIgnoreCase));
            if (dis is not null)
                foreach (var e in dis.Elements().Where(e => e.Name.LocalName == "add"))
                {
                    var k = (string?)e.Attribute("key");
                    if (k is null) continue;
                    if (string.Equals((string?)e.Attribute("value"), "true", StringComparison.OrdinalIgnoreCase)) disabled.Add(k); else disabled.Remove(k);
                }
        }
        var result = new List<string>();
        foreach (var (key, value) in list)
        {
            if (disabled.Contains(key)) continue;
            if (Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") result.Add(value);
            else warn?.Invoke($"nuget.config source '{key}' ({value}) is not an http(s) URL; skipped.");
        }
        return result;
    }
}
