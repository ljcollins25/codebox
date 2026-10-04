using System.Collections.Concurrent;
using System.ComponentModel;

namespace FsDedup;

/// <summary>A file that passed the walk's filters.</summary>
public sealed class FileEntry
{
    public required string Path { get; init; }
    public required FileIdentity Id { get; set; }
    public CacheKey Key => new(Id.VolumeSerial, Id.FileId, Id.Size, Id.WriteTicks);
    public UInt128? Partial { get; set; }
    public UInt128? Full { get; set; }
    public bool ReadFromDisk { get; set; }
}

public static class SkipReason
{
    public const string ReparsePoint = "reparse-point";
    public const string HardLinked = "hard-linked";
    public const string Encrypted = "encrypted";
    public const string BelowMinSize = "below-min-size";
    public const string OtherVolume = "other-volume";
    public const string Unreadable = "unreadable";
    public const string ToolFile = "tool-file";
    public const string ReadOnly = "read-only";
    public const string AlternateStreams = "alternate-data-streams";
    public const string ChangedSinceHashed = "changed-since-hashed";
    public const string VerifyFailed = "verify-failed";
    public const string Locked = "locked";
}

/// <summary>Walks the tree without following reparse points and applies the per-file filters.</summary>
public sealed class Scanner(string root, long minSize, ISet<string> toolFiles, ConcurrentDictionary<string, long> skipped, List<string> errors)
{
    public long FilesScanned;

    public List<FileEntry> Scan()
    {
        var rootId = Native.ReadIdentity(root);
        var result = new List<FileEntry>();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(Native.Long(dir)).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(SkipReason.Unreadable);
                errors.Add($"cannot list {dir}: {ex.Message}");
                continue;
            }
            foreach (var item in items)
            {
                var path = System.IO.Path.Combine(dir, item.Name);
                var attrs = (uint)item.Attributes;
                if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    if ((attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) Skip(SkipReason.ReparsePoint);
                    else pending.Push(path);
                    continue;
                }
                Interlocked.Increment(ref FilesScanned);
                if ((attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) { Skip(SkipReason.ReparsePoint); continue; }
                if (toolFiles.Contains(path) || item.Name.StartsWith(".fsdedup-", StringComparison.Ordinal)) { Skip(SkipReason.ToolFile); continue; }
                if ((attrs & Native.FILE_ATTRIBUTE_ENCRYPTED) != 0) { Skip(SkipReason.Encrypted); continue; }
                long length;
                try { length = ((FileInfo)item).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Skip(SkipReason.Unreadable); continue; }
                if (length < Math.Max(minSize, 1)) { Skip(SkipReason.BelowMinSize); continue; }
                FileIdentity id;
                try { id = Native.ReadIdentity(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    Skip(SkipReason.Unreadable);
                    continue;
                }
                if (id.IsReparsePoint) { Skip(SkipReason.ReparsePoint); continue; }
                if (id.IsEncrypted) { Skip(SkipReason.Encrypted); continue; }
                if (id.Links > 1) { Skip(SkipReason.HardLinked); continue; }
                if (id.VolumeSerial != rootId.VolumeSerial) { Skip(SkipReason.OtherVolume); continue; }
                result.Add(new FileEntry { Path = path, Id = id });
            }
        }
        return result;
    }

    private void Skip(string reason) => skipped.AddOrUpdate(reason, 1, (_, n) => n + 1);
}
