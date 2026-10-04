using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace FsDedup.Tests;

public class HardLinkTests
{
    private const int Size = 1_000_000;

    [RefsFact]
    public void Hardlinks_share_fileid_content_and_rerun_is_a_noop()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Write("b.bin", 1, Size);
        File.SetCreationTimeUtc(a, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var bytes = File.ReadAllBytes(a);
        var r = s.Run(false, mode: "hardlink");
        Assert.Empty(r.Errors);
        Assert.Equal(1, r.FilesReplaced);
        Assert.Equal(bytes, File.ReadAllBytes(b));
        Assert.Equal(Native.ReadIdentity(a).FileId, Native.ReadIdentity(b).FileId);
        Assert.Equal(2u, Native.ReadIdentity(a).Links);
        var again = s.Run(false, mode: "hardlink");
        Assert.Equal(0, again.FilesReplaced);
        Assert.Equal(0, again.BytesHashed);
        Assert.Equal(1, again.AlreadyShared);
        Assert.Equal(0, again.Skipped.GetValueOrDefault(SkipReason.HardLinked));
        Assert.Single(again.GroupList[0].AlreadyShared);
    }

    [RefsFact]
    public void Acl_mismatch_is_skipped_for_hardlinks()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Write("b.bin", 1, Size);
        File.SetCreationTimeUtc(a, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fi = new FileInfo(b);
        var acl = fi.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        fi.SetAccessControl(acl);
        var r = s.Run(false, mode: "hardlink");
        Assert.Equal(0, r.FilesReplaced);
        Assert.Equal(1, r.Skipped[SkipReason.HardLinkAclMismatch]);
        Assert.NotEqual(Native.ReadIdentity(a).FileId, Native.ReadIdentity(b).FileId);
    }

    [RefsFact]
    public void Readonly_mismatch_is_skipped()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Write("b.bin", 1, Size);
        File.SetCreationTimeUtc(a, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetAttributes(b, File.GetAttributes(b) | FileAttributes.ReadOnly);
        var r = s.Run(false, mode: "hardlink");
        Assert.Equal(0, r.FilesReplaced);
        Assert.Equal(1, r.Skipped[SkipReason.HardLinkAttributesMismatch]);
        File.SetAttributes(b, File.GetAttributes(b) & ~FileAttributes.ReadOnly);
    }

    [RefsFact]
    public void Hidden_and_system_attribute_mismatches_are_skipped()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Write("b.bin", 1, Size);
        File.SetCreationTimeUtc(a, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetAttributes(b, File.GetAttributes(b) | FileAttributes.Hidden);
        var report = s.Run(false, mode: "hardlink");
        Assert.Equal(0, report.FilesReplaced);
        Assert.Equal(1, report.Skipped[SkipReason.HardLinkAttributesMismatch]);
        File.SetAttributes(b, File.GetAttributes(b) & ~FileAttributes.Hidden);
    }

    [RefsFact]
    public void What_if_does_not_make_links_or_write_swap_files()

    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, Size);
        var b = s.Write("b.bin", 1, Size);
        var aid = Native.ReadIdentity(a).FileId;
        var bid = Native.ReadIdentity(b).FileId;
        var r = s.Run(true, mode: "hardlink");
        Assert.Equal(1, r.FilesToReplace);
        Assert.Equal(Size, r.BytesFreed);
        Assert.Equal(aid, Native.ReadIdentity(a).FileId);
        Assert.Equal(bid, Native.ReadIdentity(b).FileId);
        Assert.False(File.Exists(s.Path_(Journal.FileName)));
        Assert.Empty(Directory.GetFiles(s.Root, ".fsdedup-*.tmp", SearchOption.AllDirectories));
    }

    [RefsFact]
    public void Interrupted_hardlink_swap_is_restored()
    {
        using var s = new Scratch();
        var oldBytes = new byte[Size]; new Random(2).NextBytes(oldBytes);
        var newBytes = new byte[Size]; new Random(3).NextBytes(newBytes);
        var d = s.Path_("d.bin"); File.WriteAllBytes(d, newBytes);
        var c = s.Path_("c.bin"); File.WriteAllBytes(c, newBytes);
        var backup = s.Path_(".fsdedup-crash.bak"); File.WriteAllBytes(backup, oldBytes);
        var temp = s.Path_(".fsdedup-crash.tmp"); Win.HardLink(temp, c);
        var journal = s.Path_(Journal.FileName);
        Journal.Append(journal, new Journal.Record("begin", "crash", d, temp, backup, HardLink: true));
        Journal.Append(journal, new Journal.Record("swapped", "crash"));
        s.Run(false, mode: "hardlink");
        Assert.Equal(oldBytes, File.ReadAllBytes(d));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(journal));
    }

    [RefsFact]
    public void Hardlink_groups_split_at_the_limit()
    {
        using var s = new Scratch();
        for (int i = 0; i < 5; i++) s.Write($"f{i}.bin", 1, Size);
        var hooks = new RunHooks { MaxReferencesOverride = 2 };
        var r = s.Run(true, hooks, mode: "hardlink");
        Assert.Equal(2, r.FilesToReplace);
        Assert.Equal(1, r.Groups);
        Assert.Equal(2, r.GroupList.Count);
    }

    [WindowsFact]
    public void Hardlink_mode_runs_on_NTFS_when_volume_supports_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "fsdedup-hl-ntfs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a.bin"); var b = Path.Combine(root, "b.bin");
            byte[] content = new byte[Size]; new Random(8).NextBytes(content);
            File.WriteAllBytes(a, content); File.WriteAllBytes(b, content);
            Assert.Equal("NTFS", Native.GetVolume(root).FileSystem);
            var report = new DedupEngine(new DedupOptions { Root = root, MinSize = 1024, Threads = 2, Mode = "hardlink", SettleSeconds = 0 }).Run();
            Assert.Empty(report.Errors);
            Assert.Equal(1, report.FilesReplaced);
            Assert.Equal(Native.ReadIdentity(a).FileId, Native.ReadIdentity(b).FileId);
        }
        finally { Directory.Delete(root, true); }
    }
}
