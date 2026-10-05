using System.Reflection;
using Microsoft.DevTunnels.Ssh;

namespace Tbus;

/// <summary>
/// The one place that reaches into Microsoft.DevTunnels.Ssh (pinned to 3.12.42): after <see cref="WebSocketStream"/> made chisel's
/// non-standard server banner parseable, this restores the banner's original text on the library's remote version object, because the
/// SSH key exchange hash includes the server's identification string byte for byte (the server hashed what it sent).
/// </summary>
internal static class ChiselSshCompat
{
    private static readonly FieldInfo? VersionString = typeof(SshVersionInfo).GetField("<VersionString>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? RemoteClosed = typeof(SshChannel).GetField("remoteClosed", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Rejecting an incoming channel open makes the library send the failure and then, from Dispose, a channel close for the id that never
    /// opened. Go's SSH mux treats a close for an unknown channel as a protocol error and drops the whole connection, so every rejected
    /// connection would take the tunnel down. Marking the channel as already closed by the peer suppresses that close message.
    /// </summary>
    public static void PrepareForRejection(SshChannel channel) =>
        (RemoteClosed ?? throw new InvalidOperationException("Microsoft.DevTunnels.Ssh changed: SshChannel.remoteClosed not found")).SetValue(channel, true);

    public static void Attach(SshSession session, WebSocketStream stream)
    {
        session.ReportProgress += (_, p) =>
        {
            if (p.Progress != Progress.CompletedProtocolVersionExchange || stream.OriginalServerBanner == null) return;
            var rv = session.RemoteVersion ?? throw new InvalidOperationException("no remote SSH version");
            (VersionString ?? throw new InvalidOperationException("Microsoft.DevTunnels.Ssh changed: SshVersionInfo.VersionString not found")).SetValue(rv, stream.OriginalServerBanner);
        };
    }
}
