using System.Diagnostics;
using System.Net.Sockets;

namespace Tbus;

/// <summary>
/// Runs shares in the foreground: per share register -> built-in chisel client (ChiselClient) -> watch; reconnect with backoff;
/// re-register when the bus forgot the name (restart) or the connection died; unregister on cancel. Each share has its own
/// connection because every registration has its own chisel user limited to its own port. The Access headers go straight onto the
/// websocket handshake, so no secret leaves this process.
/// </summary>
internal sealed class ShareRunner(Host host, AppConfig config, Credentials creds, Log log, BusClient bus, ShareMeta? meta = null)
{
    public static string StopMarker(Host host, string name) => Path.Combine(host.Home, "stop", name);

    public async Task<int> RunAsync(IReadOnlyList<ShareSpec> specs, CancellationToken ct)
    {
        var access = creds.AccessHeaders();
        if (access.Count == 0) log.Info("note: no Cloudflare Access credentials (service token or login); the bus will refuse the calls if Access is on.");
        foreach (var s in specs) { try { File.Delete(StopMarker(host, s.Name)); } catch (IOException) { } }

        log.Info($"bus {config.Bus}; {specs.Count} share(s); Ctrl+C to stop and unregister");
        var tasks = specs.Select(s => Task.Run(() => RunShare(s, access, ct))).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var s in specs)
        {
            if (File.Exists(StopMarker(host, s.Name))) continue; // tbus stop already unregistered it
            try { await bus.UnregisterAsync(s.Name, cleanup.Token).ConfigureAwait(false); log.Info($"[{s.Name}] unregistered"); }
            catch (Exception e) { log.Error($"[{s.Name}] could not unregister: {e.Message}"); }
        }
        foreach (var s in specs) { try { File.Delete(StopMarker(host, s.Name)); } catch (IOException) { } }
        return 0;
    }

    private async Task RunShare(ShareSpec s, IReadOnlyDictionary<string, string> access, CancellationToken ct)
    {
        var backoff = host.BackoffStart;
        var tag = $"[{s.Name}]";
        await WarnIfUnreachable(s, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                log.Info($"{tag} registering");
                var reg = await bus.RegisterAsync(s.Name, ct, meta).ConfigureAwait(false);
                log.Info($"{tag} registered (bus port {reg.Port}); connecting");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, host.PollInterval.TotalMilliseconds)), ct).ConfigureAwait(false); // the server reloads its authfile
                var stopped = await RunConnection(s, reg, access, ct).ConfigureAwait(false);
                if (stopped) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is BusException or HttpRequestException or TaskCanceledException or IOException or ChiselRefusedException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
            {
                log.Error($"{tag} {e.Message}");
            }
            if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(30)) backoff = host.BackoffStart;
            log.Info($"{tag} retrying in {backoff.TotalSeconds:0.#}s");
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, host.BackoffMax.Ticks));
        }
    }

    /// <summary>Runs one chisel connection until it drops, the bus forgets the name, or tbus stop/Ctrl+C. True = do not run again.</summary>
    private async Task<bool> RunConnection(ShareSpec s, Registration reg, IReadOnlyDictionary<string, string> access, CancellationToken ct)
    {
        var tag = $"[{s.Name}]";
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var options = new ChiselOptions
        {
            Server = ChiselClient.WebSocketUrl(config.Bus),
            Headers = access,
            User = reg.User, Password = reg.Password,
            Remotes = [new ChiselRemote(reg.Port, s.TargetHost, s.TargetPort)],
            KeepAlive = host.KeepAlive,
            Log = m => log.Info($"{tag} {m}"),
            OnConnected = () => log.Info($"{tag} connected: {config.PublicUrl(s.Name)} -> {s.Target}"),
        };
        var conn = Task.Run(() => ChiselClient.RunAsync(options, run.Token), CancellationToken.None);
        var stopped = false;
        try
        {
            var lastCheck = DateTime.UtcNow;
            var tick = TimeSpan.FromMilliseconds(Math.Min(1000, host.PollInterval.TotalMilliseconds));
            while (true)
            {
                var done = await Task.WhenAny(conn, Task.Delay(tick, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested) { stopped = true; return true; }
                if (done == conn) { await conn.ConfigureAwait(false); log.Info($"{tag} connection closed"); return false; }
                if (File.Exists(StopMarker(host, s.Name))) { log.Info($"{tag} stopped by 'tbus stop'"); stopped = true; return true; }
                if (DateTime.UtcNow - lastCheck < host.PollInterval) continue;
                lastCheck = DateTime.UtcNow;
                try
                {
                    var rows = await bus.ListAsync(ct).ConfigureAwait(false);
                    if (rows.All(r => r.Name != s.Name)) { log.Info($"{tag} the bus no longer knows this name (restart?); re-registering"); return false; }
                }
                catch (Exception e) when (e is BusException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    log.Info($"{tag} bus not reachable ({e.Message}); the connection stays up");
                }
            }
        }
        finally
        {
            run.Cancel();
            try { await conn.ConfigureAwait(false); } catch (Exception) when (stopped || !conn.IsCompletedSuccessfully) { /* surfaced by the loop above when it matters */ }
        }
    }

    private async Task WarnIfUnreachable(ShareSpec s, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(2000);
            await c.ConnectAsync(s.TargetHost, s.TargetPort, t.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            log.Info($"[{s.Name}] warning: nothing answers on {s.Target} from this machine yet (sharing anyway)");
        }
    }
}
