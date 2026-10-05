using System.Text.Json.Nodes;
using Tool2App;
using Xunit;

namespace Tool2App.Tests;

public class RuntimeConfigTests
{
    private const string Console8 = """
        {
          "runtimeOptions": {
            "tfm": "net8.0",
            "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
            "rollForward": "Major",
            "configProperties": { "System.GC.Server": true, "System.Runtime.TieredPGO": false }
          }
        }
        """;

    private const string Web = """
        {
          "runtimeOptions": {
            "tfm": "net8.0",
            "frameworks": [
              { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
              { "name": "Microsoft.AspNetCore.App", "version": "8.0.0" }
            ],
            "configProperties": { "System.Reflection.Metadata.MetadataUpdater.IsSupported": false }
          }
        }
        """;

    [Fact]
    public void Reads_single_framework_and_roll_forward()
    {
        var c = RuntimeConfig.Parse(Console8);
        Assert.Equal("net8.0", c.Tfm);
        var f = Assert.Single(c.Frameworks);
        Assert.Equal(new FrameworkRef("Microsoft.NETCore.App", "8.0.0"), f);
        Assert.Equal("Major", c.RollForward);
        Assert.False(c.IsSelfContained);
    }

    [Fact]
    public void Reads_framework_array()
    {
        var c = RuntimeConfig.Parse(Web);
        Assert.Equal(new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" }, c.Frameworks.Select(f => f.Name));
        Assert.Null(c.RollForward);
    }

    [Fact]
    public void Only_aspnet_listed_pulls_in_netcore_at_the_same_version()
    {
        var c = RuntimeConfig.Parse("""{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.AspNetCore.App","version":"6.0.0"}}}""");
        var all = FrameworkRef.Expand(c.Frameworks);
        Assert.Equal(new[] { "Microsoft.AspNetCore.App", "Microsoft.NETCore.App" }, all.Select(f => f.Name));
        Assert.Equal("6.0.0", all[1].Version);
    }

    [Fact]
    public void Windows_desktop_needs_a_windows_rid_and_has_its_own_pack()
    {
        var wd = new FrameworkRef(FrameworkRef.WindowsDesktop, "8.0.0");
        Assert.Equal("Microsoft.WindowsDesktop.App.Runtime.win-x64", wd.RuntimePackId(Rid.Parse("win-x64")));
        Assert.Throws<ToolException>(() => wd.RuntimePackId(Rid.Parse("linux-x64")));
        Assert.Equal("Microsoft.NETCore.App.Runtime.linux-musl-x64", new FrameworkRef(FrameworkRef.NetCore, "8.0.0").RuntimePackId(Rid.Parse("linux-musl-x64")));
        Assert.Throws<ToolException>(() => new FrameworkRef("Some.Other.App", "1.0.0").RuntimePackId(Rid.Parse("win-x64")));
    }

    [Fact]
    public void Missing_file_is_replaced_by_the_plain_runtime_of_the_tfm()
    {
        Tfm.TryParse("net9.0", out var tfm);
        var c = RuntimeConfig.FromTfm(tfm);
        Assert.Equal(new FrameworkRef("Microsoft.NETCore.App", "9.0.0"), Assert.Single(c.Frameworks));
    }

    [Fact]
    public void Rewrite_replaces_framework_with_included_frameworks_and_keeps_the_rest()
    {
        var c = RuntimeConfig.Parse(Console8);
        var json = c.RewriteSelfContained(new[] { new FrameworkRef("Microsoft.NETCore.App", "8.0.31") });
        var opts = JsonNode.Parse(json)!["runtimeOptions"]!.AsObject();
        Assert.False(opts.ContainsKey("framework"));
        Assert.False(opts.ContainsKey("rollForward"));
        var inc = opts["includedFrameworks"]!.AsArray();
        Assert.Equal("Microsoft.NETCore.App", (string?)inc[0]!["name"]);
        Assert.Equal("8.0.31", (string?)inc[0]!["version"]);
        Assert.Equal("net8.0", (string?)opts["tfm"]);
        Assert.True((bool)opts["configProperties"]!["System.GC.Server"]!);
        Assert.False((bool)opts["configProperties"]!["System.Runtime.TieredPGO"]!);
        // the result is itself read as "already self-contained"
        Assert.True(RuntimeConfig.Parse(json).IsSelfContained);
    }

    [Fact]
    public void Rewrite_lists_every_framework_for_web_tools()
    {
        var json = RuntimeConfig.Parse(Web).RewriteSelfContained(new[] { new FrameworkRef("Microsoft.NETCore.App", "8.0.31"), new FrameworkRef("Microsoft.AspNetCore.App", "8.0.31") });
        var opts = JsonNode.Parse(json)!["runtimeOptions"]!.AsObject();
        Assert.False(opts.ContainsKey("frameworks"));
        Assert.Equal(2, opts["includedFrameworks"]!.AsArray().Count);
    }

    [Fact]
    public void Invalid_json_is_a_tool_error() => Assert.Throws<ToolException>(() => RuntimeConfig.Parse("{ nope"));
}

public class RuntimeVersionTests
{
    private static readonly string[] Available = { "8.0.0", "8.0.2", "8.0.10", "8.0.9", "8.0.11-rc.1.1", "8.1.0-preview.1", "9.0.0", "9.0.3", "10.0.0-rc.2" };

    [Fact]
    public void Takes_the_latest_patch_of_the_same_major_minor_numerically()
        => Assert.Equal("8.0.10", RuntimeVersions.Select("8.0.0", Available));

    [Fact]
    public void Prereleases_are_ignored_unless_wanted()
    {
        Assert.Equal("8.0.10", RuntimeVersions.Select("8.0.0", Available));
        Assert.Equal("8.0.11-rc.1.1", RuntimeVersions.Select("8.0.0", Available, allowPrerelease: true));
        Assert.Equal("10.0.0-rc.2", RuntimeVersions.Select("10.0.0-rc.1", Available));
    }

    [Fact]
    public void Missing_line_rolls_forward_only_as_the_tool_allows()
    {
        Assert.Equal("8.0.10", RuntimeVersions.Select("7.0.0", Available, "Major"));        // next higher line, latest patch
        Assert.Equal("9.0.3", RuntimeVersions.Select("7.0.0", new[] { "8.0.10", "9.0.3" }, "LatestMajor"));
        Assert.Throws<ToolException>(() => RuntimeVersions.Select("7.0.0", Available, "Disable"));
        Assert.Throws<ToolException>(() => RuntimeVersions.Select("7.0.0", Available, "Minor"));
        Assert.Equal("8.1.0-preview.1", RuntimeVersions.Select("8.0.0", new[] { "8.1.0-preview.1" }, "Minor", allowPrerelease: true));
    }

    [Fact]
    public void Minor_roll_forward_stays_in_the_major()
        => Assert.Equal("6.2.4", RuntimeVersions.Select("6.0.0", new[] { "6.2.1", "6.2.4", "6.3.0", "7.0.0" }, "Minor"));

    [Fact]
    public void Explicit_version_must_exist()
    {
        Assert.Equal("8.0.2", RuntimeVersions.Pin("8.0.2", Available));
        Assert.Throws<ToolException>(() => RuntimeVersions.Pin("8.0.3", Available));
    }
}

