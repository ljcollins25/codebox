using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tbus.Tests;

internal sealed record Call(string Method, string Path, Dictionary<string, string> Headers, string Body);

/// <summary>
/// A local HTTP server that speaks the router API (register, registry, unregister with the admin token as Bearer) and, like the real
/// bus, forwards /_chisel websockets to a chisel server. With a chisel server attached it keeps that server's authfile in step with the
/// registrations (each name gets a user that may open only R:0.0.0.0:&lt;its port&gt;), as the router does.
/// </summary>
internal sealed class FakeBus : IDisposable
{
    public const string Admin = "admin-token-SECRET-123";
    private readonly HttpListener _l = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _reg = new();
    private readonly ChiselServer? _chisel;
    public bool RequireAccess { get; set; }
    public List<Call> Calls { get; } = new();
    public string Url { get; }
    public string PasswordFor(string name) => "pw-" + name + "-SECRETPASS";

    public FakeBus(ChiselServer? chisel = null)
    {
        _chisel = chisel;
        Url = $"http://127.0.0.1:{ChiselServer.FreePort()}";
        _l.Prefixes.Add(Url + "/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    private Dictionary<string, (string, string[])> UsersLocked()
    {
        var u = new Dictionary<string, (string, string[])> { ["sentinel"] = ("x", ["^$"]) };
        foreach (var (name, port) in _reg) u["u-" + name] = (PasswordFor(name), [$"^R:0\\.0\\.0\\.0:{port}$"]);
        return u;
    }

    /// <summary>The bus restarts: registrations are gone and the chisel server comes back with an empty authfile.</summary>
    public async Task RestartAsync()
    {
        lock (_lock) { _reg.Clear(); _chisel?.WriteUsers(UsersLocked()); }
        if (_chisel != null) await _chisel.RestartAsync();
    }

    public bool Has(string name) { lock (_lock) return _reg.ContainsKey(name); }
    public int PortOf(string name) { lock (_lock) return _reg[name]; }
    public int Count(string method, string pathPrefix) { lock (_lock) return Calls.Count(c => c.Method == method && c.Path.StartsWith(pathPrefix)); }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch (Exception) { return; }
            if (c.Request.Url!.AbsolutePath == "/_chisel") _ = Task.Run(() => ProxyChisel(c));
            else _ = Task.Run(() => Handle(c));
        }
    }

    private static Dictionary<string, string> HeadersOf(HttpListenerRequest req) =>
        req.Headers.AllKeys.ToDictionary(k => k!, k => req.Headers[k]!, StringComparer.OrdinalIgnoreCase);

    private async Task ProxyChisel(HttpListenerContext c)
    {
        var headers = HeadersOf(c.Request);
        lock (_lock) Calls.Add(new Call(c.Request.HttpMethod, "/_chisel", headers, ""));
        if (RequireAccess && !headers.ContainsKey("CF-Access-Client-Id")) { c.Response.StatusCode = 403; c.Response.Close(); return; }
        if (_chisel == null || !c.Request.IsWebSocketRequest) { c.Response.StatusCode = 502; c.Response.Close(); return; }
        using var up = new ClientWebSocket();
        up.Options.AddSubProtocol("chisel-v3");
        try { await up.ConnectAsync(new Uri(_chisel.Url.Replace("http", "ws") + "/"), default); }
        catch (Exception) { c.Response.StatusCode = 502; c.Response.Close(); return; }
        var down = (await c.AcceptWebSocketAsync("chisel-v3")).WebSocket;
        async Task Pump(WebSocket from, WebSocket to)
        {
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    var r = await from.ReceiveAsync(new ArraySegment<byte>(buf), default);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    await to.SendAsync(new ArraySegment<byte>(buf, 0, r.Count), r.MessageType, r.EndOfMessage, default);
                }
            }
            catch (Exception) { }
        }
        await Task.WhenAny(Pump(down, up), Pump(up, down));
        up.Abort(); down.Abort();
    }

    private void Handle(HttpListenerContext c)
    {
        var req = c.Request;
        var body = new StreamReader(req.InputStream).ReadToEnd();
        var headers = HeadersOf(req);
        object? reply; var status = 200;
        lock (_lock)
        {
            Calls.Add(new Call(req.HttpMethod, req.Url!.AbsolutePath, headers, body));
            var path = req.Url.AbsolutePath;
            if (headers.GetValueOrDefault("Authorization") != "Bearer " + Admin) { status = 401; reply = new { error = "admin token required" }; }
            else if (req.HttpMethod == "POST" && path == "/_api/register")
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                if (!_reg.TryGetValue(name, out var port)) _reg[name] = port = ChiselServer.FreePort();
                _chisel?.WriteUsers(UsersLocked());
                reply = new { name, created = true, port, user = "u-" + name, password = PasswordFor(name) };
            }
            else if (req.HttpMethod == "PATCH" && path.StartsWith("/_api/register/"))
            {
                var name = path["/_api/register/".Length..];
                if (_reg.ContainsKey(name)) reply = new { name }; else { status = 404; reply = new { error = "not registered" }; }
            }
            else if (req.HttpMethod == "DELETE" && path.StartsWith("/_api/register/"))
            {
                var name = path["/_api/register/".Length..];
                if (_reg.Remove(name)) { _chisel?.WriteUsers(UsersLocked()); reply = new { removed = name }; } else { status = 404; reply = new { error = "not registered" }; }
            }
            else if (req.HttpMethod == "GET" && path == "/_api/registry") reply = _reg.Select(kv => new { name = kv.Key, port = kv.Value, up = true }).ToArray();
            else { status = 404; reply = new { error = "unknown" }; }
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
        c.Response.StatusCode = status; c.Response.ContentType = "application/json";
        c.Response.OutputStream.Write(bytes); c.Response.Close();
    }

    public void Dispose() { try { _l.Stop(); _l.Close(); } catch (Exception) { } }
}

internal sealed class TestEnv : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "tbus-test-" + Guid.NewGuid().ToString("N")[..8]);
    public ChiselServer? Chisel;
    public FakeBus Bus;
    public Dictionary<string, string> Env = new();
    public SyncWriter Out = new();
    public Func<string, bool, string?> Prompt = (_, _) => null;
    private readonly List<TcpListener> _targets = new();

    /// <summary>withChisel: a real chisel server sits behind the bus (the test is skipped when the binary cannot be had).</summary>
    public TestEnv(bool withChisel = false)
    {
        if (withChisel)
        {
            Skip.If(ChiselBinary.Locate() == null, ChiselBinary.WhyNot);
            Chisel = new ChiselServer(ChiselBinary.Locate()!, new() { ["sentinel"] = ("x", ["^$"]) });
            Chisel.StartAsync().GetAwaiter().GetResult();
        }
        Directory.CreateDirectory(Dir);
        Bus = new FakeBus(Chisel);
        Env["TUNNEL_BUS_URL"] = Bus.Url;
        Env["TUNNEL_BUS_ADMIN_TOKEN"] = FakeBus.Admin;
        Env["CF_ACCESS_CLIENT_ID"] = "access-id-SECRET-ID";
        Env["CF_ACCESS_CLIENT_SECRET"] = "access-secret-SECRET-VAL";
    }

    /// <summary>An echo server on this machine; returns its port.</summary>
    public int StartEcho()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); _targets.Add(l);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket s;
                try { s = await l.AcceptSocketAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () => { try { await ClientRig.Echo(s); } catch (Exception) { } finally { s.Dispose(); } });
            }
        });
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public Host Host() => new()
    {
        Out = Out, Err = Out, Home = Path.Combine(Dir, "home"), MachineName = "Dev-Box",
        GetEnv = k => Env.GetValueOrDefault(k) ?? (k == "PATH" ? Path.Combine(Dir, "bin") : null),
        Prompt = Prompt, PollInterval = TimeSpan.FromMilliseconds(200),
        BackoffStart = TimeSpan.FromMilliseconds(100), BackoffMax = TimeSpan.FromMilliseconds(400),
        KeepAlive = TimeSpan.FromSeconds(1),
        OpenUrl = u => Opened.Add(u),
    };
    public List<string> Opened = new();

    public Task<int> Run(CancellationToken ct, params string[] args) => App.RunAsync(args, Host(), ct);

    /// <summary>Connects to the public side of a registered name (the chisel server's port) and checks that the echo comes back.</summary>
    public async Task AssertEchoes(string name, string text)
    {
        var port = Bus.PortOf(name);
        for (var attempt = 0; ; attempt++)
        {
            using var c = new TcpClient();
            try
            {
                await c.ConnectAsync(IPAddress.Loopback, port);
                var s = c.GetStream();
                var data = Encoding.UTF8.GetBytes(text);
                await s.WriteAsync(data);
                var back = new byte[data.Length];
                await ClientRig.ReadExactly(s, back, back.Length).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(text, Encoding.UTF8.GetString(back));
                return;
            }
            catch (Exception e) when (attempt < 60 && (e is SocketException or EndOfStreamException or TimeoutException)) { await Task.Delay(100); }
        }
    }

    public static async Task WaitFor(Func<bool> cond, string what, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("timed out waiting for " + what); await Task.Delay(50); }
    }

    public int Count(string needle) { var s = AllOutput; var n = 0; for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++; return n; }

    public string AllOutput => Out.ToString();
    public void Dispose()
    {
        Bus.Dispose(); Chisel?.Dispose();
        foreach (var l in _targets) l.Stop();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class SyncWriter : StringWriter
{
    private readonly object _l = new();
    public override void Write(char value) { lock (_l) base.Write(value); }
    public override void Write(string? value) { lock (_l) base.Write(value); }
    public override void WriteLine(string? value) { lock (_l) base.WriteLine(value); }
    public override string ToString() { lock (_l) return base.ToString(); }
    public void Reset() { lock (_l) GetStringBuilder().Clear(); }
}
