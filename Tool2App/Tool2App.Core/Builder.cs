using System.Text.Json.Nodes;
using Microsoft.NET.HostModel.AppHost;
using Microsoft.NET.HostModel.Bundle;

namespace Tool2App;

public sealed class BuildOptions
{
    public Rid Rid { get; set; } = Rid.Host;
    public required string OutputDir { get; set; }
    public bool SingleFile { get; set; }
    public bool FrameworkDependent { get; set; }
    /// <summary>Use exactly this runtime version (e.g. 8.0.12) instead of the latest patch of the tool's major.minor.</summary>
    public string? RuntimeVersion { get; set; }
    public string? Command { get; set; }
    public string? Tfm { get; set; }
    public bool IncludePrerelease { get; set; }
    /// <summary>Single file: extract everything (not just native libraries) at start-up (IncludeAllContentForSelfExtract).</summary>
    public bool ExtractAll { get; set; }
    /// <summary>Single file: leave native libraries beside the exe instead of bundling them for self-extraction.</summary>
    public bool NativeBeside { get; set; }
    public bool Compress { get; set; }
    /// <summary>Windows host and Windows target only: copy the version resources of the entry dll onto the exe.</summary>
    public bool PatchResources { get; set; }
    public bool Force { get; set; }
}

public sealed record BuildResult(string ExePath, string Command, string Kind, IReadOnlyList<string> Frameworks, long ExeBytes, long TotalBytes, string ToolId, string ToolVersion);

/// <summary>Turns a tool package into a standalone app.</summary>
public sealed class Tool2AppBuilder
{
    private readonly PackageStore _store;
    private readonly Action<string> _log;

    public Tool2AppBuilder(PackageStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log ?? (_ => { });
    }

    public async Task<BuildResult> BuildAsync(PackageContent package, BuildOptions o, CancellationToken ct = default)
    {
        var rid = o.Rid;
        var identity = NuSpecInfo.Read(package);
        PackageContent pkg = package;
        PackageContent? ridPkg = null;
        string work = Path.Combine(Path.GetTempPath(), "tool2app-" + Guid.NewGuid().ToString("N"));
        var output = Path.GetFullPath(o.OutputDir);
        bool createdOutput = false;
        try
        {
            var asset = ToolAsset.Select(ToolAsset.Discover(pkg), rid, o.Tfm);

            // Tool packages published per RID: the main package only points at "<id>.<rid>" packages
            if (asset.Settings.Commands.Count == 0 && asset.Settings.RidPackages.Count > 0)
            {
                var pick = asset.Settings.RidPackages.Select(r => (r, rank: rid.Rank(r.Rid))).Where(x => x.rank >= 0).OrderBy(x => x.rank).Select(x => x.r).FirstOrDefault()
                    ?? throw new ToolException($"The package has RID-specific packages ({string.Join(", ", asset.Settings.RidPackages.Select(r => r.Rid))}) but none for {rid}.");
                if (identity is null) throw new ToolException("Cannot find the version of the main package (no .nuspec), needed to fetch " + pick.Id + ".");
                _log($"RID-specific tool package {pick.Id} {identity.Version}");
                ridPkg = PackageContent.Open(await _store.GetAsync(pick.Id, identity.Version, ct));
                pkg = ridPkg;
                asset = ToolAsset.Select(ToolAsset.Discover(pkg), rid, o.Tfm);
            }

            var cmd = asset.PickCommand(o.Command);
            if (!cmd.Runner.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new ToolException($"Command '{cmd.Name}' uses runner '{cmd.Runner}'; only 'dotnet' tools are supported.");
            var exeName = rid.ExeName(cmd.Name);
            var entry = cmd.EntryPoint.Replace('\\', '/');
            if (entry.Contains('/') ) _log($"note: entry point {entry} is in a sub folder");
            _log($"tool {identity?.Id ?? package.Description} {identity?.Version}: command '{cmd.Name}', entry {entry}, {asset.Tfm.Text}/{asset.RidDir}");

            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                if (!o.Force) throw new ToolException($"Output folder '{output}' is not empty (use --force to replace it).");
                Directory.Delete(output, true);
            }
            if (!Directory.Exists(output)) { Directory.CreateDirectory(output); createdOutput = true; }

            var app = o.SingleFile ? Path.Combine(work, "app") : output;
            Directory.CreateDirectory(app);

            // 1. the tool's own files (foreign-RID native assets are left out of RID-specific builds)
            foreach (var f in pkg.Files)
            {
                if (!f.StartsWith(asset.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = f[asset.Prefix.Length..];
                if (rel.Equals("DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!o.FrameworkDependent && rel.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase))
                {
                    var seg = rel.Split('/')[1];
                    if (rid.Rank(seg) < 0) continue;
                }
                pkg.CopyTo(f, Path.Combine(app, rel.Replace('/', Path.DirectorySeparatorChar)));
            }
            var entryPath = Path.Combine(app, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryPath)) throw new ToolException($"The entry point {entry} is not in {asset.Prefix}.");
            var entryStem = entryPath[..^Path.GetExtension(entryPath).Length];
            var rcPath = entryStem + ".runtimeconfig.json";
            var depsPath = entryStem + ".deps.json";
            var existingExe = Path.Combine(app, exeName);
            if (File.Exists(existingExe)) File.Delete(existingExe);   // e.g. an apphost the package already carries; ours replaces it

            var cfg = File.Exists(rcPath) ? RuntimeConfig.Parse(File.ReadAllText(rcPath)) : RuntimeConfig.FromTfm(asset.Tfm);
            if (cfg.IsSelfContained) throw new ToolException("The tool is already self-contained (runtimeconfig has includedFrameworks); nothing to do.");
            var needed = cfg.Frameworks.Count > 0 ? cfg.Frameworks : RuntimeConfig.FromTfm(asset.Tfm).Frameworks;
            var all = FrameworkRef.Expand(needed);
            var kind = (o.FrameworkDependent ? "framework-dependent" : "self-contained") + (o.SingleFile ? " single-file" : " folder");

            // 2. runtime versions
            var resolved = new List<(FrameworkRef fw, string packId, string version)>();
            foreach (var fw in all)
            {
                var packId = fw.RuntimePackId(rid);
                if (o.FrameworkDependent && fw.Name != FrameworkRef.NetCore) { resolved.Add((fw, packId, fw.Version)); continue; }
                var versions = await _store.VersionsAsync(packId, ct);
                if (versions.Count == 0) throw new ToolException($"{packId} was not found on the feed; is {rid} a valid rid for {fw.Name}?");
                var v = o.RuntimeVersion is not null ? RuntimeVersions.Pin(o.RuntimeVersion, versions) : RuntimeVersions.Select(fw.Version, versions, cfg.RollForward, o.IncludePrerelease);
                resolved.Add((fw, packId, v));
                _log($"{fw.Name}: tool asks {fw.Version}{(cfg.RollForward is null ? "" : " rollForward=" + cfg.RollForward)} -> {v}");
            }
            var netcore = resolved.First(r => r.fw.Name == FrameworkRef.NetCore);
            var runtimeVer = SemVer.Parse(netcore.version);

            // 3. host (apphost, or singlefilehost for self-contained single-file) from the Host pack
            var hostPackId = "Microsoft.NETCore.App.Host." + rid.PackRid;
            var hostFile = o.SingleFile && !o.FrameworkDependent ? "singlefilehost" : "apphost";
            var hostEntry = $"runtimes/{rid.PackRid}/native/{rid.ExeName(hostFile)}";
            string hostVersion = netcore.version;
            using var hostPack = PackageContent.Open(await GetOrFallbackAsync(hostPackId, hostVersion, ct));
            if (!hostPack.Contains(hostEntry)) throw new ToolException($"{hostPackId} {hostVersion} does not contain {hostEntry}.");
            Directory.CreateDirectory(work);
            var hostSrc = Path.Combine(work, "host-" + rid.ExeName(hostFile));
            hostPack.CopyTo(hostEntry, hostSrc);

            // 4. runtime + config
            if (!o.FrameworkDependent)
            {
                var fwDeps = new List<JsonObject>();
                foreach (var (fw, packId, version) in resolved)
                {
                    using var pack = PackageContent.Open(await _store.GetAsync(packId, version, ct));
                    fwDeps.Add(RuntimePack.InstallInto(pack, packId, version, rid, asset.Tfm, app, includeNative: !o.SingleFile));
                }
                File.WriteAllText(rcPath, cfg.RewriteSelfContained(resolved.Select(r => new FrameworkRef(r.fw.Name, r.version))));

                JsonObject? toolDeps = File.Exists(depsPath) ? DepsJson.Parse(File.ReadAllText(depsPath)) : null;
                if (toolDeps is not null || o.SingleFile)
                {
                    IEnumerable<string>? managed = null;
                    if (toolDeps is null)
                    {
                        var inFramework = fwDeps.SelectMany(d => d.ToJsonString().Split('"')).ToHashSet(StringComparer.Ordinal);
                        managed = Directory.EnumerateFiles(app, "*.dll").Where(DepsJson.IsManaged).Select(p => Path.GetFileName(p)!).Where(n => !inFramework.Contains(n)).ToList();
                    }
                    var merged = DepsJson.MergeSelfContained(toolDeps, fwDeps, rid, asset.Tfm, app, includeRuntimeNative: !o.SingleFile, managed, Path.GetFileNameWithoutExtension(entry));
                    File.WriteAllText(depsPath, DepsJson.Serialize(merged));
                    _log(toolDeps is null ? "deps.json: generated (the tool shipped none)" : "deps.json: merged with the framework deps");
                }
                else _log("deps.json: none (host then uses every dll in the app folder)");
            }

            // 5. bind the host to the entry dll
            string? resourceSource = null;
            if (o.PatchResources)
            {
                if (OperatingSystem.IsWindows() && rid.IsWindows) resourceSource = entryPath;
                else _log("warning: --patch-resources only works when running on Windows for a Windows target; skipped");
            }
            bool macSign = rid.IsMac && OperatingSystem.IsMacOS();
            string boundHost = o.SingleFile ? Path.Combine(work, "bound-" + exeName) : Path.Combine(output, exeName);
            HostWriter.CreateAppHost(hostSrc, boundHost, Path.GetFileName(entryPath), windowsGraphicalUserInterface: false,
                assemblyToCopyResourcesFrom: resourceSource, enableMacOSCodeSign: macSign);

            string exePath = Path.Combine(output, exeName);
            if (o.SingleFile)
            {
                var specs = new List<FileSpec> { new(boundHost, exeName) };
                foreach (var f in Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories))
                    specs.Add(new FileSpec(f, Path.GetRelativePath(app, f).Replace('\\', '/')));
                var opts = BundleOptions.None;
                if (!o.NativeBeside) opts |= BundleOptions.BundleNativeBinaries;
                if (o.ExtractAll) opts |= BundleOptions.BundleAllContent;
                if (o.Compress && !o.FrameworkDependent) opts |= BundleOptions.EnableCompression;
                var bundler = new Bundler(exeName, output, opts, rid.OsPlatform, rid.Architecture, new Version(runtimeVer.Major, runtimeVer.Minor),
                    diagnosticOutput: false, appAssemblyName: Path.GetFileNameWithoutExtension(entryPath), macosCodesign: macSign);
                bundler.GenerateBundle(specs);
                var embedded = bundler.BundleManifest.Files.Select(e => e.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
                foreach (var s in specs)
                {
                    // the bundler keeps the entry's deps.json and runtimeconfig.json in dedicated header slots, not in Files
                    if (s.SourcePath == depsPath || s.SourcePath == rcPath) continue;
                    if (s.BundleRelativePath == exeName || embedded.Contains(s.BundleRelativePath)) continue;
                    // debug symbols and XML docs are of no use to a runnable app (the SDK also keeps them out of the exe)
                    if (s.BundleRelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || s.BundleRelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    // executables and libraries for other operating systems (a Windows shim in a Linux build) can never run here
                    if (ForeignNative.Is(s.SourcePath, rid)) { _log("dropped (native code for another OS): " + s.BundleRelativePath); continue; }
                    var dst = Path.Combine(output, s.BundleRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(s.SourcePath, dst, true);
                    _log("left beside the exe: " + s.BundleRelativePath);
                }
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(exePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                var createdump = Path.Combine(output, "createdump");   // zip extraction drops the executable bit
                if (File.Exists(createdump)) File.SetUnixFileMode(createdump, File.GetUnixFileMode(exePath));
            }

            long total = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            return new BuildResult(exePath, cmd.Name, kind, resolved.Select(r => $"{r.fw.Name} {r.version}").ToList(), new FileInfo(exePath).Length, total,
                identity?.Id ?? "", identity?.Version ?? "");
        }
        catch
        {
            if (createdOutput) { try { Directory.Delete(output, true); } catch (IOException) { } }
            throw;
        }
        finally
        {
            ridPkg?.Dispose();
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Host pack of exactly this version; if the feed lacks it (rare), the latest patch of the same major.minor.</summary>
    private async Task<string> GetOrFallbackAsync(string packId, string version, CancellationToken ct)
    {
        try { return await _store.GetAsync(packId, version, ct); }
        catch (ToolException)
        {
            var want = SemVer.Parse(version);
            var versions = await _store.VersionsAsync(packId, ct);
            var alt = RuntimeVersions.Select(version, versions, "Disable", want.IsPrerelease);
            _log($"{packId} {version} not found, using {alt}");
            return await _store.GetAsync(packId, alt, ct);
        }
    }
}

/// <summary>Inspecting finished executables.</summary>
public static class Executables
{
    /// <summary>True when the file is a single-file bundle (a host with an appended bundle).</summary>
    public static bool IsBundle(string path, out long headerOffset) => Bundler.IsBundle(path, out headerOffset);
}

/// <summary>Recognises native binaries built for an operating system other than the target, by their magic numbers.</summary>
public static class ForeignNative
{
    public static bool Is(string path, Rid target)
    {
        byte[] head = new byte[4];
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Read(head, 0, 4) < 4) return false;
        }
        catch (IOException) { return false; }
        bool elf = head[0] == 0x7f && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
        bool macho = (head[0] == 0xCF || head[0] == 0xCE) && head[1] == 0xFA && head[2] == 0xED && head[3] == 0xFE
            || head[0] == 0xCA && head[1] == 0xFE && head[2] == 0xBA && head[3] == 0xBE;
        bool pe = head[0] == (byte)'M' && head[1] == (byte)'Z';
        if (pe) return !target.IsWindows && !DepsJson.IsManaged(path);   // managed dlls are portable
        if (elf) return !(target.Os is "linux" or "linux-musl" or "freebsd");
        if (macho) return !target.IsMac;
        return false;
    }
}
