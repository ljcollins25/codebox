using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Tbus;

/// <summary>
/// Credential entry. Browser login shells out to cloudflared ('cloudflared access login' opens the browser, GitHub sign-in happens on
/// Cloudflare Access, the token transfer returns a JWT that cloudflared caches; 'cloudflared access token' prints it). tbus stores
/// that JWT and sends it as cf-access-token. Without cloudflared the service token remains the way.
/// </summary>
internal static class Login
{
    private static readonly Regex JwtRx = new(@"eyJ[\w-]+\.[\w-]+\.[\w-]+", RegexOptions.Compiled);

    public static async Task<int> RunAsync(string[] args, Host host, Log log, CancellationToken ct)
    {
        var (_, opts) = App.ParseOptions(args, [], ["service-token", "admin-token"]);
        var creds = new Credentials(host);
        if (opts.ContainsKey("admin-token"))
        {
            var t = host.Prompt("Bus admin token (hidden): ", true);
            if (string.IsNullOrWhiteSpace(t)) throw new UserError("No token entered.");
            creds.Store.Set(Credentials.AdminKey, t.Trim()); Log.Register(t.Trim());
            log.Info("admin token stored (" + (OperatingSystem.IsWindows() ? "DPAPI, current user" : "file with mode 0600") + ")");
            return 0;
        }
        if (opts.ContainsKey("service-token"))
        {
            var id = host.Prompt("Access client id (CF-Access-Client-Id): ", false);
            var secret = host.Prompt("Access client secret (hidden): ", true);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw new UserError("Both the client id and the secret are needed.");
            creds.Store.Set(Credentials.IdKey, id.Trim()); creds.Store.Set(Credentials.SecretKey, secret.Trim());
            Log.Register(id.Trim()); Log.Register(secret.Trim());
            log.Info("Access service token stored (" + (OperatingSystem.IsWindows() ? "DPAPI, current user" : "file with mode 0600") + ")");
            return 0;
        }
        return await BrowserLogin(host, log, creds, ct);
    }

    private static string? FindOnPath(Host host, string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in (host.GetEnv("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try { var f = Path.Combine(dir.Trim('"'), name + ext); if (File.Exists(f)) return f; } catch (ArgumentException) { }
            }
        return null;
    }

    private static async Task<int> BrowserLogin(Host host, Log log, Credentials creds, CancellationToken ct)
    {
        var cf = FindOnPath(host, "cloudflared")
            ?? throw new UserError("Browser login needs cloudflared (https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/), which is not on PATH. Install it, or use: tbus login --service-token");
        var app = AppConfig.Load(host).Bus;
        log.Info("starting 'cloudflared access login' - finish the GitHub sign-in in the browser");
        var (code, _) = await Run(cf, ["access", "login", app], echo: true, ct);
        if (code != 0) throw new UserError("cloudflared access login failed.");
        var (code2, output) = await Run(cf, ["access", "token", "-app=" + app], echo: false, ct);
        var jwt = JwtRx.Match(output).Value;
        if (code2 != 0 || jwt.Length == 0) throw new UserError("cloudflared did not return a token for " + app);
        creds.Store.Set(Credentials.JwtKey, jwt); Log.Register(jwt);
        var exp = Jwt.Expiry(jwt);
        log.Info("browser login stored" + (exp != null ? $" (valid until {exp:u})" : "") + ". The admin token is still needed to register: tbus login --admin-token");
        return 0;
    }

    /// <summary>Runs a program, printing its output with any JWT masked; returns the exit code and the raw output.</summary>
    private static async Task<(int, string)> Run(string exe, string[] args, bool echo, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        void Line(string? l) { if (l == null) return; lock (sb) sb.AppendLine(l); if (echo) Console.Error.WriteLine(JwtRx.Replace(l, "***")); }
        p.OutputDataReceived += (_, e) => Line(e.Data); p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, sb.ToString());
    }
}
