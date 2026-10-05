using Xunit;

namespace ToolRun.Tests;

public class CliParserTests
{
    private static CliOptions P(params string[] a) => CliParser.Parse(a);

    [Fact]
    public void PackageThenToolArgsAfterDoubleDash()
    {
        var o = P("dotnet-outdated-tool", "--", "--help");
        Assert.Equal(Mode.Run, o.Mode);
        Assert.Equal("dotnet-outdated-tool", o.Package);
        Assert.Null(o.PackageVersion);
        Assert.Equal(new[] { "--help" }, o.ToolArgs);
    }

    [Fact]
    public void VersionAndPositionalToolArgsWithoutDoubleDash()
    {
        var o = P("dotnet-counters@8.0.547301", "ps");
        Assert.Equal("dotnet-counters", o.Package);
        Assert.Equal("8.0.547301", o.PackageVersion);
        Assert.Equal(new[] { "ps" }, o.ToolArgs);
    }

    [Fact]
    public void ToolrunOptionsBeforeAndAfterThePackage()
    {
        var o = P("--update", "pkg", "--source", "http://a/v3/index.json", "--runtime-version=8.0.5", "--no-download-runtime", "--", "--update", "x");
        Assert.True(o.Update);
        Assert.True(o.NoDownloadRuntime);
        Assert.Equal(new[] { "http://a/v3/index.json" }, o.Sources);
        Assert.Equal("8.0.5", o.RuntimeVersion);
        Assert.Equal(new[] { "--update", "x" }, o.ToolArgs);   // after "--" nothing is interpreted
    }

    [Fact]
    public void FirstUnknownTokenStartsToolArgsAndEverythingAfterIsTheTools()
    {
        var o = P("pkg", "--no-apphost", "--version", "--update", "-x");
        Assert.True(o.NoApphost);
        Assert.False(o.Update);                                  // came after the tool's args began
        Assert.Equal(new[] { "--version", "--update", "-x" }, o.ToolArgs);
        Assert.Equal(Mode.Run, o.Mode);
    }

    [Fact]
    public void DoubleDashItselfIsDroppedButALaterOneIsKept()
    {
        var o = P("pkg", "--", "a", "--", "b");
        Assert.Equal(new[] { "a", "--", "b" }, o.ToolArgs);
    }

    [Fact]
    public void ToolArgsAreKeptVerbatim()
    {
        var o = P("pkg", "--", "", "a b", "--source", "\"q\"");
        Assert.Equal(new[] { "", "a b", "--source", "\"q\"" }, o.ToolArgs);
        Assert.Empty(o.Sources);
    }

    [Fact]
    public void Modes()
    {
        Assert.Equal(Mode.List, P("--list").Mode);
        Assert.Equal(Mode.Version, P("--version").Mode);
        Assert.Equal(Mode.Help, P().Mode);
        Assert.Equal(Mode.Help, P("-h").Mode);
        var c = P("--clean");
        Assert.Equal(Mode.Clean, c.Mode); Assert.Null(c.Package);
        var c2 = P("--clean", "tool@1.2.3");
        Assert.Equal("tool", c2.Package); Assert.Equal("1.2.3", c2.PackageVersion);
        var w = P("--which", "tool", "--no-apphost", "--", "x");
        Assert.Equal(Mode.Which, w.Mode); Assert.Equal("tool", w.Package); Assert.True(w.NoApphost); Assert.Equal(new[] { "x" }, w.ToolArgs);
    }

    [Fact]
    public void ModeOptionsAfterThePackageBelongToTheTool()
    {
        var o = P("pkg", "--list");
        Assert.Equal(Mode.Run, o.Mode);
        Assert.Equal(new[] { "--list" }, o.ToolArgs);
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<UsageException>(() => P("--bogus", "pkg"));
        Assert.Throws<UsageException>(() => P("pkg", "--source"));
        Assert.Throws<UsageException>(() => P("pkg@notaversion"));
        Assert.Throws<UsageException>(() => P("@1.0.0"));
        Assert.Throws<UsageException>(() => P("pkg", "--runtime-version", "x"));
    }

    [Fact]
    public void AtLatestMeansNoVersion() => Assert.Null(P("pkg@latest").PackageVersion);
}
