using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Tbus.Tests;

/// <summary>A running chisel server, a user limited to one remote port, a client connected through it, and a target on this machine.</summary>
internal sealed class ClientRig : IAsyncDisposable
{
    public ChiselServer Server = null!;
    public int RemotePort;
    public TcpListener? Target;
    public int TargetPort;
    public CancellationTokenSource Cts = new();
    public Task? Client;
    public readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly List<string> Log = new();

    public static ClientRig Create(string? exe, Action<string>? onSkip = null)
    {
        Skip.If(exe == null, ChiselBinary.WhyNot);
        return new ClientRig();
    }

    public async Task StartServerAsync(int? keepAlive = null, string? allow = null)
    {
        RemotePort = ChiselServer.FreePort();
        Server = new ChiselServer(ChiselBinary.Locate()!, new() { ["sentinel"] = ("x", ["^$"]), ["u1"] = ("pw1", [allow ?? $"^R:0\\.0\\.0\\.0:{RemotePort}$"]) }, keepAlive);
        await Server.StartAsync();
    }

    public void StartTarget(Func<Socket, Task> handler)
    {
        Target = new TcpListener(IPAddress.Loopback, 0); Target.Start(); TargetPort = ((IPEndPoint)Target.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket s;
                try { s = await Target.AcceptSocketAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () => { try { await handler(s); } catch (Exception) { } finally { s.Dispose(); } });
            }
        });
    }

    public ChiselOptions Options(string user = "u1", string pw = "pw1", int? remotePort = null, int? targetPort = null, TimeSpan? keepAlive = null) => new()
    {
        Server = new Uri(Server.Url.Replace("http", "ws") + "/"),
        User = user, Password = pw,
        Remotes = [new ChiselRemote(remotePort ?? RemotePort, "127.0.0.1", targetPort ?? TargetPort)],
        KeepAlive = keepAlive ?? TimeSpan.FromSeconds(1),
        Log = m => { lock (Log) Log.Add(m); },
        OnConnected = () => Connected.TrySetResult(),
    };

    public void StartClient(ChiselOptions? o = null) => Client = Task.Run(() => ChiselClient.RunAsync(o ?? Options(), Cts.Token));

    public async Task WaitConnected()
    {
        var done = await Task.WhenAny(Connected.Task, Client!, Task.Delay(20000));
        if (done == Client) await Client; // throws the reason
        Assert.Same(Connected.Task, done);
    }

    public async Task<TcpClient> ConnectRemote()
    {
        // the server answers the config request, then binds the remote's port: the first connect can be a moment early
        for (var attempt = 0; ; attempt++)
        {
            var c = new TcpClient { NoDelay = true };
            try { await c.ConnectAsync(IPAddress.Loopback, RemotePort); return c; }
            catch (SocketException) when (attempt < 100) { c.Dispose(); await Task.Delay(50); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cts.Cancel();
        if (Client != null) { try { await Client.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { } }
        Target?.Stop();
        Server?.Dispose();
    }

    public static async Task ReadExactly(Stream s, byte[] buf, int n)
    {
        var got = 0;
        while (got < n) { var r = await s.ReadAsync(buf.AsMemory(got, n - got)); if (r == 0) throw new EndOfStreamException($"got {got} of {n}"); got += r; }
    }

    public static Task Echo(Socket s) => EchoCore(s);
    private static async Task EchoCore(Socket s)
    {
        using var ns = new NetworkStream(s);
        var buf = new byte[16384]; int n;
        while ((n = await ns.ReadAsync(buf)) > 0) await ns.WriteAsync(buf.AsMemory(0, n));
    }
}

[Collection("serial")]
public class ChiselClientTests
{
    [Fact]
    public void Config_json_has_chisels_shape()
    {
        var json = Encoding.UTF8.GetString(ChiselClient.EncodeConfig([new ChiselRemote(20000, "127.0.0.1", 3000)]));
        Assert.Equal("{\"Version\":\"1.10.1\",\"Remotes\":[{\"LocalHost\":\"0.0.0.0\",\"LocalPort\":\"20000\",\"LocalProto\":\"tcp\",\"RemoteHost\":\"127.0.0.1\",\"RemotePort\":\"3000\",\"RemoteProto\":\"tcp\",\"Socks\":false,\"Reverse\":true,\"Stdio\":false}]}", json);
    }

    [Theory]
    [InlineData("https://ctl.ref12.dev", "wss://ctl.ref12.dev/_chisel")]
    [InlineData("https://ctl.ref12.dev/", "wss://ctl.ref12.dev/_chisel")]
    [InlineData("http://127.0.0.1:8080", "ws://127.0.0.1:8080/_chisel")]
    [InlineData("https://bus.example:8443", "wss://bus.example:8443/_chisel")]
    public void Websocket_url(string bus, string expected) => Assert.Equal(expected, ChiselClient.WebSocketUrl(bus).ToString());

    [Fact]
    public void Fingerprint_is_base64_sha256_of_the_key_blob()
    {
        var blob = Encoding.ASCII.GetBytes("some key blob");
        var fp = ChiselClient.Fingerprint(blob);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(blob)), fp);
        Assert.True(ChiselClient.FingerprintMatches(fp, blob));
        Assert.False(ChiselClient.FingerprintMatches(fp + "x", blob));
        var md5 = string.Join(":", MD5.HashData(blob).Select(b => b.ToString("x2")));
        Assert.True(ChiselClient.FingerprintMatches(md5[..14], blob)); // legacy prefix form
    }

    [SkippableFact]
    public async Task Reverse_remote_round_trip()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(ClientRig.Echo);
        rig.StartClient(); await rig.WaitConnected();

        using var c = await rig.ConnectRemote();
        var s = c.GetStream();
        var msg = Encoding.UTF8.GetBytes("hello through the tunnel");
        await s.WriteAsync(msg);
        var back = new byte[msg.Length];
        await ClientRig.ReadExactly(s, back, back.Length);
        Assert.Equal(msg, back);
    }

    [SkippableFact]
    public async Task Many_concurrent_connections_keep_their_own_data()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(ClientRig.Echo);
        rig.StartClient(); await rig.WaitConnected();

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            using var c = await rig.ConnectRemote();
            var s = c.GetStream();
            var rnd = new Random(i);
            for (var round = 0; round < 5; round++)
            {
                var data = new byte[1 + rnd.Next(50000)]; rnd.NextBytes(data);
                var write = s.WriteAsync(data).AsTask();
                var back = new byte[data.Length];
                await ClientRig.ReadExactly(s, back, back.Length);
                await write;
                Assert.True(data.AsSpan().SequenceEqual(back), $"connection {i} round {round} got other bytes");
            }
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(90));
    }

    [SkippableFact]
    public async Task Fifty_megabytes_each_way_with_checksum_and_backpressure()
    {
        const long Size = 50L * 1024 * 1024;
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        // target: reads Size bytes slowly at first (so the SSH window fills and the sender must wait), answers with their SHA-256,
        // then streams Size pseudo random bytes back and finishes with their SHA-256
        rig.StartTarget(async s =>
        {
            using var ns = new NetworkStream(s);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[64 * 1024]; long got = 0; var first = true;
            while (got < Size)
            {
                var n = await ns.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, Size - got)));
                if (n == 0) return;
                sha.AppendData(buf, 0, n); got += n;
                if (first) { first = false; await Task.Delay(1500); } // a stalled consumer: backpressure
            }
            await ns.WriteAsync(sha.GetHashAndReset());
            var rnd = new Random(7); long sent = 0;
            while (sent < Size)
            {
                var n = (int)Math.Min(buf.Length, Size - sent); rnd.NextBytes(buf.AsSpan(0, n));
                sha.AppendData(buf, 0, n); await ns.WriteAsync(buf.AsMemory(0, n)); sent += n;
            }
            await ns.WriteAsync(sha.GetHashAndReset());
            await ns.FlushAsync();
            var one = new byte[1]; await ns.ReadAsync(one); // wait until the client has everything and closes
        });
        rig.StartClient(); await rig.WaitConnected();

        using var c = await rig.ConnectRemote();
        var s = c.GetStream();
        var upSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var up = Task.Run(async () =>
        {
            var rnd = new Random(3); var buf = new byte[64 * 1024]; long sent = 0;
            while (sent < Size)
            {
                var n = (int)Math.Min(buf.Length, Size - sent); rnd.NextBytes(buf.AsSpan(0, n));
                upSha.AppendData(buf, 0, n); await s.WriteAsync(buf.AsMemory(0, n)); sent += n;
            }
        });
        var targetDigest = new byte[32];
        await ClientRig.ReadExactly(s, targetDigest, 32);
        await up;
        Assert.Equal(upSha.GetHashAndReset(), targetDigest);

        var downSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rbuf = new byte[64 * 1024]; long got = 0;
        while (got < Size)
        {
            var n = await s.ReadAsync(rbuf.AsMemory(0, (int)Math.Min(rbuf.Length, Size - got)));
            Assert.True(n > 0, "download ended early at " + got);
            downSha.AppendData(rbuf, 0, n); got += n;
        }
        var digest = new byte[32]; await ClientRig.ReadExactly(s, digest, 32);
        Assert.Equal(downSha.GetHashAndReset(), digest);
    }

    /// <summary>The target sends N bytes and closes at once; the remote side must receive every byte, then EOF (regression: data lost at the close).</summary>
    [SkippableTheory]
    [InlineData(1, 50)]
    [InlineData(4, 25)]
    [InlineData(8, 25)]
    public async Task Target_that_sends_a_lot_and_closes_loses_nothing(int streams, int mb)
    {
        var size = (long)mb * 1024 * 1024;
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(async s =>
        {
            var buf = new byte[64 * 1024]; var rnd = new Random(11);
            using var ns = new NetworkStream(s);
            for (long sent = 0; sent < size;) { var n = (int)Math.Min(buf.Length, size - sent); rnd.NextBytes(buf.AsSpan(0, n)); await ns.WriteAsync(buf.AsMemory(0, n)); sent += n; }
            // no waiting, no shutdown dance: the socket is disposed right after the last write
        });
        rig.StartClient(); await rig.WaitConnected();
        var expected = new byte[32];
        { var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buf = new byte[64 * 1024]; var rnd = new Random(11);
          for (long sent = 0; sent < size;) { var n = (int)Math.Min(buf.Length, size - sent); rnd.NextBytes(buf.AsSpan(0, n)); h.AppendData(buf, 0, n); sent += n; } expected = h.GetHashAndReset(); }
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
        {
            using var c = await rig.ConnectRemote();
            var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buf = new byte[64 * 1024]; long got = 0; int n;
            var s = c.GetStream();
            while ((n = await s.ReadAsync(buf)) > 0) { h.AppendData(buf, 0, n); got += n; }
            Assert.Equal(size, got);
            Assert.Equal(expected, h.GetHashAndReset());
        })).WaitAsync(TimeSpan.FromSeconds(120));
    }

    [SkippableFact]
    public async Task Websocket_through_the_tunnel()

    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        var http = new HttpListener();
        var wsPort = ChiselServer.FreePort();
        http.Prefixes.Add($"http://127.0.0.1:{wsPort}/"); http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                        var buf = new byte[65536];
                        while (true)
                        {
                            var r = await ws.ReceiveAsync(buf, default);
                            if (r.MessageType == WebSocketMessageType.Close) { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default); return; }
                            await ws.SendAsync(buf.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, default);
                        }
                    }
                    catch (Exception) { }
                });
            }
        });
        try
        {
            rig.StartClient(rig.Options(targetPort: wsPort)); await rig.WaitConnected();
            for (var k = 0; k < 5; k++)
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.RemotePort}/"), default);
                for (var i = 0; i < 20; i++)
                {
                    var data = Encoding.UTF8.GetBytes($"message {k}/{i} " + new string('x', i * 1000));
                    await ws.SendAsync(data, WebSocketMessageType.Text, true, default);
                    var buf = new byte[100000]; var got = 0; WebSocketReceiveResult r;
                    do { r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, got, buf.Length - got), default); got += r.Count; } while (!r.EndOfMessage);
                    Assert.Equal(data, buf[..got]);
                }
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
            }
        }
        finally { http.Stop(); }
    }

    [SkippableFact]
    public async Task A_remote_the_authfile_does_not_allow_is_refused_with_the_servers_reason()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(ClientRig.Echo);
        var other = ChiselServer.FreePort();
        var ex = await Assert.ThrowsAsync<ChiselRefusedException>(() => ChiselClient.RunAsync(rig.Options(remotePort: other), CancellationToken.None));
        Assert.Contains($"access to 'R:0.0.0.0:{other}' denied", ex.Message);
        // and the allowed one still works afterwards
        rig.StartClient(); await rig.WaitConnected();
    }

    [SkippableFact]
    public async Task Wrong_password_is_refused()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(ClientRig.Echo);
        var ex = await Assert.ThrowsAsync<ChiselRefusedException>(() => ChiselClient.RunAsync(rig.Options(pw: "nope"), CancellationToken.None));
        Assert.Contains("authentication failed", ex.Message);
    }

    [SkippableFact]
    public async Task A_target_that_refuses_rejects_the_channel_and_the_tunnel_keeps_working()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        // a port nothing listens on
        var dead = ChiselServer.FreePort();
        rig.StartClient(rig.Options(targetPort: dead)); await rig.WaitConnected();
        for (var i = 0; i < 3; i++)
        {
            TcpClient c;
            try { c = await rig.ConnectRemote(); } catch (Exception e) { throw new Exception($"iteration {i}: {e.Message}; client done={rig.Client!.IsCompleted} log: " + string.Join("; ", rig.Log) + " || server: " + rig.Server.Log); }
            using var _c = c;
            var n = await c.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); // closed by the server, no data
            Assert.Equal(0, n);
        }
        Assert.Contains(rig.Log, m => m.StartsWith($"cannot reach 127.0.0.1:{dead}"));
        // the session is still up: start the target now and use the same tunnel
        rig.Target = new TcpListener(IPAddress.Loopback, dead); rig.Target.Start();
        _ = Task.Run(async () => { while (true) { Socket s; try { s = await rig.Target.AcceptSocketAsync(); } catch (Exception) { return; } _ = Task.Run(() => ClientRig.Echo(s)); } });
        Assert.False(rig.Client!.IsCompleted, "session ended: " + string.Join("; ", rig.Log) + " || " + rig.Server.Log);
        using var ok = await rig.ConnectRemote();
        await ok.GetStream().WriteAsync("ping"u8.ToArray());
        var b = new byte[4]; await ClientRig.ReadExactly(ok.GetStream(), b, 4);
        Assert.Equal("ping", Encoding.ASCII.GetString(b));
    }

    [SkippableFact]
    public async Task Half_close_from_the_remote_side_reaches_the_target()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        var gotEof = new TaskCompletionSource<string>();
        rig.StartTarget(async s =>
        {
            using var ns = new NetworkStream(s);
            var all = new MemoryStream(); await ns.CopyToAsync(all); // ends when the other side's EOF arrives
            gotEof.TrySetResult(Encoding.ASCII.GetString(all.ToArray()));
        });
        rig.StartClient(); await rig.WaitConnected();
        using var c = await rig.ConnectRemote();
        await c.GetStream().WriteAsync("last words"u8.ToArray());
        c.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal("last words", await gotEof.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [SkippableFact]
    public async Task Target_closing_closes_the_remote_connection_after_its_data()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(async s => { await s.SendAsync(Encoding.ASCII.GetBytes("bye")); s.Shutdown(SocketShutdown.Both); });
        rig.StartClient(); await rig.WaitConnected();
        using var c = await rig.ConnectRemote();
        var ms = new MemoryStream();
        await c.GetStream().CopyToAsync(ms).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("bye", Encoding.ASCII.GetString(ms.ToArray()));
    }

    [SkippableFact]
    public async Task Keepalive_pings_are_answered_in_both_directions()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync(keepAlive: 1); // the server pings us every second as well
        rig.StartTarget(ClientRig.Echo);
        rig.StartClient(rig.Options(keepAlive: TimeSpan.FromMilliseconds(500))); await rig.WaitConnected();
        await Task.Delay(4000);
        Assert.False(rig.Client!.IsCompleted, "the connection died: " + string.Join("; ", rig.Log));
        using var c = await rig.ConnectRemote();
        await c.GetStream().WriteAsync("x"u8.ToArray());
        var b = new byte[1]; await ClientRig.ReadExactly(c.GetStream(), b, 1);
    }

    [SkippableFact]
    public async Task Server_going_away_ends_the_connection_so_the_caller_can_reconnect()
    {
        await using var rig = ClientRig.Create(ChiselBinary.Locate());
        await rig.StartServerAsync();
        rig.StartTarget(ClientRig.Echo);
        rig.StartClient(); await rig.WaitConnected();
        rig.Server.Stop();
        await rig.Client!.WaitAsync(TimeSpan.FromSeconds(15)); // returns normally: the connection was lost
        // a new connection works against the restarted server
        await rig.Server.StartAsync();
        rig.Connected.TrySetResult(); // (first one already fired)
        var again = new TaskCompletionSource();
        var o = rig.Options(); 
        var opts = new ChiselOptions { Server = o.Server, User = o.User, Password = o.Password, Remotes = o.Remotes, KeepAlive = o.KeepAlive, OnConnected = () => again.TrySetResult() };
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => ChiselClient.RunAsync(opts, cts.Token));
        await again.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using var c = await rig.ConnectRemote();
        await c.GetStream().WriteAsync("again"u8.ToArray());
        var b = new byte[5]; await ClientRig.ReadExactly(c.GetStream(), b, 5);
        Assert.Equal("again", Encoding.ASCII.GetString(b));
        cts.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
