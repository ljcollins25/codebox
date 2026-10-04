using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace FsDedup;

/// <summary>
/// How a duplicate comes to share storage with its original. The safe-replace machinery (temp file, verify, re-check,
/// ReplaceFile, journal) is the same for every mode; a hard-link mode would implement this interface too
/// (Materialize = create a link at the temp path, MaxReferencesPerOriginal = the link limit) and be added to
/// <see cref="DedupStrategies.Create"/>. Only block cloning exists today.
/// </summary>
public interface IDedupStrategy
{
    string Name { get; }

    /// <summary>How many files may reference one original's storage (the original included).</summary>
    int MaxReferencesPerOriginal { get; }

    /// <summary>Throws <see cref="NotSupportedException"/> when the volume cannot do this.</summary>
    void CheckVolume(VolumeInfo volume);

    /// <summary>True when the duplicate already shares all its storage with the original.</summary>
    bool AlreadyShared(string original, string duplicate);

    /// <summary>Creates <paramref name="tempPath"/> (new, hidden) so that it shares the original's storage and has its content.</summary>
    void Materialize(string original, string tempPath, VolumeInfo volume);
}

public static class DedupStrategies
{
    public static IDedupStrategy Create(string mode) => mode.ToLowerInvariant() switch
    {
        "clone" => new CloneStrategy(),
        "hardlink" => throw new NotSupportedException("--mode hardlink is not implemented yet; only --mode clone is."),
        _ => throw new ArgumentException($"unknown mode '{mode}' (expected clone)"),
    };
}

/// <summary>Thrown when the original's clusters have as many references as ReFS allows.</summary>
public sealed class TooManyReferencesException(string message) : Exception(message);

public sealed class CloneStrategy : IDedupStrategy
{
    /// <summary>
    /// Microsoft's block-cloning documentation (learn.microsoft.com/windows-server/storage/refs/block-cloning, "Restrictions"):
    /// "The maximum number of file regions that can map to the same physical region is 8175", and one clone call must
    /// cover less than 4 GB. A cluster referenced that often fails further clones with ERROR_BLOCK_TOO_MANY_REFERENCES (347).
    /// </summary>
    public const int ReFsMaxReferencesPerCluster = 8175;

    /// <summary>Each FSCTL_DUPLICATE_EXTENTS_TO_FILE call covers at most this much (a cluster multiple; the limit is 4 GB).</summary>
    public const long MaxBytesPerCall = 1L << 30;

    public string Name => "clone";
    public int MaxReferencesPerOriginal => ReFsMaxReferencesPerCluster;

    public void CheckVolume(VolumeInfo volume)
    {
        if (!volume.SupportsBlockCloning)
            throw new NotSupportedException($"volume {volume.Root} ({volume.FileSystem}) does not support block cloning; ReFS is required.");
    }

    public bool AlreadyShared(string original, string duplicate)
    {
        try
        {
            using var a = Native.OpenRead(original, FileShare.ReadWrite | FileShare.Delete, false);
            using var b = Native.OpenRead(duplicate, FileShare.ReadWrite | FileShare.Delete, false);
            var ea = Native.GetExtents(a);
            var eb = Native.GetExtents(b);
            return ea.Count > 0 && ea.SequenceEqual(eb);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    public void Materialize(string original, string tempPath, VolumeInfo volume)
    {
        using var src = Native.OpenRead(original, FileShare.Read, false);
        var size = Native.ReadIdentity(src).Size;
        var integrity = Native.GetIntegrity(src);
        var sparse = Native.ReadIdentity(src).IsSparse;
        using var dst = Native.CreateHiddenNew(tempPath);
        // Cloning needs the same integrity-stream and sparse settings on both ends, set while the target is still empty.
        if (integrity is { } i) Native.SetIntegrity(dst, i.Algorithm, i.Flags);
        if (sparse) Native.SetSparse(dst);
        RandomAccess.SetLength(dst, size);
        long cluster = volume.ClusterSize;
        try
        {
            for (long offset = 0; offset < size; offset += MaxBytesPerCall)
            {
                long count = Math.Min(MaxBytesPerCall, size - offset);
                count = (count + cluster - 1) / cluster * cluster; // whole clusters; the target's length is the exact byte count
                Native.DuplicateExtents(dst, src, offset, offset, count);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Native.ERROR_BLOCK_TOO_MANY_REFERENCES)
        {
            throw new TooManyReferencesException(ex.Message);
        }
        RandomAccess.FlushToDisk(dst);
    }
}
