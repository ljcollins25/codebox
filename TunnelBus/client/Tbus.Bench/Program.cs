using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tbus;

// Head-to-head benchmark: the native tbus chisel client against the chisel 1.10.1 executable, same server, same load.
//   tbus-bench child                      (internal: runs the native client until killed; settings in BENCH_* env)
//   tbus-bench run --chisel PATH [--scope local|bus] [--only a,b] [--sweep]
static class Program
{
    static readonly byte[] Zeros = new byte[256 * 1024];

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child") return await Child();
        var opt = args.Skip(1).Select((a, i) => (a, i)).Where(x => x.a.StartsWith("--")).ToDictionary(x => x.a[2..], x => x.i + 1 < args.Length - 1 && !args[x.i + 2].StartsWith("--") ? args[x.i + 2] : "true");
        var chisel = opt["chisel"];
        var scope = opt.GetValueOrDefault("scope", "local");
        var only = opt.GetValueOrDefault("only")?.Split(',');
        var mb = int.Parse(opt.GetValueOrDefault("mb", "200"));
        var specs = opt.ContainsKey("sweep") ? Sweep() : Final();
        if (only != null) specs = specs.Where(s => only.Contains(s.Name)).ToList();
        var env = new Env(scope, chisel);
        await env.Init();
        var rows = new List<Result>();
        foreach (var s in specs)
        {
            Console.Error.WriteLine($"== {scope} {s.Name}");
            try { rows.Add(await env.Measure(s, mb, opt.ContainsKey("sweep"))); }
            catch (Exception e) { Console.Error.WriteLine("FAILED: " + e.Message); rows.Add(new Result(s.Name) { Error = e.Message }); }
        }
        await env.Done();
        Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    record Spec(string Name, string Kind, Dictionary<string, string> Env);

    static List<Spec> Final() =>
    [
        new("chisel-exe", "chisel", new()),
        new("native-default", "native", new()),
        new("native-tuned", "native", new() { ["BENCH_WINDOW"] = "4194304", ["BENCH_WSBUF"] = "65536", ["BENCH_COPYBUF"] = "131072", ["DOTNET_TieredPGO"] = "0", ["DOTNET_gcConcurrent"] = "0" }),
    ];

    static List<Spec> Sweep()
    {
        Spec N(string n, params (string, string)[] kv) => new(n, "native", kv.ToDictionary(x => x.Item1, x => x.Item2));
        return
        [
            N("base"),
            N("win4M", ("BENCH_WINDOW", "4194304")), N("win16M", ("BENCH_WINDOW", "16777216")),
            N("wsbuf64K", ("BENCH_WSBUF", "65536")),
            N("copy128K", ("BENCH_COPYBUF", "131072")),
            N("gc-nonconcurrent", ("DOTNET_gcConcurrent", "0")),
            N("gc-server", ("DOTNET_gcServer", "1")),
            N("gc-server-nonconc", ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "0")),
            N("tiered-pgo-off", ("DOTNET_TieredPGO", "0")),
            N("combo-4M-64K-128K", ("BENCH_WINDOW", "4194304"), ("BENCH_WSBUF", "65536"), ("BENCH_COPYBUF", "131072")),
            N("combo-16M-64K-128K", ("BENCH_WINDOW", "16777216"), ("BENCH_WSBUF", "65536"), ("BENCH_COPYBUF", "131072")),
        ];
    }

    // ---------------------------------------------------------------- child: the native client in its own process
    static async Task<int> Child()
    {
        string E(string k) => Environment.GetEnvironmentVariable(k) ?? "";
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(E("BENCH_HEADERS") is { Length: > 0 } h ? h : "{}")!;
        uint? win = uint.TryParse(E("BENCH_WINDOW"), out var w) ? w : null;
        int? wsb = int.TryParse(E("BENCH_WSBUF"), out var b) ? b : null;
        int? cb = int.TryParse(E("BENCH_COPYBUF"), out var c) ? c : null;
        var sw = Stopwatch.StartNew();
        var o = new ChiselOptions
        {
            Server = new Uri(E("BENCH_URL")), Headers = headers, User = E("BENCH_USER"), Password = E("BENCH_PASS"),
            Remotes = [new ChiselRemote(int.Parse(E("BENCH_RPORT")), "127.0.0.1", int.Parse(E("BENCH_TPORT")))],
            ChannelWindow = win, WsBufferSize = wsb, CopyBuffer = cb,
            Log = m => Console.Error.WriteLine("LOG " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + m),
            OnConnected = () => Console.Error.WriteLine($"CONNECTED {sw.ElapsedMilliseconds}ms"),
        };
        while (true)
        {
            try { await ChiselClient.RunAsync(o, CancellationToken.None); } catch (Exception e) { Console.Error.WriteLine("client: " + e.Message); }
            await Task.Delay(500);
        }
    }

    // ---------------------------------------------------------------- the environment: server (local) or the real bus
    record Result(string Name)
    {
        public string? Error { get; set; }
        public double SetupMs { get; set; }
        public double InChildConnectMs { get; set; }
        public double DownSingleMBs { get; set; }
        public double DownSingleCpu { get; set; }
        public double Down8MBs { get; set; }
        public double Down8Cpu { get; set; }
        public double UpSingleMBs { get; set; }
        public double UpSingleCpu { get; set; }
        public double Up8MBs { get; set; }
        public double Up8Cpu { get; set; }
        public double NewConnMedianMs { get; set; }
        public double NewConnP90Ms { get; set; }
        public double ReusedMedianMs { get; set; }
        public double ReusedP90Ms { get; set; }
        public double HarnessCpuDown8 { get; set; }
        public double ServerCpuDown8 { get; set; }
    }

    sealed class Env(string scope, string chiselExe)
    {
        TcpListener _target = null!; int _tport;
        Process? _server; int _sport_unused; string _authDir = "";
        BusClient? _bus; string _busUrl = ""; Dictionary<string, string> _access = new();
        const string Name = "tbus-bench";
        int _rport; int _pport; readonly int _delayMs = int.Parse(Environment.GetEnvironmentVariable("BENCH_DELAY_MS") ?? "0");
        string _user = "bu", _pass = "bp";

        public async Task Init()
        {
            _target = new TcpListener(IPAddress.Any, 0); _target.Start(); _tport = ((IPEndPoint)_target.LocalEndpoint).Port;
            _ = Task.Run(TargetLoop);
            if (scope == "local")
            {
                _sport = FreePort(); _rport = FreePort(); Env._sport = _sport;
                _authDir = Path.Combine(Path.GetTempPath(), "bench-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(_authDir);
                var af = Path.Combine(_authDir, "users.json");
                File.WriteAllText(af, JsonSerializer.Serialize(new Dictionary<string, string[]> { [_user + ":" + _pass] = [$"^R:0\\.0\\.0\\.0:{_rport}$"] }));
                _server = Process.Start(new ProcessStartInfo(chiselExe, $"server --host 127.0.0.1 --port {_sport} --reverse --authfile {af}") { RedirectStandardError = true, RedirectStandardOutput = true })!;
                _ = _server.StandardError.ReadToEndAsync(); _ = _server.StandardOutput.ReadToEndAsync();
                await Task.Delay(1000);
                if (_delayMs > 0) { _pport = FreePort(); _ = DelayProxy(_pport, _sport, _delayMs); }
            }
            else
            {
                _busUrl = Environment.GetEnvironmentVariable("BENCH_BUS") ?? "https://ctl.ref12.dev";
                _access = new() { ["CF-Access-Client-Id"] = Environment.GetEnvironmentVariable("BENCH_ID")!, ["CF-Access-Client-Secret"] = Environment.GetEnvironmentVariable("BENCH_SEC")! };
                _bus = new BusClient(_busUrl, Environment.GetEnvironmentVariable("BENCH_ADMIN")!, _access);
            }
        }

        public async Task Done()
        {
            try { _server?.Kill(true); } catch { }
            _target.Stop();
            if (_bus != null) try { await _bus.UnregisterAsync(Name, default); } catch { }
        }

        // a TCP relay that delays every chunk by ms in each direction (order kept): a stand-in for the bus's network round trip
        static async Task DelayProxy(int listen, int target, int ms)
        {
            var l = new TcpListener(IPAddress.Loopback, listen); l.Start();
            while (true)
            {
                var a = await l.AcceptTcpClientAsync(); var b = new TcpClient(); await b.ConnectAsync(IPAddress.Loopback, target);
                a.NoDelay = b.NoDelay = true;
                _ = Pump(a, b, ms); _ = Pump(b, a, ms);
            }
        }

        static readonly int _kbps = int.Parse(Environment.GetEnvironmentVariable("BENCH_KBPS") ?? "0"); static int _sport;

        static async Task Pump(TcpClient from, TcpClient to, int ms)
        {
            var q = System.Threading.Channels.Channel.CreateUnbounded<(DateTime, byte[])>();
            _ = Task.Run(async () =>
            {
                try { var ns = to.GetStream(); await foreach (var (due, data) in q.Reader.ReadAllAsync()) { var w = due - DateTime.UtcNow; if (w > TimeSpan.Zero) await Task.Delay(w); await ns.WriteAsync(data); if (_kbps > 0 && to.Client.RemoteEndPoint is IPEndPoint ep && ep.Port == _sport) await Task.Delay((int)(data.Length * 1000L / (_kbps * 1024L))); } }
                catch { }
                try { to.Client.Shutdown(SocketShutdown.Send); } catch { }
            });
            try
            {
                var ns = from.GetStream(); var buf = new byte[65536]; int n;
                while ((n = await ns.ReadAsync(buf)) > 0) await q.Writer.WriteAsync((DateTime.UtcNow.AddMilliseconds(ms), buf[..n]));
            }
            catch { }
            q.Writer.TryComplete();
        }

        static int FreePort()
 { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        // ---- the target: a tiny HTTP/1.1 server on raw sockets (same for every client)
        async Task TargetLoop()
        {
            while (true)
            {
                Socket s; try { s = await _target.AcceptSocketAsync(); } catch { return; }
                s.NoDelay = true;
                _ = Task.Run(() => Serve(s));
            }
        }

        static async Task Serve(Socket s)
        {
            using var ns = new NetworkStream(s, true);
            var buf = new byte[16384];
            try
            {
                while (true)
                {
                    var n = 0; int end;
                    while ((end = Find(buf, n)) < 0)
                    {
                        var r = await ns.ReadAsync(buf.AsMemory(n)); if (r == 0) return; n += r;
                    }
                    var head = Encoding.ASCII.GetString(buf, 0, end);
                    var extra = n - (end + 4);
                    var line = head.Split("\r\n")[0].Split(' ');
                    var close = head.Contains("Connection: close", StringComparison.OrdinalIgnoreCase);
                    var path = line[1];
                    if (line[0] == "POST")
                    {
                        var cl = long.Parse(head.Split("\r\n").First(h => h.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                        var left = cl - extra; var tmp = new byte[256 * 1024];
                        while (left > 0) { var r = await ns.ReadAsync(tmp.AsMemory(0, (int)Math.Min(tmp.Length, left))); if (r == 0) return; left -= r; }
                        await Reply(ns, "ok", close);
                    }
                    else if (path.StartsWith("/dl?n="))
                    {
                        var len = long.Parse(path[6..]);
                        await ns.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {len}\r\nContent-Type: application/octet-stream\r\n{(close ? "Connection: close\r\n" : "")}\r\n"));
                        for (long sent = 0; sent < len;) { var k = (int)Math.Min(Zeros.Length, len - sent); await ns.WriteAsync(Zeros.AsMemory(0, k)); sent += k; }
                    }
                    else await Reply(ns, "ok", close);
                    if (close) return;
                }
            }
            catch { }
        }

        static Task Reply(NetworkStream ns, string body, bool close) =>
            ns.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n{(close ? "Connection: close\r\n" : "")}\r\n{body}")).AsTask();

        static int Find(byte[] b, int n) { for (var i = 0; i + 3 < n; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i; return -1; }

        // ---- one client variant
        public async Task<Result> Measure(Spec spec, int mb, bool quick)
        {
            var res = new Result(spec.Name);
            string wsUrl, visitor;
            if (scope == "local") { wsUrl = $"http://127.0.0.1:{(_delayMs > 0 ? _pport : _sport)}"; visitor = $"http://127.0.0.1:{_rport}"; }
            else
            {
                var reg = await _bus!.RegisterAsync(Name, default);
                _user = reg.User; _pass = reg.Password; _rport = reg.Port;
                wsUrl = _busUrl; visitor = $"https://{Name}.ref12.dev";
                await Task.Delay(2500); // the server reloads its authfile
            }
            var sw = Stopwatch.StartNew();
            var proc = StartClient(spec, wsUrl);
            using var http = Client(visitor, false);
            try
            {
                // setup: process start until the first request comes back through the tunnel
                var until = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    if (proc.HasExited) throw new Exception("client exited");
                    try { using var r = await http.GetAsync("/e"); if (r.IsSuccessStatusCode) break; } catch { }
                    if (DateTime.UtcNow > until) throw new TimeoutException("no tunnel after 60 s");
                    await Task.Delay(10);
                }
                res.SetupMs = sw.Elapsed.TotalMilliseconds;
                if (spec.Kind == "native") res.InChildConnectMs = ChildConnectMs;

                // small-request latency, new connection each time and on one reused connection
                var nc = new List<double>(); var ru = new List<double>();
                for (var i = 0; i < 5; i++) { using var r = await http.GetAsync("/e"); }
                for (var i = 0; i < 100; i++)
                {
                    using var h2 = Client(visitor, true);
                    var t = Stopwatch.StartNew(); using var r = await h2.GetAsync("/e"); await r.Content.ReadAsStringAsync(); nc.Add(t.Elapsed.TotalMilliseconds);
                }
                for (var i = 0; i < 100; i++) { var t = Stopwatch.StartNew(); using var r = await http.GetAsync("/e"); await r.Content.ReadAsStringAsync(); ru.Add(t.Elapsed.TotalMilliseconds); }
                (res.NewConnMedianMs, res.NewConnP90Ms) = Stats(nc); (res.ReusedMedianMs, res.ReusedP90Ms) = Stats(ru);

                // throughput
                async Task<(double, double, double, double)> Try(bool down, int streams)
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try { return await Transfer(proc, visitor, down, streams, mb); }
                        catch (Exception e) when (attempt < 2) { Console.Error.WriteLine($"  transfer failed ({e.Message}); retrying once"); await Task.Delay(3000); }
                        catch (Exception e) { Console.Error.WriteLine($"  transfer failed again ({e.Message})"); res.Error += $"[{(down ? "down" : "up")}{streams}: {e.Message}] "; return (-1, -1, -1, -1); }
                    }
                }
                (res.DownSingleMBs, res.DownSingleCpu, _, _) = await Try(true, 1);
                (res.Down8MBs, res.Down8Cpu, res.HarnessCpuDown8, res.ServerCpuDown8) = await Try(true, Par);
                if (!quick)
                {
                    (res.UpSingleMBs, res.UpSingleCpu, _, _) = await Try(false, 1);
                    (res.Up8MBs, res.Up8Cpu, _, _) = await Try(false, Par);
                }
            }
            finally
            {
                try { proc.Kill(true); proc.WaitForExit(3000); } catch { }
                if (_bus != null) try { await _bus.UnregisterAsync(Name, default); } catch { }
                else await Task.Delay(300);
            }
            return res;
        }

        static (double, double) Stats(List<double> v) { v.Sort(); return (v[v.Count / 2], v[(int)(v.Count * 0.9)]); }

        HttpClient Client(string baseUrl, bool close)
        {
            var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = close ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, MaxConnectionsPerServer = 64 }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
            foreach (var (k, v) in _access) h.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
            if (close) h.DefaultRequestHeaders.ConnectionClose = true;
            return h;
        }

        double ChildConnectMs;
        int Par => scope == "bus" ? 4 : 8; // parallel streams

        Process StartClient(Spec s, string wsUrl)
        {
            ProcessStartInfo psi;
            if (s.Kind == "chisel")
            {
                psi = new ProcessStartInfo(chiselExe);
                foreach (var a in new[] { "client", "--keepalive", "25s", "--auth", _user + ":" + _pass }) psi.ArgumentList.Add(a);
                if (scope == "bus") { psi.ArgumentList.Add("--hostname"); psi.ArgumentList.Add(new Uri(wsUrl).Host); } // Go would send "Host: host:443", which the edge rejects
                foreach (var (k, v) in _access) { psi.ArgumentList.Add("--header"); psi.ArgumentList.Add($"{k}: {v}"); }
                psi.ArgumentList.Add(scope == "bus" ? wsUrl + "/_chisel" : wsUrl); psi.ArgumentList.Add($"R:{_rport}:127.0.0.1:{_tport}");
            }
            else
            {
                psi = new ProcessStartInfo(Environment.ProcessPath!);
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet") psi.ArgumentList.Add(typeof(Program).Assembly.Location);
                psi.ArgumentList.Add("child");
                var wss = scope == "local" ? wsUrl.Replace("http", "ws") + "/" : wsUrl.Replace("https", "wss") + "/_chisel";
                psi.Environment["BENCH_URL"] = wss; psi.Environment["BENCH_USER"] = _user; psi.Environment["BENCH_PASS"] = _pass;
                psi.Environment["BENCH_RPORT"] = _rport.ToString(); psi.Environment["BENCH_TPORT"] = _tport.ToString();
                psi.Environment["BENCH_HEADERS"] = JsonSerializer.Serialize(_access);
                foreach (var (k, v) in s.Env) psi.Environment[k] = v;
            }
            psi.RedirectStandardError = true; psi.RedirectStandardOutput = true; psi.UseShellExecute = false;
            var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && !e.Data.StartsWith("CONNECTED")) Console.Error.WriteLine("  [client] " + e.Data); if (e.Data?.StartsWith("CONNECTED") == true) ChildConnectMs = double.Parse(e.Data.Split(' ')[1].TrimEnd('m', 's')); };
            p.OutputDataReceived += (_, _) => { };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            return p;
        }

        // totalMb split over `streams` streams; returns MB/s and CPU% (100 = one core) of the client process, the harness and the server
        async Task<(double, double, double, double)> Transfer(Process client, string visitor, bool download, int streams, int totalMb)
        {
            var each = (long)totalMb * 1024 * 1024 / streams;
            using var http = Client(visitor, false);
            var self = Process.GetCurrentProcess();
            TimeSpan C(Process p) { try { p.Refresh(); return p.TotalProcessorTime; } catch { return TimeSpan.Zero; } }
            var c0 = C(client); var h0 = C(self); var s0 = _server != null ? C(_server) : TimeSpan.Zero;
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
            {
                if (download)
                {
                    using var r = await http.GetAsync($"/dl?n={each}", HttpCompletionOption.ResponseHeadersRead);
                    r.EnsureSuccessStatusCode();
                    using var st = await r.Content.ReadAsStreamAsync(); var b = new byte[256 * 1024]; long got = 0; int k;
                    while ((k = await st.ReadAsync(b)) > 0) got += k;
                    if (got != each) throw new Exception($"short download {got}/{each}");
                }
                else
                {
                    // the edge refuses request bodies over 100 MB, so a stream uploads in sequential POSTs of at most 50 MB
                    for (long left = each; left > 0;) { var part = Math.Min(left, 50L * 1024 * 1024); using var r = await http.PostAsync("/ul", new ZeroContent(part)); r.EnsureSuccessStatusCode(); left -= part; }
                }
            }));
            var secs = sw.Elapsed.TotalSeconds;
            var cpu = (C(client) - c0).TotalSeconds / secs * 100;
            var hcpu = (C(self) - h0).TotalSeconds / secs * 100;
            var scpu = _server != null ? (C(_server) - s0).TotalSeconds / secs * 100 : 0;
            return (totalMb / secs, cpu, hcpu, scpu);
        }

        sealed class ZeroContent(long len) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                for (long sent = 0; sent < len;) { var k = (int)Math.Min(Zeros.Length, len - sent); await stream.WriteAsync(Zeros.AsMemory(0, k)); sent += k; }
            }
            protected override bool TryComputeLength(out long length) { length = len; return true; }
        }
    }
}
