using System.Runtime.Versioning;
using System.Text.Json;
using FsDedup;

[assembly: SupportedOSPlatform("windows")]

return Cli.Run(args, Console.Out, Console.Error);

internal static class Cli
{
    private const string Usage = """
        usage: fsdedup <folder> [--what-if] [--min-size N] [--threads N] [--cache path] [--verbose] [--json] [--mode clone|hardlink]

          --what-if       report only; writes nothing except the hash cache
          --min-size N    ignore files smaller than N bytes (suffix K, M, G allowed; default 64K)
          --threads N     files hashed in parallel (default min(4, cores))
          --cache path    hash cache file (default: <folder>\.fsdedup-cache.jsonl)
          --verbose       list every group and action
          --json          machine-readable report on stdout
          --settle N      longest wait for free space to show what was freed (default 30 s; 0 = read at once)
          --mode clone|hardlink  block clone (ReFS) or hard links (NTFS/ReFS)
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        DedupOptions options;
        try { options = Parse(args); }
        catch (ArgumentException ex)
        {
            if (ex.Message.Length > 0) stderr.WriteLine($"fsdedup: {ex.Message}");
            stderr.WriteLine(Usage);
            return ex.Message.Length == 0 ? 0 : 2;
        }

        DedupReport report;
        try { report = new DedupEngine(options, null, stderr).Run(); }
        catch (Exception ex) when (ex is NotSupportedException or DirectoryNotFoundException or ArgumentException)
        {
            stderr.WriteLine($"fsdedup: {ex.Message}");
            return 2;
        }

        if (options.Json)
            stdout.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        else
            Print(report, options, stdout);
        return report.Errors.Any(e => !e.StartsWith("note:")) ? 1 : 0;
    }

    private static DedupOptions Parse(string[] args)
    {
        string? folder = null;
        var o = new DedupOptions { Root = "" };
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
            switch (a.ToLowerInvariant())
            {
                case "--what-if": case "-whatif": o.WhatIf = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--json": o.Json = true; break;
                case "--min-size": o.MinSize = ParseSize(Value()); break;
                case "--threads":
                    o.Threads = int.TryParse(Value(), out var t) && t > 0 ? t : throw new ArgumentException("--threads needs a positive number");
                    break;
                case "--cache": o.CachePath = Value(); break;
                case "--mode": o.Mode = Value(); break;
                case "--settle":
                    o.SettleSeconds = int.TryParse(Value(), out var st) && st >= 0 ? st : throw new ArgumentException("--settle needs a number of seconds");
                    break;
                case "-h": case "--help": case "-?": throw new ArgumentException("");
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"unknown option {a}");
                    if (folder is not null) throw new ArgumentException("only one folder can be given");
                    folder = a;
                    break;
            }
        }
        if (folder is null) throw new ArgumentException("a folder is required");
        o.Root = folder;
        return o;
    }

    private static long ParseSize(string s)
    {
        long mult = 1;
        var t = s.Trim().ToUpperInvariant().TrimEnd('B');
        if (t.EndsWith('K')) { mult = 1L << 10; t = t[..^1]; }
        else if (t.EndsWith('M')) { mult = 1L << 20; t = t[..^1]; }
        else if (t.EndsWith('G')) { mult = 1L << 30; t = t[..^1]; }
        return long.TryParse(t, out var n) && n >= 0 ? n * mult : throw new ArgumentException($"bad size '{s}'");
    }

    private static string Bytes(long n) => n switch
    {
        >= 1L << 30 => $"{n / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{n / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{n / (double)(1L << 10):F1} KB",
        _ => $"{n} B",
    };

    private static void Print(DedupReport r, DedupOptions o, TextWriter w)
    {
        w.WriteLine($"{(r.WhatIf ? "what-if: " : "")}{r.Root} ({r.FileSystem}, {r.ClusterSize} B clusters), mode {r.Mode}");
        foreach (var line in r.Recovered) w.WriteLine($"recovered: {line}");
        w.WriteLine($"files scanned: {r.FilesScanned}");
        if (r.Skipped.Count > 0) w.WriteLine("skipped: " + string.Join(", ", r.Skipped.Select(kv => $"{kv.Key} {kv.Value}")));
        w.WriteLine($"hashed: {r.FilesHashed} file(s), {Bytes(r.BytesHashed)} in {r.HashSeconds:F1} s ({r.HashMBPerSecond:F0} MB/s); {r.FilesHashedFromCache} from cache");
        w.WriteLine($"duplicate groups: {r.Groups}; duplicate files: {r.DuplicateFiles}; already sharing storage: {r.AlreadyShared}");
        if (r.WhatIf || o.Verbose)
            foreach (var g in r.GroupList)
            {
                w.WriteLine($"  group {g.Hash[..8]} ({Bytes(g.Size)} each): original {g.Original}");
                foreach (var d in g.Duplicates) w.WriteLine($"    {(r.WhatIf ? "would replace" : "replace")}  {d}");
                foreach (var d in g.AlreadyShared) w.WriteLine($"    already shared {d}");
            }
        if (r.WhatIf) w.WriteLine($"would replace {r.FilesToReplace} file(s), freeing {Bytes(r.BytesFreed)}");
        else
        {
            w.WriteLine($"replaced {r.FilesReplaced} file(s), freed {Bytes(r.BytesFreed)} ({(r.Mode == "hardlink" ? "logical file size" : "whole clusters")}) in {r.ReplaceSeconds:F1} s");
            w.WriteLine($"volume free space: {Bytes(r.FreeSpaceBefore)} before, {Bytes(r.FreeSpaceAfter)} after ({Bytes(r.FreeSpaceAfter - r.FreeSpaceBefore)} gained)");
        }
        foreach (var e in r.Errors) w.WriteLine(e.StartsWith("note:") ? e : $"error: {e}");
        w.WriteLine($"done in {r.ElapsedSeconds:F1} s");
    }
}
