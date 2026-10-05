using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Tbus.Tests;

[Collection("serial")]
public class ChiselLocatorTests
{
    [Fact]
    public async Task Env_override_then_path_are_used_before_any_download()
    {
        using var t = new TestEnv();
        var log = new Log(t.Host());
        Assert.Equal(t.Chisel, await new ChiselLocator(t.Host(), log).LocateAsync(default));
        t.Env.Remove("TBUS_CHISEL"); // the fake lives on PATH as 'chisel'
        Assert.Equal(Path.Combine(t.Dir, "bin", OperatingSystem.IsWindows() ? "chisel.cmd" : "chisel"), await new ChiselLocator(t.Host(), log).LocateAsync(default));
        t.Env["TBUS_CHISEL"] = Path.Combine(t.Dir, "missing");
        await Assert.ThrowsAsync<UserError>(() => new ChiselLocator(t.Host(), log).LocateAsync(default));
    }

    [Fact]
    public void Pinned_checksums_cover_the_supported_platforms()
    {
        foreach (var p in new[] { "windows_amd64", "windows_arm64", "linux_amd64", "linux_arm64", "darwin_amd64", "darwin_arm64" })
            Assert.Matches("^[0-9a-f]{64}$", ChiselLocator.Sha256[p]);
        Assert.True(ChiselLocator.Sha256.ContainsKey(ChiselLocator.Platform()));
    }

    private static (HttpListener l, string url) Serve(byte[] payload)
    {
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); t.Start(); var port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        var l = new HttpListener(); var url = $"http://127.0.0.1:{port}"; l.Prefixes.Add(url + "/"); l.Start();
        _ = Task.Run(async () => { while (l.IsListening) { try { var c = await l.GetContextAsync(); c.Response.OutputStream.Write(payload); c.Response.Close(); } catch (Exception) { return; } } });
        return (l, url);
    }

    private static byte[] Gz(string content)
    {
        var ms = new MemoryStream();
        using (var g = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) g.Write(Encoding.UTF8.GetBytes(content));
        return ms.ToArray();
    }

    [Fact]
    public async Task Download_is_verified_extracted_and_cached_and_a_bad_hash_is_refused()
    {
        var gz = Gz("fake chisel binary");
        var plat = ChiselLocator.Platform(); var original = ChiselLocator.Sha256[plat];
        var (l, url) = Serve(gz);
        using var t = new TestEnv(); t.Env.Remove("TBUS_CHISEL"); t.Env["PATH"] = Path.Combine(t.Dir, "empty"); t.Env["TBUS_CHISEL_BASEURL"] = url;
        try
        {
            var loc = new ChiselLocator(t.Host(), new Log(t.Host()));
            ChiselLocator.Sha256[plat] = new string('0', 64);
            var ex = await Assert.ThrowsAsync<UserError>(() => loc.LocateAsync(default));
            Assert.Contains("SHA-256", ex.Message);
            Assert.False(Directory.Exists(Path.Combine(t.Dir, "home", "chisel")));

            ChiselLocator.Sha256[plat] = Convert.ToHexString(SHA256.HashData(gz)).ToLowerInvariant();
            var exe = await loc.LocateAsync(default);
            Assert.StartsWith(Path.Combine(t.Dir, "home", "chisel", ChiselLocator.Version), exe);
            Assert.Equal("fake chisel binary", File.ReadAllText(exe));
            l.Stop(); // cached: no second download
            Assert.Equal(exe, await loc.LocateAsync(default));
        }
        finally { ChiselLocator.Sha256[plat] = original; l.Close(); }
    }
}
