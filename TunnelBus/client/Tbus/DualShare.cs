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

    public static async Task<int> RunAsync(Host host, AppConfig config, Credentials creds, Log log, ShareMeta meta, IReadOnlyList<ShareSpec> specs,
        string? mode, string? pipeUrl, string? pipeToken, CancellationToken ct, Func<AppConfig, string, string, BusClient>? makeClient = null)
    {
        if (!string.IsNullOrWhiteSpace(pipeUrl)) config.PipeBus = pipeUrl.Trim().TrimEnd('/');
        var chiselCfg = config.IsPipe ? null : config; // a legacy --bus https://...workers.dev means "the pipe bus only"
        var pipeBase = config.IsPipe ? config.Bus : config.PipeBus;
        var pipeTok = pipeToken ?? creds.PipeAdminToken ?? (config.IsPipe ? creds.AdminToken : null);
        var chiselTok = creds.AdminToken;
        var lanes = ChooseLanes(mode, chiselCfg != null && chiselTok != null, !string.IsNullOrEmpty(pipeBase) && pipeTok != null);
        if (string.IsNullOrEmpty(mode) && lanes.Count == 1)
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
        log.Info("lanes: " + string.Join(", ", lanes.Select(l => l + " -> " + string.Join(" ", specs.Select(s => (l == "pipe" ? new AppConfig { Kind = "pipe", Domain = config.Domain, Cutover = config.Cutover } : config.ForChisel()).PublicUrl(s.Name))))));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return codes.All(x => x != 0) ? 1 : 0; // fails only when every lane failed
    }
}
