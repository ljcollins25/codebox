using Xunit;

namespace Tbus.Tests;

// these tests use real chisel servers, short timings and real sockets; keep them off the other classes' threads
[Collection("serial")]
public class ShareFlowTests
{
    private static (Task<int> task, CancellationTokenSource cts) StartShare(TestEnv t, params string[] args)
    {
        var cts = new CancellationTokenSource();
        return (Task.Run(() => t.Run(cts.Token, args)), cts);
    }

    [SkippableFact]
    public async Task Register_connect_carry_traffic_and_unregister_on_cancel()
    {
        using var t = new TestEnv(withChisel: true);
        var port = t.StartEcho();
        var (task, cts) = StartShare(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected line");

        var reg = t.Bus.Calls.Single(c => c.Method == "POST");
        Assert.Equal("/_api/register", reg.Path);
        Assert.Equal("Bearer " + FakeBus.Admin, reg.Headers["Authorization"]);
        Assert.Equal("access-id-SECRET-ID", reg.Headers["CF-Access-Client-Id"]);
        Assert.Contains("\"web\"", reg.Body);

        // the websocket handshake carries the Access headers (no relay), the chisel subprotocol, and not the admin token
        var ws = t.Bus.Calls.Single(c => c.Path == "/_chisel");
        Assert.Equal("access-id-SECRET-ID", ws.Headers["CF-Access-Client-Id"]);
        Assert.Equal("access-secret-SECRET-VAL", ws.Headers["CF-Access-Client-Secret"]);
        Assert.Equal("chisel-v3", ws.Headers["Sec-WebSocket-Protocol"]);
        Assert.False(ws.Headers.ContainsKey("Authorization"));
        Assert.Contains($"connected: https://web.ref12.dev/ -> 127.0.0.1:{port}", t.AllOutput);

        await t.AssertEchoes("web", "hello via the bus");

        cts.Cancel();
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/web"));
        Assert.False(t.Bus.Has("web"));
        Assert.Contains("[web] unregistered", t.AllOutput);
    }

    [SkippableFact]
    public async Task Access_is_enforced_on_the_websocket_and_secrets_stay_out_of_the_output()
    {
        using var t = new TestEnv(withChisel: true);
        t.Bus.RequireAccess = true;
        var port = t.StartEcho();
        var (task, cts) = StartShare(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected line");
        await t.AssertEchoes("web", "x");
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));

        var secrets = new[] { FakeBus.Admin, t.Bus.PasswordFor("web"), "access-id-SECRET-ID", "access-secret-SECRET-VAL", "u-web:" };
        foreach (var s in secrets) Assert.DoesNotContain(s, t.AllOutput);
        Assert.Equal("x *** y", Log.Redact("x " + t.Bus.PasswordFor("web") + " y"));
    }

    [SkippableFact]
    public async Task Missing_access_headers_are_refused_by_the_edge_and_retried()
    {
        using var t = new TestEnv(withChisel: true);
        t.Bus.RequireAccess = true;
        t.Env.Remove("CF_ACCESS_CLIENT_ID"); t.Env.Remove("CF_ACCESS_CLIENT_SECRET");
        var (task, cts) = StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("retrying in"), "retry after 403");
        Assert.Contains("websocket connect failed", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task Bus_restart_re_registers_and_reconnects_and_traffic_flows_again()
    {
        using var t = new TestEnv(withChisel: true);
        var port = t.StartEcho();
        var (task, cts) = StartShare(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.Count("connected:") == 1, "first connection");
        await t.AssertEchoes("web", "before");
        await t.Bus.RestartAsync();
        await TestEnv.WaitFor(() => t.Count("connected:") == 2 && t.Bus.Has("web"), "reconnect after restart");
        Assert.Equal(2, t.Bus.Count("POST", "/_api/register"));
        await t.AssertEchoes("web", "after");
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Server_unreachable_retries_with_backoff()
    {
        using var t = new TestEnv(); // the bus answers the API but /_chisel gives 502
        var (task, cts) = StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("retrying in 0.1s") && t.AllOutput.Contains("retrying in 0.2s") && t.AllOutput.Contains("retrying in 0.4s"), "backoff");
        Assert.True(t.Bus.Count("GET", "/_chisel") >= 3);
        cts.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [SkippableFact]
    public async Task Several_shares_in_one_process_each_register_connect_and_unregister()
    {
        using var t = new TestEnv(withChisel: true);
        var a = t.StartEcho(); var b = t.StartEcho();
        var (task, cts) = StartShare(t, "share", $"web=127.0.0.1:{a}", $"nas=127.0.0.1:{b}");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[web] connected:") && t.AllOutput.Contains($"[nas] connected: https://nas.ref12.dev/ -> 127.0.0.1:{b}"), "both connected");
        Assert.Equal(2, t.Bus.Count("POST", "/_api/register"));
        Assert.Equal(2, t.Bus.Calls.Count(c => c.Path == "/_chisel"));
        await t.AssertEchoes("web", "from web");
        await t.AssertEchoes("nas", "from nas");
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/web"));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/nas"));
    }

    [SkippableFact]
    public async Task Stop_unregisters_and_ends_the_running_share_without_re_registering()
    {
        using var t = new TestEnv(withChisel: true);
        var (task, cts) = StartShare(t, "share", $"127.0.0.1:{t.StartEcho()}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected");
        Assert.Equal(0, await t.Run(default, "stop", "web"));
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(t.Bus.Has("web"));
        Assert.Equal(1, t.Bus.Count("POST", "/_api/register"));
        Assert.Equal(1, t.Bus.Count("DELETE", "/_api/register/web"));
        Assert.Contains("stopped by 'tbus stop'", t.AllOutput);
        Assert.Equal(1, await t.Run(default, "stop", "web"));
        cts.Dispose();
    }

    [SkippableFact]
    public async Task List_and_open()
    {
        using var t = new TestEnv(withChisel: true);
        var (task, cts) = StartShare(t, "share", $"127.0.0.1:{t.StartEcho()}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("connected:"), "connected");
        Assert.Equal(0, await t.Run(default, "list"));
        Assert.Matches(@"web\s+\d+\s+yes\s+https://web\.ref12\.dev/", t.AllOutput);
        Assert.Equal(0, await t.Run(default, "open", "web"));
        Assert.Equal(["https://web.ref12.dev/"], t.Opened);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Wrong_admin_token_is_reported_not_retried_forever_silently()
    {
        using var t = new TestEnv();
        t.Env["TUNNEL_BUS_ADMIN_TOKEN"] = "wrong-token-value";
        var (task, cts) = StartShare(t, "share", "3000", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("HTTP 401"), "401 message");
        Assert.DoesNotContain("wrong-token-value", t.AllOutput);
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

[CollectionDefinition("serial", DisableParallelization = true)]
public class SerialCollection;
