using System.Text.Json;

namespace Tbus;

internal sealed class AppConfig
{
    public string Bus { get; set; } = "https://ctl.ref12.dev";
    public string Domain { get; set; } = "ref12.dev";

    public static AppConfig Load(Host host)
    {
        var c = new AppConfig();
        try { if (File.Exists(host.ConfigPath)) c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(host.ConfigPath)) ?? c; } catch (JsonException) { }
        var bus = host.GetEnv("TUNNEL_BUS_URL"); if (!string.IsNullOrWhiteSpace(bus)) c.Bus = bus;
        var dom = host.GetEnv("TUNNEL_BUS_DOMAIN"); if (!string.IsNullOrWhiteSpace(dom)) c.Domain = dom;
        c.Bus = c.Bus.TrimEnd('/');
        return c;
    }

    public void Save(Host host)
    {
        Directory.CreateDirectory(host.Home);
        File.WriteAllText(host.ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string PublicUrl(string name) => $"https://{name}.{Domain}/";
}
