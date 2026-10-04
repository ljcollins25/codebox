using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace FsDedup;

/// <summary>How duplicate names share storage. Both modes use the same safe temp/verify/journal/swap machinery.</summary>
public interface IDedupStrategy
{
    string Name { get; }

    /// <summary>How many files may reference one original's storage (the original included).</summary>
    int MaxReferencesPerOriginal { get; }

    /// <summary>Throws <see cref="NotSupportedException"/> when the volume cannot do this.</summary>
    void CheckVolume(VolumeInfo volume);

    /// <summary>True when the duplicate already shares all its storage with the original.</summary>
    bool AlreadyShared(string original, string duplicate, VolumeInfo volume);

    /// <summary>Creates <paramref name="tempPath"/> (new, hidden) so that it shares the original's storage and has its content.</summary>
    void Materialize(string original, string tempPath, VolumeInfo volume);
}

public static class DedupStrategies
{
    public static IDedupStrategy Create(string mode) => mode.ToLowerInvariant() switch
    {
        "clone" => new CloneStrategy(),
        "hardlink" => new HardLinkStrategy(),
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

    public bool AlreadyShared(string original, string duplicate, VolumeInfo volume)
    {
        try
        {
            using var a = Native.OpenRead(original, FileShare.ReadWrite | FileShare.Delete, false);
            using var b = Native.OpenRead(duplicate, FileShare.ReadWrite | FileShare.Delete, false);
            return SharedClusters(Native.GetExtents(a), Native.GetExtents(b), RandomAccess.GetLength(a), volume.ClusterSize);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the two files map every whole cluster of their data to the same physical clusters. ReFS copies a final
    /// partial cluster instead of sharing it (observed: the clone's last extent is a new allocation), so only whole
    /// clusters count. Unallocated extents (Lcn -1: holes, or data not yet written out) prove nothing and never count.
    /// </summary>
    public static bool SharedClusters(List<Extent> a, List<Extent> b, long length, long clusterSize)
    {
        long whole = length / clusterSize;
        var ca = Clip(a, whole);
        return whole > 0 && ca.Count > 0 && ca.All(x => x.Lcn >= 0) && ca.SequenceEqual(Clip(b, whole));
    }

    private static List<Extent> Clip(List<Extent> extents, long clusters)
    {
        var result = new List<Extent>();
        foreach (var e in extents)
        {
            if (e.Vcn >= clusters) break;
            result.Add(e.Vcn + e.Length > clusters ? e with { Length = clusters - e.Vcn } : e);
        }
        return result;
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

/// <summary>
/// Creates directory entries that name the same underlying file. Windows documents 1,023 additional links for
/// CreateHardLink (1,024 names total). Microsoft publishes no ReFS-specific maximum; 1,024 is a conservative cap.
/// </summary>
public sealed class HardLinkStrategy : IDedupStrategy
{
    public const int ConservativeMaxLinks = 1024;
    public string Name => "hardlink";
    public int MaxReferencesPerOriginal => ConservativeMaxLinks;

    public void CheckVolume(VolumeInfo volume)
    {
        if (volume.FileSystem is not ("NTFS" or "ReFS"))
            throw new NotSupportedException($"hard links are not supported by {volume.FileSystem}.");
    }

    public bool AlreadyShared(string original, string duplicate, VolumeInfo volume)
    {
        try
        {
            var a = Native.ReadIdentity(original);
            var b = Native.ReadIdentity(duplicate);
            return a.VolumeSerial == b.VolumeSerial && a.FileId == b.FileId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }

    public void Materialize(string original, string tempPath, VolumeInfo volume)
    {
        if (!Native.CreateHardLink(tempPath, original, out var error))
        {
            if (error == 1142) throw new TooManyReferencesException(new Win32Exception(error).Message);
            throw new Win32Exception(error, $"CreateHardLink({tempPath}, {original})");
        }
    }
}
