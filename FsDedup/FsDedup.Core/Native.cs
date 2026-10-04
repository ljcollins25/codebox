using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

[assembly: SupportedOSPlatform("windows")]

namespace FsDedup;

/// <summary>What a handle says about a file: identity (volume + 128-bit file ID), size, times, attributes, link count.</summary>
public readonly record struct FileIdentity(
    ulong VolumeSerial, UInt128 FileId, long Size, long CreationTicks, long WriteTicks, uint Attributes, uint Links)
{
    public bool IsReparsePoint => (Attributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    public bool IsEncrypted => (Attributes & Native.FILE_ATTRIBUTE_ENCRYPTED) != 0;
    public bool IsSparse => (Attributes & Native.FILE_ATTRIBUTE_SPARSE_FILE) != 0;
    public bool IsReadOnly => (Attributes & Native.FILE_ATTRIBUTE_READONLY) != 0;

    /// <summary>The same file, unchanged: ID, size and last-write time (what the hash was keyed on).</summary>
    public bool SameContentKey(FileIdentity o) =>
        VolumeSerial == o.VolumeSerial && FileId == o.FileId && Size == o.Size && WriteTicks == o.WriteTicks;
}

public readonly record struct Extent(long Vcn, long Lcn, long Length);

public readonly record struct VolumeInfo(string Root, ulong Serial, string FileSystem, bool SupportsBlockCloning, long ClusterSize);

/// <summary>Win32 calls the tool needs. Everything is thin; policy lives in the callers.</summary>
public static class Native
{
    public const uint FILE_ATTRIBUTE_READONLY = 0x1, FILE_ATTRIBUTE_HIDDEN = 0x2, FILE_ATTRIBUTE_SYSTEM = 0x4,
        FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_SPARSE_FILE = 0x200, FILE_ATTRIBUTE_REPARSE_POINT = 0x400,
        FILE_ATTRIBUTE_ENCRYPTED = 0x4000;

    public const int ERROR_BLOCK_TOO_MANY_REFERENCES = 347;
    public const int ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33, ERROR_MORE_DATA = 234;
    private const int ERROR_HANDLE_EOF = 38, ERROR_INSUFFICIENT_BUFFER = 122;

    private const uint FILE_READ_ATTRIBUTES = 0x80, GENERIC_READ = 0x80000000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint OPEN_EXISTING = 3, ALL_SHARE = 7;
    private const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344, FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073,
        FSCTL_GET_INTEGRITY_INFORMATION = 0x0009027C, FSCTL_SET_INTEGRITY_INFORMATION = 0x0009C280,
        FSCTL_SET_SPARSE = 0x000900C4;
    private const uint FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000;

    /// <summary>Prefixes long absolute paths with \\?\ so the Win32 calls below accept them.</summary>
    public static string Long(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    // ---- identity -------------------------------------------------------------------------------------------

    /// <summary>Identity of a file or folder without following a reparse point; needs only attribute access.</summary>
    public static FileIdentity ReadIdentity(string path)
    {
        using var h = CreateFileW(Long(path), FILE_READ_ATTRIBUTES, ALL_SHARE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        return ReadIdentity(h);
    }

    public static FileIdentity ReadIdentity(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var bh)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong volume = bh.VolumeSerialNumber;
        UInt128 id = ((UInt128)bh.FileIndexHigh << 32) | bh.FileIndexLow;
        if (GetFileInformationByHandleEx(h, 18 /* FileIdInfo */, out var ex, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
        {
            volume = ex.VolumeSerialNumber;
            id = new UInt128(ex.IdHigh, ex.IdLow);
        }
        long size = ((long)bh.FileSizeHigh << 32) | bh.FileSizeLow;
        return new FileIdentity(volume, id, size, bh.CreationTime.Ticks, bh.LastWriteTime.Ticks, bh.FileAttributes, bh.NumberOfLinks);
    }

    // ---- volume ---------------------------------------------------------------------------------------------

    public static VolumeInfo GetVolume(string path)
    {
        var sb = new System.Text.StringBuilder(1024);
        if (!GetVolumePathNameW(Long(path), sb, sb.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var root = sb.ToString();
        var fs = new System.Text.StringBuilder(64);
        if (!GetVolumeInformationW(root, null, 0, out var serial, out _, out var flags, fs, fs.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), root);
        long cluster = GetDiskFreeSpaceW(root, out var spc, out var bps, out _, out _) ? (long)spc * bps : 4096;
        return new VolumeInfo(root, serial, fs.ToString(), (flags & FILE_SUPPORTS_BLOCK_REFCOUNTING) != 0, cluster);
    }

    public static long FreeSpace(string root) =>
        GetDiskFreeSpaceExW(root, out var free, out _, out _) ? (long)free : -1;

    /// <summary>
    /// Free space once it stops moving. ReFS gives back the clusters of deleted or replaced files a few seconds after
    /// the fact (observed: 10-15 s, in a jump), so a reading taken right after a run understates what it freed. Waits until
    /// the value has not moved for <paramref name="stableSeconds"/> (at most <paramref name="maxSeconds"/>); 0 reads it once.
    /// </summary>
    public static long SettledFreeSpace(string root, int stableSeconds, int maxSeconds)
    {
        long last = FreeSpace(root);
        if (stableSeconds <= 0) return last;
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        int stable = 0;
        while (stable < stableSeconds * 2 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            long now = FreeSpace(root);
            stable = now == last ? stable + 1 : 0;
            last = now;
        }
        return last;
    }

    /// <summary>Polls free space every half second until <paramref name="reached"/> says so or the time is up; returns the last reading.</summary>
    public static long WaitForFreeSpace(string root, Func<long, bool> reached, int maxSeconds)
    {
        long last = FreeSpace(root);
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (!reached(last) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            last = FreeSpace(root);
        }
        return last;
    }

    // ---- streams -----------------------------------------------------------------------------------------

    /// <summary>Names and sizes of the named (alternate) data streams, excluding the main one; sorted.</summary>
    public static List<string> AlternateStreams(string path)
    {
        var list = new List<string>();
        var h = FindFirstStreamW(Long(path), 0, out var data, 0);
        if (h == new IntPtr(-1))
        {
            var e = Marshal.GetLastWin32Error();
            if (e == ERROR_HANDLE_EOF) return list;
            throw new Win32Exception(e, path);
        }
        try
        {
            do
            {
                if (!data.StreamName.Equals("::$DATA", StringComparison.Ordinal)) list.Add($"{data.StreamName}={data.StreamSize}");
            } while (FindNextStreamW(h, out data));
        }
        finally { FindClose(h); }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    // ---- cloning --------------------------------------------------------------------------------------------

    /// <summary>The file's cluster runs (virtual start, physical start, length); a hole has Lcn -1.</summary>
    public static List<Extent> GetExtents(SafeFileHandle h)
    {
        var result = new List<Extent>();
        long start = 0;
        var input = new byte[8];
        var output = new byte[16 + 16 * 256];
        while (true)
        {
            BitConverter.TryWriteBytes(input, start);
            var ok = DeviceIoControl(h, FSCTL_GET_RETRIEVAL_POINTERS, input, input.Length, output, output.Length, out _, IntPtr.Zero);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != ERROR_MORE_DATA) throw new Win32Exception(err, "FSCTL_GET_RETRIEVAL_POINTERS");
            int count = BitConverter.ToInt32(output, 0);
            long vcn = BitConverter.ToInt64(output, 8);
            for (int i = 0; i < count; i++)
            {
                long next = BitConverter.ToInt64(output, 16 + i * 16);
                long lcn = BitConverter.ToInt64(output, 24 + i * 16);
                result.Add(new Extent(vcn, lcn, next - vcn));
                vcn = next;
            }
            if (ok) return result;
            start = vcn;
        }
    }

    /// <summary>Integrity-stream settings of a file, or null when the volume has none (not ReFS).</summary>
    public static (ushort Algorithm, uint Flags)? GetIntegrity(SafeFileHandle h)
    {
        var o = new byte[16];
        if (!DeviceIoControl(h, FSCTL_GET_INTEGRITY_INFORMATION, null, 0, o, o.Length, out _, IntPtr.Zero)) return null;
        return (BitConverter.ToUInt16(o, 0), BitConverter.ToUInt32(o, 4));
    }

    public static void SetIntegrity(SafeFileHandle h, ushort algorithm, uint flags)
    {
        var i = new byte[8];
        BitConverter.TryWriteBytes(i.AsSpan(0, 2), algorithm);
        BitConverter.TryWriteBytes(i.AsSpan(4, 4), flags);
        if (!DeviceIoControl(h, FSCTL_SET_INTEGRITY_INFORMATION, i, i.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_SET_INTEGRITY_INFORMATION");
    }

    public static void SetSparse(SafeFileHandle h)
    {
        if (!DeviceIoControl(h, FSCTL_SET_SPARSE, null, 0, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_SET_SPARSE");
    }

    /// <summary>One FSCTL_DUPLICATE_EXTENTS_TO_FILE call: clusters of src [srcOffset, +count) into dst at dstOffset.</summary>
    public static void DuplicateExtents(SafeFileHandle dst, SafeFileHandle src, long srcOffset, long dstOffset, long count)
    {
        var b = new byte[32];
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), src.DangerousGetHandle().ToInt64());
        BitConverter.TryWriteBytes(b.AsSpan(8, 8), srcOffset);
        BitConverter.TryWriteBytes(b.AsSpan(16, 8), dstOffset);
        BitConverter.TryWriteBytes(b.AsSpan(24, 8), count);
        if (!DeviceIoControl(dst, FSCTL_DUPLICATE_EXTENTS_TO_FILE, b, b.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_DUPLICATE_EXTENTS_TO_FILE");
    }

    // ---- files ----------------------------------------------------------------------------------------------

    public static bool MoveReplace(string source, string destination, out int error)
    {
        const uint MOVEFILE_REPLACE_EXISTING = 0x1, MOVEFILE_WRITE_THROUGH = 0x8;
        var ok = MoveFileExW(Long(source), Long(destination), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public static bool CreateHardLink(string newPath, string existingPath, out int error)

    {
        var ok = CreateHardLinkW(Long(newPath), Long(existingPath), IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public static bool ReplaceFile(string replaced, string replacement, string backup, out int error)
    {
        var ok = ReplaceFileW(Long(replaced), Long(replacement), Long(backup), 0, IntPtr.Zero, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>Creates a new hidden file (fails if it exists) open for read/write with no sharing.</summary>
    public static SafeFileHandle CreateHiddenNew(string path)
    {
        var h = CreateFileW(Long(path), 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 0, IntPtr.Zero, 1 /* CREATE_NEW */,
            FILE_ATTRIBUTE_HIDDEN, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        return h;
    }

    /// <summary>Opens for reading with the given sharing; sequential-scan hint.</summary>

    public static SafeFileHandle OpenRead(string path, FileShare share, bool sequential = true) =>
        File.OpenHandle(Long(path), FileMode.Open, FileAccess.Read, share,
            sequential ? FileOptions.SequentialScan : FileOptions.None);

    // ---- imports --------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low, High;
        public readonly long Ticks => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow, IdHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_ID_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileExW(string existing, string replacement, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr securityAttributes);


    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReplaceFileW(string replaced, string replacement, string backup, uint flags, IntPtr exclude, IntPtr reserved);


    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathNameW(string path, System.Text.StringBuilder volumePath, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(string root, System.Text.StringBuilder? label, int labelSize, out uint serial, out uint maxComponent, out uint flags, System.Text.StringBuilder fsName, int fsNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceExW(string root, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstStreamW(string path, int infoLevel, out WIN32_FIND_STREAM_DATA data, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool FindNextStreamW(IntPtr h, out WIN32_FIND_STREAM_DATA data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr h);
}
