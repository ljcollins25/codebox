using System.Net.WebSockets;

namespace Tbus;

/// <summary>
/// A <see cref="ClientWebSocket"/> as a duplex byte stream, the way chisel uses its websocket as a net.Conn: every write is one
/// binary message, reads return message bytes in order (message boundaries do not matter to SSH). Writes are serialised.
/// </summary>
internal sealed class WebSocketStream(WebSocket ws, bool FixBanner = false) : Stream
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private bool _eof;
    private byte[]? _head; private int _headPos; private bool _headDone;

    /// <summary>
    /// chisel's server greets with "SSH-chisel-v3-server", which is not a valid SSH identification string ("SSH-protoversion-software"):
    /// Go's SSH library accepts it, Microsoft.DevTunnels.Ssh (which insists on "SSH-2.0-...") does not. The first line is therefore
    /// read here and "2.0-" is inserted; <see cref="ChiselSshCompat"/> puts the original text back into the library's key exchange
    /// hash, which both sides must compute over the same bytes. Returns the line as the server sent it (null if it was already valid).
    /// </summary>
    public string? OriginalServerBanner { get; private set; }

    private async ValueTask<int> ReadBannerAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_head == null)
        {
            var acc = new MemoryStream(); var tmp = new byte[4096];
            while (true)
            {
                var r = await ws.ReceiveAsync(tmp, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) { _eof = true; return 0; }
                acc.Write(tmp, 0, r.Count);
                var bytes = acc.GetBuffer(); var len = (int)acc.Length;
                var nl = Array.IndexOf(bytes, (byte)'\n', 0, len);
                if (nl >= 0 || len > 8192) { _head = acc.ToArray(); break; }
            }
            var lineEnd = _head.AsSpan().IndexOfAny((byte)'\r', (byte)'\n');
            var line = System.Text.Encoding.ASCII.GetString(_head, 0, lineEnd < 0 ? _head.Length : lineEnd);
            if (line.StartsWith("SSH-", StringComparison.Ordinal) && !line.StartsWith("SSH-2.0-", StringComparison.Ordinal) && !line.StartsWith("SSH-1.99-", StringComparison.Ordinal))
            {
                OriginalServerBanner = line;
                var fixedLine = System.Text.Encoding.ASCII.GetBytes("SSH-2.0-" + line["SSH-".Length..]);
                var eol = _head.AsSpan().IndexOfAny((byte)'\r', (byte)'\n');
                var rest = _head.AsSpan(eol);
                _head = [.. fixedLine, .. rest];
            }
            _headPos = 0;
        }
        var n = Math.Min(buffer.Length, _head.Length - _headPos);
        _head.AsMemory(_headPos, n).CopyTo(buffer); _headPos += n;
        if (_headPos >= _head.Length) { _headDone = true; _head = null; }
        return n;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_eof || buffer.Length == 0) return 0;
        try
        {
            if (FixBanner && !_headDone) return await ReadBannerAsync(buffer, ct).ConfigureAwait(false);
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) { _eof = true; return 0; }
            return r.Count;
        }
        catch (WebSocketException) when (ws.State is WebSocketState.Aborted or WebSocketState.Closed or WebSocketState.CloseReceived) { _eof = true; return 0; }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(buffer, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
        catch (WebSocketException e) { throw new IOException(e.Message, e); }
        finally { _write.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { ws.Abort(); } catch (Exception) { }
        }
        base.Dispose(disposing);
    }
}
