using Tool2App;
using Tool2App.Tests;
using Xunit;

namespace ToolRun.Tests;

public class SourcesTests : IDisposable
{
    private readonly string _dir = TestFiles.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Cfg(string body) => "<configuration>" + body + "</configuration>";

    [Fact]
    public void NuGetConfigNearestWinsClearAndDisabledAreHonoured()
    {
        var far = Cfg("<packageSources><add key=\"a\" value=\"https://far/a/index.json\"/><add key=\"b\" value=\"https://far/b/v2\"/></packageSources>");
        var near = Cfg("<packageSources><add key=\"b\" value=\"https://near/b/index.json\"/><add key=\"c\" value=\"https://near/c/index.json\"/></packageSources><disabledPackageSources><add key=\"a\" value=\"true\"/></disabledPackageSources>");
        Assert.Equal(new[] { "https://near/b/index.json", "https://near/c/index.json" }, NuGetConfig.Merge(new[] { far, near }));

        var cleared = Cfg("<packageSources><clear/><add key=\"only\" value=\"https://only/index.json\"/></packageSources>");
        Assert.Equal(new[] { "https://only/index.json" }, NuGetConfig.Merge(new[] { far, cleared }));
    }

    [Fact]
    public void LocalFolderSourcesAreSkippedWithAWarning()
    {
        var warnings = new List<string>();
        var r = NuGetConfig.Merge(new[] { Cfg("<packageSources><add key=\"local\" value=\"/srv/feed\"/><add key=\"w\" value=\"https://w/index.json\"/></packageSources>") }, warnings.Add);
        Assert.Equal(new[] { "https://w/index.json" }, r);
        Assert.Single(warnings);
    }

    [Fact]
    public void NuGetConfigIsFoundInParentFolders()
    {
        var sub = Path.Combine(_dir, "a", "b");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_dir, "NuGet.Config"), Cfg("<packageSources><add key=\"x\" value=\"https://x/index.json\"/></packageSources>"));
        Assert.Contains("https://x/index.json", NuGetConfig.Sources(sub));
    }

    private SourceSet Resolve(string[] args, Dictionary<string, string?>? env = null, string? cwd = null)
        => SourceResolver.Resolve(CliParser.Parse(args), new ToolRunHome(Path.Combine(_dir, "home")), cwd ?? _dir, k => env is not null && env.TryGetValue(k, out var v) ? v : null);

    [Fact]
    public void PrecedenceIsFlagThenEnvThenConfigFileThenNuGetConfigThenDefault()
    {
        Assert.Empty(Resolve(new[] { "p" }).Tool);                                    // default: nuget.org (empty means "default feed")
        File.WriteAllText(Path.Combine(_dir, "nuget.config"), Cfg("<packageSources><add key=\"n\" value=\"https://nuget-config/index.json\"/></packageSources>"));
        Assert.Equal(new[] { "https://nuget-config/index.json" }, Resolve(new[] { "p" }).Tool);
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
        File.WriteAllText(Path.Combine(_dir, "home", "config.json"), "{ \"sources\": [\"https://cfg/index.json\"], \"runtimeSources\": [\"https://rt/v2\"] }");
        var c = Resolve(new[] { "p" });
        Assert.Equal(new[] { "https://cfg/index.json" }, c.Tool);
        Assert.Equal(new[] { "https://rt/v2" }, c.Runtime);
        Assert.Equal(new[] { "https://env/index.json" }, Resolve(new[] { "p" }, new() { ["TOOLRUN_SOURCE"] = "https://env/index.json" }).Tool);
        var flag = Resolve(new[] { "p", "--source", "https://flag/v2", "--source", "https://flag2/v2" }, new() { ["TOOLRUN_SOURCE"] = "https://env/index.json" });
        Assert.Equal(new[] { "https://flag/v2", "https://flag2/v2" }, flag.Tool);
        Assert.Equal(new[] { "https://rt/v2" }, flag.Runtime);
        var same = Resolve(new[] { "p", "--source", "https://flag/v2", "--runtime-source", "https://r2/v2" });
        Assert.Equal(new[] { "https://r2/v2" }, same.Runtime);
    }

    [Fact]
    public void LocalPathAsExplicitSourceIsRejected() =>
        Assert.Throws<ToolException>(() => FeedFactory.Create(new[] { "/some/folder" }));
}

public class FeedFallbackTests : IDisposable
{
    private readonly FakeNuGetServer _server = new();
    public void Dispose() => _server.Dispose();

    private static byte[] Pkg() => TestFiles.Zip(new Dictionary<string, byte[]> { ["a.txt"] = TestFiles.Bytes("x") });

    [Fact]
    public async Task DefaultStyleFallbackGoesFromBlockedV3ToV2()
    {
        _server.Add("Some.Tool", "1.0.0", Pkg());
        _server.BlockV3 = true;   // some networks refuse the v3 API
        var feed = new FallbackFeed(new V3Feed(_server.V3Url), new V2Feed(_server.V2Url));
        var store = new PackageStore(feed, Path.Combine(TestFiles.NewTempDir(), "c"));
        Assert.Equal("1.0.0", await store.LatestAsync("Some.Tool", false));
        Assert.True(File.Exists(await store.GetAsync("Some.Tool", "1.0.0")));
        Assert.True(_server.Count("/v3/") > 0);
        Assert.True(_server.Count("/v2/") > 0);
    }

    [Fact]
    public async Task MultiFeedTriesNextSourceWhenOneIsDown()
    {
        _server.Add("Some.Tool", "1.0.0", Pkg());
        var dead = new V2Feed("http://127.0.0.1:1/v2");
        var log = new List<string>();
        var multi = new MultiFeed(new IPackageFeed[] { dead, new V2Feed(_server.V2Url) }) { Log = log.Add };
        Assert.Equal(new[] { "1.0.0" }, await multi.GetVersionsAsync("Some.Tool"));
        var dest = Path.Combine(TestFiles.NewTempDir(), "x.nupkg");
        await multi.DownloadAsync("Some.Tool", "1.0.0", dest);
        Assert.True(File.Exists(dest));
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task MultiFeedUnionsVersionsAndDownloadsFromTheFeedThatHasThem()
    {
        using var other = new FakeNuGetServer();
        _server.Add("Some.Tool", "1.0.0", Pkg());
        other.Add("Some.Tool", "2.0.0", Pkg());
        var multi = new MultiFeed(new IPackageFeed[] { new V2Feed(_server.V2Url), new V2Feed(other.V2Url) });
        var versions = await multi.GetVersionsAsync("Some.Tool");
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, versions.OrderBy(v => v).ToArray());
        await multi.DownloadAsync("Some.Tool", "2.0.0", Path.Combine(TestFiles.NewTempDir(), "x.nupkg"));
        Assert.Equal(0, _server.Count("/v2/package/"));
        Assert.Equal(1, other.Count("/v2/package/"));
    }

    [Fact]
    public async Task MultiFeedWithEverySourceDownIsAClearError()
    {
        var multi = new MultiFeed(new IPackageFeed[] { new V2Feed("http://127.0.0.1:1/v2") });
        await Assert.ThrowsAsync<ToolException>(() => multi.GetVersionsAsync("x"));
    }

    [Fact]
    public async Task AnExplicitV3SourceThatServesWorks()
    {
        _server.Add("Some.Tool", "3.0.0", Pkg());
        var feed = FeedFactory.Create(new[] { _server.V3Url });
        Assert.Equal(new[] { "3.0.0" }, await feed.GetVersionsAsync("Some.Tool"));
    }
}
