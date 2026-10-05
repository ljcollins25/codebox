using System.Text.Json;

namespace R2Pipe;

internal static class Program
{
    private const string Usage = @"r2pipe - bulk data pipe through Cloudflare R2

  r2pipe send <file|-> [--name N] [--parallel 16] [--part-size 16M] [--mode presigned|binding] [--resume ID]
  r2pipe recv <id> [-o file|-] [--parallel 16]
  r2pipe ls
  r2pipe abort <id>
  r2pipe serve <local port> --name <name>    answer HTTP requests for /p/<name>/ (or <name>--pipe.ref12.dev) from a local app
  r2pipe login [--url U] [--client-id ID --client-secret S | --token T]

Environment: R2PIPE_URL (default https://pipe.ref12.dev), CF_ACCESS_CLIENT_ID, CF_ACCESS_CLIENT_SECRET, R2PIPE_TOKEN, R2PIPE_PARALLEL.
Common: --quiet (no progress), --stats FILE (write a JSON summary).
send prints the transfer id on stdout and the receive command on stderr; '-' reads stdin. recv without -o writes to the file name the sender gave; '-o -' writes to stdout.";

    public static async Task<int> Main(string[] argv)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") { Console.Error.WriteLine(Usage); return argv.Length == 0 ? 2 : 0; }
            var cmd = argv[0];
            var args = new Args(argv.Skip(1));
            var env = (string k) => Environment.GetEnvironmentVariable(k);
            var store = new SecretStore(Path.Combine(Credentials.StateDir(env), "secrets.json"));
            var creds = new Credentials(env, store);
            switch (cmd)
            {
                case "login": return Login(args, creds);
                case "send": return await SendAsync(args, creds, cts.Token);
                case "recv": return await RecvAsync(args, creds, cts.Token);
                case "serve":
                    {
                        var port = args.Positional.FirstOrDefault() ?? throw new UserError("Usage: r2pipe serve <local port|url> --name <name>");
                        var nm = args.Get("name") ?? throw new UserError("--name is required");
                        var target = port.Contains("://") ? port : "http://127.0.0.1:" + port;
                        var host = new ServeHost(Api(creds), nm, target, Parallel(args), s => Console.Error.WriteLine(s));
                        try { await host.RunAsync(cts.Token); } catch (OperationCanceledException) { }
                        return 0;
                    }
                case "ls": return await ListAsync(creds, cts.Token);
                case "abort":
                    {
                        var id = args.Positional.FirstOrDefault() ?? throw new UserError("Usage: r2pipe abort <id>");
                        await Api(creds).AbortAsync(id, cts.Token);
                        Console.Error.WriteLine("aborted " + id);
                        return 0;
                    }
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        catch (UserError e) { Console.Error.WriteLine("r2pipe: " + e.Message); return 2; }
        catch (OperationCanceledException) { Console.Error.WriteLine("r2pipe: cancelled"); return 130; }
        catch (PipeException e) { Console.Error.WriteLine("r2pipe: " + e.Message); return 1; }
        catch (ApiException e) { Console.Error.WriteLine("r2pipe: " + e.Message); return 1; }
    }

    private static HttpPipeApi Api(Credentials c) => new(c.Url, c.Headers());

    private static int Parallel(Args a) => a.GetInt("parallel", int.TryParse(Environment.GetEnvironmentVariable("R2PIPE_PARALLEL"), out var p) && p > 0 ? p : 16);

    private static int Login(Args a, Credentials c)
    {
        var env = (string k) => Environment.GetEnvironmentVariable(k);
        if (a.Get("url") is { } u) c.Store.Set(Credentials.UrlKey, u);
        var id = a.Get("client-id") ?? env("CF_ACCESS_CLIENT_ID");
        var secret = a.Get("client-secret") ?? env("CF_ACCESS_CLIENT_SECRET");
        if (id != null && secret != null) { c.Store.Set(Credentials.IdKey, id); c.Store.Set(Credentials.SecretKey, secret); }
        if (a.Get("token") is { } t) c.Store.Set(Credentials.TokenKey, t);
        Console.Error.WriteLine("stored" + (OperatingSystem.IsWindows() ? " (DPAPI-encrypted)" : " (file mode 0600)"));
        return 0;
    }

    private static async Task<int> ListAsync(Credentials c, CancellationToken ct)
    {
        var list = await Api(c).ListAsync(ct);
        if (list.Count == 0) { Console.Error.WriteLine("no open transfers"); return 0; }
        foreach (var t in list)
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(t.CreatedAt);
            var size = t.TotalSize ?? t.Size;
            Console.WriteLine($"{t.Id}  {t.Status,-9} {(size is { } s ? Meter.Fmt(s) : "?"),10}  {t.Mode,-9} {(int)age.TotalMinutes,4}m  {t.Name}");
        }
        return 0;
    }

    private static async Task<int> SendAsync(Args a, Credentials c, CancellationToken ct)
    {
        var src = a.Positional.FirstOrDefault() ?? throw new UserError("Usage: r2pipe send <file|-> [--name N]");
        Stream input; long? size = null; string? name = a.Get("name");
        if (src == "-") input = Console.OpenStandardInput();
        else
        {
            if (!File.Exists(src)) throw new UserError("No such file: " + src);
            var fi = new FileInfo(src); size = fi.Length; name ??= fi.Name;
            input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        }
        var api = Api(c);
        var o = new SendOptions
        {
            Name = name, Size = size, Parallel = Parallel(a), UrlBatch = a.GetInt("url-batch", 16), Mode = a.Get("mode"), ResumeId = a.Get("resume"),
            PartSize = a.Get("part-size") is { } ps ? Sizes.Parse(ps) : 16L << 20,
            Info = s => Console.Error.WriteLine(s),
        };
        var meter = new Meter();
        var started = DateTimeOffset.UtcNow;
        bool quiet = a.Has("quiet", "q");
        using var stop = new CancellationTokenSource();
        Task? report = null; string? idOut = null; string? mode = null;
        using (input)
        {
            var res = await Sender.RunAsync(api, api, input, o, meter, cr =>
            {
                idOut = cr.Id; mode = cr.Mode;
                Console.Out.WriteLine(cr.Id); Console.Out.Flush();
                var pre = c.Url == "https://pipe.ref12.dev" ? "" : $"R2PIPE_URL={c.Url} ";
                Console.Error.WriteLine($"transfer {cr.Id} ({cr.Mode}, {Meter.Fmt(cr.PartSize)} parts, {o.Parallel} in flight)");
                Console.Error.WriteLine($"receive with:  {pre}r2pipe recv {cr.Id}");
                if (!quiet) report = meter.ReportAsync("sent", size, Console.Error, !Console.IsErrorRedirected, stop.Token);
            }, ct);
            stop.Cancel(); if (report != null) await report;
            Console.Error.WriteLine($"sent {Meter.Fmt(res.Size)} in {res.Parts} parts, {meter.MBps:0.0} MB/s, sha256 {res.Sha256}");
            WriteStats(a, "send", idOut, mode, meter, started, res.Size);
        }
        return 0;
    }

    private static async Task<int> RecvAsync(Args a, Credentials c, CancellationToken ct)
    {
        var id = a.Positional.FirstOrDefault() ?? throw new UserError("Usage: r2pipe recv <id> [-o file|-]");
        var api = Api(c);
        var started = DateTimeOffset.UtcNow;
        var first = await api.StateAsync(id, -1, 0, ct);
        var outPath = a.Get("o", "output") ?? SafeName(first.Meta.Name, id);
        ISink sink; ResumeFile resume;
        if (outPath == "-") { sink = new StreamSink(Console.OpenStandardOutput()); resume = new ResumeFile(null, id); }
        else
        {
            var statePath = outPath + ".r2pipe";
            resume = new ResumeFile(statePath, id);
            sink = new FileSink(outPath, keep: resume.Done.Count > 0 && File.Exists(outPath));
            if (resume.Done.Count > 0) Console.Error.WriteLine($"resuming: {resume.Done.Count} parts already written to {outPath}");
        }
        var o = new ReceiveOptions { Parallel = Parallel(a), Info = s => Console.Error.WriteLine(s) };
        var meter = new Meter();
        bool quiet = a.Has("quiet", "q");
        using var stop = new CancellationTokenSource();
        var report = quiet ? Task.CompletedTask : meter.ReportAsync("received", first.Meta.Size, Console.Error, !Console.IsErrorRedirected, stop.Token);
        await using (sink)
        {
            var res = await Receiver.RunAsync(api, api, id, sink, resume, o, meter, ct);
            stop.Cancel(); await report;
            Console.Error.WriteLine($"received {Meter.Fmt(res.Size)} in {res.Parts} parts ({res.Resumed} resumed), {meter.MBps:0.0} MB/s, first byte after {meter.FirstByteMs} ms, sha256 {res.Sha256} verified");
            WriteStats(a, "recv", id, first.Meta.Mode, meter, started, res.Size);
        }
        return 0;
    }

    private static string SafeName(string name, string id)
    {
        var n = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(n) || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n is "." or "..") n = id + ".bin";
        return n;
    }

    private static void WriteStats(Args a, string role, string? id, string? mode, Meter m, DateTimeOffset started, long size)
    {
        if (a.Get("stats") is not { } path) return;
        var o = new
        {
            role, id, mode, bytes = size, seconds = m.Elapsed.TotalSeconds, mbps = m.MBps, parts = m.Parts, retries = m.Retries,
            startUnixMs = started.ToUnixTimeMilliseconds(), firstByteMs = m.FirstByteMs, firstByteUnixMs = started.ToUnixTimeMilliseconds() + m.FirstByteMs,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(o));
    }
}
