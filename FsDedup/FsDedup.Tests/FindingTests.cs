using Xunit;

namespace FsDedup.Tests;

public class FindingTests
{
    [WindowsFact]
    public void Same_size_but_different_content_is_not_grouped()
    {
        using var s = new Scratch();
        // Same first and last 64 KB, differing in the middle: only the full hash can tell them apart.
        var a = s.Write("a.bin", 1, 400_000);
        var bytes = File.ReadAllBytes(a);
        bytes[200_000] ^= 0xFF;
        File.WriteAllBytes(s.Path_("b.bin"), bytes);
        s.Write("c.bin", 2, 400_000);

        var r = s.Run(whatIf: true);
        Assert.Equal(0, r.Groups);
        Assert.Equal(3, r.FilesScanned);
        Assert.True(r.FilesHashed >= 2);
    }

    [WindowsFact]
    public void Identical_files_form_one_group_and_unique_sizes_are_never_read()
    {
        using var s = new Scratch();
        s.Write("a/x.bin", 1, 300_000);
        s.Write("b/x.bin", 1, 300_000);
        s.Write("c/x.bin", 1, 300_000);
        s.Write("lonely.bin", 5, 123_457); // unique size
        var r = s.Run(whatIf: true);
        Assert.Equal(1, r.Groups);
        Assert.Equal(2, r.DuplicateFiles);
        Assert.Equal(2, r.FilesToReplace);
        Assert.Equal(3, r.FilesHashed);
        Assert.Single(r.GroupList);
        Assert.Equal(2, r.GroupList[0].Duplicates.Count);
        // 3 files x (partial + full) = reads of at most 64K*2 + 300K each; the lonely file adds nothing.
        Assert.True(r.BytesHashed <= 3 * (300_000 + 2 * 64 * 1024), $"read {r.BytesHashed} bytes");
        Assert.Equal(0, r.Skipped.GetValueOrDefault("below-min-size"));
    }

    [WindowsFact]
    public void Files_below_min_size_are_skipped_and_counted()
    {
        using var s = new Scratch();
        s.Write("a.bin", 1, 500);
        s.Write("b.bin", 1, 500);
        var r = s.Run(whatIf: true);
        Assert.Equal(0, r.Groups);
        Assert.Equal(2, r.Skipped["below-min-size"]);
    }

    [WindowsFact]
    public void Oldest_by_creation_time_is_the_original_ties_by_path()
    {
        using var s = new Scratch();
        var x = s.Write("x.bin", 1, 100_000);
        var y = s.Write("y.bin", 1, 100_000);
        var z = s.Write("z.bin", 1, 100_000);
        var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(x, t.AddDays(5));
        File.SetCreationTimeUtc(y, t);
        File.SetCreationTimeUtc(z, t);
        var r = s.Run(whatIf: true);
        Assert.Equal(y, r.GroupList[0].Original); // y and z tie; y sorts first
        Assert.Equal(new[] { z, x }, r.GroupList[0].Duplicates.OrderByDescending(p => p == z));
    }

    [WindowsFact]
    public void Groups_beyond_the_reference_limit_are_split_across_originals()
    {
        using var s = new Scratch();
        for (int i = 0; i < 5; i++) s.Write($"f{i}.bin", 1, 100_000);
        var hooks = new RunHooks { MaxReferencesOverride = 2 };
        var r = s.Run(whatIf: true, hooks);
        Assert.Equal(3, r.GroupList.Count); // chunks of 2, 2, 1 files: originals f0, f2, f4 (f4 has no duplicates)
        Assert.Equal(2, r.FilesToReplace);
        Assert.Equal(1, r.Groups);
    }

    [WindowsFact]
    public void Hard_linked_files_are_skipped_as_source_and_target()
    {
        using var s = new Scratch();
        var a = s.Write("a.bin", 1, 100_000);
        Win.HardLink(s.Path_("b.bin"), a);
        s.Write("c.bin", 1, 100_000);
        var r = s.Run(whatIf: true);
        Assert.Equal(2, r.Skipped["hard-linked"]);
        Assert.Equal(0, r.Groups); // only c remains, alone
    }

    [WindowsFact]
    public void Reparse_points_are_not_followed_and_files_that_are_reparse_points_are_skipped()
    {
        using var s = new Scratch();
        var real = s.Write("real/a.bin", 1, 100_000);
        s.Write("other/a.bin", 1, 100_000);
        var junction = s.Path_("junction");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{s.Path_("real")}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(junction), "could not create the junction");
        File.CreateSymbolicLink(s.Path_("link.bin"), real);

        var r = s.Run(whatIf: true);
        Assert.Equal(2, r.Skipped["reparse-point"]); // the junction and the file symlink
        Assert.Equal(1, r.Groups);
        var all = r.GroupList.SelectMany(g => g.Duplicates.Append(g.Original)).ToList();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, f => f.Contains("junction") || f.Contains("link.bin"));
        Directory.Delete(junction); // remove the junction itself, not its target
    }

    [WindowsFact]
    public void The_tools_own_files_are_never_candidates()
    {
        using var s = new Scratch();
        s.Write("a.bin", 1, 100_000);
        s.Write(".fsdedup-x.tmp", 1, 100_000);
        var r = s.Run(whatIf: true);
        Assert.Equal(0, r.Groups);
        Assert.Equal(1, r.Skipped["tool-file"]);
    }
}
