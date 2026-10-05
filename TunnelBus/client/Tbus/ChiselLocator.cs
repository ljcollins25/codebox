using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tbus;

/// <summary>Finds chisel: TBUS_CHISEL, then PATH, then the cached pinned release, downloading it (SHA-256 checked) on first use.</summary>
internal sealed class ChiselLocator(Host host, Log log)
{
    public const string Version = "1.10.1";

    /// <summary>SHA-256 of the release archives (chisel_1.10.1_checksums.txt from the v1.10.1 release).</summary>
    public static readonly Dictionary<string, string> Sha256 = new()
    {
        ["darwin_amd64"] = "5a7e6ba198e840492d34d7110e1c90118642faac542773121a4c4cc9b44c226a",
        ["darwin_arm64"] = "474bf6d3dd92c9162950d1a8d8e912709b1e48494ca6af5dc5d0381ab80e56bd",
        ["linux_386"] = "9737179a33736ecfd3bc6c25d48c0b64285bbc7d06f6494f8650e945cf538741",
        ["linux_amd64"] = "0525aa3c5d457f2a4075e66221d5125d434bedf15006d3271c213f5cd6ff2230",
        ["linux_arm64"] = "f55beb68fb99b69903df1adcff4197fbfdb82cb0ee596848c0f055dc219da983",
        ["linux_armv7"] = "f81c1497d22caa0bb6e461b36e9f46a27f9edeaae3e3994aae2f8a5c2c9b84ef",
        ["windows_386"] = "9af93373a3cfb8a43dd857050d3b265b70270b23184ce7ae47674c69ca44fd3c",
        ["windows_amd64"] = "42fd40bb0e6e8e0072a83f3a824de5045636c1cc8e3819daa3c9b7a985a2cc58",
        ["windows_arm64"] = "20f6f8967ac6e4c0b9113cd07fff7b1f7061887ab469e7246724be67410f5663",
    };

    public static string Platform()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", Architecture.X86 => "386", Architecture.Arm => "armv7",
            var a => throw new UserError($"No chisel build for architecture {a}."),
        };
        return os + "_" + arch;
    }

    public async Task<string> LocateAsync(CancellationToken ct)
    {
        var env = host.GetEnv("TBUS_CHISEL");
        if (!string.IsNullOrEmpty(env))
            return File.Exists(env) ? env : throw new UserError($"TBUS_CHISEL points to a missing file: {env}");
        var onPath = FindOnPath("chisel");
        if (onPath != null) return onPath;

        var plat = Platform();
        var exe = Path.Combine(host.Home, "chisel", Version, plat, OperatingSystem.IsWindows() ? "chisel.exe" : "chisel");
        if (File.Exists(exe)) return exe;
        if (!Sha256.TryGetValue(plat, out var expected)) throw new UserError($"No pinned chisel checksum for {plat}; install chisel and put it on PATH.");

        var baseUrl = host.GetEnv("TBUS_CHISEL_BASEURL")?.TrimEnd('/') ?? $"https://github.com/jpillora/chisel/releases/download/v{Version}";
        var url = $"{baseUrl}/chisel_{Version}_{plat}.gz";
        log.Info($"downloading chisel {Version} ({plat}) from GitHub (first use)");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var gz = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(gz)).ToLowerInvariant();
        if (actual != expected) throw new UserError($"chisel download failed its SHA-256 check (expected {expected}, got {actual}); not using it.");

        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        var tmp = exe + ".tmp";
        using (var gzs = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
        using (var fs = File.Create(tmp)) await gzs.CopyToAsync(fs, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(tmp, exe, overwrite: true);
        log.Info($"chisel {Version} verified and cached at {exe}");
        return exe;
    }

    public string? FindOnPath(string name)
    {
        var path = host.GetEnv("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try { var f = Path.Combine(dir.Trim('"'), name + ext); if (File.Exists(f)) return f; } catch (ArgumentException) { }
            }
        return null;
    }
}
