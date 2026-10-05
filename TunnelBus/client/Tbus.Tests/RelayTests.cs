using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tbus.Tests;

public class RelayTests
{
    private static async Task<string> ReadHead(Stream s)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(one) == 0) break; sb.Append((char)one[0]); }
        return sb.ToString();
    }

    [Fact]
    public async Task Adds_access_headers_rewrites_path_and_host_and_pipes_bytes_both_ways()
    {
        // fake "bus": reads the request head, answers 101, then echoes
        var up = new TcpListener(IPAddress.Loopback, 0); up.Start();
        var upPort = ((IPEndPoint)up.LocalEndpoint).Port;
        string? seenHead = null;
        var upstream = Task.Run(async () =>
        {
            using var c = await up.AcceptTcpClientAsync(); var s = c.GetStream();
            seenHead = await ReadHead(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"));
            var buf = new byte[64]; var n = await s.ReadAsync(buf);
            await s.WriteAsync(buf.AsMemory(0, n));
        });

        var headers = new Dictionary<string, string> { ["CF-Access-Client-Id"] = "the-id", ["CF-Access-Client-Secret"] = "the-secret" };
        await using var relay = new Relay(new Uri($"http://127.0.0.1:{upPort}"), headers);
        var u = new Uri(relay.ChiselUrl);
        Assert.Equal("127.0.0.1", u.Host);

        using var client = new TcpClient(); await client.ConnectAsync(u.Host, u.Port);
        var cs = client.GetStream();
        await cs.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {u.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1:{u.Port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nCF-Access-Client-Secret: forged\r\nSec-WebSocket-Key: abc\r\n\r\n"));
        var resp = await ReadHead(cs).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 101", resp);
        await cs.WriteAsync(Encoding.ASCII.GetBytes("ping-bytes"));
        var echo = new byte[10]; var read = 0;
        while (read < 10) read += await cs.ReadAsync(echo.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ping-bytes", Encoding.ASCII.GetString(echo));
        await upstream.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith("GET /_chisel HTTP/1.1\r\n", seenHead);
        Assert.Contains($"Host: 127.0.0.1:{upPort}\r\n", seenHead);
        Assert.Contains("CF-Access-Client-Id: the-id\r\n", seenHead);
        Assert.Contains("CF-Access-Client-Secret: the-secret\r\n", seenHead);
        Assert.DoesNotContain("forged", seenHead);
        Assert.Contains("Sec-WebSocket-Key: abc", seenHead);
        up.Stop();
    }

    [Fact]
    public async Task Paths_outside_the_random_prefix_get_404_and_never_reach_the_bus()
    {
        var up = new TcpListener(IPAddress.Loopback, 0); up.Start();
        await using var relay = new Relay(new Uri($"http://127.0.0.1:{((IPEndPoint)up.LocalEndpoint).Port}"), new Dictionary<string, string> { ["x"] = "y" });
        var port = new Uri(relay.ChiselUrl).Port;
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", port);
        var cs = client.GetStream();
        await cs.WriteAsync(Encoding.ASCII.GetBytes("GET /_chisel HTTP/1.1\r\nHost: x\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 404", await ReadHead(cs).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(up.Pending());
        up.Stop();
    }
}
