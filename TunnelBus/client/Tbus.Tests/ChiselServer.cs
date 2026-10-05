using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tbus.Tests;

/// <summary>
/// A real chisel 1.10.1 server (the release binary, downloaded once into a cache folder and SHA-256 checked) for protocol tests. Test-only and Linux/macOS-only: never shipped, never run on Windows.
/// <see cref="Locate"/> returns null when it cannot be had (offline), and the tests then skip.
/// TBUS_TEST_CHISEL points at an existing binary instead; TBUS_TEST_CACHE moves the cache folder.
/// </summary>
internal static class ChiselBinary
{
    public const string Version = "1.10.1";

    // chisel_1.10.1_checksums.txt from the v1.10.1 release (checksums of the archives)
    private static readonly Dictionary<string, string> Sha256 = new()
    {
        ["linux_amd64"] = "0525aa3c5d457f2a4075e66221d5125d434bedf15006d3271c213f5cd6ff2230",
        ["linux_arm64"] = "f55beb68fb99b69903df1adcff4197fbfdb82cb0ee596848c0f055dc219da983",
        ["darwin_amd64"] = "5a7e6ba198e840492d34d7110e1c90118642faac542773121a4c4cc9b44c226a",
        ["darwin_arm64"] = "474bf6d3dd92c9162950d1a8d8e912709b1e48494ca6af5dc5d0381ab80e56bd",
    };

    private static readonly Lazy<(string? Path, string Why)> Found = new(Find);
    public static string? Locate() => Found.Value.Path;
    public static string WhyNot => Found.Value.Why;

    private static (string?, string) Find()
    {
        // Defender flags the chisel executable as a hacktool on Windows: never download or run it there (tests skip).
        if (OperatingSystem.IsWindows()) return (null, "the chisel server is only run on Linux/macOS (Windows Defender blocks the chisel executable)");
        var env = Environment.GetEnvironmentVariable("TBUS_TEST_CHISEL");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? (env, "") : (null, "TBUS_TEST_CHISEL does not exist: " + env);
        try
        {
            var os = OperatingSystem.IsMacOS() ? "darwin" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            var plat = os + "_" + arch;
            if (!Sha256.TryGetValue(plat, out var expected)) return (null, "no pinned chisel checksum for " + plat);
            var cache = Environment.GetEnvironmentVariable("TBUS_TEST_CACHE")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } l ? l : Path.GetTempPath(), "tbus-test-cache");
            var exe = Path.Combine(cache, $"chisel-{Version}-{plat}" );
            if (File.Exists(exe)) return (exe, "");
            Directory.CreateDirectory(cache);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var gz = http.GetByteArrayAsync($"https://github.com/jpillora/chisel/releases/download/v{Version}/chisel_{Version}_{plat}.gz").GetAwaiter().GetResult();
            var actual = Convert.ToHexString(SHA256.HashData(gz)).ToLowerInvariant();
            if (actual != expected) return (null, $"chisel download failed its SHA-256 check ({actual})");
            var tmp = exe + "." + Environment.ProcessId + ".tmp";
            using (var g = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var fs = File.Create(tmp)) g.CopyTo(fs);
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try { File.Move(tmp, exe, overwrite: true); } catch (IOException) { if (!File.Exists(exe)) throw; }
            return (exe, "");
        }
        catch (Exception e) { return (null, "could not get chisel " + Version + ": " + e.Message); }
    }
}

/// <summary>One running chisel server process (--reverse, authfile, short keepalive), restartable on the same port.</summary>
internal sealed class ChiselServer : IDisposable
{
    private readonly string _exe, _dir;
    private Process? _proc;
    private readonly StringBuilder _log = new();
    public int Port { get; }
    public string AuthFile { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public string Log { get { lock (_log) return _log.ToString(); } }
    public int? KeepAliveSeconds { get; init; }
    public string? ExtraArgs { get; init; }

    /// <summary>Users: name -> (password, allowed regexes). Chisel's authfile format is {"user:pass": ["regex", ...]}.</summary>
    public ChiselServer(string exe, Dictionary<string, (string Password, string[] Allow)> users, int? keepAlive = null)
    {
        _exe = exe; KeepAliveSeconds = keepAlive;
        _dir = Path.Combine(Path.GetTempPath(), "tbus-chisel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        AuthFile = Path.Combine(_dir, "users.json");
        WriteUsers(users);
        Port = FreePort();
    }

    public void WriteUsers(Dictionary<string, (string Password, string[] Allow)> users) =>
        File.WriteAllText(AuthFile, JsonSerializer.Serialize(users.ToDictionary(u => u.Key + ":" + u.Value.Password, u => u.Value.Allow)));

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public async Task StartAsync()
    {
        var psi = new ProcessStartInfo(_exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "server", "--host", "127.0.0.1", "--port", Port.ToString(), "--reverse", "--authfile", AuthFile })
            psi.ArgumentList.Add(a);
        if (KeepAliveSeconds is { } k) { psi.ArgumentList.Add("--keepalive"); psi.ArgumentList.Add(k + "s"); }
        _proc = new Process { StartInfo = psi };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.AppendLine(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.AppendLine(e.Data); };
        _proc.Start(); _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (_proc.HasExited) throw new InvalidOperationException("chisel server exited: " + Log);
            try { if ((await http.GetStringAsync(Url + "/health")).StartsWith("OK")) return; } catch (Exception) { await Task.Delay(50); }
        }
        throw new TimeoutException("chisel server did not start: " + Log);
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(5000); } } catch (Exception) { }
        _proc = null;
    }

    public async Task RestartAsync() { Stop(); await Task.Delay(200); await StartAsync(); }

    public void Dispose() { Stop(); try { Directory.Delete(_dir, true); } catch (Exception) { } }
}
