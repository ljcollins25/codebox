using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace R2Pipe;

/// <summary>
/// Named secrets in secrets.json under the state folder. Windows: each value is encrypted with DPAPI (current user).
/// Linux/macOS: the file has mode 0600 and holds the values as they are.
/// </summary>
internal sealed class SecretStore(string path, bool? useDpapi = null)
{
    private readonly bool _dpapi = useDpapi ?? OperatingSystem.IsWindows();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("r2pipe-v1");

    public string? Get(string name)
    {
        if (!Read().TryGetValue(name, out var v)) return null;
        if (!_dpapi) return v;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public void Set(string name, string value)
    {
        var all = Read();
        all[name] = _dpapi ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser)) : value;
        Write(all);
    }

    public void Delete(string name) { var all = Read(); if (all.Remove(name)) Write(all); }

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

/// <summary>What calls to the Worker need: the Access service token pair (env first, then the store), or a bearer token for workers.dev testing.</summary>
internal sealed class Credentials(Func<string, string?> getEnv, SecretStore store)
{
    public const string IdKey = "access_client_id", SecretKey = "access_client_secret", UrlKey = "url", TokenKey = "bearer_token";
    public SecretStore Store => store;

    public string Url => First(getEnv("R2PIPE_URL"), store.Get(UrlKey)) ?? "https://pipe.ref12.dev";

    public Dictionary<string, string> Headers()
    {
        var id = First(getEnv("CF_ACCESS_CLIENT_ID"), store.Get(IdKey));
        var secret = First(getEnv("CF_ACCESS_CLIENT_SECRET"), store.Get(SecretKey));
        var h = new Dictionary<string, string>();
        if (id != null && secret != null) { h["CF-Access-Client-Id"] = id; h["CF-Access-Client-Secret"] = secret; }
        else if (id != null || secret != null) throw new UserError("Set both CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET (or run: r2pipe login).");
        var bearer = First(getEnv("R2PIPE_TOKEN"), store.Get(TokenKey));
        if (bearer != null) h["Authorization"] = "Bearer " + bearer;
        if (h.Count == 0) throw new UserError("No credentials. Set CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET, or run: r2pipe login");
        return h;
    }

    public static string StateDir(Func<string, string?> getEnv)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(getEnv("LOCALAPPDATA") ?? Path.GetTempPath(), "r2pipe");
        var cfg = getEnv("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(getEnv("HOME") ?? Path.GetTempPath(), ".config");
        return Path.Combine(cfg, "r2pipe");
    }

    private static string? First(params string?[] v) => v.FirstOrDefault(x => !string.IsNullOrEmpty(x));
}

internal sealed class UserError(string message) : Exception(message);
