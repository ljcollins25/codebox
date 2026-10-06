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
    public void Metadata_options_are_parsed_validated_and_optional()
    {
        var (_, o) = App.ParseOptions(["3000", "--description", "Staging build", "--label=My app", "--kind", "App", "--owner", "hexad project"], ["name", .. ShareMeta.ValueOptions], []);
        var m = ShareMeta.FromOptions(o);
        Assert.Equal(new ShareMeta("Staging build", "My app", "app", "hexad project"), m);
        Assert.Equal(["description", "kind", "label", "owner"], m.ToFields().Keys.Order().ToArray());
        Assert.True(ShareMeta.FromOptions(new Dictionary<string, string>()).IsEmpty); // old behaviour: nothing is sent
        Assert.Empty(ShareMeta.FromOptions(new Dictionary<string, string>()).ToFields());
        Assert.Equal("env owner", ShareMeta.FromOptions(new Dictionary<string, string>(), k => k == "TBUS_OWNER" ? "env owner" : null).Owner);
        Assert.Equal("", ShareMeta.FromOptions(new Dictionary<string, string> { ["description"] = "" }).Description); // clears
        Assert.Throws<UserError>(() => ShareMeta.FromOptions(new Dictionary<string, string> { ["description"] = new string('x', 201) }));
        Assert.Throws<UserError>(() => ShareMeta.FromOptions(new Dictionary<string, string> { ["kind"] = "Not Valid!" }));
        Assert.Throws<UserError>(() => ShareMeta.FromOptions(new Dictionary<string, string> { ["label"] = new string('x', 61) }));
    }

    [Fact]
    public void Session_options_build_a_session_object_and_validate_the_url()
    {
        var o = new Dictionary<string, string?> { ["session-name"] = "csharp-wasm-2", ["session-id"] = "s-1", ["hexad"] = "hexad project", ["session-url"] = "https://h.test/s/1" };
        var f = ShareMeta.FromOptions(o).ToFields();
        var s = Assert.IsType<Dictionary<string, string>>(f["session"]);
        Assert.Equal(["hexad", "id", "name"], s.Keys.Order().ToArray());
        Assert.Equal("https://h.test/s/1", f["sessionUrl"]);
        var env = ShareMeta.FromOptions(new Dictionary<string, string?>(), k => k == "TBUS_SESSION_NAME" ? "from-env" : null);
        Assert.Equal("from-env", env.SessionName);
        Assert.False(ShareMeta.FromOptions(new Dictionary<string, string?>()).ToFields().ContainsKey("session"));
        Assert.Throws<UserError>(() => ShareMeta.FromOptions(new Dictionary<string, string?> { ["session-url"] = "javascript:alert(1)" }));
        Assert.Throws<UserError>(() => ShareMeta.FromOptions(new Dictionary<string, string?> { ["session-name"] = new string('x', 61) }));
    }

    [Fact]
    public async Task Register_sends_only_the_metadata_that_was_given_and_update_patches()
    {
        using var t = new TestEnv();
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => t.Run(cts.Token, "share", "3000", "--name", "web", "--description", "Staging <b>build</b>", "--kind", "app"));
        await TestEnv.WaitFor(() => t.Bus.Calls.Any(c => c.Method == "POST"), "register call");
        cts.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(15));
        var body = System.Text.Json.JsonDocument.Parse(t.Bus.Calls.First(c => c.Method == "POST").Body).RootElement;
        Assert.Equal("Staging <b>build</b>", body.GetProperty("description").GetString()); // sent verbatim; the dashboard escapes
        Assert.Equal("app", body.GetProperty("kind").GetString());
        Assert.False(body.TryGetProperty("label", out _));
        Assert.False(body.TryGetProperty("owner", out _));

        Assert.Equal(2, await t.Run(default, "update", "web"));                         // nothing to change
        Assert.Equal(2, await t.Run(default, "share", "3000", "--description", new string('x', 201)));
        Assert.Equal(2, await t.Run(default, "share", "3000", "3001", "--label", "x"));
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
