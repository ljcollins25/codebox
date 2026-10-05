using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Tbus;

/// <summary>
/// Loopback relay between chisel and the bus that adds the Cloudflare Access headers. chisel can only take headers as
/// command-line arguments, which every local process can list, so chisel dials this relay (http://127.0.0.1:port/secret) and the
/// secrets stay inside tbus. It listens on 127.0.0.1 only, serves only paths below a random prefix, and is a plain HTTP/1.1 upgrade
/// pipe: it rewrites the request head (path, Host, Access headers), then copies bytes both ways (WebSocket frames pass untouched).
/// </summary>
internal sealed class Relay : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Uri _bus;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _prefix;
    private readonly bool _acceptAnyCert;

    public string LocalUrl { get; }

    public Relay(Uri bus, IReadOnlyDictionary<string, string> headers, bool trustAnyCert = false)
    {
        _bus = bus; _headers = headers; _acceptAnyCert = trustAnyCert;
        _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_prefix}";
        _loop = Task.Run(AcceptLoop);
    }

    /// <summary>The chisel server URL: the relay prefix plus the bus's chisel endpoint.</summary>
    public string ChiselUrl => LocalUrl + "/_chisel";

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _c = client;
        TcpClient? upstream = null;
        try
        {
            var cs = client.GetStream();
            var (head, extra) = await ReadHead(cs, _cts.Token).ConfigureAwait(false);
            if (head == null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || !(parts[1] == _prefix || parts[1].StartsWith(_prefix + "/") || parts[1].StartsWith(_prefix + "?")))
            {
                await cs.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
                return;
            }
            var path = parts[1][_prefix.Length..]; if (path.Length == 0 || path[0] == '?') path = "/" + path;
            var sb = new StringBuilder();
            sb.Append(parts[0]).Append(' ').Append(path).Append(' ').Append(parts[2]).Append("\r\n");
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                var name = line[..Math.Max(0, line.IndexOf(':'))];
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.StartsWith("cf-access-", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(line).Append("\r\n");
            }
            sb.Append("Host: ").Append(_bus.IsDefaultPort ? _bus.Host : _bus.Authority).Append("\r\n");
            foreach (var (k, v) in _headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
            sb.Append("\r\n");

            upstream = new TcpClient { NoDelay = true };
            await upstream.ConnectAsync(_bus.Host, _bus.Port, _cts.Token).ConfigureAwait(false);
            Stream us = upstream.GetStream();
            if (_bus.Scheme == Uri.UriSchemeHttps)
            {
                var ssl = new SslStream(us, false, _acceptAnyCert ? (_, _, _, _) => true : null);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _bus.Host }, _cts.Token).ConfigureAwait(false);
                us = ssl;
            }
            await us.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token).ConfigureAwait(false);
            if (extra.Length > 0) await us.WriteAsync(extra, _cts.Token).ConfigureAwait(false);
            await us.FlushAsync(_cts.Token).ConfigureAwait(false);
            var up = cs.CopyToAsync(us, _cts.Token);
            var down = us.CopyToAsync(cs, _cts.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception) { /* connection ended or cancelled */ }
        finally { upstream?.Dispose(); }
    }

    private static async Task<(string? head, byte[] extra)> ReadHead(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[16384]; var n = 0;
        while (n < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false);
            if (r == 0) return (null, []);
            n += r;
            var end = Find(buf, n);
            if (end >= 0) return (Encoding.ASCII.GetString(buf, 0, end), buf[(end + 4)..n]);
        }
        return (null, []);
    }

    private static int Find(byte[] b, int n)
    {
        for (var i = 0; i + 3 < n; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop.ConfigureAwait(false); } catch (Exception) { }
        _cts.Dispose();
    }
}
