using Xunit;

namespace Tbus.Tests;

public class ArgsTests
{
    [Fact]
    public void Bare_port_shares_localhost_with_machine_port_name()
    {
        var s = ShareSpec.Parse("3000", "Dev-Box");
        Assert.Equal(new ShareSpec("dev-box-3000", "localhost", 3000), s);
        Assert.Equal("localhost:3000", s.Target);
    }

    [Fact]
    public void Host_port_is_a_remote_target_and_name_overrides_default()
    {
        var s = ShareSpec.Parse("192.168.1.20:8080", "pc", "nas");
        Assert.Equal(new ShareSpec("nas", "192.168.1.20", 8080), s);
        Assert.Equal("pc-8080", ShareSpec.Parse("192.168.1.20:8080", "pc").Name);
    }

    [Fact]
    public void Name_equals_target_and_ipv6()
    {
        Assert.Equal(new ShareSpec("web", "localhost", 3000), ShareSpec.Parse("web=3000", "pc"));
        var v6 = ShareSpec.Parse("[::1]:80", "pc", "six");
        Assert.Equal("::1", v6.TargetHost); Assert.Equal("[::1]:80", v6.Target);
    }

    [Theory]
    [InlineData("0")] [InlineData("70000")] [InlineData("abc")] [InlineData("host:")] [InlineData("a:b:80")] [InlineData(":80")]
    public void Bad_targets_are_rejected(string arg) => Assert.Throws<UserError>(() => ShareSpec.Parse(arg, "pc"));

    [Theory]
    [InlineData("My_App")] [InlineData("a--b")] [InlineData("-a")] [InlineData("a-")] [InlineData("")]
    public void Bad_names_are_rejected(string name) => Assert.Throws<UserError>(() => ShareSpec.Parse("3000", "pc", name));

    [Fact]
    public void Name_longer_than_40_is_rejected_and_machine_names_are_sanitised_and_trimmed()
    {
        Assert.Throws<UserError>(() => ShareSpec.Parse("3000", "pc", new string('a', 41)));
        Assert.Equal("my-pc-01-3000", ShareSpec.DefaultName("My PC_01", 3000));
        var longName = ShareSpec.DefaultName(new string('x', 80), 3000);
        Assert.True(longName.Length <= 40); Assert.EndsWith("-3000", longName);
        Assert.Equal("host-1", ShareSpec.DefaultName("___", 1));
    }

    [Fact]
    public void Option_parser_handles_values_flags_and_unknowns()
    {
        var (pos, o) = App.ParseOptions(["3000", "--name", "x", "--flag"], ["name"], ["flag"]);
        Assert.Equal(["3000"], pos); Assert.Equal("x", o["name"]); Assert.Equal("true", o["flag"]);
        Assert.Equal("y", App.ParseOptions(["--name=y"], ["name"], []).options["name"]);
        Assert.Throws<UserError>(() => App.ParseOptions(["--nope"], ["name"], []));
        Assert.Throws<UserError>(() => App.ParseOptions(["--name"], ["name"], []));
    }

    [Fact]
    public async Task Share_usage_errors_exit_2()
    {
        using var t = new TestEnv();
        Assert.Equal(2, await t.Run(default, "share"));
        Assert.Equal(2, await t.Run(default, "share", "3000", "3001", "--name", "x"));
        Assert.Equal(2, await t.Run(default, "share", "a=3000", "a=3001"));
        Assert.Equal(2, await t.Run(default, "bogus"));
        t.Env.Remove("TUNNEL_BUS_ADMIN_TOKEN");
        Assert.Equal(2, await t.Run(default, "share", "3000"));
        Assert.Contains("admin token", t.AllOutput);
    }
}
