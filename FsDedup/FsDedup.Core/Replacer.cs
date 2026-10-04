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
            // Hold D open without write sharing from here to the swap (delete sharing stays so ReplaceFile can rename it).
            try { held = Native.OpenRead(dup.Path, FileShare.Read | FileShare.Delete); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(ReplaceStatus.Skipped, SkipReason.Locked);
            }
            var now = Native.ReadIdentity(held);
            if (!now.SameContentKey(dup.Id)) return new(ReplaceStatus.Skipped, SkipReason.ChangedSinceHashed);
            if (now.Links > 1) return new(ReplaceStatus.Skipped, SkipReason.HardLinked);
            if (now.IsReparsePoint) return new(ReplaceStatus.Skipped, SkipReason.ReparsePoint);
            if (now.IsEncrypted) return new(ReplaceStatus.Skipped, SkipReason.Encrypted);
            if (now.IsReadOnly) return new(ReplaceStatus.Skipped, SkipReason.ReadOnly);
            var streamsBefore = Native.AlternateStreams(dup.Path);
            var securityBefore = Sddl(dup.Path);

            journal.Begin(id, dup.Path, temp, backup);
            begun = true;

            // 1. The clone, in a hidden temp file next to D, with D's times (ReplaceFile carries over D's ACL, which is checked below).
            strategy.Materialize(original.Path, temp, volume);
            File.SetCreationTimeUtc(temp, DateTime.FromFileTimeUtc(now.CreationTicks));
            File.SetLastWriteTimeUtc(temp, DateTime.FromFileTimeUtc(now.WriteTicks));

            // 2. Byte-for-byte: the temp file against D itself.
            if (!SameBytes(held, temp, now.Size)) return new(ReplaceStatus.Skipped, SkipReason.VerifyFailed);

            hooks.BeforeSwap?.Invoke(dup.Path);

            // 3. D unchanged since it was hashed (and still the file at this path)?
            var again = Native.ReadIdentity(held);
            var byPath = Native.ReadIdentity(dup.Path);
            if (!again.SameContentKey(dup.Id) || !byPath.SameContentKey(again) || again.Links != 1)
                return new(ReplaceStatus.Skipped, SkipReason.ChangedSinceHashed);

            // 4. The swap. ReplaceFile keeps D's ACL, attributes, creation time and named streams (checked below).
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
            if (after.WriteTicks != now.WriteTicks) File.SetLastWriteTimeUtc(dup.Path, DateTime.FromFileTimeUtc(now.WriteTicks));
            after = Native.ReadIdentity(dup.Path);
            var differences = new List<string>();
            if (after.WriteTicks != now.WriteTicks) differences.Add("last-write time");
            if (after.CreationTicks != now.CreationTicks) differences.Add("creation time");
            if (after.Attributes != now.Attributes) differences.Add($"attributes {now.Attributes:x} -> {after.Attributes:x}");
            if (!Native.AlternateStreams(dup.Path).SequenceEqual(streamsBefore)) differences.Add("named streams");
            var securityAfter = Sddl(dup.Path);
            if (securityAfter != securityBefore) differences.Add($"ACL ({securityBefore} -> {securityAfter})");
            if (differences.Count > 0)
            {
                // Something was not carried over: put the old file back.
                held.Dispose(); held = null;
                File.Move(backup, dup.Path, overwrite: true);
                journal.End(id, "rolled-back");
                begun = false;
                return new(ReplaceStatus.Failed, "differed after the swap (" + string.Join("; ", differences) + "); original restored");
            }

            // 5. Only now is the backup (D's old clusters) released.
            held.Dispose(); held = null;
            DeleteWithRetry(backup);
            journal.End(id, "done");
            begun = false;
            return new(ReplaceStatus.Replaced, NewIdentity: after);
        }
        catch (TooManyReferencesException)
        {
            return new(ReplaceStatus.TooManyReferences);
        }
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
            {
                try { journal.End(id, swapped ? "done-backup-left" : "aborted"); } catch { /* journal is best effort here */ }
            }
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
