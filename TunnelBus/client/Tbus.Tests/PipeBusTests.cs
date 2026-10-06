using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Tbus.Tests;

public class PipeBusTests
{
    [Theory]
    [InlineData("https://r2pipe.x.workers.dev", null, true)]
    [InlineData("https://ctl.ref12.dev", null, false)]
    [InlineData("https://ctl.ref12.dev", "pipe", true)]
    [InlineData("https://r2pipe.x.workers.dev", "chisel", false)]
    public void KindIsChosenByUrlOrSetting(string bus, string? kind, bool pipe) =>
        Assert.Equal(pipe, new AppConfig { Bus = bus, Kind = kind }.IsPipe);

    [Fact]
    public void ViewerUrlsFollowTheSuffix()
    {
        var c = new AppConfig { Kind = "pipe" };
        Assert.Equal("https://app--pipe.ref12.dev/", c.PublicUrl("app"));
        c.ViewerSuffix = "";
        Assert.Equal("https://app.ref12.dev/", c.PublicUrl("app"));
        Assert.Equal("https://app.ref12.dev/", new AppConfig().PublicUrl("app"));
    }

    [Fact]
    public async Task BodyFeedStreamsInlineThenPartsInOrder()
    {
        using var r2 = new HttpListener(); var port = Free(); r2.Prefixes.Add($"http://127.0.0.1:{port}/"); r2.Start();
        _ = Task.Run(async () => { while (r2.IsListening) { try { var c = await r2.GetContextAsync(); var b = Encoding.ASCII.GetBytes(c.Request.Url!.AbsolutePath.Trim('/')); await c.Response.OutputStream.WriteAsync(b); c.Response.Close(); } catch { } } });
        var feed = new PipeProvider.BodyFeed(new HttpClient());
        feed.AddInline(Encoding.ASCII.GetBytes("in-"));
        feed.AddPart($"http://127.0.0.1:{port}/p1", 2); feed.AddPart($"http://127.0.0.1:{port}/p2", 2);
        feed.End();
        using var ms = new MemoryStream(); await feed.CopyToAsync(ms);
        Assert.Equal("in-p1p2", Encoding.ASCII.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task ProviderServesHttpAndWebSocketsOverTheBusSocket()
    {
        // a fake Worker: accepts the provider socket (checking the bearer token), sends a request and a ws-open, reads the answers
        var lp = Free(); var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{lp}/"); l.Start();
        var app = Free(); var appL = new HttpListener(); appL.Prefixes.Add($"http://127.0.0.1:{app}/"); appL.Start();
        _ = Task.Run(async () => { while (appL.IsListening) { try { var c = await appL.GetContextAsync(); if (c.Request.IsWebSocketRequest) { var w = (await c.AcceptWebSocketAsync(null)).WebSocket; var b = new byte[1024]; var r = await w.ReceiveAsync(b, default); await w.SendAsync(b.AsMemory(0, r.Count), r.MessageType, true, default); } else { var o = Encoding.ASCII.GetBytes("hi " + c.Request.RawUrl); await c.Response.OutputStream.WriteAsync(o); c.Response.Close(); } } catch { } } });
        string? auth = null; var got = new List<string>(); var done = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            var c = await l.GetContextAsync(); auth = c.Request.Headers["Authorization"];
            var ws = (await c.AcceptWebSocketAsync(null)).WebSocket;
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"req\",\"rid\":1,\"method\":\"GET\",\"path\":\"/a?b=1\",\"headers\":[],\"hasBody\":false}"), WebSocketMessageType.Text, true, default);
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"ws-open\",\"sid\":9,\"path\":\"/s\",\"headers\":[],\"protocols\":[]}"), WebSocketMessageType.Text, true, default);
            await Task.Delay(500);
            await ws.SendAsync(new byte[] { 4, 0, 0, 0, 9, 1, (byte)'x', (byte)'y' }, WebSocketMessageType.Binary, true, default);
            var buf = new byte[4096]; int bodyOk = 0;
            while (got.Count < 3 && ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, default); if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Text) got.Add(Encoding.UTF8.GetString(buf, 0, r.Count)); else got.Add("bin:" + buf[0] + ":" + Encoding.UTF8.GetString(buf, buf[0] == 5 ? 6 : 5, r.Count - (buf[0] == 5 ? 6 : 5)));
                if (got.Any(g => g == "bin:5:xy") && got.Any(g => g.StartsWith("{\"t\":\"end\""))) { bodyOk = 1; break; }
            }
            done.TrySetResult(); await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default);
        });
        var p = new PipeProvider($"ws://127.0.0.1:{lp}/_bus/ws/n", "pb_secret", "127.0.0.1", app, _ => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = p.RunOnceAsync(() => { }, cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        Assert.Equal("Bearer pb_secret", auth);
        Assert.Contains(got, g => g.Contains("\"t\":\"res\"") && g.Contains("200"));
        Assert.Contains(got, g => g == "bin:3:hi /a?b=1");
        Assert.Contains(got, g => g == "bin:5:xy");
        l.Close(); appL.Close();
    }

    private static int Free() { var s = new TcpListener(IPAddress.Loopback, 0); s.Start(); var p = ((IPEndPoint)s.LocalEndpoint).Port; s.Stop(); return p; }
}
