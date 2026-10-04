using Xunit;

namespace FsDedup.Tests;

public class CacheTests
{
    [WindowsFact]
    public void A_rerun_with_the_cache_reads_nothing()
    {
        using var s = new Scratch();
        s.Write("a.bin", 1, 600_000);
        s.Write("b.bin", 1, 600_000);
        s.Write("c.bin", 2, 600_000);

        var first = s.Run(whatIf: true);
        Assert.True(first.BytesHashed > 0);
        Assert.Equal(3, first.FilesHashed); // a, b and c share a size; c drops out after the partial hash
        var second = s.Run(whatIf: true);
        Assert.Equal(0, second.BytesHashed);
        Assert.Equal(0, second.FilesHashed);
        Assert.True(second.FilesHashedFromCache >= 2);
        Assert.Equal(first.Groups, second.Groups);
        Assert.Equal(first.GroupList[0].Hash, second.GroupList[0].Hash);

        var header = File.ReadLines(s.Path_(".fsdedup-cache.jsonl")).First();
        Assert.Contains("\"version\":1", header);
    }

    [WindowsFact]
    public void Only_changed_files_are_read_again()
    {
        using var s = new Scratch();
        s.Write("a.bin", 1, 600_000);
        var b = s.Write("b.bin", 1, 600_000);
        s.Run(whatIf: true);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddHours(1)); // content key changes
        var r = s.Run(whatIf: true);
        Assert.Equal(1, r.FilesHashed);
        Assert.Equal(1, r.FilesHashedFromCache);
        Assert.True(r.BytesHashed > 0 && r.BytesHashed <= 600_000 + 2 * 64 * 1024);
    }

    [WindowsFact]
    public void A_damaged_cache_is_ignored_and_the_cache_path_can_be_chosen()
    {
        using var s = new Scratch();
        s.Write("a.bin", 1, 200_000);
        s.Write("b.bin", 1, 200_000);
        var cache = Path.Combine(Path.GetTempPath(), "fsdedup-cache-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(cache, "garbage\n{not json}\n");
            var r = s.Run(true, null, o => o.CachePath = cache);
            Assert.Equal(1, r.Groups);
            Assert.True(r.BytesHashed > 0);
            Assert.False(File.Exists(s.Path_(".fsdedup-cache.jsonl")));
            var again = s.Run(true, null, o => o.CachePath = cache);
            Assert.Equal(0, again.BytesHashed);
        }
        finally { File.Delete(cache); }
    }
}
