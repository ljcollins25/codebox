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

    /// <summary>Dual mode: the pipe bus's own URL (its connect/registry host, workers.dev). Empty: no pipe bus configured.</summary>
    public string? PipeBus { get; set; } = "https://r2pipe.ref12cf.workers.dev";
    public bool ChiselConfigured => !string.IsNullOrEmpty(Bus) && !IsPipe;
    public AppConfig ForChisel() => new() { Bus = Bus, Domain = Domain, Kind = "chisel", ViewerSuffix = ViewerSuffix, PipeBus = PipeBus };
    public AppConfig ForPipe() => new() { Bus = PipeBus!.TrimEnd('/'), Domain = Domain, Kind = "pipe", ViewerSuffix = ViewerSuffix, PipeBus = PipeBus, Cutover = Cutover };

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
        if (string.Equals(host.GetEnv("TUNNEL_PIPE_CUTOVER"), "true", StringComparison.OrdinalIgnoreCase)) c.Cutover = true;
        var pb = host.GetEnv("TUNNEL_PIPE_URL"); if (!string.IsNullOrWhiteSpace(pb)) c.PipeBus = pb;
        c.PipeBus = string.IsNullOrWhiteSpace(c.PipeBus) ? null : c.PipeBus.TrimEnd('/');
        c.Bus = c.Bus.TrimEnd('/');
        return c;
    }

    public void Save(Host host)
    {
        Directory.CreateDirectory(host.Home);
        File.WriteAllText(host.ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Chisel bus: name.domain. Pipe bus (transition): p-name.domain; after the cutover the pipe takes name.domain (set Cutover).</summary>
    public bool Cutover { get; set; }
    public string PublicUrl(string name) => IsPipe && !Cutover ? $"https://p-{name}.{Domain}/" : $"https://{name}.{Domain}/";
}
