using System.Text;

namespace Tbus;

/// <summary>Everything the app touches from the outside, so tests can swap it: output, environment, state folder, timing.</summary>
internal sealed class Host
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;
    /// <summary>State folder: config.json, secrets, cached chisel. TBUS_HOME overrides it.</summary>
    public string Home { get; init; } = DefaultHome(Environment.GetEnvironmentVariable);
    /// <summary>Prompts and reads one line; hidden = no echo. Null when no console input.</summary>
    public Func<string, bool, string?> Prompt { get; init; } = ConsolePrompt;
    public string MachineName { get; init; } = Environment.MachineName;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BackoffStart { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);
    public Action<string> OpenUrl { get; init; } = Browser.Open;

    public static string DefaultHome(Func<string, string?> env)
    {
        var h = env("TBUS_HOME");
        if (!string.IsNullOrWhiteSpace(h)) return h;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tbus");
    }

    public string ConfigPath => Path.Combine(Home, "config.json");
    public string SecretsPath => Path.Combine(Home, "secrets.json");

    private static readonly object ConsoleLock = new();
    private static string? ConsolePrompt(string prompt, bool hidden)
    {
        if (Console.IsInputRedirected && !hidden) { return Console.In.ReadLine(); }
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        var sb = new StringBuilder();
        lock (ConsoleLock)
        {
            while (true)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; if (!hidden) Console.Error.Write("\b \b"); } continue; }
                if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); if (!hidden) Console.Error.Write(k.KeyChar); }
            }
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}

/// <summary>All output goes through here so known secrets are masked even if a library or child process echoes one.</summary>
internal sealed class Log(Host host)
{
    private static readonly List<string> Secrets = new();
    private static readonly object L = new();

    public static void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
        lock (L) { if (!Secrets.Contains(secret)) Secrets.Add(secret); }
    }

    public static string Redact(string s)
    {
        string[] snapshot; lock (L) snapshot = Secrets.ToArray();
        foreach (var x in snapshot) s = s.Replace(x, "***");
        return s;
    }

    public void Info(string msg) { lock (L) host.Out.WriteLine(Redact(msg)); }
    public void Error(string msg) { lock (L) host.Err.WriteLine(Redact(msg)); }
}

internal static class Browser
{
    public static void Open(string url)
    {
        if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", url);
        else System.Diagnostics.Process.Start("xdg-open", url);
    }
}
