namespace Tbus;

/// <summary>
/// One command, two addresses: runs the same shares on the container bus (chisel, name.domain) and on the pipe bus (p-name.domain)
/// as two independent lanes. Each lane has its own BusClient, registration, reconnect loop, backoff and log tag ("[chisel:x]", "[pipe:x]"),
/// so one bus being down or refusing a registration never stops the other. Same name and metadata go to both registries.
/// </summary>
internal static class DualShare
{
    public static bool IsMode(string? v) => v is "both" or "chisel" or "pipe";

    /// <summary>Which lanes to run. Default (no mode): every configured bus. An explicitly requested bus that is not configured is an error.</summary>
    public static List<string> ChooseLanes(string? mode, bool chiselConfigured, bool pipeConfigured)
    {
        mode = string.IsNullOrEmpty(mode) ? "default" : mode;
        var lanes = new List<string>();
        if (mode is "default" or "both" or "chisel") { if (chiselConfigured) lanes.Add("chisel"); else if (mode == "chisel") throw new UserError("--bus chisel: no container bus admin token (TUNNEL_BUS_ADMIN_TOKEN or: tbus login --admin-token)."); }
        if (mode is "default" or "both" or "pipe") { if (pipeConfigured) lanes.Add("pipe"); else if (mode == "pipe") throw new UserError("--bus pipe: the pipe bus needs its URL and token (--pipe-url/TUNNEL_PIPE_URL and --pipe-token/TUNNEL_PIPE_ADMIN_TOKEN)."); }
        if (mode == "both" && lanes.Count < 2) throw new UserError("--bus both: both buses must be configured (container bus admin token and pipe URL + pipe token); use --bus chisel or --bus pipe for one.");
        if (lanes.Count == 0) throw new UserError("No bus admin token. Set TUNNEL_BUS_ADMIN_TOKEN (container bus; or: tbus login --admin-token) and/or TUNNEL_PIPE_ADMIN_TOKEN (pipe bus).");
        return lanes;
    }

    /// <summary>
    /// --no-register: the name is already registered (by whoever holds the admin tokens); connect with this name's own credentials only.
    /// Never touches an admin token (not the env, not the secret store). A lane without its credentials is skipped with one line.
    ///   chisel: TBUS_CHISEL_USER, TBUS_CHISEL_PASSWORD, TBUS_CHISEL_PORT (the port from the register reply; chisel's per-name auth is limited to it)
    ///   pipe:   TBUS_PIPE_NAME_TOKEN + the pipe bus URL (TUNNEL_PIPE_URL / --pipe-url)
    /// </summary>
    public static async Task<int> RunNoRegisterAsync(Host host, AppConfig config, Credentials creds, Log log, IReadOnlyList<ShareSpec> specs,
        string? mode, string? pipeUrl, CancellationToken ct)
    {
        if (specs.Count != 1) throw new UserError("--no-register works with a single target and --name (the credentials belong to one name).");
        var spec = specs[0];
        if (!string.IsNullOrWhiteSpace(pipeUrl)) config.PipeBus = pipeUrl.Trim().TrimEnd('/');
        string? E(string k) { var v = host.GetEnv(k); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        var wantChisel = mode is null or "both" or "chisel"; var wantPipe = mode is null or "both" or "pipe";
        var cUser = E("TBUS_CHISEL_USER"); var cPass = E("TBUS_CHISEL_PASSWORD"); var cPortText = E("TBUS_CHISEL_PORT");
        var pTok = E("TBUS_PIPE_NAME_TOKEN");
        var pipeBase = config.IsPipe ? config.Bus : config.PipeBus;
        if (cPass != null) Log.Register(cPass);
        if (pTok != null) Log.Register(pTok);

        var lanes = new List<(string lane, AppConfig cfg, Registration reg)>();
        if (wantChisel)
        {
            var miss = new List<string>();
            if (cUser == null) miss.Add("TBUS_CHISEL_USER");
            if (cPass == null) miss.Add("TBUS_CHISEL_PASSWORD");
            int cPort = 0;
            if (cPortText == null) miss.Add("TBUS_CHISEL_PORT"); else if (!int.TryParse(cPortText, out cPort) || cPort < 1 || cPort > 65535) miss.Add("a valid TBUS_CHISEL_PORT");
            if (config.IsPipe) miss.Add("a container bus URL (TUNNEL_BUS_URL)");
            if (miss.Count == 0) lanes.Add(("chisel", config.ForChisel(), new Registration(spec.Name, cPort, cUser!, cPass!)));
            else { var msg = "--no-register: chisel lane skipped, missing " + string.Join(", ", miss); if (mode == "chisel") throw new UserError(msg); log.Info(msg); }
        }
        if (wantPipe)
        {
            var miss = new List<string>();
            if (pTok == null) miss.Add("TBUS_PIPE_NAME_TOKEN");
            if (string.IsNullOrEmpty(pipeBase)) miss.Add("TUNNEL_PIPE_URL");
            if (miss.Count == 0) lanes.Add(("pipe", new AppConfig { Bus = pipeBase!.TrimEnd('/'), Domain = config.Domain, Kind = "pipe", PipeBus = pipeBase, Cutover = config.Cutover }, new Registration(spec.Name, 0, "", "", pTok, "/_bus/ws/" + spec.Name)));
            else { var msg = "--no-register: pipe lane skipped, missing " + string.Join(", ", miss); if (mode == "pipe") throw new UserError(msg); log.Info(msg); }
        }
        if (lanes.Count == 0) throw new UserError("--no-register: no lane has its per-name credentials. Chisel: TBUS_CHISEL_USER, TBUS_CHISEL_PASSWORD, TBUS_CHISEL_PORT. Pipe: TBUS_PIPE_NAME_TOKEN and TUNNEL_PIPE_URL.");

        var codes = new int[lanes.Count];
        var tasks = new List<Task>();
        for (var i = 0; i < lanes.Count; i++)
        {
            var (lane, cfg, reg) = lanes[i]; var idx = i;
            var access = lane == "pipe" ? new Dictionary<string, string>() : creds.AccessHeaders(); // Access service token only, never an admin token
            tasks.Add(Task.Run(async () =>
            {
                try { codes[idx] = await new ShareRunner(host, cfg, creds, log, null, null, lanes.Count > 1 ? lane : "", access, reg).RunAsync(new[] { spec }, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception e) { log.Error($"[{lane}] stopped: {e.Message}"); codes[idx] = 1; }
            }));
        }
        log.Info("lanes: " + string.Join(", ", lanes.Select(l => l.lane + " -> " + l.cfg.PublicUrl(spec.Name))));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return codes.All(x => x != 0) ? 1 : 0;
    }

    public static async Task<int> RunAsync(
Host host, AppConfig config, Credentials creds, Log log, ShareMeta meta, IReadOnlyList<ShareSpec> specs,
        string? mode, string? pipeUrl, string? pipeToken, CancellationToken ct, Func<AppConfig, string, string, BusClient>? makeClient = null)
    {
        if (!string.IsNullOrWhiteSpace(pipeUrl)) config.PipeBus = pipeUrl.Trim().TrimEnd('/');
        var chiselCfg = config.IsPipe ? null : config; // a legacy --bus https://...workers.dev means "the pipe bus only"
        var pipeBase = config.IsPipe ? config.Bus : config.PipeBus;
        var pipeTok = pipeToken ?? creds.PipeAdminToken ?? (config.IsPipe ? creds.AdminToken : null);
        var chiselTok = creds.AdminToken;
        var lanes = ChooseLanes(mode, chiselCfg != null && chiselTok != null, !string.IsNullOrEmpty(pipeBase) && pipeTok != null);
        if (string.IsNullOrEmpty(mode) && lanes.Count == 1 && lanes[0] != "chisel") // chisel only: exactly the old output, no extra note
            log.Info(lanes[0] == "chisel" ? "note: no pipe bus configured (set TUNNEL_PIPE_ADMIN_TOKEN); sharing on the container bus only" : "note: no container bus admin token; sharing on the pipe bus only");

        var tasks = new List<Task>();
        var codes = new int[lanes.Count];
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i]; var idx = i;
            var cfg = lane == "pipe" ? new AppConfig { Bus = pipeBase!.TrimEnd('/'), Domain = config.Domain, Kind = "pipe", PipeBus = pipeBase, Cutover = config.Cutover } : config.ForChisel();
            var tok = lane == "pipe" ? pipeTok! : chiselTok!;
            Log.Register(tok);
            // the Access credentials go to the container bus only; the pipe bus's connect path (workers.dev) never sees them
            var access = lane == "pipe" ? new Dictionary<string, string>() : creds.AccessHeaders();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var bus = makeClient?.Invoke(cfg, tok, lane) ?? new BusClient(cfg.Bus, tok, access);
                    codes[idx] = await new ShareRunner(host, cfg, creds, log, bus, meta, lanes.Count > 1 ? lane : "", access).RunAsync(specs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { log.Error($"[{lane}] stopped: {e.Message}"); codes[idx] = 1; } // this lane only; the other keeps running
            }));
        }
        if (lanes.Count > 1 || lanes[0] != "chisel") log.Info("lanes: " + string.Join(", ", lanes.Select(l => l + " -> " + string.Join(" ", specs.Select(s => (l == "pipe" ? new AppConfig { Kind = "pipe", Domain = config.Domain, Cutover = config.Cutover } : config.ForChisel()).PublicUrl(s.Name))))));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return codes.All(x => x != 0) ? 1 : 0; // fails only when every lane failed
    }
}
