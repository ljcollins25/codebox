using System.Reflection;
using Tool2App;

namespace ToolRun;

public static class Program
{
    private const string Usage = """
        toolrun - download and run a .NET tool from NuGet (no 'dotnet tool install', .NET need not be installed)
        The tool runs through the real .NET host (an apphost for the tool) on toolrun's own private runtime, so no .NET needs to be installed.

        usage:  toolrun <package>[@version] [toolrun options] [--] <tool args...>

          toolrun dotnet-outdated-tool -- --help
          toolrun dotnet-counters@8.0.547301 ps

        Toolrun options are read before the package and right after it; the tool's arguments start at the first
        other token or after "--" and are passed on unchanged. Use "--" whenever a tool argument looks like a toolrun option.

        options:
          --update                  look for a newer version of the tool (default: use what is cached, no network)
          --prerelease              allow prerelease tool and runtime versions
          --source <url>            package source (repeatable); .json = NuGet v3, otherwise v2. Default: nearby nuget.config, else nuget.org (v3, then v2)
          --runtime-source <url>    where runtime and host packs come from (default: the same sources)
          --runtime-version <v>     also put this shared runtime version in the private runtime folder (added to the private runtime folder; downloaded once)
          --no-download-runtime     never download a runtime pack (ASP.NET Core, --runtime-version, or a build without an embedded runtime)
          --no-apphost              run 'dotnet <entry.dll>' instead of an apphost (installed .NET; implies --prefer-installed)
          --prefer-installed        use an installed .NET when it has the tool's framework line, instead of toolrun's private runtime
          --command <name>          choose a command when the package has several
          --config <file>           config file (default <home>/config.json): { "sources": [...], "runtimeSources": [...] }
          --home <dir>              cache folder (default %LOCALAPPDATA%\toolrun, ~/.local/share/toolrun; env TOOLRUN_HOME)
          --verbose                 say what is being decided

        other commands:
          toolrun --list            cached tools and runtimes with sizes
          toolrun --clean [id[@version]]   remove one tool (or version), or everything
          toolrun --which <package>[@version]   print the command line toolrun would run
          toolrun --version | --help

        toolrun's own errors exit with 1 and start with "toolrun:"; otherwise the exit code is the tool's.
        """;

    public static int Main(string[] args)
    {
        CliOptions o;
        try { o = CliParser.Parse(args); }
        catch (UsageException ex) { Console.Error.WriteLine("toolrun: " + ex.Message); return 2; }

        void Info(string m) => Console.Error.WriteLine("toolrun: " + m);
        void Debug(string m) { if (o.Verbose) Console.Error.WriteLine("toolrun: " + m); }
        using var cts = new CancellationTokenSource();
        try
        {
            switch (o.Mode)
            {
                case Mode.Help: Console.Out.WriteLine(Usage); return args.Length == 0 ? 2 : 0;
                case Mode.Version:
                    Console.Out.WriteLine("toolrun " + (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0"));
                    return 0;
                case Mode.List: return Commands.List(ToolRunHome.Default(o.Home), Console.Out);
                case Mode.Clean: return Commands.Clean(ToolRunHome.Default(o.Home), o.Package, o.PackageVersion, Console.Out);
            }
            var app = new ToolRunApp(o, ToolRunHome.Default(o.Home), info: Info, debug: Debug);
            if (o.Mode == Mode.Which)
            {
                var plan = app.PrepareAsync(dryRun: true, cts.Token).GetAwaiter().GetResult();
                Console.Out.WriteLine(plan.Describe());
                foreach (var n in plan.Notes) Console.Error.WriteLine("# " + n);
                return 0;
            }
            var run = app.PrepareAsync(dryRun: false, cts.Token).GetAwaiter().GetResult();
            Debug("mode: " + run.ModeLine);
            Debug("running " + run.Describe());
            return ChildRunner.Run(run);
        }
        catch (ToolException ex) { Console.Error.WriteLine("toolrun: " + ex.Message); return 1; }
        catch (UsageException ex) { Console.Error.WriteLine("toolrun: " + ex.Message); return 2; }
        catch (HttpRequestException ex) { Console.Error.WriteLine("toolrun: network error: " + ex.Message); return 1; }
        catch (IOException ex) { Console.Error.WriteLine("toolrun: " + ex.Message); return 1; }
    }
}

public static class Commands
{
    public static int List(ToolRunHome home, TextWriter w)
    {
        w.WriteLine("cache: " + home.Root);
        long total = 0;
        var tools = new ToolCache(home).Entries().ToList();
        w.WriteLine();
        w.WriteLine("tools:");
        if (tools.Count == 0) w.WriteLine("  (none)");
        foreach (var t in tools)
        {
            var size = ToolRunHome.SizeOf(t.Dir); total += size;
            var fws = string.Join(", ", t.Meta.Commands.SelectMany(c => c.Frameworks).Select(f => $"{f.Name} {f.Version}").Distinct());
            w.WriteLine($"  {t.Meta.Id} {t.Meta.Version}  {ToolRunHome.FormatSize(size)}  [{string.Join(", ", t.Meta.Commands.Select(c => c.Name))}]  needs {fws}");
        }
        w.WriteLine();
        w.WriteLine("private runtime (" + home.DotnetRoot + "):");
        var rts = new RuntimeResolver(home, () => throw new InvalidOperationException(), Rid.Host, _ => { }, _ => { }).CachedRuntimes().ToList();
        if (rts.Count == 0) w.WriteLine("  (none)");
        foreach (var r in rts)
        {
            var size = ToolRunHome.SizeOf(r.Dir); total += size;
            w.WriteLine($"  {r.Framework} {r.Version}  {ToolRunHome.FormatSize(size)}");
        }
        w.WriteLine();
        w.WriteLine("total: " + ToolRunHome.FormatSize(total));
        return 0;
    }

    public static int Clean(ToolRunHome home, string? id, string? version, TextWriter w)
    {
        long freed;
        if (id is not null)
        {
            freed = new ToolCache(home).Remove(id, version);
            w.WriteLine($"removed {id}{(version is null ? "" : " " + version)}: freed {ToolRunHome.FormatSize(freed)}");
            return 0;
        }
        freed = ToolRunHome.SizeOf(home.Root);
        foreach (var d in new[] { home.Tools, home.DotnetRoot, home.AppHostTemplate, home.Packages }) ToolRunHome.DeleteDir(d);
        w.WriteLine($"removed all tools and runtimes: freed {ToolRunHome.FormatSize(freed)}");
        return 0;
    }
}
