using System.ComponentModel;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace FsDedup;

public enum ReplaceStatus { Replaced, Skipped, Failed, TooManyReferences }

public sealed record ReplaceOutcome(ReplaceStatus Status, string? Reason = null, FileIdentity? NewIdentity = null);

/// <summary>
/// Replaces one duplicate D with a clone of an original, safely. D is only ever touched by the final ReplaceFile call;
/// everything before it works on a hidden temp file next to D, and a failure at any step deletes that file and leaves D
/// as it was. The journal says which step an interrupted run was in.
/// </summary>
public sealed class Replacer(IDedupStrategy strategy, VolumeInfo volume, Journal journal, RunHooks hooks)
{
    public ReplaceOutcome Replace(FileEntry original, FileEntry dup)
    {
        hooks.AfterHash?.Invoke(dup.Path);
        string dir = Path.GetDirectoryName(dup.Path)!;
        string id = Guid.NewGuid().ToString("N")[..16];
        string temp = Path.Combine(dir, $".fsdedup-{id}.tmp");
        string backup = Path.Combine(dir, $".fsdedup-{id}.bak");
        SafeFileHandle? held = null;
        bool begun = false, swapStarted = false, swapped = false;
        try
        {
            try { held = Native.OpenRead(dup.Path, FileShare.Read | FileShare.Delete); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ReplaceStatus.Skipped, SkipReason.Locked); }
            var now = Native.ReadIdentity(held);
            if (!now.SameContentKey(dup.Id)) return new(ReplaceStatus.Skipped, SkipReason.ChangedSinceHashed);
            if (strategy is CloneStrategy && now.Links > 1) return new(ReplaceStatus.Skipped, SkipReason.HardLinked);
            if (now.IsReparsePoint) return new(ReplaceStatus.Skipped, SkipReason.ReparsePoint);
            if (now.IsEncrypted) return new(ReplaceStatus.Skipped, SkipReason.Encrypted);
            if (strategy is CloneStrategy && now.IsReadOnly) return new(ReplaceStatus.Skipped, SkipReason.ReadOnly);

            var streamsBefore = Native.AlternateStreams(dup.Path);
            var securityBefore = Sddl(dup.Path);
            DateTime originalCreationTime = File.GetCreationTimeUtc(original.Path);

            if (strategy is HardLinkStrategy)
            {
                string? mismatch = HardLinkMetadataMismatch(original.Path, dup.Path);
                if (mismatch is not null) return new(ReplaceStatus.Skipped, mismatch);
            }

            journal.Begin(id, dup.Path, temp, backup, strategy is HardLinkStrategy, strategy is HardLinkStrategy ? original.Path : null,
                strategy is HardLinkStrategy ? originalCreationTime.ToFileTimeUtc() : null);
            begun = true;
            strategy.Materialize(original.Path, temp, volume);
            if (strategy is CloneStrategy)
            {
                File.SetCreationTimeUtc(temp, DateTime.FromFileTimeUtc(now.CreationTicks));
                File.SetLastWriteTimeUtc(temp, DateTime.FromFileTimeUtc(now.WriteTicks));
            }
            if (!SameBytes(held, temp, now.Size)) return new(ReplaceStatus.Skipped, SkipReason.VerifyFailed);
            hooks.BeforeSwap?.Invoke(dup.Path);

            var again = Native.ReadIdentity(held);
            var byPath = Native.ReadIdentity(dup.Path);
            if (!again.SameContentKey(dup.Id) || !byPath.SameContentKey(again) ||
                (strategy is CloneStrategy && again.Links != 1))
                return new(ReplaceStatus.Skipped, SkipReason.ChangedSinceHashed);
            if (strategy is HardLinkStrategy)
            {
                string? mismatch = HardLinkMetadataMismatch(original.Path, dup.Path);
                if (mismatch is not null) return new(ReplaceStatus.Skipped, mismatch);
            }

            journal.Swapping(id);
            swapStarted = true;
            if (!Native.ReplaceFile(dup.Path, temp, backup, out var error))
            {
                RestoreAfterFailedSwap(dup.Path, backup);
                return new(ReplaceStatus.Failed, $"ReplaceFile failed: {new Win32Exception(error).Message} ({error})");
            }
            swapped = true;
            journal.Swapped(id);
            var after = Native.ReadIdentity(dup.Path);

            if (strategy is HardLinkStrategy)
            {
                // ReplaceFile propagates D's creation time onto a hard-linked file; restore C's metadata explicitly.
                File.SetCreationTimeUtc(original.Path, originalCreationTime);
                bool sameId =
 after.VolumeSerial == Native.ReadIdentity(original.Path).VolumeSerial &&
                    after.FileId == Native.ReadIdentity(original.Path).FileId;
                bool sameMetadata = HardLinkMetadataMismatch(original.Path, dup.Path) is null &&
                    File.GetCreationTimeUtc(dup.Path) == File.GetCreationTimeUtc(original.Path) &&
                    File.GetLastWriteTimeUtc(dup.Path) == File.GetLastWriteTimeUtc(original.Path) &&
                    Native.AlternateStreams(dup.Path).SequenceEqual(Native.AlternateStreams(original.Path));
                if (!sameId || !sameMetadata)
                {
                    held.Dispose(); held = null;
                    File.Move(backup, dup.Path, overwrite: true);
                    journal.End(id, "rolled-back"); begun = false;
                    return new(ReplaceStatus.Failed, "hard-link identity or metadata check failed; original restored");
                }
                held.Dispose(); held = null;
                DeleteWithRetry(backup);
                journal.End(id, "done"); begun = false;
                return new(ReplaceStatus.Replaced, NewIdentity: after);
            }

            if (after.WriteTicks != now.WriteTicks) File.SetLastWriteTimeUtc(dup.Path, DateTime.FromFileTimeUtc(now.WriteTicks));
            after = Native.ReadIdentity(dup.Path);
            var differences = new List<string>();
            if (after.WriteTicks != now.WriteTicks) differences.Add("last-write time");
            if (after.CreationTicks != now.CreationTicks) differences.Add("creation time");
            if (after.Attributes != now.Attributes) differences.Add($"attributes {now.Attributes:x} -> {after.Attributes:x}");
            if (!Native.AlternateStreams(dup.Path).SequenceEqual(streamsBefore)) differences.Add("named streams");
            if (Sddl(dup.Path) != securityBefore) differences.Add("ACL");
            if (differences.Count > 0)
            {
                held.Dispose(); held = null;
                File.Move(backup, dup.Path, overwrite: true);
                journal.End(id, "rolled-back"); begun = false;
                return new(ReplaceStatus.Failed, "metadata or streams differed after the swap; original restored");
            }

            held.Dispose(); held = null;
            DeleteWithRetry(backup);
            journal.End(id, "done"); begun = false;
            return new(ReplaceStatus.Replaced, NewIdentity: after);
        }
        catch (TooManyReferencesException) { return new(ReplaceStatus.TooManyReferences); }
        catch (Exception ex)
        {
            if (swapStarted && !swapped) RestoreAfterFailedSwap(dup.Path, backup);
            return new(ReplaceStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            held?.Dispose();
            if (!swapped || File.Exists(temp)) TryDelete(temp);
            if (begun)
                try { journal.End(id, swapped ? "done-backup-left" : "aborted"); } catch { /* journal uses recovery */ }
        }
    }

    /// <summary>ReplaceFile can fail after moving D to the backup name; put it back.</summary>
    private static void RestoreAfterFailedSwap(string dup, string backup)
    {
        try { if (!File.Exists(dup) && File.Exists(backup)) File.Move(backup, dup); }
        catch { /* recovery on the next start will retry from the journal */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(Native.Long(path)); } catch { /* the journal lets the next run clean up */ }
    }

    private static void DeleteWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { File.Delete(Native.Long(path)); return; }
            catch (Exception ex) when (i < 5 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50 * (i + 1)); }
        }
    }

    private static string Sddl(string path)
    {
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        return Normalize(new FileInfo(path).GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections));

    }

    /// <summary>
    /// Owner, group and the DACL's entries and protection must survive. Windows may add the "auto-inherited" control
    /// flag (AI/AR) to the file ReplaceFile produces; it carries no permission, so it is left out of the comparison.
    /// </summary>
    public static string Normalize(string sddl) => System.Text.RegularExpressions.Regex.Replace(sddl, @"D:(P)?(?:AI|AR)+", "D:$1");

    public static string? HardLinkMetadataMismatch(string original, string duplicate)
    {

        if (Sddl(original) != Sddl(duplicate)) return SkipReason.HardLinkAclMismatch;
        const uint mask = Native.FILE_ATTRIBUTE_READONLY | Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM;
        var originalId = Native.ReadIdentity(original);
        var duplicateId = Native.ReadIdentity(duplicate);
        if ((originalId.Attributes & mask) != (duplicateId.Attributes & mask))
            return SkipReason.HardLinkAttributesMismatch;
        return null;    }

    private static bool SameBytes
(SafeFileHandle a, string tempPath, long size)
    {
        using var b = Native.OpenRead(tempPath, FileShare.Read);
        if (RandomAccess.GetLength(a) != size || RandomAccess.GetLength(b) != size) return false;
        var x = new byte[Hasher.BufferSize];
        var y = new byte[Hasher.BufferSize];
        for (long offset = 0; offset < size;)
        {
            int want = (int)Math.Min(x.Length, size - offset);
            Hasher.ReadExactly(a, x, offset, want);
            Hasher.ReadExactly(b, y, offset, want);
            if (!x.AsSpan(0, want).SequenceEqual(y.AsSpan(0, want))) return false;
            offset += want;
        }
        return true;
    }
}
