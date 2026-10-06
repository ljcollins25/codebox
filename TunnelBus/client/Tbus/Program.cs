using System.Diagnostics;

namespace Tbus;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        return await App.RunAsync(args, new Host(), cts.Token).ConfigureAwait(false);
    }
}

internal static class App
{
    public const string Usage = """
        tbus - share a local (or remote) port on the tunnel bus

        usage:
          tbus share <target>... [--name NAME] [--description TEXT] [--label TITLE] [--kind KIND] [--owner WHO]
                       [--session-name N] [--session-id ID] [--hexad H] [--session-url URL]
                                                 share and stay in the foreground; Ctrl+C unregisters.
                                                 The description (max 200 chars), label (60), kind (hexad, app, vscode, ...)
                                                 and owner ("hexad project") are shown on the bus dashboard.
              tbus share 3000 --label "My app" --description "Staging build" --kind app
              <target>: PORT | HOST:PORT | [IPV6]:PORT, optionally NAME=<target>
              tbus share 3000                    -> https://<machine>-3000.<domain>
              tbus share 3000 --name myapp       -> https://myapp.<domain>
              tbus share 192.168.1.20:8080 --name nas
              tbus share web=3000 api=3001       several shares in one process
          tbus list                              what the bus has registered
          tbus update <name> [--description T] [--label T] [--kind K] [--owner W]
                                                 change what the dashboard shows, without re-registering
          tbus stop <name>                       unregister (also ends a running 'tbus share' of that name)
          tbus open <name>                       open https://<name>.<domain> in the browser
          tbus config [--bus URL] [--domain D]   show or set the bus URL and base domain
          tbus login                             browser login through cloudflared (Access JWT)
          tbus login --service-token             store the Access service token (prompts, no echo)
          tbus login --admin-token               store the bus admin token (prompts, no echo)
          tbus logout                            forget stored credentials
        environment: TUNNEL_BUS_ADMIN_TOKEN, CF_ACCESS_CLIENT_ID, CF_ACCESS_CLIENT_SECRET, TUNNEL_BUS_URL, TUNNEL_BUS_DOMAIN,
                     TBUS_HOME (state folder)
        """;

    public static async Task<int> RunAsync(string[] args, Host host, CancellationToken ct)
    {
        var log = new Log(host);
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { host.Out.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            var rest = args[1..];
            return args[0] switch
            {
                "share" => await Share(rest, host, log, ct),
                "list" => await List(host, log, ct),
                "stop" => await Stop(rest, host, log, ct),
                "update" => await Update(rest, host, log, ct),
                "open" => Open(rest, host, log),
                "config" => Config(rest, host, log),
                "login" => await Login.RunAsync(rest, host, log, ct),
                "logout" => Logout(host, log),
                "version" or "--version" => Version(host),
                _ => throw new UserError($"Unknown command '{args[0]}'. Run 'tbus help'."),
            };
        }
        catch (UserError e) { log.Error("error: " + e.Message); return 2; }
        catch (BusException e) { log.Error("error: " + e.Message); return 1; }
        catch (HttpRequestException e) { log.Error("error: cannot reach the bus: " + e.Message); return 1; }
        catch (OperationCanceledException) { return 0; }
    }

    private static int Version(Host host) { host.Out.WriteLine("tbus 1.0.0 (chisel " + ChiselClient.Version + " protocol, built in)"); return 0; }

    internal static (List<string> positional, Dictionary<string, string?> options) ParseOptions(string[] args, string[] valueOptions, string[] flags)
    {
        var pos = new List<string>(); var opts = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..]; string? val = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { val = key[(eq + 1)..]; key = key[..eq]; }
                if (valueOptions.Contains(key)) { val ??= i + 1 < args.Length ? args[++i] : throw new UserError($"--{key} needs a value."); opts[key] = val; }
                else if (flags.Contains(key)) opts[key] = "true";
                else throw new UserError($"Unknown option --{key}.");
            }
            else pos.Add(a);
        }
        return (pos, opts);
    }

    private static async Task<int> Share(string[] args, Host host, Log log, CancellationToken ct)
    {
        var (pos, opts) = ParseOptions(args, ["name", "bus", "kind", ..ShareMeta.ValueOptions], []);
        var meta = ShareMeta.FromOptions(opts, host.GetEnv);
        if (meta.Label != null && pos.Count > 1) throw new UserError("--label works with a single target; one label cannot title several shares.");
        if (pos.Count == 0) throw new UserError("Nothing to share. Example: tbus share 3000 --name myapp");
        opts.TryGetValue("name", out var name);
        if (name != null && pos.Count > 1) throw new UserError("--name works with a single target; use NAME=TARGET for several (tbus share web=3000 api=3001).");
        var specs = pos.Select(p => ShareSpec.Parse(p, host.MachineName, name)).ToList();
        var dup = specs.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new UserError($"The name '{dup.Key}' is used twice; give each share its own name (NAME=TARGET).");

        var config = AppConfig.Load(host);
        if (opts.TryGetValue("bus", out var busOpt)) config.Bus = NormalizeBus(busOpt!);
        if (opts.TryGetValue("kind", out var kindOpt)) config.Kind = kindOpt!.Trim().ToLowerInvariant();
        var creds = new Credentials(host);
        var admin = creds.RequireAdminToken();
        using var bus = new BusClient(config.Bus, admin, creds.AccessHeaders());
        return await new ShareRunner(host, config, creds, log, bus, meta).RunAsync(specs, ct);
    }

    private static BusClient Client(Host host, out AppConfig config)
    {
        config = AppConfig.Load(host);
        var creds = new Credentials(host);
        return new BusClient(config.Bus, creds.RequireAdminToken(), creds.AccessHeaders());
    }

    private static async Task<int> List(Host host, Log log, CancellationToken ct)
    {
        using var bus = Client(host, out var config);
        var rows = await bus.ListAsync(ct);
        if (rows.Count == 0) { log.Info("nothing registered on " + config.Bus); return 0; }
        var w = Math.Max(4, rows.Max(r => r.Name.Length));
        log.Info($"{"NAME".PadRight(w)}  PORT   UP   URL");
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.Ordinal))
            log.Info($"{r.Name.PadRight(w)}  {r.Port,-5}  {(r.Up ? "yes" : "no"),-3}  {config.PublicUrl(r.Name)}");
        return 0;
    }

    private static async Task<int> Update(string[] args, Host host, Log log, CancellationToken ct)
    {
        var (pos, opts) = ParseOptions(args, ShareMeta.ValueOptions, []);
        if (pos.Count != 1) throw new UserError("usage: tbus update <name> [--description T] [--label T] [--kind K] [--owner W]");
        var name = pos[0].ToLowerInvariant(); ShareSpec.Validate(name);
        var meta = ShareMeta.FromOptions(opts, _ => null); // TBUS_OWNER is for registering, not for updating
        if (meta.IsEmpty) throw new UserError("Nothing to update: give --description, --label, --kind or --owner (an empty value clears the field).");
        using var bus = Client(host, out _);
        var found = await bus.UpdateAsync(name, meta, ct);
        log.Info(found ? $"updated {name}" : $"{name} is not registered");
        return found ? 0 : 1;
    }

    private static async Task<int> Stop(string[] args, Host host, Log log, CancellationToken ct)
    {
        if (args.Length != 1) throw new UserError("usage: tbus stop <name>");
        var name = args[0].ToLowerInvariant(); ShareSpec.Validate(name);
        using var bus = Client(host, out _);
        // a 'tbus share' for this name (in another process) watches this marker, so it ends instead of re-registering
        Directory.CreateDirectory(Path.Combine(host.Home, "stop"));
        File.WriteAllText(ShareRunner.StopMarker(host, name), DateTime.UtcNow.ToString("O"));
        var removed = await bus.UnregisterAsync(name, ct);
        log.Info(removed ? $"unregistered {name}" : $"{name} was not registered");
        return removed ? 0 : 1;
    }

    private static int Open(string[] args, Host host, Log log)
    {
        if (args.Length != 1) throw new UserError("usage: tbus open <name>");
        var name = args[0].ToLowerInvariant(); ShareSpec.Validate(name);
        var url = AppConfig.Load(host).PublicUrl(name);
        log.Info("opening " + url);
        try { host.OpenUrl(url); } catch (Exception e) { throw new UserError($"could not open a browser ({e.Message}); open {url} yourself."); }
        return 0;
    }

    private static int Config(string[] args, Host host, Log log)
    {
        var (_, opts) = ParseOptions(args, ["bus", "domain", "kind", "viewer-suffix"], []);
        var c = AppConfig.Load(host);
        if (opts.Count > 0)
        {
            var saved = File.Exists(host.ConfigPath) ? AppConfigFile(host) : new AppConfig();
            if (opts.TryGetValue("bus", out var bus)) saved.Bus = NormalizeBus(bus!);
            if (opts.TryGetValue("kind", out var kd)) { kd = kd!.Trim().ToLowerInvariant(); if (kd is not ("chisel" or "pipe" or "")) throw new UserError("--kind is chisel or pipe."); saved.Kind = kd == "" ? null : kd; }
            if (opts.TryGetValue("viewer-suffix", out var vsf)) saved.ViewerSuffix = vsf!.Trim().Trim('.').ToLowerInvariant();
            if (opts.TryGetValue("domain", out var dom)) saved.Domain = dom!.Trim().Trim('.').ToLowerInvariant();
            saved.Save(host);
            c = AppConfig.Load(host);
        }
        log.Info($"bus:    {c.Bus}");
        log.Info($"domain: {c.Domain}");
        log.Info($"kind:   {(c.IsPipe ? "pipe (Worker, no chisel)" : "chisel")}" + (c.IsPipe ? $"; viewers {c.PublicUrl("<name>")}" : ""));
        log.Info($"state:  {host.Home}");
        var creds = new Credentials(host);
        log.Info($"admin token: {(creds.AdminToken != null ? "set" : "not set")}");
        log.Info($"access:      {Describe(creds)}");
        return 0;
    }

    private static string Describe(Credentials c)
    {
        var h = c.AccessHeaders();
        return h.ContainsKey("CF-Access-Client-Id") ? "service token" : h.ContainsKey("cf-access-token") ? "browser login (JWT)" : "not set";
    }

    private static AppConfig AppConfigFile(Host host)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(host.ConfigPath)) ?? new AppConfig(); }
        catch (System.Text.Json.JsonException) { return new AppConfig(); }
    }

    private static string NormalizeBus(string bus)
    {
        if (!bus.Contains("://")) bus = "https://" + bus;
        if (!Uri.TryCreate(bus, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) throw new UserError($"Bad bus URL '{bus}'.");
        return u.GetLeftPart(UriPartial.Authority);
    }

    private static int Logout(Host host, Log log)
    {
        var s = new SecretStore(host.SecretsPath);
        foreach (var k in new[] { Credentials.AdminKey, Credentials.IdKey, Credentials.SecretKey, Credentials.JwtKey }) s.Delete(k);
        log.Info("stored credentials removed");
        return 0;
    }
}
