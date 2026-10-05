using System.Text;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;

namespace Tbus;

// chisel (Go's x/crypto/ssh) puts the type-specific data of channel opens, global requests and their replies at the end of the
// packet as raw bytes ("rest"), without the length prefix an SSH string would have. These messages read and write it that way.

/// <summary>SSH_MSG_GLOBAL_REQUEST with a raw payload: chisel's "config" and "ping".</summary>
internal sealed class ChiselRequestMessage : SessionRequestMessage
{
    public byte[] Payload { get; set; } = [];

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Payload = reader.ReadBinary((uint)reader.Available).ToArray();
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Microsoft.DevTunnels.Ssh.Buffer.From(Payload));
    }
}

/// <summary>SSH_MSG_REQUEST_SUCCESS with a raw payload ("pong").</summary>
internal sealed class ChiselSuccessMessage : SessionRequestSuccessMessage
{
    public byte[] Payload { get; set; } = [];

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Payload = reader.ReadBinary((uint)reader.Available).ToArray();
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Microsoft.DevTunnels.Ssh.Buffer.From(Payload));
    }
}

/// <summary>Channel open of type "chisel": the extra data is the target "host:port" as raw bytes.</summary>
internal sealed class ChiselChannelOpenMessage : ChannelOpenMessage
{
    public string Target { get; set; } = "";

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Target = Encoding.UTF8.GetString(reader.ReadBinary((uint)reader.Available).ToArray());
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Microsoft.DevTunnels.Ssh.Buffer.From(Encoding.UTF8.GetBytes(Target)));
    }
}

/// <summary>SSH_MSG_REQUEST_FAILURE; chisel appends the reason as raw text (Go's Request.Reply(false, payload)).</summary>
internal sealed class ChiselFailureMessage : SessionRequestFailureMessage
{
    public string Reason { get; set; } = "";

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Reason = Encoding.UTF8.GetString(reader.ReadBinary((uint)reader.Available).ToArray());
    }
}
