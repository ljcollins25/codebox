using System.Diagnostics;
using System.Net.Sockets;

namespace Tbus;

/// <summary>
/// Runs shares in the foreground: per share register -> chisel client -> watch; reconnect with backoff; re-register when the
/// bus forgot the name (restart) or chisel died; unregister on cancel. One relay (Access headers) is shared by all chisel clients;
/// each share has its own chisel process because every registration has its own chisel user limited to its own port.
/// </summary>
internal sealed class ShareRunner(Host host, AppConfig config, Credentials creds, Log log, BusClient bus, ChiselLocator chisel)
{
    public static string StopMarker(Host host, string name) => Path.Combine(host.Home, "stop", name);

    public async Task<int> RunAsync(IReadOnlyList<ShareSpec> specs, CancellationToken ct)
    {
        var access = creds.AccessHeaders();
        if (access.Count == 0) log.Info("note: no Cloudflare Access credentials (service token or login); the bus will refuse the calls if Access is on.");
        var chiselExe = await chisel.LocateAsync(ct).ConfigureAwait(false);
        await using var relay = new Relay(new Uri(config.Bus), access);
        foreach (var s in specs) { try { File.Delete(StopMarker(host, s.Name)); } catch (IOException) { } }

        log.Info($"bus {config.Bus}; {specs.Count} share(s); Ctrl+C to stop and unregister");
        var tasks = specs.Select(s => Task.Run(() => RunShare(s, chiselExe, relay, ct))).ToArray();
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

    private async Task RunShare(ShareSpec s, string chiselExe, Relay relay, CancellationToken ct)
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
                var reg = await bus.RegisterAsync(s.Name, ct).ConfigureAwait(false);
                log.Info($"{tag} registered (bus port {reg.Port}); starting chisel");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, host.PollInterval.TotalMilliseconds)), ct).ConfigureAwait(false); // chisel reloads its authfile
                var stopped = await RunChisel(s, reg, chiselExe, relay, ct).ConfigureAwait(false);
                if (stopped) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is BusException or HttpRequestException or TaskCanceledException or IOException or System.ComponentModel.Win32Exception)
            {
                log.Error($"{tag} {e.Message}");
            }
            if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(30)) backoff = host.BackoffStart;
            log.Info($"{tag} retrying in {backoff.TotalSeconds:0.#}s");
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, host.BackoffMax.Ticks));
        }
    }

    /// <summary>Runs one chisel client until it exits, the bus forgets the name, or tbus stop/Ctrl+C. True = do not run again.</summary>
    private async Task<bool> RunChisel(ShareSpec s, Registration reg, string chiselExe, Relay relay, CancellationToken ct)
    {
        var tag = $"[{s.Name}]";
        var psi = new ProcessStartInfo(chiselExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "client", "--keepalive", "25s", relay.ChiselUrl, $"R:{reg.Port}:{s.Target}" }) psi.ArgumentList.Add(a);
        // The credentials are passed in chisel's AUTH environment variable (not --auth, which is visible in the process list), and
        // chisel does not get our other secrets.
        foreach (var k in new[] { "TUNNEL_BUS_ADMIN_TOKEN", "CF_ACCESS_CLIENT_ID", "CF_ACCESS_CLIENT_SECRET" }) psi.Environment.Remove(k);
        psi.Environment["AUTH"] = reg.User + ":" + reg.Password;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var connected = false;
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            log.Info($"{tag} chisel: {line.Trim()}");
            if (!connected && line.Contains("Connected", StringComparison.Ordinal))
            {
                connected = true;
                log.Info($"{tag} connected: {config.PublicUrl(s.Name)} -> {s.Target}");
            }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Start();
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();

        try
        {
            var lastCheck = DateTime.UtcNow;
            var tick = TimeSpan.FromMilliseconds(Math.Min(1000, host.PollInterval.TotalMilliseconds));
            while (true)
            {
                var exited = proc.WaitForExitAsync(CancellationToken.None);
                var done = await Task.WhenAny(exited, Task.Delay(tick, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return true;
                if (done == exited) { log.Info($"{tag} chisel exited (code {proc.ExitCode})"); return false; }
                if (File.Exists(StopMarker(host, s.Name))) { log.Info($"{tag} stopped by 'tbus stop'"); return true; }
                if (DateTime.UtcNow - lastCheck < host.PollInterval) continue;
                lastCheck = DateTime.UtcNow;
                try
                {
                    var rows = await bus.ListAsync(ct).ConfigureAwait(false);
                    if (rows.All(r => r.Name != s.Name)) { log.Info($"{tag} the bus no longer knows this name (restart?); re-registering"); return false; }
                }
                catch (Exception e) when (e is BusException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    log.Info($"{tag} bus not reachable ({e.Message}); chisel keeps trying");
                }
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (Exception) { }
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
