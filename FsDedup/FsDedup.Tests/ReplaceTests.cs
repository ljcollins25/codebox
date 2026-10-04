using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace FsDedup.Tests;

public class ReplaceTests
{
    private const int Size = 5_000_000;

    [RefsFact]
    public void What_if_writes_nothing_except_the_cache()
    {
        using var s = new Scratch();
        s.Write("a/x.bin", 1, Size);
        s.Write("b/x.bin", 1, Size);
        s.Write("c/y.bin", 2, Size);
        var before = s.Snapshot();
        var freeBefore = Native.FreeSpace(s.Root);

        var r = s.Run(whatIf: true);

        Assert.Equal(before, s.Snapshot());
        Assert.Equal(1, r.Groups);
        Assert.Equal(1, r.FilesToReplace);
        Assert.InRange(r.BytesFreed, Size, Size + 4096); // rounded up to clusters
        Assert.Equal(0, r.FilesReplaced);
        Assert.False(File.Exists(s.Path_(Journal.FileName)));
        Assert.Empty(Directory.GetFiles(s.Root, ".fsdedup-*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(s.Root, ".fsdedup-*.bak", SearchOption.AllDirectories));
        Assert.True(File.Exists(s.Path_(".fsdedup-cache.jsonl")));
        Assert.InRange(freeBefore - Native.FreeSpace(s.Root), -8_000_000, 8_000_000);
    }

    [RefsFact]
    public void A_real_run_replaces_duplicates_with_clones_that_share_clusters()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Write("b.bin", 1, Size);
        var c = s.Write("sub/c.bin", 1, Size + 0);
        var odd = s.Write("odd.bin", 3, Size - 1234); // not cluster aligned, unique
        var oddCopy = s.Write("oddcopy.bin", 3, Size - 1234);
        var other = s.Write("other.bin", 2, Size);
        File.SetCreationTimeUtc(a, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(odd, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var contentA = File.ReadAllBytes(a);
        var contentOdd = File.ReadAllBytes(odd);
        Assert.False(Win.SameClusters(a, b));
        var free0 = Native.FreeSpace(s.Root);

        var r = s.Run(whatIf: false);

        Assert.Empty(r.Errors);
        Assert.Equal(3, r.FilesReplaced);
        Assert.Equal(2, r.Groups);
        Assert.Equal(contentA, File.ReadAllBytes(a));
        Assert.Equal(contentA, File.ReadAllBytes(b));
        Assert.Equal(contentA, File.ReadAllBytes(c));
        Assert.Equal(contentOdd, File.ReadAllBytes(oddCopy));
        Assert.True(Win.SameClusters(a, b));
        Assert.True(Win.SameClusters(a, c));
        Assert.True(Win.SameClusters(odd, oddCopy));
        Assert.False(Win.SameClusters(a, other));
        // Independent of the extent query: the volume really got space back (3 x 5 MB + 1 x ~5 MB, minus metadata).
        long gained = Native.FreeSpace(s.Root) - free0;
        Assert.True(gained > 0.8 * (3 * Size + Size - 1234), $"free space grew by only {gained}");
        Assert.True(r.FreeSpaceAfter > r.FreeSpaceBefore);
        Assert.Empty(Directory.GetFiles(s.Root, ".fsdedup-*", SearchOption.AllDirectories).Where(f => !f.EndsWith("cache.jsonl")));
        Assert.False(File.Exists(s.Path_(Journal.FileName)));

        // A rerun has nothing to do and reads nothing.
        var again = s.Run(whatIf: false);
        Assert.Equal(0, again.FilesReplaced);
        Assert.Equal(0, again.BytesHashed);
        Assert.Equal(3, again.AlreadyShared);
        Assert.Equal(0, again.FilesToReplace);
    }

    [RefsFact]
    public void The_duplicates_ACL_attributes_timestamps_and_streams_are_kept()
    {
        using var s = new Scratch();
        var c = s.Write("c.bin", 1, Size);
        var d = s.Write("d.bin", 1, Size);
        File.SetCreationTimeUtc(c, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var created = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var written = new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetCreationTimeUtc(d, created);
        File.SetLastWriteTimeUtc(d, written);
        File.SetAttributes(d, FileAttributes.Archive | FileAttributes.Hidden);
        var fi = new FileInfo(d);
        var sec = fi.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null), FileSystemRights.Write, AccessControlType.Deny));
        fi.SetAccessControl(sec);
        File.WriteAllText(d + ":notes", "alternate stream content");
        File.SetCreationTimeUtc(d, created);
        File.SetLastWriteTimeUtc(d, written);
        var sddl = Win.Sddl(d);
        var attrs = File.GetAttributes(d);

        var r = s.Run(whatIf: false);

        Assert.Empty(r.Errors);
        Assert.Equal(1, r.FilesReplaced);
        Assert.True(Win.SameClusters(c, d));
        Assert.Equal(sddl, Win.Sddl(d));
        Assert.Equal(attrs, File.GetAttributes(d));
        Assert.Equal(created, File.GetCreationTimeUtc(d));
        Assert.Equal(written, File.GetLastWriteTimeUtc(d));
        Assert.Equal("alternate stream content", File.ReadAllText(d + ":notes"));
        Assert.Equal(File.ReadAllBytes(c), File.ReadAllBytes(d));
    }

    [RefsFact]
    public void Hard_linked_files_are_left_alone_in_a_real_run()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Path_("b.bin");
        Win.HardLink(b, a);
        var c = s.Write("c.bin", 1, Size);
        var d = s.Write("d.bin", 1, Size);
        File.SetCreationTimeUtc(c, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var idA = Native.ReadIdentity(a).FileId;

        var r = s.Run(whatIf: false);

        Assert.Equal(1, r.FilesReplaced); // d only; c is the original
        Assert.Equal(2u, Native.ReadIdentity(a).Links);
        Assert.Equal(idA, Native.ReadIdentity(a).FileId);
        Assert.Equal(idA, Native.ReadIdentity(b).FileId);
        Assert.False(Win.SameClusters(a, c));
    }

    [RefsFact]
    public void A_file_changed_after_hashing_is_left_alone()
    {
        using var s = new Scratch();
        var c = s.Write("c.bin", 1, Size);
        var d = s.Write("d.bin", 1, Size);
        File.SetCreationTimeUtc(c, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var hooks = new RunHooks { AfterHash = p => { if (p == d) File.AppendAllText(d, "more"); } };

        var r = s.Run(whatIf: false, hooks);

        Assert.Equal(0, r.FilesReplaced);
        Assert.Equal(1, r.Skipped["changed-since-hashed"]);
        Assert.Equal(Size + 4, new FileInfo(d).Length);
        Assert.False(Win.SameClusters(c, d));
        Assert.Empty(Directory.GetFiles(s.Root, ".fsdedup-*.tmp"));
    }

    [RefsFact]
    public void A_change_that_keeps_size_and_timestamp_is_caught_by_the_byte_comparison()
    {
        using var s = new Scratch();
        var c = s.Write("c.bin", 1, Size);
        var d = s.Write("d.bin", 1, Size);
        File.SetCreationTimeUtc(c, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var stamp = File.GetLastWriteTimeUtc(d);
        byte[] changed = File.ReadAllBytes(d);
        changed[Size / 2] ^= 0x55;
        var hooks = new RunHooks
        {
            AfterHash = p =>
            {
                if (p != d) return;
                File.WriteAllBytes(d, changed);
                File.SetLastWriteTimeUtc(d, stamp);
            },
        };

        var r = s.Run(whatIf: false, hooks);

        Assert.Equal(0, r.FilesReplaced);
        Assert.Equal(1, r.Skipped["verify-failed"]);
        Assert.Equal(changed, File.ReadAllBytes(d));
        Assert.Empty(Directory.GetFiles(s.Root, ".fsdedup-*.tmp"));
    }

    [RefsFact]
    public void The_duplicate_is_held_without_write_sharing_while_swapping()
    {
        using var s = new Scratch();
        var c = s.Write("c.bin", 1, Size);
        var d = s.Write("d.bin", 1, Size);
        File.SetCreationTimeUtc(c, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Exception? attempt = null;
        var hooks = new RunHooks
        {
            BeforeSwap = p =>
            {
                try { using var w = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (Exception ex) { attempt = ex; }
            },
        };
        var r = s.Run(whatIf: false, hooks);
        Assert.IsType<IOException>(attempt);
        Assert.Equal(1, r.FilesReplaced);
    }

    [RefsFact]
    public void An_interrupted_run_is_cleaned_up_on_the_next_start()
    {
        using var s = new Scratch();
        var oldBytes = new byte[300_000];
        new Random(9).NextBytes(oldBytes);
        var cloneBytes = new byte[300_000];
        new Random(10).NextBytes(cloneBytes);
        var journal = s.Path_(Journal.FileName);

        // 1. Died after creating the temp file: D untouched, temp must go.
        var d1 = s.Path_("d1.bin"); File.WriteAllBytes(d1, oldBytes);
        var t1 = s.Path_(".fsdedup-aaaa.tmp"); File.WriteAllBytes(t1, cloneBytes);
        Journal.Append(journal, new Journal.Record("begin", "1", d1, t1, s.Path_(".fsdedup-aaaa.bak")));

        // 2. Died inside ReplaceFile after both renames: D is the new file, the backup holds the old one: roll back.
        var d2 = s.Path_("d2.bin"); File.WriteAllBytes(d2, cloneBytes);
        var b2 = s.Path_(".fsdedup-bbbb.bak"); File.WriteAllBytes(b2, oldBytes);
        var t2 = s.Path_(".fsdedup-bbbb.tmp");
        Journal.Append(journal, new Journal.Record("begin", "2", d2, t2, b2));
        Journal.Append(journal, new Journal.Record("swapping", "2"));

        // 3. Died after D was moved to the backup but before the temp took its place: D missing, put the backup back.
        var d3 = s.Path_("d3.bin");
        var b3 = s.Path_(".fsdedup-cccc.bak"); File.WriteAllBytes(b3, oldBytes);
        var t3 = s.Path_(".fsdedup-cccc.tmp"); File.WriteAllBytes(t3, cloneBytes);
        Journal.Append(journal, new Journal.Record("begin", "3", d3, t3, b3));
        Journal.Append(journal, new Journal.Record("swapping", "3"));

        // 4. The swap finished, the backup delete did not: keep D, delete the backup.
        var d4 = s.Path_("d4.bin"); File.WriteAllBytes(d4, cloneBytes);
        var b4 = s.Path_(".fsdedup-dddd.bak"); File.WriteAllBytes(b4, oldBytes);
        Journal.Append(journal, new Journal.Record("begin", "4", d4, s.Path_(".fsdedup-dddd.tmp"), b4));
        Journal.Append(journal, new Journal.Record("swapped", "4"));

        // 5. Finished cleanly: nothing to do (its temp name is reused by an unrelated file that must survive).
        var keep = s.Path_("keep.bin"); File.WriteAllBytes(keep, oldBytes);
        Journal.Append(journal, new Journal.Record("begin", "5", keep, null, null));
        Journal.Append(journal, new Journal.Record("end", "5", Note: "done"));
        File.AppendAllText(journal, "{\"op\":\"begin\",\"id\":\"6\",\"d\":\"torn"); // a torn last line

        var r = s.Run(whatIf: false);

        Assert.Equal(oldBytes, File.ReadAllBytes(d1));
        Assert.False(File.Exists(t1));
        Assert.Equal(oldBytes, File.ReadAllBytes(d2));
        Assert.False(File.Exists(b2));
        Assert.Equal(oldBytes, File.ReadAllBytes(d3));
        Assert.False(File.Exists(b3));
        Assert.False(File.Exists(t3));
        Assert.Equal(cloneBytes, File.ReadAllBytes(d4));
        Assert.False(File.Exists(b4));
        Assert.Equal(oldBytes, File.ReadAllBytes(keep));
        Assert.False(File.Exists(journal));
        Assert.True(r.Recovered.Count >= 4, string.Join("; ", r.Recovered));
        Assert.NotEmpty(r.Recovered);
        Assert.Empty(r.Errors);
    }

    [RefsFact]
    public void What_if_reports_a_pending_journal_but_does_not_touch_it()
    {
        using var s = new Scratch();
        var d = s.Path_("d.bin"); File.WriteAllBytes(d, new byte[2000]);
        var t = s.Path_(".fsdedup-x.tmp"); File.WriteAllBytes(t, new byte[10]);
        Journal.Append(s.Path_(Journal.FileName), new Journal.Record("begin", "1", d, t, s.Path_(".fsdedup-x.bak")));
        var r = s.Run(whatIf: true);
        Assert.True(File.Exists(t));
        Assert.True(File.Exists(s.Path_(Journal.FileName)));
        Assert.Contains(r.Errors, e => e.StartsWith("note:") && e.Contains("interrupted"));
    }
}
