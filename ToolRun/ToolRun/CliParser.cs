namespace ToolRun;

public enum Mode { Run, Which, List, Clean, Help, Version }

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

public sealed class CliOptions
{
    public Mode Mode { get; set; } = Mode.Run;
    public string? Package { get; set; }
    /// <summary>Version from package@version; null means "latest (or what is cached)".</summary>
    public string? PackageVersion { get; set; }
    public List<string> Sources { get; } = new();
    public List<string> RuntimeSources { get; } = new();
    public bool Update { get; set; }
    public bool NoDownloadRuntime { get; set; }
    public bool NoApphost { get; set; }
    /// <summary>Use an installed .NET when it matches the tool's framework line, instead of toolrun's own private runtime.</summary>
    public bool PreferInstalled { get; set; }
    public bool Prerelease { get; set; }
    public bool Verbose { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? Command { get; set; }
    public string? Home { get; set; }
    public string? ConfigFile { get; set; }
    /// <summary>Everything after the toolrun options: handed to the tool unchanged.</summary>
    public List<string> ToolArgs { get; } = new();
}

/// <summary>
/// toolrun [options] &lt;package&gt;[@version] [toolrun options] [--] &lt;tool args...&gt;
/// Toolrun options are recognised before the package and directly after it. The tool's arguments start at the first
/// token that is not a toolrun option, or after "--" (which is itself dropped). Nothing after that point is interpreted.
/// </summary>
public static class CliParser
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions();
        int i = 0;
        bool haveMode = false;

        string Value(string name, string? inline)
        {
            if (inline is not null) return inline;
            if (i + 1 >= args.Count) throw new UsageException($"Option {name} needs a value.");
            return args[++i];
        }

        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                o.ToolArgs.AddRange(args.Skip(i + 1));
                break;
            }
            if (a.Length > 1 && a[0] == '-')
            {
                string name = a, inline = null!;
                var eq = a.StartsWith("--") ? a.IndexOf('=') : -1;
                if (eq > 0) { name = a[..eq]; inline = a[(eq + 1)..]; }
                bool known = true;
                switch (name)
                {
                    case "--source": o.Sources.Add(Value(name, inline)); break;
                    case "--runtime-source": o.RuntimeSources.Add(Value(name, inline)); break;
                    case "--config": o.ConfigFile = Value(name, inline); break;
                    case "--home": o.Home = Value(name, inline); break;
                    case "--command": o.Command = Value(name, inline); break;
                    case "--runtime-version": o.RuntimeVersion = Value(name, inline); break;
                    case "--update": o.Update = true; break;
                    case "--prerelease": o.Prerelease = true; break;
                    case "--no-download-runtime": o.NoDownloadRuntime = true; break;
                    case "--no-apphost": o.NoApphost = true; o.PreferInstalled = true; break;
                    case "--prefer-installed": o.PreferInstalled = true; break;
                    case "--verbose": o.Verbose = true; break;
                    default:
                        known = false;
                        break;
                }
                if (known) continue;

                // modes: only meaningful before a package has been named (afterwards the token belongs to the tool)
                if (o.Package is null && !haveMode)
                {
                    switch (name)
                    {
                        case "--list": o.Mode = Mode.List; haveMode = true; continue;
                        case "--help": case "-h": case "-?": o.Mode = Mode.Help; haveMode = true; continue;
                        case "--version": o.Mode = Mode.Version; haveMode = true; continue;
                        case "--which":
                            o.Mode = Mode.Which; haveMode = true;
                            SetPackage(o, Value(name, inline));
                            continue;
                        case "--clean":
                            o.Mode = Mode.Clean; haveMode = true;
                            if (inline is not null) SetPackage(o, inline);
                            else if (i + 1 < args.Count && !args[i + 1].StartsWith('-')) SetPackage(o, args[++i]);
                            continue;
                    }
                }
                if (o.Package is null) throw new UsageException($"Unknown option {a}. Run 'toolrun --help'.");
                o.ToolArgs.AddRange(args.Skip(i));
                break;
            }
            // a plain word
            if (o.Package is null && o.Mode is Mode.Run) { SetPackage(o, a); continue; }
            o.ToolArgs.AddRange(args.Skip(i));
            break;
        }

        if (o.Mode is Mode.Run && o.Package is null) o.Mode = Mode.Help;
        if (o.RuntimeVersion is not null && !Tool2App.SemVer.TryParse(o.RuntimeVersion, out _))
            throw new UsageException($"--runtime-version '{o.RuntimeVersion}' is not a version.");
        return o;
    }

    private static void SetPackage(CliOptions o, string text)
    {
        var at = text.IndexOf('@');
        if (at == 0) throw new UsageException($"'{text}' has no package id.");
        if (at < 0) { o.Package = text; return; }
        o.Package = text[..at];
        var v = text[(at + 1)..];
        if (v.Length == 0 || v.Equals("latest", StringComparison.OrdinalIgnoreCase)) return;
        if (!Tool2App.SemVer.TryParse(v, out _)) throw new UsageException($"'{v}' is not a valid version (in '{text}').");
        o.PackageVersion = v;
    }
}
