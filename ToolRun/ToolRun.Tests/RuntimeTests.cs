using System.Text;
using Tool2App;
using Tool2App.Tests;
using Xunit;

namespace ToolRun.Tests;

public class RollForwardTests
{
    private static readonly string[] Installed = { "6.0.36", "8.0.1", "8.0.31", "8.1.0", "9.0.2", "10.0.0-preview.1" };

    [Theory]
    [InlineData("8.0.0", null, "8.0.31")]              // default Minor: same line, latest patch
    [InlineData("8.0.0", "LatestPatch", "8.0.31")]
    [InlineData("8.0.31", "LatestPatch", "8.0.31")]
    [InlineData("8.0.32", "LatestPatch", null)]        // patch too low... nothing newer
    [InlineData("8.0.1", "Disable", "8.0.1")]
    [InlineData("8.0.0", "Disable", null)]
    [InlineData("7.0.0", null, null)]                  // Minor never crosses a major
    [InlineData("7.0.0", "Major", "8.0.31")]           // nearest higher major
    [InlineData("7.0.0", "LatestMajor", "9.0.2")]      // highest, but not a prerelease
    [InlineData("8.0.0", "LatestMinor", "8.1.0")]
    [InlineData("8.0.0", "LatestMajor", "9.0.2")]
    [InlineData("8.2.0", null, null)]
    [InlineData("8.2.0", "Major", "9.0.2")]
    [InlineData("8.0.0", "Minor", "8.0.31")]
    [InlineData("10.0.0-preview.1", null, "10.0.0-preview.1")]
    public void Picks(string requested, string? policy, string? expected) =>
        Assert.Equal(expected, RollForward.Pick(requested, policy, Installed));

    [Fact]
    public void MinorPrefersTheNearestHigherMinorOverTheLatest() =>
        Assert.Equal("8.1.0", RollForward.Pick("8.0.0", "Minor", new[] { "8.1.0", "8.3.0" }.Where(v => v != "8.0.5")));

    [Fact]
    public void MinorTakesTheLowestHigherMinorWhenTheRequestedLineIsMissing() =>
        Assert.Equal("8.1.4", RollForward.Pick("8.0.0", "Minor", new[] { "8.3.0", "8.1.4", "8.1.2" }));
}

public class RuntimeSelectionTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    private static string[] Run(params string[] a) => a;
    private string Root => _f.Home.DotnetRoot;
    private string Fx(string fw, string v) => Path.Combine(Root, "shared", fw, v);

    [Fact]
    public async Task APrivateRootIsBuiltOnceInTheLayoutTheHostExpects()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "10.0.0");
        var plan = await _f.App(Run("tiny.tool", "--", "a", "b"), Array.Empty<string>()).PrepareAsync(false);

        Assert.False(plan.SystemRuntime);
        Assert.Equal(Root, plan.RuntimeRoot);
        Assert.Equal(Root, plan.Env["DOTNET_ROOT"]);
        var own = _f.OwnVersion;                                    // latest patch of the running line; this build embeds none, so it came from the feed
        Assert.True(File.Exists(Path.Combine(Fx("Microsoft.NETCore.App", own), "System.Private.CoreLib.dll")));
        Assert.True(File.Exists(Path.Combine(Fx("Microsoft.NETCore.App", own), "Microsoft.NETCore.App.deps.json")));
        Assert.True(File.Exists(Path.Combine(Fx("Microsoft.NETCore.App", own), _f.CoreclrName)));
        Assert.True(File.Exists(Path.Combine(Root, "host", "fxr", own, _f.HostfxrName)));
        Assert.False(File.Exists(Path.Combine(Fx("Microsoft.NETCore.App", own), _f.HostfxrName)));
        // an apphost named after the command next to the dll, args unchanged
        Assert.True(plan.UsesApphost);
        Assert.Equal(Path.Combine(plan.ToolDir, "tools", "net8.0", "any", _f.Rid.ExeName("tiny-cmd")), plan.FileName);
        Assert.True(File.Exists(plan.FileName));
        Assert.Equal(new[] { "a", "b" }, plan.Args);
        // the tool's framework (10.0) is in the root: nothing to override
        Assert.False(plan.Env.ContainsKey("DOTNET_ROLL_FORWARD"));
        Assert.Equal(1, _f.Requests("/v2/package/Microsoft.NETCore.App.Runtime." + _f.Rid.PackRid));

        // the second time nothing is fetched, not even version lists
        var before = _f.Server.Requests.Count;
        var plan2 = await _f.App(Run("tiny.tool"), Array.Empty<string>(), feeds: _ => throw new InvalidOperationException("network used")).PrepareAsync(false);
        Assert.Equal(Root, plan2.RuntimeRoot);
        Assert.Equal(before, _f.Server.Requests.Count);
    }

    [Theory]
    [InlineData("8.0.0", null, true)]           // net8, default Minor: not in a 10.x root, so the host must roll forward across majors
    [InlineData("9.0.0", "LatestPatch", true)]
    [InlineData("8.0.0", "Major", false)]       // the tool already allows it: the host does it on its own
    [InlineData("8.0.0", "LatestMajor", false)]
    public async Task RollForwardIsOverriddenOnlyWhenTheToolsFrameworkIsNotInTheRoot(string tool, string? policy, bool envExpected)
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: tool, rollForward: policy);
        var plan = await _f.App(Run("tiny.tool"), Array.Empty<string>()).PrepareAsync(false);
        // Major/LatestMajor tools: Pick finds the 10.x runtime itself; others need the env var
        Assert.Equal(envExpected, plan.Env.ContainsKey("DOTNET_ROLL_FORWARD"));
        if (envExpected) { Assert.Equal("Major", plan.Env["DOTNET_ROLL_FORWARD"]); Assert.Contains("DOTNET_ROLL_FORWARD=Major", string.Join("\n", plan.Notes)); }
    }

    [Fact]
    public async Task ASameLineToolNeedsNoOverride()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: _f.OwnLine + ".0");
        var plan = await _f.App(Run("tiny.tool"), Array.Empty<string>()).PrepareAsync(false);
        Assert.False(plan.Env.ContainsKey("DOTNET_ROLL_FORWARD"));
    }

    [Fact]
    public async Task AToolNewerThanToolrunsRuntimeIsAClearError()
    {
        _f.AddTool("Future.Tool", "1.0.0", cmd: "future", fwVersion: "99.0.0");
        var ex = await Assert.ThrowsAsync<ToolException>(() => _f.App(Run("future.tool"), Array.Empty<string>()).PrepareAsync(false));
        Assert.Contains("99.0.0", ex.Message);
    }

    [Fact]
    public async Task AspNetCoreIsAddedToTheSameRoot()
    {
        _f.AddTool("Web.Tool", "1.0.0", cmd: "web", fw: "Microsoft.AspNetCore.App");
        var plan = await _f.App(Run("web.tool"), Array.Empty<string>()).PrepareAsync(false);
        Assert.Equal(Root, plan.RuntimeRoot);
        Assert.True(File.Exists(Path.Combine(Fx("Microsoft.AspNetCore.App", _f.OwnVersion), "Microsoft.AspNetCore.dll")));
        Assert.True(File.Exists(Path.Combine(Fx("Microsoft.AspNetCore.App", _f.OwnVersion), "Microsoft.AspNetCore.App.runtimeconfig.json")));
        Assert.True(Directory.Exists(Fx("Microsoft.NETCore.App", _f.OwnVersion)));
        Assert.Equal("Major", plan.Env["DOTNET_ROLL_FORWARD"]);               // the tool asks for 8.0
        Assert.Equal(1, _f.Requests("/v2/package/Microsoft.AspNetCore.App.Runtime."));
        Assert.False(Directory.Exists(Path.Combine(_f.HomeDir, "roots")));     // no composed roots any more
    }

    [Fact]
    public async Task RuntimeVersionAddsThatRuntimeToTheRootAndTheHostThenPrefersItsLine()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "8.0.0");
        var plan = await _f.App(Run("tiny.tool", "--runtime-version", "8.0.1"), Array.Empty<string>()).PrepareAsync(false);
        Assert.True(Directory.Exists(Fx("Microsoft.NETCore.App", "8.0.1")));
        Assert.True(Directory.Exists(Fx("Microsoft.NETCore.App", _f.OwnVersion)));
        Assert.False(plan.Env.ContainsKey("DOTNET_ROLL_FORWARD"));              // 8.0 is in the root now: the host's normal Minor rule picks it
        var ex = await Assert.ThrowsAsync<ToolException>(() => _f.App(Run("tiny.tool", "--runtime-version", "8.0.99"), Array.Empty<string>()).PrepareAsync(false));
        Assert.Contains("8.0.99", ex.Message);
    }

    [Fact]
    public async Task NoDownloadRuntimeStopsAtWhatIsAlreadyThere()
    {
        _f.AddTool("Tiny.Tool", "1.0.0"); _f.AddTool("Web.Tool", "1.0.0", cmd: "web", fw: "Microsoft.AspNetCore.App");
        var ex = await Assert.ThrowsAsync<ToolException>(() => _f.App(Run("tiny.tool", "--no-download-runtime"), Array.Empty<string>()).PrepareAsync(false));
        Assert.Contains("--no-download-runtime", ex.Message);
        Assert.Equal(0, _f.Requests("Runtime."));
        await _f.App(Run("tiny.tool"), Array.Empty<string>()).PrepareAsync(false);        // root now exists
        await _f.App(Run("tiny.tool", "--no-download-runtime"), Array.Empty<string>()).PrepareAsync(false);
        var ex2 = await Assert.ThrowsAsync<ToolException>(() => _f.App(Run("web.tool", "--no-download-runtime"), Array.Empty<string>()).PrepareAsync(false));
        Assert.Contains("Microsoft.AspNetCore.App", ex2.Message);
    }

    private static byte[] EmbeddedRoot(Fixture f, string version)
    {
        return TestFiles.Zip(new Dictionary<string, byte[]>
        {
            [$"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll"] = TestFiles.Bytes("embedded corelib"),
            [$"shared/Microsoft.NETCore.App/{version}/Microsoft.NETCore.App.deps.json"] = TestFiles.Bytes("{}"),
            [$"host/fxr/{version}/{f.HostfxrName}"] = TestFiles.Bytes("embedded hostfxr"),
        });
    }

    [Fact]
    public async Task AnEmbeddedRuntimeNeedsNoNetworkAtAll()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "8.0.0");
        var zip = EmbeddedRoot(_f, "10.0.77");
        var apphost = _f.Server; // (host pack stand-in below)
        EmbeddedAssets.Override.Value = name => name == EmbeddedAssets.RuntimeZip ? new MemoryStream(zip)
            : name == EmbeddedAssets.AppHost ? new MemoryStream(Encoding.ASCII.GetBytes("APPHOST-STUB\0c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2\0").Concat(new byte[64]).ToArray()) : null;
        try
        {
            Assert.Equal("10.0.77", EmbeddedAssets.RuntimeVersion());
            var before = _f.Server.Requests.Count;
            var plan = await _f.App(Run("tiny.tool", "--no-download-runtime"), Array.Empty<string>()).PrepareAsync(false);
            Assert.Equal("embedded corelib", File.ReadAllText(Path.Combine(Fx("Microsoft.NETCore.App", "10.0.77"), "System.Private.CoreLib.dll")));
            Assert.True(File.Exists(Path.Combine(Root, "host", "fxr", "10.0.77", _f.HostfxrName)));
            Assert.True(File.Exists(plan.FileName));                                  // apphost made from the embedded template
            Assert.Equal("Major", plan.Env["DOTNET_ROLL_FORWARD"]);
            Assert.Equal(0, _f.Server.Requests.Skip(before).Count(r => r.Contains("Microsoft.") || r.Contains("FindPackagesById")  && r.Contains("Microsoft.")));
            Assert.Contains("unpacked", string.Join("\n", _f.Info));
        }
        finally { EmbeddedAssets.Override.Value = null; }
    }

    [Fact]
    public async Task PreferInstalledUsesAnInstalledRuntimeOfTheToolsLineAndOtherwiseThePrivateOne()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "8.0.0");
        var dn8 = _f.FakeDotnet("dn8", ("Microsoft.NETCore.App", "8.0.5"));
        var dn9 = _f.FakeDotnet("dn9", ("Microsoft.NETCore.App", "9.0.1"));
        var plan = await _f.App(Run("tiny.tool", "--prefer-installed"), new[] { dn9, dn8 }).PrepareAsync(false);
        Assert.True(plan.SystemRuntime);
        Assert.Equal(dn8, plan.RuntimeRoot);
        Assert.Equal(dn8, plan.Env["DOTNET_ROOT"]);
        Assert.False(plan.Env.ContainsKey("DOTNET_ROLL_FORWARD"));
        Assert.False(Directory.Exists(_f.Home.DotnetRoot));
        // only a 9.x installed: not the tool's line, so toolrun's own runtime is used (the default is the private one anyway)
        var plan2 = await _f.App(Run("tiny.tool", "--prefer-installed"), new[] { dn9 }).PrepareAsync(false);
        Assert.False(plan2.SystemRuntime);
        Assert.Equal(Root, plan2.RuntimeRoot);
        // and without the flag an installed runtime is never used
        var plan3 = await _f.App(Run("tiny.tool"), new[] { dn8 }).PrepareAsync(false);
        Assert.False(plan3.SystemRuntime);
    }

    [Fact]
    public async Task PreferInstalledNeedsEveryFrameworkInOneRoot()
    {
        _f.AddTool("Web.Tool", "1.0.0", cmd: "web", fw: "Microsoft.AspNetCore.App");
        var dn = _f.FakeDotnet("dn", ("Microsoft.NETCore.App", "8.0.5"));
        Assert.False((await _f.App(Run("web.tool", "--prefer-installed"), new[] { dn }).PrepareAsync(false)).SystemRuntime);
        var both = _f.FakeDotnet("both", ("Microsoft.NETCore.App", "8.0.5"), ("Microsoft.AspNetCore.App", "8.0.5"));
        Assert.True((await _f.App(Run("web.tool", "--prefer-installed"), new[] { dn, both }).PrepareAsync(false)).SystemRuntime);
    }

    [Fact]
    public async Task DryRunDownloadsAndCreatesNothing()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        var plan = await _f.App(Run("tiny.tool", "--", "x"), Array.Empty<string>()).PrepareAsync(true);
        Assert.False(Directory.Exists(_f.Home.DotnetRoot));
        Assert.False(File.Exists(plan.FileName));
        Assert.Equal(0, _f.Requests("/v2/package/Microsoft"));
        Assert.Contains("DOTNET_ROOT=", plan.Describe().Replace("set \"", ""));
    }

    [Fact]
    public async Task NoApphostRunsThroughTheInstalledDotnetMuxer()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "8.0.0");
        var muxer = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var dn = _f.FakeDotnet("dn", ("Microsoft.NETCore.App", "8.0.5"));
        File.WriteAllText(Path.Combine(dn, muxer), "x");
        var plan = await _f.App(Run("tiny.tool", "--no-apphost", "--", "p"), new[] { dn }).PrepareAsync(false);
        Assert.False(plan.UsesApphost);
        Assert.Equal(Path.Combine(dn, muxer), plan.FileName);
        Assert.Equal(new[] { Path.Combine(plan.ToolDir, "tools", "net8.0", "any", "tiny.dll"), "p" }, plan.Args);
        var ex = await Assert.ThrowsAsync<ToolException>(() => _f.App(Run("tiny.tool", "--no-apphost"), Array.Empty<string>()).PrepareAsync(false));
        Assert.Contains("--no-apphost", ex.Message);
    }

    private sealed class NoHostPackFeed : IPackageFeed
    {
        private readonly IPackageFeed _inner;
        public NoHostPackFeed(IPackageFeed inner) { _inner = inner; }
        public string Description => _inner.Description;
        public Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct = default) =>
            id.StartsWith("Microsoft.NETCore.App.Host.") ? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()) : _inner.GetVersionsAsync(id, ct);
        public Task DownloadAsync(string id, string version, string destinationFile, CancellationToken ct = default) => _inner.DownloadAsync(id, version, destinationFile, ct);
    }

    [Fact]
    public async Task ApphostFailureFallsBackToDotnetWhenAnInstalledMuxerIsInUse()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "8.0.0");
        var muxer = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var dn = _f.FakeDotnet("dn", ("Microsoft.NETCore.App", "8.0.5"));
        File.WriteAllText(Path.Combine(dn, muxer), "x");
        var plan = await _f.App(Run("tiny.tool", "--prefer-installed"), new[] { dn }, feeds: _ => new NoHostPackFeed(new V2Feed(_f.Server.V2Url))).PrepareAsync(false);
        Assert.False(plan.UsesApphost);
        Assert.Equal(Path.Combine(dn, muxer), plan.FileName);
        // the private root has no muxer: the same failure is an error
        await Assert.ThrowsAsync<ToolException>(() => _f.App(Run("tiny.tool"), Array.Empty<string>(), feeds: _ => new NoHostPackFeed(new V2Feed(_f.Server.V2Url))).PrepareAsync(false));
    }
}
