using System.Text;
using System.Text.RegularExpressions;

namespace Tbus;

/// <summary>One share: public name, and the host:port chisel's reverse remote should reach from this machine.</summary>
internal sealed record ShareSpec(string Name, string TargetHost, int TargetPort)
{
    public static readonly Regex NameRx = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    public const int MaxName = 40;
    public string Target => TargetHost.Contains(':') ? $"[{TargetHost}]:{TargetPort}" : $"{TargetHost}:{TargetPort}";

    /// <summary>
    /// Forms: "3000", "host:port", "[::1]:port", each optionally "name=" in front. defaultName (from --name) applies to a bare target.
    /// Without a name: &lt;machine&gt;-&lt;port&gt;.
    /// </summary>
    public static ShareSpec Parse(string arg, string machine, string? nameOverride = null)
    {
        string? name = nameOverride; var target = arg;
        var eq = arg.IndexOf('=');
        if (eq > 0) { name = arg[..eq]; target = arg[(eq + 1)..]; }
        string host; string portText;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0 || close + 1 >= target.Length || target[close + 1] != ':') throw new UserError($"Bad target '{target}'. Use PORT, HOST:PORT or [IPV6]:PORT.");
            host = target[1..close]; portText = target[(close + 2)..];
        }
        else if (target.Contains(':'))
        {
            var i = target.LastIndexOf(':');
            host = target[..i]; portText = target[(i + 1)..];
            if (host.Contains(':')) throw new UserError($"Bad target '{target}': write an IPv6 address as [addr]:port.");
        }
        else { host = "localhost"; portText = target; }
        if (host.Length == 0 || host.Any(char.IsWhiteSpace)) throw new UserError($"Bad host in '{target}'.");
        if (!int.TryParse(portText, out var port) || port < 1 || port > 65535) throw new UserError($"Bad port '{portText}' in '{target}' (1-65535).");
        name = name == null ? DefaultName(machine, port) : name.ToLowerInvariant();
        Validate(name);
        return new ShareSpec(name, host, port);
    }

    public static string DefaultName(string machine, int port)
    {
        var m = Regex.Replace(machine.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (m.Length == 0) m = "host";
        var suffix = "-" + port;
        if (m.Length + suffix.Length > MaxName) m = m[..(MaxName - suffix.Length)].Trim('-');
        return m + suffix;
    }

    public static void Validate(string name)
    {
        if (name.Length > MaxName || !NameRx.IsMatch(name))
            throw new UserError($"Bad name '{name}': lowercase letters, digits and single hyphens, up to {MaxName} characters (no '--').");
    }
}
