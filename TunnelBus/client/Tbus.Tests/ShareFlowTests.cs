using Xunit;

namespace Tbus.Tests;

// these tests set a real environment variable and use short timings; keep them off the other classes' threads
[Collection("serial")]
public class ShareFlowTests
{
    private static async Task<(Task<int> task, CancellationTokenSource cts)> StartShare(TestEnv t, params string[] args)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => t.Run(cts.Token, args));
        await Task.CompletedTask;
        return (task, cts);
    }

    [Fact]
    public async Task Register_connect_and_unregister_on_cancel()
    {
        using var t = new TestEnv();
        var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected line");

        var reg = t.Bus.Calls.Single(c => c.Method == "POST");
        Assert.Equal("/_api/register", reg.Path);
        Assert.Equal("Bearer " + FakeBus.Admin, reg.Headers["Authorization"]);
        Assert.Equal("access-id-SECRET-ID", reg.Headers["CF-Access-Client-Id"]);
        Assert.Equal("access-secret-SECRET-VAL", reg.Headers["CF-Access-Client-Secret"]);
        Assert.Contains("\"web\"", reg.Body);

        var lines = FakeChisel.ReadShared(t.ChiselLog);
        Assert.Contains(lines, l => l.StartsWith("AUTH=u-web:" + t.Bus.PasswordFor("web")));
        var args = lines.Single(l => l.StartsWith("ARGS:"));
        Assert.Matches(@"^ARGS: client --keepalive 25s http://127\.0\.0\.1:\d+/[0-9a-f]{24}/_chisel R:20000:localhost:3000$", args);
        Assert.Contains("registering", t.AllOutput);
        Assert.Contains("connected: https://web.ref12.dev/ -> localhost:3000", t.AllOutput);

        cts.Cancel();
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/web"));
        Assert.False(t.Bus.Has("web"));
        Assert.Contains("[web] unregistered", t.AllOutput);
    }

    [Fact]
    public async Task Secrets_stay_out_of_chisel_args_environment_and_output()
    {
        using var t = new TestEnv();
        Environment.SetEnvironmentVariable("TUNNEL_BUS_ADMIN_TOKEN", FakeBus.Admin); // really in our environment: chisel must not inherit it
        try
        {
            var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
            await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected line");
            cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { Environment.SetEnvironmentVariable("TUNNEL_BUS_ADMIN_TOKEN", null); }

        var args = FakeChisel.ReadShared(t.ChiselLog).Single(l => l.StartsWith("ARGS:"));
        var secrets = new[] { FakeBus.Admin, t.Bus.PasswordFor("web"), "access-id-SECRET-ID", "access-secret-SECRET-VAL", "u-web:" };
        foreach (var s in secrets) { Assert.DoesNotContain(s, args); Assert.DoesNotContain(s, t.AllOutput); }
        Assert.DoesNotContain("--auth", args); Assert.DoesNotContain("--header", args);
        Assert.Contains("ADMIN=unset", FakeChisel.ReadShared(t.ChiselLog));
        // the redactor masks a secret even if chisel echoed it
        Assert.Equal("x *** y", Log.Redact("x " + t.Bus.PasswordFor("web") + " y"));
    }

    [Fact]
    public async Task Bus_restart_re_registers_and_reconnects()
    {
        using var t = new TestEnv();
        var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => FakeChisel.Starts(t.ChiselLog).Count == 1 && t.Bus.Has("web"), "first connection");
        t.Bus.Restart();
        await TestEnv.WaitFor(() => FakeChisel.Starts(t.ChiselLog).Count == 2 && t.Bus.Has("web"), "reconnect after restart");
        Assert.Equal(2, t.Bus.Count("POST", "/_api/register"));
        Assert.Contains("re-registering", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Chisel_exit_reconnects_with_backoff()
    {
        using var t = new TestEnv(chiselExits: true);
        var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => FakeChisel.Starts(t.ChiselLog).Count >= 3, "three attempts");
        Assert.Contains("retrying in 0.1s", t.AllOutput);
        Assert.Contains("retrying in 0.2s", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Several_shares_in_one_process_each_register_and_unregister()
    {
        using var t = new TestEnv();
        var (task, cts) = await StartShare(t, "share", "web=3000", "nas=127.0.0.2:8080");
        await TestEnv.WaitFor(() => FakeChisel.Starts(t.ChiselLog).Count == 2, "two chisel clients");
        var starts = FakeChisel.Starts(t.ChiselLog);
        Assert.Contains(starts, s => s.EndsWith("R:20000:localhost:3000") || s.EndsWith("R:20001:localhost:3000"));
        Assert.Contains(starts, s => s.EndsWith(":127.0.0.2:8080"));
        Assert.Equal(2, t.Bus.Count("POST", "/_api/register"));
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[web] connected:") && t.AllOutput.Contains("[nas] connected: https://nas.ref12.dev/ -> 127.0.0.2:8080"), "both connected");
        // each chisel has its own credentials
        var auths = FakeChisel.ReadShared(t.ChiselLog).Where(l => l.StartsWith("AUTH=")).ToList();
        Assert.Equal(2, auths.Distinct().Count());
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/web"));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/nas"));
    }

    [Fact]
    public async Task Stop_unregisters_and_ends_the_running_share_without_re_registering()
    {
        using var t = new TestEnv();
        var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected");
        Assert.Equal(0, await t.Run(default, "stop", "web"));
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(t.Bus.Has("web"));
        Assert.Equal(1, t.Bus.Count("POST", "/_api/register"));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/web")); // by stop; the share did not delete again
        Assert.Contains("stopped by 'tbus stop'", t.AllOutput);
        Assert.Equal(1, await t.Run(default, "stop", "web")); // already gone
        cts.Dispose();
    }

    [Fact]
    public async Task List_and_open()
    {
        using var t = new TestEnv();
        var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected");
        Assert.Equal(0, await t.Run(default, "list"));
        Assert.Matches(@"web\s+20000\s+yes\s+https://web\.ref12\.dev/", t.AllOutput);
        Assert.Equal(0, await t.Run(default, "open", "web"));
        Assert.Equal(["https://web.ref12.dev/"], t.Opened);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Wrong_admin_token_is_reported_not_retried_forever_silently()
    {
        using var t = new TestEnv();
        t.Env["TUNNEL_BUS_ADMIN_TOKEN"] = "wrong-token-value";
        var (task, cts) = await StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("HTTP 401"), "401 message");
        Assert.DoesNotContain("wrong-token-value", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

[CollectionDefinition("serial", DisableParallelization = true)]
public class SerialCollection;
