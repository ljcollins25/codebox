using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace FsDedup;

/// <summary>Shared progress counters, read by the progress printer while hashing runs.</summary>
public sealed class Progress
{
    public long BytesHashed, FilesHashed, FilesFromCache, FilesPlanned;
}

/// <summary>xxHash-128 of files: a partial hash (first and last 64 KB) and a full hash, with large sequential reads.</summary>
public static class Hasher
{
    public const int PartialBytes = 64 * 1024;
    public const int BufferSize = 1024 * 1024;

    public static UInt128 Partial(SafeFileHandle h, long size, Progress progress)
    {
        var hash = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(PartialBytes);
        try
        {
            Span<byte> len = stackalloc byte[8];
            BitConverter.TryWriteBytes(len, size);
            hash.Append(len);
            long firstEnd = Math.Min(PartialBytes, size);
            ReadExactly(h, buffer, 0, (int)firstEnd);
            hash.Append(buffer.AsSpan(0, (int)firstEnd));
            long lastStart = Math.Max(firstEnd, size - PartialBytes);
            int lastLen = (int)(size - lastStart);
            if (lastLen > 0)
            {
                ReadExactly(h, buffer, lastStart, lastLen);
                hash.Append(buffer.AsSpan(0, lastLen));
            }
            Interlocked.Add(ref progress.BytesHashed, firstEnd + lastLen);
            return hash.GetCurrentHashAsUInt128();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static UInt128 Full(SafeFileHandle h, long size, Progress progress)
    {
        var hash = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            for (long offset = 0; offset < size;)
            {
                int want = (int)Math.Min(BufferSize, size - offset);
                ReadExactly(h, buffer, offset, want);
                hash.Append(buffer.AsSpan(0, want));
                offset += want;
                Interlocked.Add(ref progress.BytesHashed, want);
            }
            return hash.GetCurrentHashAsUInt128();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Reads exactly count bytes at offset; a file that ends early (it shrank) is an error.</summary>
    public static void ReadExactly(SafeFileHandle h, byte[] buffer, long offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = RandomAccess.Read(h, buffer.AsSpan(done, count - done), offset + done);
            if (n <= 0) throw new IOException("file ended before its recorded size");
            done += n;
        }
    }
}
