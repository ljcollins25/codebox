using Tool2App;

namespace ToolRun;

/// <summary>Resolve the tool (cache first), the runtime and the host, and say what to launch.</summary>
public sealed class ToolRunApp
{
    private readonly CliOptions _o;
    private readonly ToolRunHome _home;
    private readonly ToolCache _cache;
    private readonly Rid _rid;
    private readonly Action<string> _info;
    private readonly Action<string> _debug;
    private readonly Lazy<PackageStore> _toolStore;
    private readonly Lazy<PackageStore> _packStore;

    /// <summary>The dotnet roots to look for installed runtimes in; replaceable for tests.</summary>
    public Func<IReadOnlyList<string>> SystemRoots { get; set; } = () => SystemRuntimes.Candidates();

    public ToolRunApp(CliOptions o, ToolRunHome home, Func<IReadOnlyList<string>, IPackageFeed>? feeds = null, Func<string, string?>? env = null,
        string? cwd = null, Rid? rid = null, Action<string>? info = null, Action<string>? debug = null)
    {
        _o = o; _home = home; _cache = new ToolCache(home);
        _rid = rid ?? Rid.Host;
        _info = info ?? (_ => { });
        _debug = debug ?? (_ => { });
        env ??= Environment.GetEnvironmentVariable;
        feeds ??= s => FeedFactory.Create(s, null, _debug);
        var sources = SourceResolver.Resolve(o, home, cwd ?? Directory.GetCurrentDirectory(), env, m => _info("warning: " + m));
        _debug($"sources: {(sources.Tool.Count == 0 ? "nuget.org" : string.Join(", ", sources.Tool))} [{sources.Origin}]");
        _toolStore = new Lazy<PackageStore>(() => NewStore(feeds(sources.Tool)));
        bool same = sources.Runtime.SequenceEqual(sources.Tool);
        _packStore = same ? _toolStore : new Lazy<PackageStore>(() => NewStore(feeds(sources.Runtime)));
    }

    private PackageStore NewStore(IPackageFeed feed) => new(feed, _home.Packages) { Log = m => { if (m.StartsWith("downloading")) _info(m); else _debug(m); } };

    public ToolCache Cache => _cache;


    /// <summary>The tool folder to run: a cached version, or a fresh install (latest stable, the given version, or a newer one with --update).</summary>
    public async Task<CachedTool> ResolveToolAsync(CancellationToken ct = default)
    {
        var id = _o.Package ?? throw new UsageException("No package given.");
        if (_o.PackageVersion is { } ver)
        {
            var hit = _cache.Find(id, ver);
            if (hit is not null) { _debug($"cached {hit.Meta.Id} {hit.Meta.Version}"); return hit; }
            _info($"installing {id} {ver} ...");
            return await _cache.InstallAsync(_toolStore.Value, id, ver, _rid, _debug, ct);
        }

        var cached = _cache.FindLatest(id, _o.Prerelease);
        if (cached is not null && !_o.Update) { _debug($"cached {cached.Meta.Id} {cached.Meta.Version}"); return cached; }

        string latest;
        try { latest = await _toolStore.Value.LatestAsync(id, _o.Prerelease, ct); }
        catch (Exception ex) when (cached is not null && ex is ToolException or HttpRequestException or TaskCanceledException)
        {
            _info($"warning: could not check for updates ({ex.Message}); using cached {cached.Meta.Id} {cached.Meta.Version}");
            return cached;
        }
        if (cached is not null && SemVer.TryParse(cached.Meta.Version, out var cv) && SemVer.TryParse(latest, out var lv) && cv.CompareTo(lv) >= 0)
        {
            _debug($"{cached.Meta.Id} {cached.Meta.Version} is the latest");
            return cached;
        }
        var existing = _cache.Find(id, latest);
        if (existing is not null) return existing;
        _info($"installing {id} {latest} ...");
        return await _cache.InstallAsync(_toolStore.Value, id, latest, _rid, _debug, ct);
    }

    public async Task<LaunchPlan> PrepareAsync(bool dryRun, CancellationToken ct = default)
    {
        var tool = await ResolveToolAsync(ct);
        var cmd = PickCommand(tool.Meta, _o.Command, _o.Package!);
        var entry = tool.EntryPath(cmd);

        var resolver = new RuntimeResolver(_home, () => _packStore.Value, _rid, _info, _debug);
        var rt = await resolver.ResolveAsync(cmd, SystemRoots(), _o.PreferInstalled, _o.RuntimeVersion, _o.NoDownloadRuntime, _o.Prerelease, dryRun, ct);
        foreach (var n in rt.Notes) _debug("runtime: " + n);

        var env = new Dictionary<string, string> { ["DOTNET_ROOT"] = rt.DotnetRoot };
        if (rt.RollForwardEnv is not null) env["DOTNET_ROLL_FORWARD"] = rt.RollForwardEnv;   // beats the tool's runtimeconfig; set only when its framework is not in the root
        var arch = _rid.Arch.ToUpperInvariant();
        env["DOTNET_ROOT_" + arch] = rt.DotnetRoot;   // an apphost prefers the architecture-specific variable

        string file; List<string> args; bool apphost = false;
        var notes = rt.Notes.ToList();
        if (_o.NoApphost)
        {
            if (rt.Muxer is null) throw new ToolException("--no-apphost needs an installed .NET (a 'dotnet' executable); the runtime in use is a private download without one.");
            file = rt.Muxer; args = new List<string> { entry };
        }
        else
        {
            try
            {
                file = await AppHosts.EnsureAsync(entry, cmd.Name, _rid, rt.NetCoreVersion, rt.IsSystem ? rt.DotnetRoot : null, _home, () => _packStore.Value, _info, dryRun, ct);
                args = new List<string>(); apphost = true;
            }
            catch (Exception ex) when (rt.Muxer is not null && ex is ToolException or HttpRequestException or IOException or TaskCanceledException)
            {
                _debug($"no apphost ({ex.Message}); running through 'dotnet' instead");
                file = rt.Muxer; args = new List<string> { entry };
            }
        }
        args.AddRange(_o.ToolArgs);
        return new LaunchPlan
        {
            FileName = file, Args = args, Env = env, ToolId = tool.Meta.Id, ToolVersion = tool.Meta.Version, ToolDir = tool.Dir,
            RuntimeRoot = rt.DotnetRoot, SystemRuntime = rt.IsSystem, UsesApphost = apphost, Notes = notes,
            ModeLine = $"real .NET host, {(rt.IsSystem ? "installed runtime" : "toolrun's private runtime")}; {string.Join("; ", notes)}",
        };
    }

    public static CommandMeta PickCommand(ToolMeta meta, string? name, string package)
    {
        if (name is not null)
            return meta.Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"{meta.Id} has no command '{name}'; it has: {string.Join(", ", meta.Commands.Select(c => c.Name))}.");
        return meta.Commands.FirstOrDefault(c => c.Name.Equals(package, StringComparison.OrdinalIgnoreCase)) ?? meta.Commands[0];
    }
}
