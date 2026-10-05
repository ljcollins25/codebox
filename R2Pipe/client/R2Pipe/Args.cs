namespace R2Pipe;

/// <summary>Minimal option parsing: positional values, --flag value, --flag=value, -o value, bare -.</summary>
internal sealed class Args
{
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new() { "quiet", "q", "help", "h", "service-token" };

    public Args(IEnumerable<string> args)
    {
        var a = args.ToList();
        for (int i = 0; i < a.Count; i++)
        {
            var s = a[i];
            if (s == "-" || !s.StartsWith('-')) { Positional.Add(s); continue; }
            var key = s.TrimStart('-');
            string? val = null;
            int eq = key.IndexOf('=');
            if (eq >= 0) { val = key[(eq + 1)..]; key = key[..eq]; }
            else if (!Switches.Contains(key))
            {
                if (i + 1 >= a.Count) throw new UserError($"Option {s} needs a value.");
                val = a[++i];
            }
            _opts[key] = val ?? "true";
        }
    }

    public string? Get(params string[] names) { foreach (var n in names) if (_opts.TryGetValue(n, out var v)) return v; return null; }
    public bool Has(params string[] names) => names.Any(_opts.ContainsKey);
    public int GetInt(string name, int def) => Get(name) is { } v ? (int.TryParse(v, out var i) && i > 0 ? i : throw new UserError($"--{name} must be a positive number.")) : def;
}

internal static class Sizes
{
    /// <summary>"32M", "16MiB", "1G", "65536".</summary>
    public static long Parse(string s)
    {
        s = s.Trim().ToLowerInvariant().Replace("ib", "").Replace("b", "");
        long mul = 1;
        if (s.EndsWith('k')) { mul = 1L << 10; s = s[..^1]; }
        else if (s.EndsWith('m')) { mul = 1L << 20; s = s[..^1]; }
        else if (s.EndsWith('g')) { mul = 1L << 30; s = s[..^1]; }
        if (!long.TryParse(s, out var n) || n <= 0) throw new UserError("Bad size: " + s);
        return n * mul;
    }
}
