using Tool2App;

return await Cli.RunAsync(args, Console.Out, Console.Error);

internal static class Cli
{
    private const string Usage = """
        usage: tool2app <package>[@version] | <file.nupkg> | <folder>  [options]

          Turns a .NET tool package into a standalone app: no 'dotnet tool install', no installed .NET needed.

          --rid <rid>              target runtime identifier (default: this machine, e.g. linux-x64); win-x64 can be built on Linux and back
          --single-file            one executable (bundle) instead of a folder
          --framework-dependent    small apphost + the tool's files; needs .NET installed (combine with --single-file for one file)
          -o, --output <dir>       output folder (default: ./<command>-<rid>)
          --force                  replace a non-empty output folder
          --runtime-version <v>    exact runtime version to ship (default: latest patch of the tool's major.minor)
          --command <name>         which command, for packages that define several
          --tfm <tfm>              pick this tools/<tfm> folder instead of the highest
          --prerelease             allow prerelease tool and runtime versions
          --feed <url>             package feed (default nuget.org: v3, falling back to v2); env TOOL2APP_FEED
          --feed-kind <v2|v3|auto> kind of --feed (auto: .json = v3, else v2); env TOOL2APP_FEED_KIND
          --cache <dir>            download cache (default: local app data/tool2app/packages); env TOOL2APP_CACHE
          single file:  --extract-all (extract all content at start), --native-beside (do not bundle native libs), --compress
          --patch-resources        Windows only: copy the entry dll's version resources onto the exe
          -v, --verbose            show what is being done
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? input = null, rid = null, output = null, runtimeVersion = null, command = null, tfm = null, feed = Environment.GetEnvironmentVariable("TOOL2APP_FEED"),
            feedKind = Environment.GetEnvironmentVariable("TOOL2APP_FEED_KIND"), cache = null;
        bool single = false, fd = false, force = false, pre = false, extractAll = false, nativeBeside = false, compress = false, patch = false, verbose = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ToolException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "-h": case "--help": case "-?": stdout.WriteLine(Usage); return 0;
                    case "--rid": rid = Next(); break;
                    case "--single-file": single = true; break;
                    case "--framework-dependent": fd = true; break;
                    case "-o": case "--output": output = Next(); break;
                    case "--force": force = true; break;
                    case "--runtime-version": runtimeVersion = Next(); break;
                    case "--command": command = Next(); break;
                    case "--tfm": tfm = Next(); break;
                    case "--prerelease": pre = true; break;
                    case "--feed": feed = Next(); break;
                    case "--feed-kind": feedKind = Next(); break;
                    case "--cache": cache = Next(); break;
                    case "--extract-all": extractAll = true; break;
                    case "--native-beside": nativeBeside = true; break;
                    case "--compress": compress = true; break;
                    case "--patch-resources": patch = true; break;
                    case "-v": case "--verbose": verbose = true; break;
                    default:
                        if (args[i].StartsWith('-') && args[i].Length > 1) throw new ToolException($"Unknown option {args[i]} (see --help).");
                        if (input is not null) throw new ToolException("Only one package can be converted at a time.");
                        input = args[i]; break;
                }
            }
            if (input is null) { stderr.WriteLine(Usage); return 2; }

            void Log(string m) { if (verbose) stderr.WriteLine("tool2app: " + m); }
            var targetRid = rid is null ? Rid.Host : Rid.Parse(rid);
            var http = FeedHttp.CreateClient();
            var theFeed = Feeds.Create(feed, feedKind, http, Log);
            var store = new PackageStore(theFeed, cache) { Log = Log };

            string packagePath;
            if (File.Exists(input) || Directory.Exists(input)) packagePath = input;
            else
            {
                string id = input, version;
                var at = input.LastIndexOf('@');
                if (at > 0) { id = input[..at]; version = input[(at + 1)..]; }
                else version = await store.LatestAsync(id, pre);
                Log($"{id} {version}");
                packagePath = await store.GetAsync(id, version);
            }

            using var pkg = PackageContent.Open(packagePath);
            var spec = NuSpecInfo.Read(pkg);
            var asset = ToolAsset.Select(ToolAsset.Discover(pkg), targetRid, tfm);
            var cmdName = asset.Settings.Commands.Count > 0 ? asset.PickCommand(command).Name : (spec?.Id ?? "tool");
            output ??= $"{cmdName}-{targetRid}{(fd ? "-fd" : "")}{(single ? "-single" : "")}";

            var builder = new Tool2AppBuilder(store, Log);
            var result = await builder.BuildAsync(pkg, new BuildOptions
            {
                Rid = targetRid, OutputDir = output, SingleFile = single, FrameworkDependent = fd, RuntimeVersion = runtimeVersion,
                Command = command, Tfm = tfm, IncludePrerelease = pre, ExtractAll = extractAll, NativeBeside = nativeBeside, Compress = compress,
                PatchResources = patch, Force = force,
            });
            stdout.WriteLine($"{result.ToolId} {result.ToolVersion} -> {result.Kind} for {targetRid}");
            stdout.WriteLine($"  frameworks: {(result.Frameworks.Count == 0 ? "(none)" : string.Join(", ", result.Frameworks))}");
            stdout.WriteLine($"  exe:   {result.ExePath} ({result.ExeBytes / 1048576.0:F1} MB)");
            stdout.WriteLine($"  total: {result.TotalBytes / 1048576.0:F1} MB in {Path.GetFullPath(output)}");
            return 0;
        }
        catch (ToolException ex)
        {
            stderr.WriteLine("tool2app: " + ex.Message);
            return 1;
        }
    }
}
