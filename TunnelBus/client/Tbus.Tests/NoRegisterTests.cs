using Xunit;

namespace Tbus.Tests;

/// <summary>tbus share --no-register: connect with one name's own credentials; no registration, no admin token.</summary>
[Collection("serial")]
public class NoRegisterTests
{
    private static (Task<int>, CancellationTokenSource) Start(TestEnv t, params string[] args)
    { var cts = new CancellationTokenSource(); return (Task.Run(() => t.Run(cts.Token, args)), cts); }

    private static void DropAdmin(TestEnv t) { t.Env.Remove("TUNNEL_BUS_ADMIN_TOKEN"); t.Env.Remove("TUNNEL_PIPE_ADMIN_TOKEN"); }

    private static async Task<Registration> RegisterChisel(TestEnv t, string name)
    {
        using var admin = new BusClient(t.Bus.Url, FakeBus.Admin, new Dictionary<string, string>());
        var reg = await admin.RegisterAsync(name, default);
        await Task.Delay(1500); // the server reloads its authfile
        return reg;
    }

    private static void GiveChisel(TestEnv t, Registration r)
    { t.Env["TBUS_CHISEL_USER"] = r.User; t.Env["TBUS_CHISEL_PASSWORD"] = r.Password; t.Env["TBUS_CHISEL_PORT"] = r.Port.ToString(); }

    private static void AssertNoAdmin(TestEnv t, FakePipeBus? p = null)
    {
        Assert.DoesNotContain(t.EnvReads, k => k.Contains("ADMIN"));
        Assert.DoesNotContain(t.Bus.Calls, c => c.Method is "POST" or "DELETE" or "PATCH" || c.Path.StartsWith("/_api"));
        if (p != null) Assert.DoesNotContain(p.SeenAuthHeaders, h => h.Contains(FakePipeBus.Admin) || h.StartsWith("/_api"));
    }

    [Fact]
    public async Task Help_lists_no_register()
    {
        using var t = new TestEnv();
        Assert.Equal(0, await t.Run(default, "share", "--help"));
        Assert.Contains("--no-register", t.AllOutput);
        t.Out.Reset();
        Assert.Equal(0, await t.Run(default, "help"));
        Assert.Contains("--no-register", t.AllOutput);
    }

    [SkippableFact]
    public async Task Chisel_lane_alone_connects_without_registering_or_admin()
    {
        using var t = new TestEnv(withChisel: true);
        var reg = await RegisterChisel(t, "web");
        t.Bus.Calls.Clear(); DropAdmin(t); GiveChisel(t, reg); t.EnvReads.Clear();
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--no-register");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[web] connected: https://web.ref12.dev/ ->"), "chisel connected");
        Assert.Contains("pipe lane skipped", t.AllOutput);
        await t.AssertEchoes("web", "per-name");
        cts.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(t.Bus.Has("web")); // not unregistered
        AssertNoAdmin(t);
        Assert.DoesNotContain(reg.Password, t.AllOutput);
    }

    [SkippableFact]
    public async Task Chisel_with_another_names_credentials_is_refused()
    {
        using var t = new TestEnv(withChisel: true);
        var web = await RegisterChisel(t, "web"); var other = await RegisterChisel(t, "other");
        DropAdmin(t);
        t.Env["TBUS_CHISEL_USER"] = other.User; t.Env["TBUS_CHISEL_PASSWORD"] = other.Password; t.Env["TBUS_CHISEL_PORT"] = web.Port.ToString();
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--no-register", "--bus", "chisel");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("retrying"), "refusal");
        Assert.DoesNotContain("connected: https://web.ref12.dev/", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Pipe_lane_alone_connects_with_the_name_token_only()
    {
        using var t = new TestEnv(); using var p = new FakePipeBus();
        p.Tokens["web"] = "pb_name_token_SECRET";
        DropAdmin(t);
        t.Env["TUNNEL_PIPE_URL"] = p.Url; t.Env["TBUS_PIPE_NAME_TOKEN"] = "pb_name_token_SECRET";
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--no-register");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[web] connected: https://p-web.ref12.dev/ ->"), "pipe connected");
        Assert.Contains("chisel lane skipped", t.AllOutput);
        Assert.True(p.IsConnected("web"));
        cts.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(p.Has("web")); // not unregistered
        AssertNoAdmin(t, p);
        Assert.DoesNotContain("pb_name_token_SECRET", t.AllOutput);
    }

    [Fact]
    public async Task Pipe_refuses_another_names_token()
    {
        using var t = new TestEnv(); using var p = new FakePipeBus();
        p.Tokens["web"] = "pb_web"; p.Tokens["other"] = "pb_other";
        DropAdmin(t);
        t.Env["TUNNEL_PIPE_URL"] = p.Url; t.Env["TBUS_PIPE_NAME_TOKEN"] = "pb_other";
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--no-register");
        await TestEnv.WaitFor(() => p.SocketAuthFailures.Contains("web"), "401 for the wrong token");
        Assert.DoesNotContain("connected: https://p-web", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain("pb_other", t.AllOutput);
    }

    [SkippableFact]
    public async Task Both_lanes_run_together_with_tagged_logs()
    {
        using var t = new TestEnv(withChisel: true); using var p = new FakePipeBus();
        var reg = await RegisterChisel(t, "web"); p.Tokens["web"] = "pb_both_SECRET";
        t.Bus.Calls.Clear(); DropAdmin(t); GiveChisel(t, reg); t.EnvReads.Clear();
        t.Env["TUNNEL_PIPE_URL"] = p.Url; t.Env["TBUS_PIPE_NAME_TOKEN"] = "pb_both_SECRET";
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--no-register");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[chisel:web] connected: https://web.ref12.dev/ ->") && t.AllOutput.Contains("[pipe:web] connected: https://p-web.ref12.dev/ ->"), "both connected");
        await t.AssertEchoes("web", "both");
        cts.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(t.Bus.Has("web") && p.Has("web"));
        AssertNoAdmin(t, p);
        Assert.DoesNotContain(reg.Password, t.AllOutput); Assert.DoesNotContain("pb_both_SECRET", t.AllOutput);
        Assert.DoesNotContain("access-secret-SECRET-VAL", t.AllOutput);
    }

    [Fact]
    public async Task Neither_lane_with_credentials_exits_non_zero_and_explains()
    {
        using var t = new TestEnv(); DropAdmin(t);
        Assert.NotEqual(0, await t.Run(default, "share", "3000", "--name", "web", "--no-register"));
        Assert.Contains("TBUS_PIPE_NAME_TOKEN", t.AllOutput); Assert.Contains("TBUS_CHISEL_USER", t.AllOutput);
        Assert.DoesNotContain(t.EnvReads, k => k.Contains("ADMIN"));
    }

    [Fact]
    public async Task Admin_tokens_present_are_still_never_read_or_used()
    {
        using var t = new TestEnv(); using var p = new FakePipeBus();
        p.Tokens["web"] = "pb_x"; // TUNNEL_*_ADMIN_TOKEN stay set in the environment on purpose
        t.Env["TUNNEL_PIPE_ADMIN_TOKEN"] = FakePipeBus.Admin; t.Env["TUNNEL_PIPE_URL"] = p.Url; t.Env["TBUS_PIPE_NAME_TOKEN"] = "pb_x";
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--no-register");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[web] connected: https://p-web"), "pipe connected");
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        AssertNoAdmin(t, p);
    }

    [Fact]
    public async Task Requires_one_name()
    {
        using var t = new TestEnv(); DropAdmin(t);
        Assert.NotEqual(0, await t.Run(default, "share", "web=3000", "api=3001", "--no-register"));
    }
}
