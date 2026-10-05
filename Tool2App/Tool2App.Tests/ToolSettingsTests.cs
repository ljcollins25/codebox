using Tool2App;
using Xunit;

namespace Tool2App.Tests;

public class ToolSettingsTests
{
    private const string V1 = """
        <?xml version="1.0" encoding="utf-8"?>
        <DotNetCliTool Version="1">
          <Commands>
            <Command Name="dotnet-counters" EntryPoint="dotnet-counters.dll" Runner="dotnet" />
          </Commands>
        </DotNetCliTool>
        """;

    private const string V2 = """
        <DotNetCliTool Version="2">
          <Commands><Command Name="tool" EntryPoint="tool.dll" Runner="dotnet" /></Commands>
          <RuntimeIdentifierPackages>
            <RuntimeIdentifierPackage RuntimeIdentifier="win-x64" Id="tool.win-x64" />
            <RuntimeIdentifierPackage RuntimeIdentifier="linux-x64" Id="tool.linux-x64" />
          </RuntimeIdentifierPackages>
        </DotNetCliTool>
        """;

    [Fact]
    public void Parses_command_name_entry_point_and_runner()
    {
        var s = ToolSettings.Parse(V1);
        var c = Assert.Single(s.Commands);
        Assert.Equal("dotnet-counters", c.Name);
        Assert.Equal("dotnet-counters.dll", c.EntryPoint);
        Assert.Equal("dotnet", c.Runner);
        Assert.Empty(s.RidPackages);
    }

    [Fact]
    public void Parses_rid_specific_package_list()
    {
        var s = ToolSettings.Parse(V2);
        Assert.Equal(new[] { "win-x64", "linux-x64" }, s.RidPackages.Select(r => r.Rid));
        Assert.Equal("tool.linux-x64", s.RidPackages[1].Id);
    }

    [Fact]
    public void Rejects_bad_xml_and_empty_settings()
    {
        Assert.Throws<ToolException>(() => ToolSettings.Parse("<nope"));
        Assert.Throws<ToolException>(() => ToolSettings.Parse("<DotNetCliTool Version=\"1\"><Commands/></DotNetCliTool>"));
    }

    private static PackageContent Package(params string[] dirs)
    {
        var root = TestFiles.NewTempDir();
        foreach (var d in dirs) TestFiles.Write(Path.Combine(root, "tools", d.Replace('/', Path.DirectorySeparatorChar), "DotnetToolSettings.xml"), V1);
        TestFiles.Write(Path.Combine(root, "tools", "net8.0", "any", "x.dll"), "x");
        return new DirectoryPackageContent(root);
    }

    [Fact]
    public void Tfm_parsing_accepts_modern_dotnet_only()
    {
        Assert.True(Tfm.TryParse("net8.0", out var t) && t.Version.Major == 8);
        Assert.True(Tfm.TryParse("netcoreapp3.1", out var c) && c.Version.Minor == 1);
        Assert.True(Tfm.TryParse("net10.0-windows7.0", out var w) && w.Platform == "windows7.0");
        Assert.False(Tfm.TryParse("net48", out _));
        Assert.False(Tfm.TryParse("netstandard2.0", out _));
        Assert.False(Tfm.TryParse("net4.8", out _));
    }

    [Fact]
    public void Picks_highest_tfm()
    {
        using var pkg = Package("net6.0/any", "net8.0/any", "net7.0/any");
        var a = ToolAsset.Select(ToolAsset.Discover(pkg), Rid.Parse("linux-x64"));
        Assert.Equal("net8.0", a.Tfm.Text);
        Assert.Equal("tools/net8.0/any/", a.Prefix);
    }

    [Fact]
    public void Tfm_can_be_forced()
    {
        using var pkg = Package("net6.0/any", "net8.0/any");
        Assert.Equal("net6.0", ToolAsset.Select(ToolAsset.Discover(pkg), Rid.Parse("linux-x64"), "net6.0").Tfm.Text);
        Assert.Throws<ToolException>(() => ToolAsset.Select(ToolAsset.Discover(pkg), Rid.Parse("linux-x64"), "net5.0"));
    }

    [Fact]
    public void Rid_specific_folder_beats_any_for_the_same_tfm_and_other_rids_are_ignored()
    {
        using var pkg = Package("net8.0/any", "net8.0/linux-x64", "net8.0/win-x64");
        var all = ToolAsset.Discover(pkg);
        Assert.Equal("linux-x64", ToolAsset.Select(all, Rid.Parse("linux-x64")).RidDir);
        Assert.Equal("win-x64", ToolAsset.Select(all, Rid.Parse("win-x64")).RidDir);
        Assert.Equal("any", ToolAsset.Select(all, Rid.Parse("osx-arm64")).RidDir);
    }

    [Fact]
    public void A_higher_tfm_wins_even_over_a_more_specific_rid()
    {
        using var pkg = Package("net6.0/linux-x64", "net8.0/any");
        Assert.Equal("net8.0", ToolAsset.Select(ToolAsset.Discover(pkg), Rid.Parse("linux-x64")).Tfm.Text);
    }

    [Fact]
    public void Rid_only_package_without_a_match_explains_what_it_offers()
    {
        using var pkg = Package("net8.0/win-x64");
        var ex = Assert.Throws<ToolException>(() => ToolAsset.Select(ToolAsset.Discover(pkg), Rid.Parse("linux-x64")));
        Assert.Contains("net8.0/win-x64", ex.Message);
    }

    [Fact]
    public void Not_a_tool_package_is_reported()
    {
        var root = TestFiles.NewTempDir();
        TestFiles.Write(Path.Combine(root, "lib", "net8.0", "a.dll"), "x");
        using var pkg = new DirectoryPackageContent(root);
        Assert.Throws<ToolException>(() => ToolAsset.Select(ToolAsset.Discover(pkg), Rid.Parse("linux-x64")));
    }

    [Fact]
    public void Unknown_command_lists_the_known_ones()
    {
        using var pkg = Package("net8.0/any");
        var a = ToolAsset.Discover(pkg)[0];
        Assert.Equal("dotnet-counters", a.PickCommand(null).Name);
        var ex = Assert.Throws<ToolException>(() => a.PickCommand("other"));
        Assert.Contains("dotnet-counters", ex.Message);
    }
}

