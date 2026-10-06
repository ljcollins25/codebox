using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tbus;

/// <summary>
/// Stores named secrets in secrets.json under the state folder. Windows: each value is encrypted with DPAPI (current user).
/// Linux/macOS: the file is created with mode 0600 (owner only) and holds the values as they are; no keychain dependency.
/// </summary>
internal sealed class SecretStore(string path, bool? useDpapi = null)
{
    private readonly bool _dpapi = useDpapi ?? OperatingSystem.IsWindows();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("tbus-v1");

    public string? Get(string name)
    {
        var all = Read();
        if (!all.TryGetValue(name, out var v)) return null;
        if (!_dpapi) return v;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public void Set(string name, string value)
    {
        var all = Read();
        all[name] = _dpapi
            ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser))
            : value;
        Write(all);
    }

    public void Delete(string name)
    {
        var all = Read();
        if (all.Remove(name)) Write(all);
    }

    private Dictionary<string, string> Read()
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { }
        return new();
    }

    private void Write(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, opts))
        using (var w = new StreamWriter(fs)) w.Write(JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>What the bus calls need: the admin token (registration) and Access credentials (service token or browser JWT).</summary>
internal sealed class Credentials(Host host)
{
    public const string AdminKey = "admin_token", IdKey = "access_client_id", SecretKey = "access_client_secret", JwtKey = "access_jwt";
    private readonly SecretStore _store = new(host.SecretsPath);
    public SecretStore Store => _store;

    public string? AdminToken => First(host.GetEnv("TUNNEL_BUS_ADMIN_TOKEN"), _store.Get(AdminKey));

    public const string PipeAdminKey = "pipe_admin_token";
    /// <summary>Admin token of the pipe bus (its own registry; never the container bus's token).</summary>
    public string? PipeAdminToken => First(host.GetEnv("TUNNEL_PIPE_ADMIN_TOKEN"), _store.Get(PipeAdminKey));

    /// <summary>Headers Access wants: the service token pair, else the browser-login JWT (cf-access-token).</summary>
    public Dictionary<string, string> AccessHeaders()
    {
        var id = First(host.GetEnv("CF_ACCESS_CLIENT_ID"), _store.Get(IdKey));
        var secret = First(host.GetEnv("CF_ACCESS_CLIENT_SECRET"), _store.Get(SecretKey));
        var h = new Dictionary<string, string>();
        if (id != null && secret != null)
        {
            h["CF-Access-Client-Id"] = id; h["CF-Access-Client-Secret"] = secret;
            Log.Register(id); Log.Register(secret);
        }
        else if (id != null || secret != null) throw new UserError("Set both CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET (or run: tbus login --service-token).");
        else
        {
            var jwt = _store.Get(JwtKey);
            if (jwt != null && !Jwt.Expired(jwt)) { h["cf-access-token"] = jwt; Log.Register(jwt); }
        }
        return h;
    }

    public string RequireAdminToken()
    {
        var t = AdminToken ?? throw new UserError("No bus admin token. Set TUNNEL_BUS_ADMIN_TOKEN or run: tbus login --admin-token");
        Log.Register(t);
        return t;
    }

    private static string? First(params string?[] v) => v.FirstOrDefault(x => !string.IsNullOrEmpty(x));
}

internal sealed class UserError(string message) : Exception(message);

internal static class Jwt
{
    public static DateTimeOffset? Expiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.'); if (parts.Length < 2) return null;
            var s = parts[1].Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            return doc.RootElement.TryGetProperty("exp", out var e) ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()) : null;
        }
        catch (Exception) { return null; }
    }
    public static bool Expired(string jwt) => Expiry(jwt) is { } e && e < DateTimeOffset.UtcNow.AddMinutes(1);
}
