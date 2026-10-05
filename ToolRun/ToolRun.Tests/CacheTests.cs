using Tool2App;
using Tool2App.Tests;
using Xunit;

namespace ToolRun.Tests;

public class CacheTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    private static readonly string[] Args = { "tiny.tool", "--" };

    private static IReadOnlyList<string> AnyDotnet(Fixture f) => new[] { f.FakeDotnet("dn", ("Microsoft.NETCore.App", "8.0.5")) };

    [Fact]
    public async Task LatestStableIsInstalledAndLaidOutLikeDotnetToolInstall()
    {
        _f.AddTool("Tiny.Tool", "1.0.0"); _f.AddTool("Tiny.Tool", "1.1.0"); _f.AddTool("Tiny.Tool", "2.0.0-beta.1");
        var tool = await _f.App(Args, AnyDotnet(_f)).ResolveToolAsync();

        Assert.Equal("1.1.0", tool.Meta.Version);          // not the prerelease
        var dir = Path.Combine(_f.HomeDir, "tools", "tiny.tool", "1.1.0");
        Assert.Equal(dir, tool.Dir);
        Assert.True(File.Exists(Path.Combine(dir, "toolrun.json")));
        Assert.True(File.Exists(Path.Combine(dir, "Tiny.Tool.nuspec")));
        Assert.True(File.Exists(Path.Combine(dir, "tools", "net8.0", "any", "tiny.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "tools", "net8.0", "any", "DotnetToolSettings.xml")));
        Assert.Equal(Path.Combine(dir, "tools", "net8.0", "any", "tiny.dll"), tool.EntryPath(tool.Meta.Commands[0]));
        // natives of other platforms are not kept
        var natives = Directory.EnumerateFiles(Path.Combine(dir, "tools"), "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToList();
        Assert.Equal(_f.Rid.Os == "linux" ? 1 : 0, natives.Count(n => n == "libfoo.so"));
        Assert.Equal(_f.Rid.Os == "win" ? 1 : 0, natives.Count(n => n == "foo.dll"));
        Assert.Equal("tiny-cmd", tool.Meta.Commands[0].Name);
        Assert.Equal("Microsoft.NETCore.App", tool.Meta.Commands[0].Frameworks[0].Name);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_f.HomeDir, "packages"), "*", SearchOption.AllDirectories));   // downloads are not kept
    }

    [Fact]
    public async Task PrereleaseOnRequest()
    {
        _f.AddTool("Tiny.Tool", "1.1.0"); _f.AddTool("Tiny.Tool", "2.0.0-beta.1");
        var tool = await _f.App(new[] { "tiny.tool", "--prerelease", "--" }, AnyDotnet(_f)).ResolveToolAsync();
        Assert.Equal("2.0.0-beta.1", tool.Meta.Version);
    }

    [Fact]
    public async Task ExplicitVersionIsInstalledSideBySide()
    {
        _f.AddTool("Tiny.Tool", "1.0.0"); _f.AddTool("Tiny.Tool", "1.1.0");
        var a = await _f.App(new[] { "tiny.tool@1.0.0" }).ResolveToolAsync();
        var b = await _f.App(new[] { "tiny.tool@1.1.0" }).ResolveToolAsync();
        Assert.Equal("1.0.0", a.Meta.Version);
        Assert.Equal("1.1.0", b.Meta.Version);
        Assert.Equal(2, new ToolCache(_f.Home).Entries("Tiny.Tool").Count());
    }

    [Fact]
    public async Task CachedRunsDoNotTouchTheNetwork()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        await _f.App(Args).ResolveToolAsync();
        var before = _f.Server.Requests.Count;
        Assert.True(before > 0);

        // no version: latest cached; a feed factory that throws proves the network is never even set up
        var again = await _f.App(Args, feeds: _ => throw new InvalidOperationException("network used")).ResolveToolAsync();
        Assert.Equal("1.0.0", again.Meta.Version);
        // exact version, spelled differently, is a hit too
        var exact = await _f.App(new[] { "tiny.tool@1.0", "--" }, feeds: _ => throw new InvalidOperationException("network used")).ResolveToolAsync();
        Assert.Equal("1.0.0", exact.Meta.Version);
        Assert.Equal(before, _f.Server.Requests.Count);
    }

    [Fact]
    public async Task UpdateInstallsANewerVersionAndKeepsTheOld()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        await _f.App(Args).ResolveToolAsync();
        _f.AddTool("Tiny.Tool", "1.2.0");

        var same = await _f.App(Args).ResolveToolAsync();              // no --update: stays on the cache
        Assert.Equal("1.0.0", same.Meta.Version);
        var upd = await _f.App(new[] { "tiny.tool", "--update", "--" }).ResolveToolAsync();
        Assert.Equal("1.2.0", upd.Meta.Version);
        var latest = await _f.App(Args).ResolveToolAsync();            // and now that is what the cache's latest is
        Assert.Equal("1.2.0", latest.Meta.Version);
        Assert.Equal(2, new ToolCache(_f.Home).Entries("tiny.tool").Count());
    }

    [Fact]
    public async Task UpdateWithNothingNewDownloadsNothing()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        await _f.App(Args).ResolveToolAsync();
        var downloads = _f.Requests("/v2/package/");
        await _f.App(new[] { "tiny.tool", "--update", "--" }).ResolveToolAsync();
        Assert.Equal(downloads, _f.Requests("/v2/package/"));
        Assert.Single(new ToolCache(_f.Home).Entries());
    }

    [Fact]
    public async Task UpdateOfflineFallsBackToTheCacheWithAWarning()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        await _f.App(Args).ResolveToolAsync();
        _f.Server.Dispose();
        var o = await _f.App(new[] { "tiny.tool", "--update", "--source", "http://127.0.0.1:1/v2", "--" }).ResolveToolAsync();
        Assert.Equal("1.0.0", o.Meta.Version);
        Assert.Contains(_f.Info, m => m.Contains("could not check for updates"));
    }

    [Fact]
    public async Task UnknownPackageAndVersionAreClearErrors()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        var ex = await Assert.ThrowsAsync<ToolException>(() => _f.App(new[] { "nope.tool" }).ResolveToolAsync());
        Assert.Contains("nope.tool", ex.Message);
        var ex2 = await Assert.ThrowsAsync<ToolException>(() => _f.App(new[] { "tiny.tool@9.9.9" }).ResolveToolAsync());
        Assert.Contains("9.9.9", ex2.Message);
        Assert.Empty(new ToolCache(_f.Home).Entries());
    }

    [Fact]
    public async Task CleanRemovesOneToolOrEverything()
    {
        _f.AddTool("Tiny.Tool", "1.0.0"); _f.AddTool("Other.Tool", "3.0.0", cmd: "other");
        await _f.App(Args).ResolveToolAsync();
        await _f.App(new[] { "other.tool" }).ResolveToolAsync();
        var cache = new ToolCache(_f.Home);
        Assert.True(cache.Remove("tiny.tool") > 0);
        Assert.Equal(new[] { "Other.Tool" }, cache.Entries().Select(e => e.Meta.Id).ToArray());
        var w = new StringWriter();
        Commands.Clean(_f.Home, null, null, w);
        Assert.Empty(cache.Entries());
        Assert.Contains("freed", w.ToString());
    }

    [Fact]
    public async Task ListShowsToolsAndRuntimes()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        await _f.App(Args).ResolveToolAsync();
        var w = new StringWriter();
        Commands.List(_f.Home, w);
        Assert.Contains("Tiny.Tool 1.0.0", w.ToString());
        Assert.Contains("Microsoft.NETCore.App 8.0.0", w.ToString());
        Assert.Contains("total:", w.ToString());
    }

    [Fact]
    public async Task PerRidToolPackageIsFollowed()
    {
        var rid = _f.Rid.PackRid;
        const string p = "tools/net8.0/any/";
        _f.Server.Add("Rid.Tool", "1.0.0", TestFiles.Zip(new Dictionary<string, byte[]>
        {
            ["Rid.Tool.nuspec"] = TestFiles.Bytes("<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>Rid.Tool</id><version>1.0.0</version></metadata></package>"),
            [p + "DotnetToolSettings.xml"] = TestFiles.Bytes($"<DotNetCliTool Version=\"1\"><RuntimeIdentifierPackages><RuntimeIdentifierPackage RuntimeIdentifier=\"{rid}\" Id=\"Rid.Tool.{rid}\"/></RuntimeIdentifierPackages></DotNetCliTool>"),
        }));
        _f.Server.Add("Rid.Tool." + rid, "1.0.0", _f.ToolPackage("Rid.Tool." + rid, "1.0.0", cmd: "rid-cmd"));
        var tool = await _f.App(new[] { "rid.tool" }).ResolveToolAsync();
        Assert.Equal("rid-cmd", tool.Meta.Commands[0].Name);
        Assert.Equal("Rid.Tool", tool.Meta.Id);
    }
}
