using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;

namespace Tbus;

/// <summary>A reverse remote: the server listens on <see cref="ServerPort"/> and sends connections to Host:Port from this machine.</summary>
internal sealed record ChiselRemote(int ServerPort, string Host, int Port)
{
    /// <summary>What chisel calls the remote string (for logs): R:port:host:port.</summary>
    public override string ToString() => $"R:{ServerPort}:{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
}

internal sealed class ChiselOptions
{
    public required Uri Server { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public required string User { get; init; }
    public required string Password { get; init; }
    public required IReadOnlyList<ChiselRemote> Remotes { get; init; }
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(25);
    /// <summary>chisel --fingerprint: base64 SHA-256 of the server host key (or the legacy MD5 hex form). Null = do not check.</summary>
    public string? Fingerprint { get; init; }
    public Action<string>? Log { get; init; }
    public Action? OnConnected { get; init; }
    public Func<string, int, CancellationToken, Task<Socket>>? Dial { get; init; }
    /// <summary>Tuning (benchmarks): receive window advertised for each channel (library default 1 MiB), websocket buffer size, copy buffer size.</summary>
    public uint? ChannelWindow { get; init; }
    public int? WsBufferSize { get; init; }
    public int? CopyBuffer { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The server refused us (bad credentials, a remote the authfile does not allow, a wrong fingerprint).</summary>
internal sealed class ChiselRefusedException(string message) : Exception(message);

/// <summary>
/// chisel 1.10.x client protocol, reverse remotes only. A WebSocket (subprotocol "chisel-v3") carries an SSH connection
/// ("SSH-chisel-v3-client"): password auth, then a "config" global request whose payload is JSON {Version, Remotes}; the server
/// replies success or a failure with the reason as text. Afterwards the server opens "chisel" channels (extra data = "host:port") for every
/// connection to a remote's port; we dial that target and copy bytes both ways. "ping" global requests are answered with "pong" and
/// we send our own at the keepalive interval.
/// </summary>
internal static class ChiselClient
{
    public const string Protocol = "chisel-v3";
    public const string Version = "1.10.1";
    public const string ChannelType = "chisel";

    /// <summary>The JSON payload of the config request: the same shape chisel's settings.Config encodes (Go field names, no tags).</summary>
    public static byte[] EncodeConfig(IReadOnlyList<ChiselRemote> remotes)
    {
        var o = new
        {
            Version,
            Remotes = remotes.Select(r => new
            {
                LocalHost = "0.0.0.0", LocalPort = r.ServerPort.ToString(), LocalProto = "tcp",
                RemoteHost = r.Host, RemotePort = r.Port.ToString(), RemoteProto = "tcp",
                Socks = false, Reverse = true, Stdio = false,
            }).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(o);
    }

    public static Uri WebSocketUrl(string bus)
    {
        var u = new UriBuilder(bus.TrimEnd('/') + "/_chisel");
        u.Scheme = u.Scheme == Uri.UriSchemeHttps || u.Scheme == "wss" ? "wss" : "ws";
        u.Port = u.Uri.IsDefaultPort || u.Port == 443 && u.Scheme == "wss" || u.Port == 80 && u.Scheme == "ws" ? -1 : u.Port;
        return u.Uri;
    }

    /// <summary>chisel's key fingerprint: base64 (standard, padded) of the SHA-256 of the key in SSH wire format.</summary>
    public static string Fingerprint(byte[] keyBlob) => Convert.ToBase64String(SHA256.HashData(keyBlob));

    public static bool FingerprintMatches(string expected, byte[] keyBlob)
    {
        if (expected == Fingerprint(keyBlob)) return true;
        // legacy: colon separated MD5 hex, a prefix is enough (chisel accepts that for non-base64 values)
        var md5 = string.Join(":", MD5.HashData(keyBlob).Select(b => b.ToString("x2")));
        return expected.Contains(':') && md5.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One connection: dial, handshake, send config, then serve channels until the SSH session ends. Returns normally when the
    /// connection was lost after it was established (or cancelled); throws <see cref="ChiselRefusedException"/> for refusals and
    /// other exceptions for connection failures. <see cref="ChiselOptions.OnConnected"/> fires once the server accepted the config.
    /// </summary>
    public static async Task RunAsync(ChiselOptions o, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(Protocol);
        foreach (var (k, v) in o.Headers) ws.Options.SetRequestHeader(k, v);
        if (o.WsBufferSize is { } wb) ws.Options.SetBuffer(wb, wb);
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(o.ConnectTimeout);
            try { await ws.ConnectAsync(o.Server, t.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("timed out connecting to " + o.Server); }
            catch (WebSocketException e) { throw new IOException("websocket connect failed: " + (e.InnerException?.Message ?? e.Message), e); }
        }
        // (chisel's Go client does not insist on the server echoing the subprotocol back either: the server only checks the request)

        var wsStream = new WebSocketStream(ws, FixBanner: true);
        await using var stream = new StreamHolder(wsStream);
        var config = new SshSessionConfiguration(useSecurity: true);
        config.KeepAliveTimeoutInSeconds = 0; // chisel's own ping handles liveness
        using var session = new SshClientSession(config, SshTrace());
        ChiselSshCompat.Attach(session, wsStream);
        session.Authenticating += (_, e) => OnAuthenticating(o, e);

        // channels the server opens for each connection to a reverse remote's port
        var conns = new List<Task>();
        var connLock = new object();
        var pending = new System.Collections.Concurrent.ConcurrentDictionary<SshChannel, (string Target, Task<Socket> Dial)>();
        session.ChannelOpening += (_, e) =>
        {
            if (e.IsRemoteRequest && e.Request.ChannelType == ChannelType) HandleChannelOpening(o, e, pending, ct);
            else if (e.IsRemoteRequest) { e.FailureReason = SshChannelOpenFailureReason.UnknownChannelType; e.FailureDescription = "only chisel channels"; }
        };
        session.Request += (_, e) =>
        {
            if (e.RequestType == "ping") e.ResponseTask = Task.FromResult<SshMessage>(new ChiselSuccessMessage { Payload = Encoding.ASCII.GetBytes("pong") });
        };

        var closed = new TaskCompletionSource();
        session.Closed += (_, _) => closed.TrySetResult();
        using var reg = ct.Register(() => { try { session.Dispose(); } catch (Exception) { } closed.TrySetResult(); });

        try
        {
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(o.ConnectTimeout);
                try
                {
                    await session.ConnectAsync(stream.Inner, t.Token).ConfigureAwait(false);
                    if (!await session.AuthenticateAsync(new SshClientCredentials(o.User, o.Password), t.Token).ConfigureAwait(false))
                        throw new ChiselRefusedException("authentication failed (wrong user or password)");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("timed out in the SSH handshake"); }
                catch (SshConnectionException e) when (e.DisconnectReason == SshDisconnectReason.NoMoreAuthMethodsAvailable) { throw new ChiselRefusedException("authentication failed (wrong user or password)"); }
                catch (SshConnectionException e) when (e.Message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)) { throw new ChiselRefusedException(e.Message); }
            }

            // the chisel handshake: config request, the server answers success or a failure whose payload is the reason
            var req = new ChiselRequestMessage { RequestType = "config", WantReply = true, Payload = EncodeConfig(o.Remotes) };
            (ChiselSuccessMessage? Success, ChiselFailureMessage? Failure) answer;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(o.ConnectTimeout);
                answer = await session.RequestAsync<ChiselSuccessMessage, ChiselFailureMessage>(req, t.Token).ConfigureAwait(false);
            }
            if (answer.Success == null)
            {
                var why = answer.Failure?.Reason;
                throw new ChiselRefusedException("the server refused the config" + (string.IsNullOrWhiteSpace(why) ? " (is a remote not allowed for this user?)" : ": " + why));
            }
            _ = AcceptLoop(session, o, pending, conns, connLock, ct);
            o.OnConnected?.Invoke();

            using var ka = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (o.KeepAlive > TimeSpan.Zero) _ = KeepAliveLoop(session, o.KeepAlive, o.Log, ka.Token);
            await closed.Task.ConfigureAwait(false);
            ka.Cancel();
        }
        finally
        {
            try { session.Dispose(); } catch (Exception) { }
            Task[] live; lock (connLock) live = conns.ToArray();
            try { await Task.WhenAll(live).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <summary>TBUS_SSH_TRACE=1 prints the SSH library's trace to stderr (the trace never contains passwords).</summary>
    private static System.Diagnostics.TraceSource SshTrace()
    {
        var ts = new System.Diagnostics.TraceSource("tbus-ssh");
        if (Environment.GetEnvironmentVariable("TBUS_SSH_TRACE") == "1")
        {
            ts.Switch.Level = System.Diagnostics.SourceLevels.Verbose;
            ts.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        }
        return ts;
    }

    private static void OnAuthenticating(ChiselOptions o, SshAuthenticatingEventArgs e)
    {
        if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
        {
            if (o.Fingerprint != null)
            {
                var blob = e.PublicKey!.GetPublicKeyBytes().ToArray();
                if (!FingerprintMatches(o.Fingerprint, blob)) { e.AuthenticationTask = Task.FromResult<System.Security.Claims.ClaimsPrincipal?>(null); o.Log?.Invoke($"invalid fingerprint ({Fingerprint(blob)})"); return; }
            }
            e.AuthenticationTask = Task.FromResult<System.Security.Claims.ClaimsPrincipal?>(new System.Security.Claims.ClaimsPrincipal());
        }
        else e.AuthenticationTask = Task.FromResult<System.Security.Claims.ClaimsPrincipal?>(new System.Security.Claims.ClaimsPrincipal());
    }

    private static async Task KeepAliveLoop(SshClientSession session, TimeSpan every, Action<string>? log, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(TimeSpan.FromSeconds(30));
                var (reply, _) = await session.RequestAsync<ChiselSuccessMessage, ChiselFailureMessage>(
                    new ChiselRequestMessage { RequestType = "ping", WantReply = true }, t.Token).ConfigureAwait(false);
                if (reply == null || (reply.Payload.Length > 0 && Encoding.ASCII.GetString(reply.Payload) != "pong")) { log?.Invoke("keepalive: unexpected reply, closing"); break; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) { log?.Invoke("keepalive failed: " + e.Message); }
        try { session.Dispose(); } catch (Exception) { }
    }

    /// <summary>
    /// Channel opens are answered one at a time by the SSH loop, so the dial gets a short head start (<see cref="DialGrace"/>): a target that
    /// refuses quickly (the normal case) rejects the channel with "connect failed". A slow target is accepted and its dial finishes in the
    /// background; if it fails the channel is closed (what chisel does for every failure).
    /// </summary>
    internal static TimeSpan DialGrace = TimeSpan.FromSeconds(2);

    private static void HandleChannelOpening(ChiselOptions o, SshChannelOpeningEventArgs e, System.Collections.Concurrent.ConcurrentDictionary<SshChannel, (string Target, Task<Socket> Dial)> pending, CancellationToken ct)
    {
        var target = e.Request.ConvertTo<ChiselChannelOpenMessage>().Target;
        if (o.ChannelWindow is { } win) e.Channel.MaxWindowSize = win;
        var dial = DialAsync(o, target, ct);
        pending[e.Channel] = (target, dial);
        e.OpeningTask = Task.Run<ChannelMessage>(async () =>
        {
            await Task.WhenAny(dial, Task.Delay(DialGrace, ct)).ConfigureAwait(false);
            if (dial.IsFaulted || dial.IsCanceled)
            {
                pending.TryRemove(e.Channel, out _);
                var msg = dial.Exception?.GetBaseException().Message ?? "cancelled";
                o.Log?.Invoke($"cannot reach {target}: {msg}");
                ChiselSshCompat.PrepareForRejection(e.Channel);
                return new ChannelOpenFailureMessage { ReasonCode = SshChannelOpenFailureReason.ConnectFailed, Description = msg };
            }
            return new ChannelOpenConfirmationMessage();
        });
    }

    private static async Task AcceptLoop(SshClientSession session, ChiselOptions o, System.Collections.Concurrent.ConcurrentDictionary<SshChannel, (string Target, Task<Socket> Dial)> pending, List<Task> conns, object connLock, CancellationToken ct)
    {
        while (true)
        {
            SshChannel ch;
            try { ch = await session.AcceptChannelAsync(ChannelType, ct).ConfigureAwait(false); }
            catch (Exception) { return; } // session ended or cancelled
            if (!pending.TryRemove(ch, out var p)) { _ = ch.CloseAsync(CancellationToken.None); continue; }
            var pipe = Task.Run(async () =>
            {
                Socket sock;
                try { sock = await p.Dial.ConfigureAwait(false); }
                catch (Exception ex) { o.Log?.Invoke($"cannot reach {p.Target}: {ex.Message}"); try { await ch.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } return; }
                await Pipe.RunAsync(sock, ch, o.Log, ct, o.CopyBuffer ?? 32 * 1024).ConfigureAwait(false);
            });
            lock (connLock) { conns.RemoveAll(t => t.IsCompleted); conns.Add(pipe); }
        }
    }


    private static async Task<Socket> DialAsync(ChiselOptions o, string target, CancellationToken ct)
    {
        var i = target.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(target[(i + 1)..], out var port)) throw new IOException($"bad target '{target}'");
        var host = target[..i].Trim('[', ']');
        if (o.Dial != null) return await o.Dial(host, port, ct).ConfigureAwait(false);
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(TimeSpan.FromSeconds(30));
            await s.ConnectAsync(host, port, t.Token).ConfigureAwait(false);
            return s;
        }
        catch { s.Dispose(); throw; }
    }
}

/// <summary>Owns the websocket stream for the life of one connection.</summary>
internal sealed class StreamHolder(Stream inner) : IAsyncDisposable
{
    public Stream Inner { get; } = inner;
    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
