using System.IO.Compression;
using System.Runtime.InteropServices;
using Tool2App;

namespace ToolRun;

public sealed record RuntimeInstall(string Root, string Framework, string Version);

/// <summary>.NET runtimes installed on the machine: dotnet roots with host/fxr and shared/&lt;framework&gt;/&lt;version&gt;.</summary>
public static class SystemRuntimes
{
    public static IReadOnlyList<string> Candidates(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { p = Path.GetFullPath(p); } catch (ArgumentException) { return; }
            if (Directory.Exists(p) && !list.Contains(p, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) list.Add(p);
        }
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        Add(env("DOTNET_ROOT_" + arch));
        Add(env("DOTNET_ROOT"));
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var dir in (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = Path.Combine(dir.Trim('"'), exe);
                if (!File.Exists(f)) continue;
                var fi = new FileInfo(f);
                var target = fi.ResolveLinkTarget(true);
                Add(Path.GetDirectoryName((target ?? fi).FullName));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            Add(Path.Combine(env("ProgramFiles") ?? @"C:\Program Files", "dotnet"));
        }
        else
        {
            foreach (var p in new[] { "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet", "/opt/dotnet", "/opt/homebrew/opt/dotnet/libexec" }) Add(p);
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        }
        return list;
    }

    public static List<RuntimeInstall> Scan(IEnumerable<string> roots)
    {
        var result = new List<RuntimeInstall>();
        foreach (var root in roots)
        {
            var shared = Path.Combine(root, "shared");
            if (!Directory.Exists(shared) || !Directory.Exists(Path.Combine(root, "host", "fxr"))) continue;
            foreach (var fw in Directory.EnumerateDirectories(shared))
                foreach (var v in Directory.EnumerateDirectories(fw))
                    if (SemVer.TryParse(Path.GetFileName(v), out _))
                        result.Add(new RuntimeInstall(root, Path.GetFileName(fw), Path.GetFileName(v)));
        }
        return result;
    }

    /// <summary>Frameworks under one dotnet root's shared/ folder (no host/fxr needed).</summary>
    public static List<RuntimeInstall> ScanShared(string root)
    {
        var result = new List<RuntimeInstall>();
        var shared = Path.Combine(root, "shared");
        if (!Directory.Exists(shared)) return result;
        foreach (var fw in Directory.EnumerateDirectories(shared))
            foreach (var v in Directory.EnumerateDirectories(fw))
                if (SemVer.TryParse(Path.GetFileName(v), out _)) result.Add(new RuntimeInstall(root, Path.GetFileName(fw), Path.GetFileName(v)));
        return result;
    }

    public static string? Muxer(string root)
    {
        var f = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(f) ? f : null;
    }
}

/// <summary>What the host does with a framework reference: which installed version satisfies it.</summary>
public static class RollForward
{
    /// <summary>
    /// Disable: exactly the requested version. LatestPatch: same major.minor, patch at least the requested one (the latest).
    /// Minor (the default): same major, the lowest minor that is at least the requested one, its latest patch.
    /// Major: the same, else the lowest higher major. LatestMinor / LatestMajor: the highest available instead of the nearest.
    /// Prerelease versions only count when the request is a prerelease.
    /// </summary>
    public static string? Pick(string requested, string? policy, IEnumerable<string> available)
    {
        var req = SemVer.Parse(requested);
        var all = available.Select(a => SemVer.TryParse(a, out var v) ? v : null).Where(v => v is not null && (req.IsPrerelease || !v.IsPrerelease)).Select(v => v!).ToList();
        var p = (policy ?? "Minor").ToLowerInvariant();
        if (p == "disable") return all.FirstOrDefault(v => v.CompareTo(req) == 0)?.Text;

        var line = all.Where(v => v.Major == req.Major && v.Minor == req.Minor && v.CompareTo(req) >= 0).OrderBy(v => v).LastOrDefault();
        if (p == "latestpatch") return line?.Text;
        if (p is "minor" or "major" && line is not null) return line.Text;

        // pick a major.minor line from the pool (the lowest or the highest) and take its newest patch
        static string? FromLine(List<SemVer> pool, bool highest)
        {
            if (pool.Count == 0) return null;
            var first = (highest ? pool.OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor) : pool.OrderBy(v => v.Major).ThenBy(v => v.Minor)).First();
            return pool.Where(v => v.Major == first.Major && v.Minor == first.Minor).Max()!.Text;
        }
        var higherMinor = all.Where(v => v.Major == req.Major && v.Minor > req.Minor).ToList();
        switch (p)
        {
            case "minor": return FromLine(higherMinor, false);
            case "major": return FromLine(higherMinor, false) ?? FromLine(all.Where(v => v.Major > req.Major).ToList(), false);
            case "latestminor": return FromLine(all.Where(v => v.Major == req.Major && v.CompareTo(req) >= 0).ToList(), true);
            case "latestmajor": return FromLine(all.Where(v => v.CompareTo(req) >= 0).ToList(), true);
            default: return null;
        }
    }
}

public sealed record InstalledChoice(string Root, IReadOnlyList<(FrameworkRef fw, string version)> Frameworks);

public static class RuntimeSelector
{
    /// <summary>The first dotnet root that satisfies every framework (the host looks in one root only), or null. forcedVersion: exactly that version.</summary>
    public static InstalledChoice? FindInstalled(IReadOnlyList<FrameworkRef> needed, string? rollForward, IReadOnlyList<RuntimeInstall> installs, string? forcedVersion = null)
    {
        foreach (var root in installs.Select(i => i.Root).Distinct())
        {
            var picks = new List<(FrameworkRef, string)>();
            foreach (var fw in needed)
            {
                var versions = installs.Where(i => i.Root == root && i.Framework == fw.Name).Select(i => i.Version).ToList();
                var pick = forcedVersion is not null
                    ? versions.FirstOrDefault(v => SemVer.TryParse(v, out var a) && SemVer.TryParse(forcedVersion, out var b) && a.CompareTo(b) == 0)
                    : Pick(fw, rollForward, versions);
                if (pick is null) { picks = null; break; }
                picks.Add((fw, pick));
            }
            if (picks is not null) return new InstalledChoice(root, picks);
        }
        return null;
    }

    private static string? Pick(FrameworkRef fw, string? rollForward, List<string> versions) => RollForward.Pick(fw.Version, rollForward, versions);
}

public sealed record RuntimePlan(string DotnetRoot, bool IsSystem, string NetCoreVersion, string? Muxer, string? RollForwardEnv, IReadOnlyList<string> Notes);

/// <summary>Runtime and host files that travel inside the toolrun executable (built with the runtime pack of toolrun's own version and RID).</summary>
public static class EmbeddedAssets
{
    public const string RuntimeZip = "toolrun.runtime.zip";
    public const string AppHost = "toolrun.apphost";

    /// <summary>Tests replace the embedded files for their own async flow.</summary>
    internal static readonly AsyncLocal<Func<string, Stream?>?> Override = new();

    public static Stream? Open(string name) => Override.Value is { } f ? f(name) : typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(name);

    /// <summary>The version stamped in the embedded root (the folder name under shared/Microsoft.NETCore.App/), or null when this build embeds none.</summary>
    public static string? RuntimeVersion()
    {
        using var s = Open(RuntimeZip);
        if (s is null) return null;
        using var z = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read);
        const string prefix = "shared/Microsoft.NETCore.App/";
        foreach (var e in z.Entries)
            if (e.FullName.StartsWith(prefix, StringComparison.Ordinal))
                return e.FullName[prefix.Length..].Split('/')[0];
        return null;
    }

    /// <summary>Unpacks the embedded root into a new folder.</summary>
    public static void ExtractRuntime(string dir)
    {
        using var s = Open(RuntimeZip) ?? throw new ToolException("This toolrun has no embedded runtime.");
        using var z = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read);
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            var dest = Path.Combine(dir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
    }
}

/// <summary>
/// toolrun's private dotnet root, <home>/dotnet: host/fxr/&lt;v&gt;/hostfxr and shared/&lt;framework&gt;/&lt;v&gt;/ in the layout an installer would make, which is all the real host
/// (hostfxr + hostpolicy, run by the apphost) needs to find the frameworks. It starts as toolrun's own runtime, taken from the executable itself (or, in a
/// build without one, downloaded once); ASP.NET Core and WindowsDesktop are added from NuGet when a tool needs them.
/// </summary>
public sealed class PrivateRuntime
{
    private readonly ToolRunHome _home;
    private readonly Func<PackageStore> _packs;
    private readonly Rid _rid;
    private readonly Action<string> _info;
    private readonly Action<string> _debug;

    public string Dir => _home.DotnetRoot;

    public PrivateRuntime(ToolRunHome home, Func<PackageStore> packs, Rid rid, Action<string> info, Action<string> debug)
    {
        _home = home; _packs = packs; _rid = rid; _info = info; _debug = debug;
    }

    public List<RuntimeInstall> Installed() => SystemRuntimes.ScanShared(Dir);

    /// <summary>A folder under the root (host/fxr/&lt;v&gt; or shared/&lt;fw&gt;/&lt;v&gt;, the version being its name) that has files in it: finished by whoever made it.</summary>
    private static bool FolderComplete(string dir) => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    public static string FrameworkDir(string root, string framework, string version) => Path.Combine(root, "shared", framework, version);

    public bool Has(string framework, string version) =>
        Directory.Exists(FrameworkDir(Dir, framework, version)) && (framework != FrameworkRef.NetCore || Directory.Exists(Path.Combine(Dir, "host", "fxr", version)));

    /// <summary>The version of toolrun's own runtime: stamped in the embedded root; else a runtime of the running line already in the root; else (needs the feed) the latest patch of that line.</summary>
    public async Task<string> OwnVersionAsync(bool noDownload, bool prerelease, CancellationToken ct)
    {
        var embedded = EmbeddedAssets.RuntimeVersion();
        if (embedded is not null) return embedded;
        var run = Environment.Version;
        var have = Installed().Where(i => i.Framework == FrameworkRef.NetCore && SemVer.TryParse(i.Version, out var v) && v.Major == run.Major && v.Minor == run.Minor)
            .Select(i => SemVer.Parse(i.Version)).OrderBy(v => v).LastOrDefault();
        if (have is not null) return have.Text;
        if (noDownload) throw new ToolException($"This toolrun has no embedded runtime and --no-download-runtime was given; nothing is in {Dir}.");
        var versions = await _packs().VersionsAsync(FrameworkRef.NetCore + ".Runtime." + _rid.PackRid, ct).ConfigureAwait(false);
        return RuntimeVersions.Select($"{run.Major}.{run.Minor}.0", versions, "Disable", prerelease);
    }

    /// <summary>Makes sure framework + version is in the root: extracted from the executable (toolrun's own NETCore), else downloaded from NuGet.</summary>
    public async Task<string> EnsureAsync(FrameworkRef fw, string version, bool noDownload, bool dryRun, CancellationToken ct)
    {
        if (Has(fw.Name, version)) return "present";
        if (dryRun) return "missing";
        if (fw.Name == FrameworkRef.NetCore && version == EmbeddedAssets.RuntimeVersion())
        {
            Directory.CreateDirectory(Dir);
            var tmp = Path.Combine(Dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                EmbeddedAssets.ExtractRuntime(tmp);
                foreach (var sub in new[] { Path.Combine("host", "fxr", version), Path.Combine("shared", fw.Name, version) })
                {
                    var dest = Path.Combine(Dir, sub);
                    if (Directory.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    SafeMove.Dir(Path.Combine(tmp, sub), dest, () => FolderComplete(dest));
                }
            }
            finally { SafeMove.DiscardQuietly(tmp); }
            _info($"unpacked {fw.Name} {version} from toolrun to {Dir}");
            return "unpacked";
        }
        if (noDownload) throw new ToolException($"{fw.Name} {version} is not in toolrun's runtime folder and --no-download-runtime was given.");
        await DownloadAsync(fw, version, ct).ConfigureAwait(false);
        return "downloaded";
    }

    private async Task DownloadAsync(FrameworkRef fw, string version, CancellationToken ct)
    {
        var packId = fw.RuntimePackId(_rid);
        var store = _packs();
        var versions = await store.VersionsAsync(packId, ct).ConfigureAwait(false);
        if (versions.Count == 0) throw new ToolException($"{packId} was not found on {store.Description}.");
        var exact = versions.FirstOrDefault(v => v.Equals(version, StringComparison.OrdinalIgnoreCase));
        if (exact is null)
        {
            if (fw.Name == FrameworkRef.NetCore) throw new ToolException($"Runtime version {version} is not available on {store.Description}.");
            // ASP.NET Core and the others are versioned with .NET: the same version, else the newest patch of that line
            exact = RuntimeVersions.Select(version, versions, "Disable", SemVer.Parse(version).IsPrerelease);
        }
        _info($"downloading {packId} {exact} ...");
        var nupkg = await store.GetAsync(packId, exact, ct).ConfigureAwait(false);
        var tmp = Path.Combine(Dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var pack = PackageContent.Open(nupkg)) ExtractRuntimePack(pack, packId, fw.Name, exact, _rid, tmp);
            foreach (var sub in new[] { Path.Combine("host", "fxr", exact), Path.Combine("shared", fw.Name, exact) })
            {
                var src = Path.Combine(tmp, sub);
                if (!Directory.Exists(src)) continue;
                var dest = Path.Combine(Dir, sub);
                if (Directory.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                SafeMove.Dir(src, dest, () => FolderComplete(dest));
            }
            _info($"installed {fw.Name} {exact} ({ToolRunHome.FormatSize(ToolRunHome.SizeOf(FrameworkDir(Dir, fw.Name, exact)))}) in {Dir}");
        }
        finally
        {
            SafeMove.DiscardQuietly(tmp);
            ToolRunHome.DeleteDownload(nupkg);
        }
    }

    /// <summary>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/** and native/** go to shared/&lt;framework&gt;/&lt;version&gt;/, except hostfxr, which goes to host/fxr/&lt;version&gt; (NETCore only).</summary>
    public static void ExtractRuntimePack(PackageContent pack, string packId, string framework, string version, Rid rid, string rootDir)
    {
        var fwDir = FrameworkDir(rootDir, framework, version);
        var libPrefix = $"runtimes/{rid.PackRid}/lib/";
        var nativePrefix = $"runtimes/{rid.PackRid}/native/";
        var hostfxr = rid.IsWindows ? "hostfxr.dll" : rid.IsMac ? "libhostfxr.dylib" : "libhostfxr.so";
        int count = 0; bool fxr = false;
        foreach (var f in pack.Files)
        {
            string? rel = null; bool native = false;
            if (f.StartsWith(libPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = f[libPrefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash < 0) continue;
                rel = rest[(slash + 1)..];   // Microsoft.AspNetCore.App.runtimeconfig.json stays: it is how the host learns that ASP.NET Core sits on NETCore
            }
            else if (f.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase)) { rel = f[nativePrefix.Length..]; native = true; }
            if (rel is null || rel.Length == 0) continue;
            count++;
            if (native && rel.Equals(hostfxr, StringComparison.OrdinalIgnoreCase))
            {
                pack.CopyTo(f, Path.Combine(rootDir, "host", "fxr", version, rel));
                fxr = true;
                continue;
            }
            pack.CopyTo(f, Path.Combine(fwDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        }
        if (count == 0) throw new ToolException($"{packId} {version} has nothing for {rid.PackRid} (runtimes/{rid.PackRid}/...).");
        if (framework == FrameworkRef.NetCore && !fxr) throw new ToolException($"{packId} {version} does not contain {hostfxr}.");
        if (!OperatingSystem.IsWindows())
        {
            var p = Path.Combine(fwDir, "createdump");
            if (File.Exists(p)) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}

/// <summary>Chooses the dotnet root a tool runs with: toolrun's private one by default, an installed one with --prefer-installed when it has the tool's framework line.</summary>
public sealed class RuntimeResolver
{
    private readonly ToolRunHome _home;
    private readonly PrivateRuntime _private;

    public RuntimeResolver(ToolRunHome home, Func<PackageStore> packs, Rid rid, Action<string> info, Action<string> debug)
    {
        _home = home;
        _private = new PrivateRuntime(home, packs, rid, info, debug);
    }

    public async Task<RuntimePlan> ResolveAsync(CommandMeta cmd, IReadOnlyList<string> systemRoots, bool preferInstalled, string? runtimeVersion, bool noDownload, bool prerelease, bool dryRun, CancellationToken ct)
    {
        var declared = cmd.Frameworks.Select(f => new FrameworkRef(f.Name, f.Version)).ToList();
        var needed = FrameworkRef.Expand(declared);

        if (preferInstalled && runtimeVersion is null)
        {
            // "matches": the tool's major.minor, any newer patch (what the host picks for LatestPatch); the host then applies the tool's own rollForward on top
            var sys = RuntimeSelector.FindInstalled(needed, "LatestPatch", SystemRuntimes.Scan(systemRoots));
            if (sys is not null)
            {
                var nc = sys.Frameworks.First(f => f.fw.Name == FrameworkRef.NetCore).version;
                return new RuntimePlan(sys.Root, true, nc, SystemRuntimes.Muxer(sys.Root), null,
                    sys.Frameworks.Select(f => $"{f.fw.Name} {f.version} (installed, {sys.Root})").ToList());
            }
        }

        var notes = new List<string>();
        var own = await _private.OwnVersionAsync(noDownload, prerelease, ct);
        var how = await _private.EnsureAsync(new FrameworkRef(FrameworkRef.NetCore, own), own, noDownload, dryRun, ct);
        notes.Add($"{FrameworkRef.NetCore} {own} (toolrun's own runtime, {how})");
        foreach (var fw in needed.Where(f => f.Name != FrameworkRef.NetCore))
        {
            var h = await _private.EnsureAsync(fw, own, noDownload, dryRun, ct);
            notes.Add($"{fw.Name} {own} ({h})");
        }
        if (runtimeVersion is not null)
        {
            foreach (var fw in needed)
            {
                var h = await _private.EnsureAsync(fw, runtimeVersion, noDownload, dryRun, ct);
                notes.Add($"{fw.Name} {runtimeVersion} (--runtime-version, {h})");
            }
        }

        // does the root satisfy the tool's own framework reference? If not, the host must be allowed to roll forward to a newer major
        var installed = dryRun ? needed.SelectMany(f => new[] { new RuntimeInstall(_private.Dir, f.Name, own) }).ToList() : _private.Installed();
        string? env = null;
        foreach (var fw in needed)
        {
            var versions = installed.Where(i => i.Framework == fw.Name).Select(i => i.Version).ToList();
            if (RollForward.Pick(fw.Version, cmd.RollForward, versions) is not null) continue;
            var want = SemVer.Parse(fw.Version);
            var newest = versions.Select(v => SemVer.Parse(v)).OrderBy(v => v).LastOrDefault();
            if (newest is null || newest.Major < want.Major || (newest.Major == want.Major && newest.Minor < want.Minor))
                throw new ToolException($"The tool needs {fw.Name} {fw.Version}, newer than toolrun's runtime ({newest?.Text ?? "none"}); update toolrun, or use --prefer-installed with a newer .NET.");
            env = "Major";
            notes.Add($"{fw.Name} {fw.Version} is not in the root (rollForward {cmd.RollForward ?? "Minor"}): DOTNET_ROLL_FORWARD=Major lets the host use {newest.Text}");
        }
        return new RuntimePlan(_private.Dir, false, own, null, env, notes);
    }

    public IEnumerable<(string Framework, string Version, string Dir)> CachedRuntimes()
    {
        var root = _home.DotnetRoot;
        var shared = Path.Combine(root, "shared");
        if (!Directory.Exists(shared)) yield break;
        foreach (var fw in Directory.EnumerateDirectories(shared).OrderBy(d => d, StringComparer.Ordinal))
            foreach (var v in Directory.EnumerateDirectories(fw).OrderBy(d => d, StringComparer.Ordinal))
                yield return (Path.GetFileName(fw), Path.GetFileName(v), v);
    }
}
