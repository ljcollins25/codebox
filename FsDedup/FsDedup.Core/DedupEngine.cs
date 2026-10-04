using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace FsDedup;

/// <summary>The whole run: walk, group by size, partial hash, full hash, plan, replace, report.</summary>
public sealed class DedupEngine(DedupOptions options, RunHooks? hooks = null, TextWriter? log = null)
{
    private readonly RunHooks hooks = hooks ?? new RunHooks();
    private readonly Progress progress = new();

    private sealed class Chunk(FileEntry original, List<FileEntry> dups, List<FileEntry> shared)
    {
        public FileEntry Original = original;
        public List<FileEntry> Dups = dups;
        public List<FileEntry> Shared = shared;
        public GroupReport Report = null!;
    }

    public DedupReport Run(CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        var strategy = DedupStrategies.Create(options.Mode);
        var root = Path.GetFullPath(options.Root);
        if (root.Length > 3) root = root.TrimEnd('\\', '/');
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"folder not found: {root}");
        var volume = Native.GetVolume(root);
        var report = new DedupReport { Root = root, Mode = strategy.Name, WhatIf = options.WhatIf, FileSystem = volume.FileSystem, ClusterSize = volume.ClusterSize };
        if (!options.WhatIf) strategy.CheckVolume(volume);
        else if (!volume.SupportsBlockCloning) report.Errors.Add($"note: {volume.Root} is {volume.FileSystem}; a real run needs ReFS block cloning.");

        var cachePath = Path.GetFullPath(options.CachePath ?? Path.Combine(root, ".fsdedup-cache.jsonl"));
        var journalPath = Path.Combine(root, Journal.FileName);

        if (!options.WhatIf) report.Recovered = JournalRecovery.Run(root, report.Errors);
        else if (JournalRecovery.CountPending(root) is > 0 and var pending)
            report.Errors.Add($"note: {pending} interrupted operation(s) in {journalPath}; a real run will recover them first.");

        report.FreeSpaceBefore = options.WhatIf ? Native.FreeSpace(volume.Root) : Native.SettledFreeSpace(volume.Root, Math.Min(options.SettleSeconds, 3), 10);

        var skipped = new ConcurrentDictionary<string, long>();
        var errors = new ConcurrentBag<string>();
        var errorList = new List<string>();
        var cache = HashCache.Load(cachePath);

        // 1. Walk.
        var scanner = new Scanner(root, Math.Max(options.MinSize, volume.ClusterSize), new HashSet<string>(new[] { cachePath, cachePath + ".tmp", journalPath }, StringComparer.OrdinalIgnoreCase), skipped, errorList);
        var files = scanner.Scan();
        report.FilesScanned = scanner.FilesScanned;
        var seen = new HashSet<CacheKey>(files.Select(f => f.Key));
        foreach (var f in files)
            if (cache.Get(f.Key) is { } e) { f.Partial = e.Partial; f.Full = e.Full; }

        // 2. Same size only; a size seen once is never opened.
        var hashTimer = Stopwatch.StartNew();
        var sized = files.GroupBy(f => f.Id.Size).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        progress.FilesPlanned = sized.Sum(g => g.Count);
        using var printer = options.Json || log is null ? null : new ProgressPrinter(progress, log);

        // 3. Partial hash, then 4. full hash, only for what survived the step before.
        var candidates = sized.SelectMany(g => g).ToList();
        HashAll(candidates, (f, h) => f.Partial ??= Hasher.Partial(h, f.Id.Size, progress), f => f.Partial is not null, cache, (e, f) => e.Partial = f.Partial, skipped, errors, cancel);
        candidates = candidates.Where(f => f.Partial is not null).ToList();
        var partialGroups = candidates.GroupBy(f => (f.Id.Size, f.Partial)).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        HashAll(partialGroups, (f, h) => f.Full ??= Hasher.Full(h, f.Id.Size, progress), f => f.Full is not null, cache, (e, f) => e.Full = f.Full, skipped, errors, cancel);
        var groups = partialGroups.Where(f => f.Full is not null).GroupBy(f => (f.Id.Size, f.Full)).Where(g => g.Count() > 1).ToList();
        hashTimer.Stop();
        report.HashSeconds = hashTimer.Elapsed.TotalSeconds;
        report.BytesHashed = progress.BytesHashed;
        report.FilesHashed = candidates.Count(f => f.ReadFromDisk);
        report.FilesHashedFromCache = candidates.Count(f => !f.ReadFromDisk && f.Partial is not null);
        printer?.Stop();

        // 5. Plan: oldest by creation time is the original (ties by path); split by the strategy's reference limit.
        var chunks = new List<Chunk>();
        int limit = Math.Max(2, hooks.MaxReferencesOverride ?? strategy.MaxReferencesPerOriginal);
        foreach (var g in groups.OrderBy(g => g.Key.Size).ThenBy(g => g.Key.Full))
        {
            var members = g.OrderBy(f => f.Id.CreationTicks).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            report.Groups++;
            report.DuplicateFiles += members.Count - 1;
            for (int i = 0; i < members.Count; i += limit)
            {
                var part = members.Skip(i).Take(limit).ToList();
                var original = part[0];
                var dups = new List<FileEntry>();
                var shared = new List<FileEntry>();
                foreach (var d in part.Skip(1))
                    (volume.SupportsBlockCloning && strategy.AlreadyShared(original.Path, d.Path, volume) ? shared : dups).Add(d);
                report.AlreadyShared += shared.Count;
                report.FilesToReplace += dups.Count;
                report.BytesFreed += dups.Count * Whole(original.Id.Size, volume.ClusterSize);
                var c = new Chunk(original, dups, shared)
                {
                    Report = new GroupReport
                    {
                        Size = original.Id.Size,
                        Hash = original.Full!.Value.ToString("x32"),
                        Original = original.Path,
                        Duplicates = dups.Select(d => d.Path).ToList(),
                        AlreadyShared = shared.Select(d => d.Path).ToList(),
                    },
                };
                chunks.Add(c);
            }
        }
        report.GroupList = chunks.Where(c => c.Dups.Count > 0 || c.Shared.Count > 0).Select(c => c.Report).ToList();

        // 6. Replace (not in what-if).
        if (options.WhatIf)
        {
            foreach (var c in chunks) log?.WriteLineIf(options.Verbose, $"would replace {c.Dups.Count} file(s) with clones of {c.Original.Path}");
        }
        else if (chunks.Any(c => c.Dups.Count > 0))
        {
            var replaceTimer = Stopwatch.StartNew();
            report.BytesFreed = 0;
            report.FilesToReplace = 0;
            using var journal = new Journal(root);
            var replacer = new Replacer(strategy, volume, journal, hooks);
            foreach (var c in chunks)
            {
                var original = c.Original;
                foreach (var d in c.Dups)
                {
                    cancel.ThrowIfCancellationRequested();
                    var outcome = replacer.Replace(original, d);
                    if (outcome.Status == ReplaceStatus.TooManyReferences)
                    {
                        // This original's clusters are at the reference limit: D (untouched) becomes the next original.
                        log?.WriteLineIf(options.Verbose, $"reference limit reached on {original.Path}; {d.Path} is the new original");
                        original = d;
                        continue;
                    }
                    switch (outcome.Status)
                    {
                        case ReplaceStatus.Replaced:
                            report.FilesReplaced++;
                            report.BytesFreed += Whole(d.Id.Size, volume.ClusterSize);
                            if (outcome.NewIdentity is { } ni)
                            {
                                var newKey = new CacheKey(ni.VolumeSerial, ni.FileId, ni.Size, ni.WriteTicks);
                                var e = cache.GetOrAdd(newKey);
                                e.Partial = d.Partial; e.Full = d.Full;
                                seen.Add(newKey);
                            }
                            log?.WriteLineIf(options.Verbose, $"replaced {d.Path}");
                            break;
                        case ReplaceStatus.Skipped:
                            skipped.AddOrUpdate(outcome.Reason!, 1, (_, n) => n + 1);
                            log?.WriteLineIf(options.Verbose, $"skipped {d.Path}: {outcome.Reason}");
                            break;
                        default:
                            errorList.Add($"{d.Path}: {outcome.Reason}");
                            break;
                    }
                }
            }
            report.ReplaceSeconds = replaceTimer.Elapsed.TotalSeconds;
            string jp = journal.Path;
            journal.Dispose();
            try { File.Delete(jp); } catch (IOException) { /* a leftover journal is harmless: recovery has nothing pending */ }
        }

        // 7. Cache, free space, report.
        try { cache.Save(cachePath, seen); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errorList.Add($"cache not written: {ex.Message}"); }
        report.FreeSpaceAfter = report.FilesReplaced > 0 && options.SettleSeconds > 0
            ? Native.WaitForFreeSpace(volume.Root, free => free - report.FreeSpaceBefore >= report.BytesFreed * 0.9, options.SettleSeconds)
            : Native.FreeSpace(volume.Root);
        foreach (var kv in skipped) report.Skipped[kv.Key] = kv.Value;
        report.Errors.AddRange(errors);
        report.Errors.AddRange(errorList);
        report.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return report;
    }

    /// <summary>Bytes in whole clusters: what sharing can free (a final partial cluster is copied, not shared).</summary>
    private static long Whole(long size, long cluster) => size / cluster * cluster;

    /// <summary>Gives every file the hash it lacks (cache first, then a read), in parallel; failures drop out of the run.</summary>
    private void HashAll(List<FileEntry> files, Action<FileEntry, Microsoft.Win32.SafeHandles.SafeFileHandle> compute,
        Func<FileEntry, bool> has, HashCache cache, Action<CacheEntry, FileEntry> store,
        ConcurrentDictionary<string, long> skipped, ConcurrentBag<string> errors, CancellationToken cancel)
    {
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads), CancellationToken = cancel }, f =>
        {
            if (has(f)) return;
            try
            {
                using var h = Native.OpenRead(f.Path, FileShare.Read);
                compute(f, h);
                if (!Native.ReadIdentity(h).SameContentKey(f.Id)) throw new IOException("changed while being hashed");
                if (!f.ReadFromDisk) { f.ReadFromDisk = true; Interlocked.Increment(ref progress.FilesHashed); }
                store(cache.GetOrAdd(f.Key), f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                skipped.AddOrUpdate(SkipReason.Unreadable, 1, (_, n) => n + 1);
                errors.Add($"{f.Path}: {ex.Message}");
                f.Partial = null; f.Full = null;
            }
        });
    }

}
internal static class WriterExtensions
{
    public static void WriteLineIf(this TextWriter w, bool condition, string text)
    {
        if (condition) w.WriteLine(text);
    }
}

/// <summary>Prints bytes hashed, rate and files about every two seconds while hashing runs.</summary>
internal sealed class ProgressPrinter : IDisposable
{
    private readonly Timer timer;
    private readonly Progress progress;
    private readonly TextWriter log;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public ProgressPrinter(Progress progress, TextWriter log)
    {
        this.progress = progress; this.log = log;
        timer = new Timer(_ => Print(), null, 2000, 2000);
    }

    private void Print()
    {
        double mb = progress.BytesHashed / 1048576.0;
        log.WriteLine($"hashing: {mb:F0} MB, {mb / Math.Max(clock.Elapsed.TotalSeconds, 0.001):F0} MB/s, {progress.FilesHashed + progress.FilesFromCache} of {progress.FilesPlanned} files");
    }

    public void Stop() => timer.Dispose();
    public void Dispose() => timer.Dispose();
}
