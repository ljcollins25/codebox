using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tbus.Tests;

/// <summary>A minimal pipe bus: per-name tokens, 401 on a socket whose token belongs to another name, the same /_api shapes.</summary>
internal sealed class FakePipeBus : IDisposable
{
    public const string Admin = "pipe-admin-SECRET-777";
    private readonly HttpListener _l = new();
    private readonly object _lock = new();
    public readonly Dictionary<string, string> Tokens = new();
    public readonly Dictionary<string, string> Bodies = new();
    public readonly HashSet<string> Connected = new();
    public readonly List<string> SocketAuthFailures = new();
    public readonly List<string> SeenAuthHeaders = new();
    public bool Down;
    public string Url { get; }
    public FakePipeBus() { Url = $"http://127.0.0.1:{ChiselServer.FreePort()}"; _l.Prefixes.Add(Url + "/"); _l.Start(); _ = Task.Run(Loop); }
    public bool Has(string n) { lock (_lock) return Tokens.ContainsKey(n); }
    public bool IsConnected(string n) { lock (_lock) return Connected.Contains(n); }
    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c; try { c = await _l.GetContextAsync(); } catch (Exception) { return; }
            _ = Task.Run(() => Handle(c));
        }
    }
    private async Task Handle(HttpListenerContext c)
    {
        try
        {
            var path = c.Request.Url!.AbsolutePath; var auth = c.Request.Headers["Authorization"] ?? "";
            lock (_lock) SeenAuthHeaders.Add(path + " " + auth);
            if (Down) { c.Response.StatusCode = 503; c.Response.Close(); return; }
            if (path.StartsWith("/_bus/ws/"))
            {
                var name = path["/_bus/ws/".Length..];
                bool ok; lock (_lock) ok = Tokens.TryGetValue(name, out var t) && auth == "Bearer " + t;
                if (!ok) { lock (_lock) SocketAuthFailures.Add(name); c.Response.StatusCode = 401; c.Response.Close(); return; }
                var ws = (await c.AcceptWebSocketAsync(null)).WebSocket;
                lock (_lock) Connected.Add(name);
                try { var b = new byte[4096]; while (ws.State == WebSocketState.Open) { var r = await ws.ReceiveAsync(b, default); if (r.MessageType == WebSocketMessageType.Close) break; } } catch (Exception) { }
                finally { lock (_lock) Connected.Remove(name); }
                return;
            }
            if (auth != "Bearer " + Admin) { c.Response.StatusCode = 401; c.Response.Close(); return; }
            string body = ""; using (var sr = new StreamReader(c.Request.InputStream)) body = await sr.ReadToEndAsync();
            if (path == "/_api/register" && c.Request.HttpMethod == "POST")
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                if (name.StartsWith("p-")) { c.Response.StatusCode = 400; c.Response.Close(); return; }
                var tok = "pb_" + Guid.NewGuid().ToString("N");
                lock (_lock) { Tokens[name] = tok; Bodies[name] = body; }
                await Json(c, new { name, token = tok, path = "/_bus/ws/" + name });
            }
            else if (path == "/_api/registry") { string[] names; lock (_lock) names = Tokens.Keys.ToArray(); await Json(c, names.Select(n => new { name = n, port = 0, up = IsConnected(n) })); }
            else if (path.StartsWith("/_api/register/") && c.Request.HttpMethod == "DELETE") { bool had; lock (_lock) had = Tokens.Remove(path["/_api/register/".Length..]); c.Response.StatusCode = had ? 200 : 404; c.Response.Close(); }
            else { c.Response.StatusCode = 404; c.Response.Close(); }
        }
        catch (Exception) { try { c.Response.Abort(); } catch (Exception) { } }
    }
    private static async Task Json(HttpListenerContext c, object o) { var b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)); c.Response.ContentType = "application/json"; await c.Response.OutputStream.WriteAsync(b); c.Response.Close(); }
    public void Dispose() { try { _l.Stop(); _l.Close(); } catch (Exception) { } }
}

[Collection("serial")]
public class DualBusTests
{
    private static (Task<int>, CancellationTokenSource) Start(TestEnv t, params string[] args)
    { var cts = new CancellationTokenSource(); return (Task.Run(() => t.Run(cts.Token, args)), cts); }

    private static void ConfigurePipe(TestEnv t, FakePipeBus p) { t.Env["TUNNEL_PIPE_URL"] = p.Url; t.Env["TUNNEL_PIPE_ADMIN_TOKEN"] = FakePipeBus.Admin; }

    [Theory]
    [InlineData(null, true, true, "chisel,pipe")]
    [InlineData("both", true, true, "chisel,pipe")]
    [InlineData("chisel", true, true, "chisel")]
    [InlineData("pipe", true, true, "pipe")]
    [InlineData(null, true, false, "chisel")]
    [InlineData(null, false, true, "pipe")]
    public void Lanes_follow_the_flag_and_what_is_configured(string? mode, bool chisel, bool pipe, string expected) =>
        Assert.Equal(expected, string.Join(",", DualShare.ChooseLanes(mode, chisel, pipe)));

    [Fact]
    public void Asking_for_a_bus_that_is_not_configured_is_an_error()
    {
        Assert.Throws<UserError>(() => DualShare.ChooseLanes("both", true, false));
        Assert.Throws<UserError>(() => DualShare.ChooseLanes("pipe", true, false));
        Assert.Throws<UserError>(() => DualShare.ChooseLanes(null, false, false));
    }

    [Fact]
    public void The_p_prefix_is_reserved_for_names()
    {
        Assert.Throws<UserError>(() => ShareSpec.Parse("3000", "m", "p-x"));
        ShareSpec.Parse("3000", "m", "px");
    }

    [SkippableFact]
    public async Task Without_a_pipe_token_share_is_chisel_only_with_the_old_output()
    {
        using var t = new TestEnv(withChisel: true);
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[web] connected"), "chisel lane connected");
        await t.AssertEchoes("web", "plain");
        cts.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.DoesNotContain("[chisel", t.AllOutput); Assert.DoesNotContain("pipe", t.AllOutput); Assert.DoesNotContain("lanes:", t.AllOutput);
        Assert.False(t.Bus.Has("web"));
    }

    [SkippableFact]
    public async Task Stop_unregisters_both_lanes()
    {
        using var t = new TestEnv(withChisel: true); using var p = new FakePipeBus(); ConfigurePipe(t, p);
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[chisel:web] connected") && t.AllOutput.Contains("[pipe:web] connected"), "both lanes connected");
        Assert.Equal(0, await t.Run(default, "stop", "web"));
        Assert.False(t.Bus.Has("web")); Assert.False(p.Has("web"));
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task Both_buses_connect_with_one_command_and_the_same_name_and_metadata()
    {
        using var t = new TestEnv(withChisel: true); using var p = new FakePipeBus(); ConfigurePipe(t, p);
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web", "--description", "same text", "--label", "Web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[chisel:web] connected") && t.AllOutput.Contains("[pipe:web] connected"), "both lanes connected");
        Assert.Contains("https://web.ref12.dev/", t.AllOutput);
        Assert.Contains("https://p-web.ref12.dev/", t.AllOutput);
        await t.AssertEchoes("web", "via chisel");
        Assert.True(p.IsConnected("web"));
        var chiselBody = t.Bus.Calls.Single(c => c.Method == "POST").Body;
        Assert.Equal(chiselBody, p.Bodies["web"]); // same name and metadata on both registrations
        Assert.Contains("same text", chiselBody);
        cts.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(t.Bus.Has("web")); Assert.False(p.Has("web")); // both unregistered
    }

    [SkippableFact]
    public async Task One_bus_down_does_not_stop_the_other_and_it_joins_when_it_comes_back()
    {
        using var t = new TestEnv(withChisel: true); using var p = new FakePipeBus(); ConfigurePipe(t, p);
        p.Down = true;
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[chisel:web] connected"), "chisel lane up while pipe is down");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[pipe:web]") && t.AllOutput.Contains("retrying"), "pipe lane retrying");
        await t.AssertEchoes("web", "chisel works alone");
        Assert.DoesNotContain("[pipe:web] connected", t.AllOutput);
        p.Down = false;
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[pipe:web] connected"), "pipe lane joins");
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task The_chisel_bus_down_does_not_stop_the_pipe_lane()
    {
        using var t = new TestEnv(withChisel: true); using var p = new FakePipeBus(); ConfigurePipe(t, p);
        t.Bus.Dispose(); // the container bus is gone
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"127.0.0.1:{port}", "--name", "web");
        await TestEnv.WaitFor(() => t.AllOutput.Contains("[pipe:web] connected"), "pipe lane up while chisel bus is down");
        Assert.True(p.IsConnected("web"));
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task Tokens_are_scoped_per_bus_and_per_name()
    {
        using var t = new TestEnv(withChisel: true); using var p = new FakePipeBus(); ConfigurePipe(t, p);
        var port = t.StartEcho();
        var (task, cts) = Start(t, "share", $"web=127.0.0.1:{port}", $"api=127.0.0.1:{port}");
        await TestEnv.WaitFor(() => p.IsConnected("web") && p.IsConnected("api"), "both names on the pipe bus");
        var webTok = p.Tokens["web"]; var apiTok = p.Tokens["api"];
        cts.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        // the container bus saw its admin token, never the pipe bus's; the pipe bus the reverse
        Assert.All(t.Bus.Calls.Where(c => c.Headers.ContainsKey("Authorization")), c => Assert.Equal("Bearer " + FakeBus.Admin, c.Headers["Authorization"]));
        Assert.DoesNotContain(p.SeenAuthHeaders, h => h.Contains(FakeBus.Admin));
        Assert.DoesNotContain(t.Bus.Calls, c => c.Headers.Values.Any(v => v.Contains(FakePipeBus.Admin)));
        Assert.DoesNotContain("access-id-SECRET-ID", string.Join("\n", p.SeenAuthHeaders)); // no Access credentials to the connect path
        Assert.NotEqual(webTok, apiTok);
        // a socket with another name's token is refused (re-register so the name exists)
        using (var hc = new HttpClient()) { hc.DefaultRequestHeaders.Add("Authorization", "Bearer " + FakePipeBus.Admin); await hc.PostAsync(p.Url + "/_api/register", new StringContent("{\"name\":\"web\"}")); }
        using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Authorization", "Bearer " + apiTok);
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(p.Url.Replace("http", "ws") + "/_bus/ws/web"), default));
        Assert.Contains("web", p.SocketAuthFailures);
        // secrets stay out of the output
        Assert.DoesNotContain(FakePipeBus.Admin, t.AllOutput);
        Assert.DoesNotContain(FakeBus.Admin, t.AllOutput);
    }
}
