using System.Net;
using System.Text;
using System.Text.Json;

namespace Tbus.Tests;

internal sealed record Call(string Method, string Path, Dictionary<string, string> Headers, string Body);

/// <summary>A local HTTP server that speaks the router API: register, registry, unregister (admin token as Bearer).</summary>
internal sealed class FakeBus : IDisposable
{
    public const string Admin = "admin-token-SECRET-123";
    private readonly HttpListener _l = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _reg = new();
    private int _next = 20000;
    public List<Call> Calls { get; } = new();
    public string Url { get; }
    public string PasswordFor(string name) => "pw-" + name + "-SECRETPASS";

    public FakeBus()
    {
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); t.Start();
        var port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        Url = $"http://127.0.0.1:{port}";
        _l.Prefixes.Add(Url + "/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    public void Restart() { lock (_lock) _reg.Clear(); }
    public bool Has(string name) { lock (_lock) return _reg.ContainsKey(name); }
    public int Count(string method, string pathPrefix) { lock (_lock) return Calls.Count(c => c.Method == method && c.Path.StartsWith(pathPrefix)); }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch (Exception) { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private void Handle(HttpListenerContext c)
    {
        var req = c.Request;
        var body = new StreamReader(req.InputStream).ReadToEnd();
        var headers = req.Headers.AllKeys.ToDictionary(k => k!, k => req.Headers[k]!, StringComparer.OrdinalIgnoreCase);
        object? reply; var status = 200;
        lock (_lock)
        {
            Calls.Add(new Call(req.HttpMethod, req.Url!.AbsolutePath, headers, body));
            var path = req.Url.AbsolutePath;
            if (headers.GetValueOrDefault("Authorization") != "Bearer " + Admin) { status = 401; reply = new { error = "admin token required" }; }
            else if (req.HttpMethod == "POST" && path == "/_api/register")
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                if (!_reg.TryGetValue(name, out var port)) _reg[name] = port = _next++;
                reply = new { name, created = true, port, user = "u-" + name, password = PasswordFor(name) };
            }
            else if (req.HttpMethod == "DELETE" && path.StartsWith("/_api/register/"))
            {
                var name = path["/_api/register/".Length..];
                if (_reg.Remove(name)) reply = new { removed = name }; else { status = 404; reply = new { error = "not registered" }; }
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

/// <summary>
/// A fake chisel: a shell script on Linux/macOS, a .cmd on Windows. It appends its arguments, its AUTH variable and whether the admin
/// token leaked into its environment to a log file, prints chisel's "Connected" line, then stays running (or exits at once).
/// </summary>
internal static class FakeChisel
{
    public static string Create(string dir, string log, bool exitImmediately = false)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            var f = Path.Combine(dir, "chisel.cmd");
            File.WriteAllText(f, string.Join("\r\n", [
                // Two chisel clients start at the same moment; cmd's `>>` fails with a sharing violation when another process has the
                // log open. So write the whole record in one redirected block and retry until the append succeeds.
                "@echo off",
                ":retry",
                "(",
                "echo ARGS: %*",
                "echo AUTH=%AUTH%",
                "if defined TUNNEL_BUS_ADMIN_TOKEN (echo ADMIN=leaked) else (echo ADMIN=unset)",
                $") >> \"{log}\" 2>nul || (ping -n 1 127.0.0.1 >nul & goto retry)",
                "echo client: Connected (Latency 1ms) 1>&2",
                exitImmediately ? "exit /b 1" : "ping -n 600 127.0.0.1 >nul", ""]));
            return f;
        }
        var sh = Path.Combine(dir, "chisel");
        File.WriteAllText(sh, string.Join("\n", [
            "#!/bin/sh",
            $"echo \"ARGS: $*\" >> '{log}'",
            $"echo \"AUTH=$AUTH\" >> '{log}'",
            $"echo \"ADMIN=${{TUNNEL_BUS_ADMIN_TOKEN:-unset}}\" >> '{log}'",
            "echo 'client: Connected (Latency 1ms)' >&2",
            exitImmediately ? "exit 1" : "exec sleep 600", ""]));
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    public static List<string> Starts(string log) => File.Exists(log) ? ReadShared(log).Where(l => l.StartsWith("ARGS:")).ToList() : new();
    public static string[] ReadShared(string log)
    {
        try { using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray(); }
        catch (IOException) { return []; }
    }
}

internal sealed class TestEnv : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "tbus-test-" + Guid.NewGuid().ToString("N")[..8]);
    public FakeBus Bus = new();
    public string ChiselLog;
    public string Chisel;
    public Dictionary<string, string> Env = new();
    public SyncWriter Out = new();
    public Func<string, bool, string?> Prompt = (_, _) => null;

    public TestEnv(bool chiselExits = false)
    {
        Directory.CreateDirectory(Dir);
        ChiselLog = Path.Combine(Dir, "chisel.log");
        Chisel = FakeChisel.Create(Path.Combine(Dir, "bin"), ChiselLog, chiselExits);
        Env["TBUS_CHISEL"] = Chisel;
        Env["TUNNEL_BUS_URL"] = Bus.Url;
        Env["TUNNEL_BUS_ADMIN_TOKEN"] = FakeBus.Admin;
        Env["CF_ACCESS_CLIENT_ID"] = "access-id-SECRET-ID";
        Env["CF_ACCESS_CLIENT_SECRET"] = "access-secret-SECRET-VAL";
    }

    public Host Host() => new()
    {
        Out = Out, Err = Out, Home = Path.Combine(Dir, "home"), MachineName = "Dev-Box",
        GetEnv = k => Env.GetValueOrDefault(k) ?? (k == "PATH" ? Path.Combine(Dir, "bin") : null),
        Prompt = Prompt, PollInterval = TimeSpan.FromMilliseconds(200),
        BackoffStart = TimeSpan.FromMilliseconds(100), BackoffMax = TimeSpan.FromMilliseconds(400),
        OpenUrl = u => Opened.Add(u),
    };
    public List<string> Opened = new();

    public Task<int> Run(CancellationToken ct, params string[] args) => App.RunAsync(args, Host(), ct);

    public static async Task WaitFor(Func<bool> cond, string what, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("timed out waiting for " + what); await Task.Delay(50); }
    }

    public string AllOutput => Out.ToString();
    public void Dispose() { Bus.Dispose(); try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
