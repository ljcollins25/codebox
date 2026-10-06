using System.Text.Json;

namespace Tbus;

internal sealed class AppConfig
{
    public string Bus { get; set; } = "https://ctl.ref12.dev";
    public string Domain { get; set; } = "ref12.dev";
    /// <summary>"chisel" (the container bus) or "pipe" (the Worker-only bus). Empty: pipe when the bus URL is a workers.dev address.</summary>
    public string? Kind { get; set; }
    /// <summary>Pipe bus: the host suffix viewers use, "&lt;name&gt;--&lt;suffix&gt;". Empty after the cutover (then "&lt;name&gt;.&lt;domain&gt;").</summary>
    public string? ViewerSuffix { get; set; } = "pipe.ref12.dev";

    public bool IsPipe => string.Equals(Kind, "pipe", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(Kind) && Uri.TryCreate(Bus, UriKind.Absolute, out var u) && u.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase));
    public string ProviderSocketUrl(string path) => Bus.Replace("https://", "wss://").Replace("http://", "ws://") + path;

    public static AppConfig Load(Host host)
    {
        var c = new AppConfig();
        try { if (File.Exists(host.ConfigPath)) c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(host.ConfigPath)) ?? c; } catch (JsonException) { }
        var bus = host.GetEnv("TUNNEL_BUS_URL"); if (!string.IsNullOrWhiteSpace(bus)) c.Bus = bus;
        var dom = host.GetEnv("TUNNEL_BUS_DOMAIN"); if (!string.IsNullOrWhiteSpace(dom)) c.Domain = dom;
        var kind = host.GetEnv("TUNNEL_BUS_KIND"); if (!string.IsNullOrWhiteSpace(kind)) c.Kind = kind.Trim().ToLowerInvariant();
        var vs = host.GetEnv("TUNNEL_BUS_VIEWER_SUFFIX"); if (vs != null && !string.IsNullOrWhiteSpace(vs)) c.ViewerSuffix = vs.Trim().Trim('.').ToLowerInvariant();
        c.Bus = c.Bus.TrimEnd('/');
        return c;
    }

    public void Save(Host host)
    {
        Directory.CreateDirectory(host.Home);
        File.WriteAllText(host.ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string PublicUrl(string name) => IsPipe && !string.IsNullOrEmpty(ViewerSuffix) ? $"https://{name}--{ViewerSuffix}/" : $"https://{name}.{Domain}/";
}
