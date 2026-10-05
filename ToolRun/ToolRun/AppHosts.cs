using Microsoft.NET.HostModel.AppHost;
using Tool2App;

namespace ToolRun;

/// <summary>An apphost for the tool's entry dll, so the process is called after the tool and the real host (hostfxr/hostpolicy) runs it. Created once, next to the dll.</summary>
public static class AppHosts
{
    public static async Task<string> EnsureAsync(string entryPath, string commandName, Rid rid, string hostVersionHint, string? systemRoot,
        ToolRunHome home, Func<PackageStore> packs, Action<string> info, bool dryRun, CancellationToken ct)
    {
        var exe = Path.Combine(Path.GetDirectoryName(entryPath)!, rid.ExeName(commandName));
        if (File.Exists(exe) || dryRun) return exe;

        var template = await TemplateAsync(rid, hostVersionHint, systemRoot, home, packs, info, ct).ConfigureAwait(false);
        var tmp = exe + "." + Guid.NewGuid().ToString("N") + ".tmp";
        HostWriter.CreateAppHost(template, tmp, Path.GetFileName(entryPath), windowsGraphicalUserInterface: false,
            enableMacOSCodeSign: rid.IsMac && OperatingSystem.IsMacOS());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(tmp, exe, true);
        return exe;
    }

    /// <summary>The generic apphost: cached in the home folder; else the one inside toolrun; else an installed SDK's host pack; else the host pack from NuGet.</summary>
    private static async Task<string> TemplateAsync(Rid rid, string hostVersionHint, string? systemRoot, ToolRunHome home, Func<PackageStore> packs, Action<string> info, CancellationToken ct)
    {
        var cached = Path.Combine(home.AppHostTemplate, rid.PackRid + "-" + rid.ExeName("apphost"));
        if (File.Exists(cached)) return cached;
        Directory.CreateDirectory(home.AppHostTemplate);
        var part = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";

        using (var emb = EmbeddedAssets.Open(EmbeddedAssets.AppHost))
        {
            if (emb is not null)
            {
                using (var f = File.Create(part)) emb.CopyTo(f);
                File.Move(part, cached, true);
                return cached;
            }
        }
        var sdk = FindInSdk(systemRoot, rid);
        if (sdk is not null) { File.Copy(sdk, part, true); File.Move(part, cached, true); return cached; }

        var packId = "Microsoft.NETCore.App.Host." + rid.PackRid;
        var store = packs();
        var versions = await store.VersionsAsync(packId, ct).ConfigureAwait(false);
        if (versions.Count == 0) throw new ToolException($"{packId} was not found on {store.Description}.");
        var v = versions.Contains(hostVersionHint, StringComparer.OrdinalIgnoreCase) ? hostVersionHint
            : RuntimeVersions.Select(hostVersionHint, versions, "LatestMajor", SemVer.Parse(hostVersionHint).IsPrerelease);
        info($"downloading {packId} {v} ...");
        var nupkg = await store.GetAsync(packId, v, ct).ConfigureAwait(false);
        var entry = $"runtimes/{rid.PackRid}/native/{rid.ExeName("apphost")}";
        using (var pack = PackageContent.Open(nupkg))
        {
            if (!pack.Contains(entry)) throw new ToolException($"{packId} {v} does not contain {entry}.");
            pack.CopyTo(entry, part);
        }
        ToolRunHome.DeleteDownload(nupkg);
        File.Move(part, cached, true);
        return cached;
    }

    private static string? FindInSdk(string? root, Rid rid)
    {
        if (root is null) return null;
        var dir = Path.Combine(root, "packs", "Microsoft.NETCore.App.Host." + rid.PackRid);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir).OrderByDescending(d => SemVer.TryParse(Path.GetFileName(d), out var v) ? v : null)
            .Select(d => Path.Combine(d, "runtimes", rid.PackRid, "native", rid.ExeName("apphost"))).FirstOrDefault(File.Exists);
    }
}
