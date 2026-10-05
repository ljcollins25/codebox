using System.IO.Compression;

namespace Tool2App;

/// <summary>Read access to a package: a .nupkg zip or an extracted folder. Paths use '/' and are relative to the package root.</summary>
public abstract class PackageContent : IDisposable
{
    public abstract string Description { get; }
    public abstract IReadOnlyList<string> Files { get; }
    public abstract Stream OpenRead(string path);
    public virtual void Dispose() { }

    public bool Contains(string path) => Files.Contains(path, StringComparer.OrdinalIgnoreCase);

    public string ReadText(string path)
    {
        using var s = OpenRead(path);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public void CopyTo(string path, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var s = OpenRead(path);
        using var d = File.Create(destination);
        s.CopyTo(d);
    }

    public static PackageContent Open(string path)
    {
        if (Directory.Exists(path)) return new DirectoryPackageContent(path);
        if (File.Exists(path)) return new ZipPackageContent(path);
        throw new ToolException($"'{path}' is not a file or folder.");
    }
}

public sealed class ZipPackageContent : PackageContent
{
    private readonly string _path;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ZipPackageContent(string path)
    {
        _path = path;
        try { _zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException ex) { throw new ToolException($"'{path}' is not a valid .nupkg (zip) file.", ex); }
        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            var name = e.FullName.Contains('%') ? Uri.UnescapeDataString(e.FullName) : e.FullName;
            _entries[name.Replace('\\', '/')] = e;
        }
    }

    public override string Description => _path;
    public override IReadOnlyList<string> Files => _entries.Keys.ToList();
    public override Stream OpenRead(string path) =>
        _entries.TryGetValue(path, out var e) ? e.Open() : throw new FileNotFoundException($"{path} is not in {_path}");
    public override void Dispose() => _zip.Dispose();
}

public sealed class DirectoryPackageContent : PackageContent
{
    private readonly string _root;
    private readonly List<string> _files;

    public DirectoryPackageContent(string root)
    {
        _root = Path.GetFullPath(root);
        _files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).ToList();
    }

    public override string Description => _root;
    public override IReadOnlyList<string> Files => _files;
    public override Stream OpenRead(string path) => File.OpenRead(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>Identity read from the package's .nuspec.</summary>
public sealed record NuSpecInfo(string Id, string Version)
{
    public static NuSpecInfo? Read(PackageContent pkg)
    {
        var nuspec = pkg.Files.FirstOrDefault(f => !f.Contains('/') && f.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (nuspec is null) return null;
        var doc = System.Xml.Linq.XDocument.Parse(pkg.ReadText(nuspec));
        var meta = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string? Get(string n) => meta?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim();
        var id = Get("id"); var version = Get("version");
        return id is null || version is null ? null : new NuSpecInfo(id, version);
    }
}

/// <summary>Downloads packages into a cache folder (default: the user's local app data) and picks versions.</summary>
public sealed class PackageStore
{
    private readonly IPackageFeed _feed;
    public string CacheDir { get; }
    public Action<string>? Log { get; set; }

    public PackageStore(IPackageFeed feed, string? cacheDir = null)
    {
        _feed = feed;
        CacheDir = cacheDir ?? DefaultCacheDir();
    }

    public static string DefaultCacheDir()
    {
        var env = Environment.GetEnvironmentVariable("TOOL2APP_CACHE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return Path.Combine(local, "tool2app", "packages");
    }

    public string PathFor(string id, string version) =>
        Path.Combine(CacheDir, id.ToLowerInvariant(), version.ToLowerInvariant(), id.ToLowerInvariant() + "." + version.ToLowerInvariant() + ".nupkg");

    public Task<IReadOnlyList<string>> VersionsAsync(string id, CancellationToken ct = default) => _feed.GetVersionsAsync(id, ct);

    /// <summary>Latest listed version of an id (stable only unless asked), or throws.</summary>
    public async Task<string> LatestAsync(string id, bool includePrerelease, CancellationToken ct = default)
    {
        var all = await _feed.GetVersionsAsync(id, ct).ConfigureAwait(false);
        if (all.Count == 0) throw new ToolException($"Package '{id}' was not found on {_feed.Description}.");
        var best = SemVer.Max(all, includePrerelease) ?? SemVer.Max(all, true);
        return best!.Text;
    }

    public async Task<string> GetAsync(string id, string version, CancellationToken ct = default)
    {
        var path = PathFor(id, version);
        if (File.Exists(path) && new FileInfo(path).Length > 0) { Log?.Invoke($"cached {id} {version}"); return path; }
        Log?.Invoke($"downloading {id} {version} from {_feed.Description}");
        try { await _feed.DownloadAsync(id, version, path, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { throw new ToolException($"Package {id} {version} was not found on {_feed.Description}.", ex); }
        return path;
    }
}
