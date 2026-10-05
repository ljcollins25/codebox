using Tool2App;
using Xunit;

namespace Tool2App.Tests;

public class FeedTests
{
    private static byte[] Pkg(string marker) => TestFiles.Zip(new Dictionary<string, byte[]> { ["marker.txt"] = TestFiles.Bytes(marker) });

    private static FakeNuGetServer Server()
    {
        var s = new FakeNuGetServer();
        foreach (var v in new[] { "1.0.0", "1.1.0", "2.0.0-beta.1", "1.10.0", "1.2.0" }) s.Add("My.Tool", v, Pkg("my.tool " + v));
        return s;
    }

    [Fact]
    public async Task V2_lists_versions_across_pages()
    {
        using var s = Server();
        var feed = new V2Feed(s.V2Url);
        var versions = await feed.GetVersionsAsync("My.Tool");
        Assert.Equal(5, versions.Count);                        // 5 versions at 2 per page: three requests
        Assert.Equal(3, s.Count("FindPackagesById"));
        Assert.Equal("1.10.0", SemVer.Max(versions, false)!.Text);
        Assert.Equal("2.0.0-beta.1", SemVer.Max(versions, true)!.Text);
    }

    [Fact]
    public async Task V2_unknown_package_has_no_versions()
        => Assert.Empty(await new V2Feed(Server().V2Url).GetVersionsAsync("Nope"));

    [Fact]
    public async Task V2_downloads_the_nupkg()
    {
        using var s = Server();
        var dest = Path.Combine(TestFiles.NewTempDir(), "a", "p.nupkg");
        await new V2Feed(s.V2Url).DownloadAsync("My.Tool", "1.1.0", dest);
        using var pkg = PackageContent.Open(dest);
        Assert.Equal("my.tool 1.1.0", pkg.ReadText("marker.txt"));
        Assert.Contains(s.Requests, r => r == "/v2/package/My.Tool/1.1.0");
    }

    [Fact]
    public async Task V2_download_of_missing_version_is_a_404()
    {
        using var s = Server();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new V2Feed(s.V2Url).DownloadAsync("My.Tool", "9.9.9", Path.Combine(TestFiles.NewTempDir(), "x")));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task V3_finds_the_flat_container_through_the_index()
    {
        using var s = Server();
        var feed = new V3Feed(s.V3Url);
        Assert.Equal(5, (await feed.GetVersionsAsync("My.Tool")).Count);
        var dest = Path.Combine(TestFiles.NewTempDir(), "p.nupkg");
        await feed.DownloadAsync("My.Tool", "1.2.0", dest);
        using var pkg = PackageContent.Open(dest);
        Assert.Equal("my.tool 1.2.0", pkg.ReadText("marker.txt"));
        Assert.Empty(await feed.GetVersionsAsync("missing"));
    }

    [Fact]
    public async Task Fallback_uses_v2_when_v3_is_refused()
    {
        using var s = Server();
        s.BlockV3 = true;
        var log = new List<string>();
        var feed = new FallbackFeed(new V3Feed(s.V3Url), new V2Feed(s.V2Url)) { Log = log.Add };
        Assert.Equal(5, (await feed.GetVersionsAsync("My.Tool")).Count);
        Assert.Contains(log, l => l.Contains("failed"));
        var dest = Path.Combine(TestFiles.NewTempDir(), "p.nupkg");
        await feed.DownloadAsync("My.Tool", "1.0.0", dest);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task Fallback_reports_all_failures()
    {
        using var s = Server();
        s.BlockV3 = true;
        var ex = await Assert.ThrowsAsync<ToolException>(() => new FallbackFeed(new V3Feed(s.V3Url)).GetVersionsAsync("My.Tool"));
        Assert.Contains("No feed could serve", ex.Message);
    }

    [Fact]
    public void Feed_kind_is_detected_from_the_url_or_forced()
    {
        Assert.IsType<V3Feed>(Feeds.Create("https://x/v3/index.json"));
        Assert.IsType<V2Feed>(Feeds.Create("https://x/api/v2"));
        Assert.IsType<V2Feed>(Feeds.Create("https://x/index.json", "v2"));
        Assert.IsType<V3Feed>(Feeds.Create("https://x/feed", "v3"));
        Assert.IsType<FallbackFeed>(Feeds.Create(null));
        Assert.IsType<V2Feed>(Feeds.Create(null, "v2"));
        Assert.Throws<ToolException>(() => Feeds.Create("https://x", "v9"));
    }

    [Fact]
    public async Task Store_downloads_once_and_caches_by_id_and_version()
    {
        using var s = Server();
        var cache = TestFiles.NewTempDir();
        var store = new PackageStore(new V2Feed(s.V2Url), cache);
        var p1 = await store.GetAsync("My.Tool", "1.0.0");
        var p2 = await store.GetAsync("My.Tool", "1.0.0");
        Assert.Equal(p1, p2);
        Assert.StartsWith(cache, p1);
        Assert.Equal(1, s.Count("/v2/package/"));
        Assert.Equal("1.10.0", await store.LatestAsync("My.Tool", false));
        Assert.Equal("2.0.0-beta.1", await store.LatestAsync("My.Tool", true));
        var ex = await Assert.ThrowsAsync<ToolException>(() => store.GetAsync("My.Tool", "7.7.7"));
        Assert.Contains("not found", ex.Message);
        await Assert.ThrowsAsync<ToolException>(() => store.LatestAsync("Nope", false));
    }

    [Fact]
    public void Default_cache_lives_under_local_app_data_unless_overridden()
    {
        var old = Environment.GetEnvironmentVariable("TOOL2APP_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("TOOL2APP_CACHE", null);
            var d = PackageStore.DefaultCacheDir();
            Assert.Contains("tool2app", d);
            Assert.EndsWith("packages", d);
            Environment.SetEnvironmentVariable("TOOL2APP_CACHE", "/somewhere");
            Assert.Equal("/somewhere", PackageStore.DefaultCacheDir());
        }
        finally { Environment.SetEnvironmentVariable("TOOL2APP_CACHE", old); }
    }
}

