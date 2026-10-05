using System.Text.Json;
using Tool2App;

namespace ToolRun;

public sealed class FrameworkMeta
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class CommandMeta
{
    public string Name { get; set; } = "";
    /// <summary>Entry dll, relative to the version folder, with '/' separators.</summary>
    public string Entry { get; set; } = "";
    public string? RollForward { get; set; }
    public List<FrameworkMeta> Frameworks { get; set; } = new();
}

/// <summary>toolrun.json in every cached tool folder: what is needed to run it without looking at the package again.</summary>
public sealed class ToolMeta
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Rid { get; set; } = "";
    public string Tfm { get; set; } = "";
    public List<CommandMeta> Commands { get; set; } = new();
    public DateTime InstalledUtc { get; set; }
}

public sealed record CachedTool(ToolMeta Meta, string Dir)
{
    public string EntryPath(CommandMeta c) => Path.Combine(Dir, c.Entry.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>tools/&lt;id&gt;/&lt;version&gt;/ holds the extracted tools/&lt;tfm&gt;/&lt;rid&gt;/ folder of the package (as dotnet tool install keeps it), the nuspec and toolrun.json.</summary>
public sealed class ToolCache
{
    public const string MetaFile = "toolrun.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ToolRunHome _home;

    public ToolCache(ToolRunHome home) { _home = home; }

    public IEnumerable<CachedTool> Entries(string? id = null)
    {
        if (!Directory.Exists(_home.Tools)) yield break;
        var idDirs = id is null ? Directory.EnumerateDirectories(_home.Tools) : new[] { Path.Combine(_home.Tools, id.ToLowerInvariant()) };
        foreach (var idDir in idDirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!Directory.Exists(idDir)) continue;
            foreach (var vDir in Directory.EnumerateDirectories(idDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (Path.GetFileName(vDir).StartsWith('.')) continue;
                var meta = Read(vDir);
                if (meta is not null) yield return new CachedTool(meta, vDir);
            }
        }
    }

    public static ToolMeta? Read(string dir)
    {
        var f = Path.Combine(dir, MetaFile);
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<ToolMeta>(File.ReadAllText(f), Json); }
        catch (JsonException) { return null; }
    }

    public CachedTool? Find(string id, string version)
    {
        if (!SemVer.TryParse(version, out var want)) return null;
        return Entries(id).FirstOrDefault(e => SemVer.TryParse(e.Meta.Version, out var v) && v.CompareTo(want) == 0);
    }

    /// <summary>Highest cached version: stable ones unless asked for prereleases (or only prereleases exist).</summary>
    public CachedTool? FindLatest(string id, bool includePrerelease)
    {
        var all = Entries(id).ToList();
        if (all.Count == 0) return null;
        var best = SemVer.Max(all.Select(e => e.Meta.Version), includePrerelease) ?? SemVer.Max(all.Select(e => e.Meta.Version), true);
        return all.First(e => e.Meta.Version == best!.Text);
    }

    public async Task<CachedTool> InstallAsync(PackageStore store, string id, string version, Rid rid, Action<string> log, CancellationToken ct = default)
    {
        var nupkg = await store.GetAsync(id, version, ct).ConfigureAwait(false);
        var final = _home.ToolDir(id, version);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var tmp = Path.Combine(Path.GetDirectoryName(final)!, "." + Path.GetFileName(final) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var downloaded = new List<string> { nupkg };
        try
        {
            using var main = PackageContent.Open(nupkg);
            PackageContent pkg = main;
            PackageContent? ridPkg = null;
            try
            {
                var asset = ToolAsset.Select(ToolAsset.Discover(pkg), rid);
                // tool packages published per RID: the main package only points at "<id>.<rid>" packages
                if (asset.Settings.Commands.Count == 0 && asset.Settings.RidPackages.Count > 0)
                {
                    var pick = asset.Settings.RidPackages.Select(r => (r, rank: rid.Rank(r.Rid))).Where(x => x.rank >= 0).OrderBy(x => x.rank).Select(x => x.r).FirstOrDefault()
                        ?? throw new ToolException($"{id} has RID-specific packages ({string.Join(", ", asset.Settings.RidPackages.Select(r => r.Rid))}) but none for {rid}.");
                    log($"RID-specific tool package {pick.Id} {version}");
                    var p2 = await store.GetAsync(pick.Id, version, ct).ConfigureAwait(false);
                    downloaded.Add(p2);
                    ridPkg = PackageContent.Open(p2);
                    pkg = ridPkg;
                    asset = ToolAsset.Select(ToolAsset.Discover(pkg), rid);
                }

                var commands = asset.Settings.Commands.Where(c => c.Runner.Equals("dotnet", StringComparison.OrdinalIgnoreCase)).ToList();
                if (commands.Count == 0) throw new ToolException($"{id} {version} has no 'dotnet' command (runners: {string.Join(", ", asset.Settings.Commands.Select(c => c.Runner))}).");

                Directory.CreateDirectory(tmp);
                foreach (var f in pkg.Files)
                {
                    if (!f.StartsWith(asset.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = f[asset.Prefix.Length..];
                    if (rel.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) && rel.Split('/').Length > 2 && rid.Rank(rel.Split('/')[1]) < 0) continue;   // another platform's natives
                    pkg.CopyTo(f, Path.Combine(tmp, f.Replace('/', Path.DirectorySeparatorChar)));
                }
                foreach (var f in main.Files.Where(f => f.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !f.Contains('/')))
                    main.CopyTo(f, Path.Combine(tmp, f));

                var meta = new ToolMeta { Id = NuSpecInfo.Read(main)?.Id ?? id, Version = version, Rid = rid.Value, Tfm = asset.Tfm.Text, InstalledUtc = DateTime.UtcNow };
                foreach (var c in commands)
                {
                    var entry = asset.Prefix + c.EntryPoint.Replace('\\', '/');
                    var entryFile = Path.Combine(tmp, entry.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(entryFile)) throw new ToolException($"The entry point {c.EntryPoint} of command '{c.Name}' is not in {asset.Prefix}.");
                    var rcFile = entryFile[..^Path.GetExtension(entryFile).Length] + ".runtimeconfig.json";
                    var cfg = File.Exists(rcFile) ? RuntimeConfig.Parse(File.ReadAllText(rcFile)) : RuntimeConfig.FromTfm(asset.Tfm);
                    var fws = cfg.Frameworks.Count > 0 ? cfg.Frameworks : RuntimeConfig.FromTfm(asset.Tfm).Frameworks;
                    meta.Commands.Add(new CommandMeta
                    {
                        Name = c.Name, Entry = entry, RollForward = cfg.RollForward,
                        Frameworks = fws.Select(f => new FrameworkMeta { Name = f.Name, Version = f.Version }).ToList(),
                    });
                }
                File.WriteAllText(Path.Combine(tmp, MetaFile), JsonSerializer.Serialize(meta, Json));

                try { Directory.Move(tmp, final); }
                catch (IOException) when (Directory.Exists(final) && Read(final) is not null) { /* another toolrun installed it first */ }
                log($"installed {meta.Id} {version} ({ToolRunHome.FormatSize(ToolRunHome.SizeOf(final))}) to {final}");
                return new CachedTool(Read(final) ?? meta, final);
            }
            finally { ridPkg?.Dispose(); }
        }
        finally
        {
            try { if (Directory.Exists(tmp)) ToolRunHome.DeleteDir(tmp); } catch (IOException) { }
            foreach (var p in downloaded) ToolRunHome.DeleteDownload(p);
        }
    }

    /// <summary>Removes cached versions of an id (all, or one); returns bytes freed.</summary>
    public long Remove(string id, string? version = null)
    {
        long freed = 0;
        foreach (var e in Entries(id).ToList())
        {
            if (version is not null && !(SemVer.TryParse(version, out var want) && SemVer.TryParse(e.Meta.Version, out var v) && v.CompareTo(want) == 0)) continue;
            freed += ToolRunHome.SizeOf(e.Dir);
            ToolRunHome.DeleteDir(e.Dir);
        }
        var idDir = Path.Combine(_home.Tools, id.ToLowerInvariant());
        if (Directory.Exists(idDir) && !Directory.EnumerateFileSystemEntries(idDir).Any()) Directory.Delete(idDir);
        return freed;
    }
}
