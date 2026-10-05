using System.Net.Sockets;
using Microsoft.DevTunnels.Ssh;

namespace Tbus;

/// <summary>Copies bytes between a TCP socket and an SSH channel until either side ends, then closes both (as chisel's cio.Pipe does).</summary>
internal static class Pipe
{
    public static async Task RunAsync(Socket sock, SshChannel channel, Action<string>? log, CancellationToken ct, int bufferSize = 32 * 1024)
    {
        using var ns = new NetworkStream(sock, ownsSocket: true);
        using var ss = new SshStream(channel);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // tunnel -> target
        var toTarget = Task.Run(async () =>
        {
            var buf = new byte[bufferSize];
            try
            {
                int n;
                while ((n = await ss.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                    await ns.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                try { sock.Shutdown(SocketShutdown.Send); } catch (Exception) { } // the other end sent EOF: half-close towards the target
            }
            catch (Exception) { }
        });
        // target -> tunnel; the SSH library has no channel EOF, so the end of the target's data closes the channel (after what was sent)
        var toTunnel = Task.Run(async () =>
        {
            var buf = new byte[bufferSize];
            try
            {
                int n;
                while ((n = await ns.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                    await ss.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                await ss.FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
        });
        await Task.WhenAny(toTarget, toTunnel).ConfigureAwait(false);
        if (toTunnel.IsCompleted) { try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } }
        else { try { await Task.WhenAny(toTunnel, Task.Delay(TimeSpan.FromSeconds(30), ct)).ConfigureAwait(false); } catch (Exception) { } }
        cts.Cancel();
        await Task.WhenAll(toTarget, toTunnel).ConfigureAwait(false);
    }
}
