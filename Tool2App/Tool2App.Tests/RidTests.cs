using Tool2App;
using Xunit;

namespace Tool2App.Tests;

public class RidTests
{
    [Theory]
    [InlineData("win-x64", "win", "x64", "win-x64")]
    [InlineData("linux-musl-arm64", "linux-musl", "arm64", "linux-musl-arm64")]
    [InlineData("osx.12-arm64", "osx", "arm64", "osx-arm64")]
    [InlineData("win10-x86", "win", "x86", "win-x86")]
    public void Parse_splits_os_and_arch(string text, string os, string arch, string packRid)
    {
        var rid = Rid.Parse(text);
        Assert.Equal(os, rid.Os);
        Assert.Equal(arch, rid.Arch);
        Assert.Equal(packRid, rid.PackRid);
    }

    [Fact]
    public void Parse_rejects_nonsense() => Assert.Throws<ToolException>(() => Rid.Parse("banana"));

    [Fact]
    public void Compatible_chain_for_linux_goes_from_specific_to_any()
        => Assert.Equal(new[] { "linux-x64", "linux", "unix", "any" }, Rid.Parse("linux-x64").Compatible);

    [Fact]
    public void Compatible_chain_for_musl_falls_back_to_glibc_linux()
        => Assert.Equal(new[] { "linux-musl-x64", "linux-musl", "linux-x64", "linux", "unix", "any" }, Rid.Parse("linux-musl-x64").Compatible);

    [Fact]
    public void Compatible_chain_for_windows_has_no_unix()
        => Assert.Equal(new[] { "win-x64", "win", "any" }, Rid.Parse("win-x64").Compatible);

    [Fact]
    public void Rank_orders_specific_before_general_and_rejects_foreign()
    {
        var rid = Rid.Parse("linux-x64");
        Assert.True(rid.Rank("linux-x64") < rid.Rank("linux"));
        Assert.True(rid.Rank("linux") < rid.Rank("any"));
        Assert.Equal(-1, rid.Rank("win-x64"));
        Assert.Equal(-1, rid.Rank("linux-arm64"));
    }

    [Fact]
    public void Exe_name_gets_extension_only_on_windows()
    {
        Assert.Equal("t.exe", Rid.Parse("win-arm64").ExeName("t"));
        Assert.Equal("t", Rid.Parse("osx-x64").ExeName("t"));
    }

    [Fact]
    public void SemVer_orders_prereleases_below_releases()
    {
        Assert.True(SemVer.Parse("8.0.1").CompareTo(SemVer.Parse("8.0.1-rc.2")) > 0);
        Assert.True(SemVer.Parse("8.0.10").CompareTo(SemVer.Parse("8.0.9")) > 0);
        Assert.True(SemVer.Parse("1.0.0-beta.11").CompareTo(SemVer.Parse("1.0.0-beta.2")) > 0);
        Assert.Equal("8.0.3", SemVer.Max(new[] { "8.0.1", "8.0.3", "9.0.0-preview.1", "8.0.2" }, false)!.Text);
        Assert.Equal("9.0.0-preview.1", SemVer.Max(new[] { "8.0.3", "9.0.0-preview.1" }, true)!.Text);
    }
}

