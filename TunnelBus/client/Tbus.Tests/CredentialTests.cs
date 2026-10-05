using System.Text;
using System.Text.Json;
using Xunit;

namespace Tbus.Tests;

[Collection("serial")]
public class CredentialTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "tbus-cred-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void File_store_round_trips_and_is_owner_only_on_unix()
    {
        var dir = Temp(); var path = Path.Combine(dir, "secrets.json");
        try
        {
            var s = new SecretStore(path, useDpapi: false);
            Assert.Null(s.Get("a"));
            s.Set("a", "secret-A"); s.Set("b", "secret-B");
            Assert.Equal("secret-A", new SecretStore(path, false).Get("a"));
            Assert.Equal("secret-B", new SecretStore(path, false).Get("b"));
            s.Delete("a"); Assert.Null(s.Get("a")); Assert.Equal("secret-B", s.Get("b"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Default_store_on_this_platform_round_trips_and_on_windows_is_encrypted()
    {
        var dir = Temp(); var path = Path.Combine(dir, "secrets.json");
        try
        {
            var s = new SecretStore(path); // DPAPI on Windows, file elsewhere
            s.Set("token", "plain-text-value-123");
            Assert.Equal("plain-text-value-123", new SecretStore(path).Get("token"));
            var raw = File.ReadAllText(path);
            if (OperatingSystem.IsWindows()) Assert.DoesNotContain("plain-text-value-123", raw);
            else Assert.Contains("plain-text-value-123", raw);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Login_prompts_store_credentials_that_the_app_then_uses_and_env_wins()
    {
        using var t = new TestEnv();
        t.Env.Clear(); t.Env["TUNNEL_BUS_URL"] = t.Bus.Url;
        var answers = new Queue<string>(["stored-id-ABCD", "stored-secret-EFGH"]); var hidden = new List<bool>();
        t.Prompt = (_, h) => { hidden.Add(h); return answers.Dequeue(); };
        Assert.Equal(0, await t.Run(default, "login", "--service-token"));
        Assert.Equal([false, true], hidden); // the secret is read without echo
        t.Prompt = (_, h) => { Assert.True(h); return FakeBus.Admin; };
        Assert.Equal(0, await t.Run(default, "login", "--admin-token"));
        Assert.DoesNotContain("stored-secret-EFGH", t.AllOutput); Assert.DoesNotContain(FakeBus.Admin, t.AllOutput);

        Assert.Equal(0, await t.Run(default, "list")); // uses the stored admin token and service token
        var call = t.Bus.Calls.Last();
        Assert.Equal("stored-id-ABCD", call.Headers["CF-Access-Client-Id"]);
        Assert.Equal("stored-secret-EFGH", call.Headers["CF-Access-Client-Secret"]);

        t.Env["CF_ACCESS_CLIENT_ID"] = "env-id"; t.Env["CF_ACCESS_CLIENT_SECRET"] = "env-secret";
        Assert.Equal(0, await t.Run(default, "list"));
        Assert.Equal("env-id", t.Bus.Calls.Last().Headers["CF-Access-Client-Id"]);

        Assert.Equal(0, await t.Run(default, "logout"));
        t.Env.Remove("CF_ACCESS_CLIENT_ID"); t.Env.Remove("CF_ACCESS_CLIENT_SECRET");
        Assert.Equal(2, await t.Run(default, "list")); // admin token gone
    }

    [Fact]
    public async Task Config_sets_and_shows_bus_and_domain()
    {
        using var t = new TestEnv();
        t.Env.Clear();
        Assert.Equal(0, await t.Run(default, "config"));
        Assert.Contains("https://ctl.ref12.dev", t.AllOutput); Assert.Contains("ref12.dev", t.AllOutput);
        Assert.Equal(0, await t.Run(default, "config", "--bus", "bus.example.org/", "--domain", "Example.ORG"));
        Assert.Contains("bus:    https://bus.example.org", t.AllOutput);
        Assert.Contains("domain: example.org", t.AllOutput);
        t.Out.Reset();
        await t.Run(default, "config");
        Assert.Contains("https://bus.example.org", t.AllOutput);
        Assert.Equal(2, await t.Run(default, "config", "--bus", "ftp://x"));
    }

    private static string MakeJwt(DateTimeOffset exp)
    {
        static string B(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B("{\"alg\":\"none\"}") + "." + B(JsonSerializer.Serialize(new { exp = exp.ToUnixTimeSeconds() })) + ".sig";
    }

    [Fact]
    public void Jwt_expiry_is_read_and_expired_tokens_are_not_sent()
    {
        Assert.True(Jwt.Expired(MakeJwt(DateTimeOffset.UtcNow.AddMinutes(-5))));
        Assert.False(Jwt.Expired(MakeJwt(DateTimeOffset.UtcNow.AddHours(1))));
        using var t = new TestEnv(); t.Env.Remove("CF_ACCESS_CLIENT_ID"); t.Env.Remove("CF_ACCESS_CLIENT_SECRET");
        var creds = new Credentials(t.Host());
        creds.Store.Set(Credentials.JwtKey, MakeJwt(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(creds.AccessHeaders().ContainsKey("cf-access-token"));
        creds.Store.Set(Credentials.JwtKey, MakeJwt(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Empty(creds.AccessHeaders());
    }

    [Fact]
    public async Task Browser_login_shells_out_to_cloudflared_and_stores_the_jwt_without_printing_it()
    {
        using var t = new TestEnv(); t.Env.Remove("CF_ACCESS_CLIENT_ID"); t.Env.Remove("CF_ACCESS_CLIENT_SECRET");
        var jwt = MakeJwt(DateTimeOffset.UtcNow.AddHours(8)).Replace("sig", "abc");
        var bin = Path.Combine(t.Dir, "bin"); Directory.CreateDirectory(bin);
        if (OperatingSystem.IsWindows())
            File.WriteAllText(Path.Combine(bin, "cloudflared.cmd"), "@echo off\r\nif \"%2\"==\"login\" (echo Successfully fetched your token: " + jwt + ") else (echo " + jwt + ")\r\n");
        else
        {
            var f = Path.Combine(bin, "cloudflared");
            File.WriteAllText(f, "#!/bin/sh\nif [ \"$2\" = login ]; then echo \"Successfully fetched your token: " + jwt + "\"; else echo " + jwt + "; fi\n");
            File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Equal(0, await t.Run(default, "login"));
        Assert.Equal(jwt, new Credentials(t.Host()).Store.Get(Credentials.JwtKey));
        Assert.DoesNotContain(jwt, t.AllOutput);
        Assert.Equal("cf-access-token", new Credentials(t.Host()).AccessHeaders().Keys.Single());
    }

    [Fact]
    public async Task Browser_login_without_cloudflared_explains_the_alternative()
    {
        using var t = new TestEnv();
        t.Env["PATH"] = Path.Combine(t.Dir, "empty");
        Assert.Equal(2, await t.Run(default, "login"));
        Assert.Contains("cloudflared", t.AllOutput); Assert.Contains("--service-token", t.AllOutput);
    }
}
