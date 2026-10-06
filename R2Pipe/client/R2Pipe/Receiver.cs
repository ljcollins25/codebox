using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace R2Pipe;

/// <summary>Where received bytes go. Parts are written in order (the receiver gates them), at an offset for files.</summary>
internal interface ISink : IAsyncDisposable
{
    Task WriteAsync(long offset, byte[] data, int length, CancellationToken ct);
    /// <summary>SHA-256 of everything in the sink, if it can be read back (files); null for a pipe.</summary>
    Task<string?> HashAllAsync(CancellationToken ct);
}

internal sealed class StreamSink(Stream s) : ISink
{
    public async Task WriteAsync(long offset, byte[] data, int length, CancellationToken ct) { await s.WriteAsync(data.AsMemory(0, length), ct).ConfigureAwait(false); await s.FlushAsync(ct).ConfigureAwait(false); }
    public Task<string?> HashAllAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public ValueTask DisposeAsync() => s.DisposeAsync();
}

internal sealed class FileSink : ISink
{
    private readonly FileStream _fs;
    public string Path { get; }
    public FileSink(string path, bool keep)
    {
        Path = path;
        _fs = new FileStream(path, keep ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
    }
    public async Task WriteAsync(long offset, byte[] data, int length, CancellationToken ct)
    {
        _fs.Position = offset;
        await _fs.WriteAsync(data.AsMemory(0, length), ct).ConfigureAwait(false);
    }
    public async Task<string?> HashAllAsync(CancellationToken ct)
    {
        await _fs.FlushAsync(ct).ConfigureAwait(false);
        _fs.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(_fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
    public ValueTask DisposeAsync() => _fs.DisposeAsync();
}

/// <summary>Parts the receiver has already written and acked, kept next to the output file so a restart can carry on.</summary>
internal sealed class ResumeFile
{
    private readonly string? _path;
    private readonly object _lock = new();
    public HashSet<int> Done { get; } = new();
    public string Id { get; private set; } = "";
    public ResumeFile(string? path, string id)
    {
        _path = path; Id = id;
        if (path != null && File.Exists(path))
        {
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(path));
                if (d.RootElement.GetProperty("id").GetString() == id)
                    foreach (var e in d.RootElement.GetProperty("done").EnumerateArray()) Done.Add(e.GetInt32());
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException) { Done.Clear(); }
        }
    }
    public void Add(int n)
    {
        lock (_lock)
        {
            Done.Add(n);
            if (_path != null) File.WriteAllText(_path, JsonSerializer.Serialize(new { id = Id, done = Done.OrderBy(x => x) }));
        }
    }
    public void Delete() { if (_path != null) try { File.Delete(_path); } catch (IOException) { } }
}

internal sealed class ReceiveOptions
{
    public int Parallel { get; init; } = 4;
    public int Attempts { get; init; } = 5;
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public Action<string>? Info { get; init; }
    public int PollSeconds { get; init; } = 25;
}

internal sealed record ReceiveResult(int Parts, long Size, string Sha256, int Resumed);

/// <summary>What the Worker has told us so far: which parts are ready, and whether the sender is finished.</summary>
internal sealed class Tracker
{
    private readonly object _l = new();
    private TaskCompletionSource _changed = NewTcs();
    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, PartInfo> _parts = new();
    public MetaInfo? Meta { get; private set; }
    public string? Failure { get; private set; }

    public void Update(StateResult s)
    {
        lock (_l)
        {
            if (Meta == null || s.Meta.Version >= Meta.Version) Meta = s.Meta;
            foreach (var p in s.Parts)
            {
                // keep a URL we already know when a later push (an ack) has none
                if (_parts.TryGetValue(p.N, out var old) && p.Url == null && old.Url != null) _parts[p.N] = p with { Url = old.Url };
                else _parts[p.N] = p;
            }
            if (s.Meta.Status is "aborted" or "expired") Failure = $"transfer {s.Meta.Status}";
            Bump();
        }
    }
    private void Bump() { var old = _changed; _changed = NewTcs(); old.TrySetResult(); }
    public void Fail(string why) { lock (_l) { Failure ??= why; Bump(); } }

    /// <summary>The part info once part <paramref name="n"/> exists; null when the transfer is complete and has fewer parts.</summary>
    public async Task<PartInfo?> WaitForPartAsync(int n, CancellationToken ct)
    {
        while (true)
        {
            Task t;
            lock (_l)
            {
                if (Failure != null) throw new PipeException(Failure);
                if (_parts.TryGetValue(n, out var p)) return p;
                if (Meta is { TotalParts: { } tp } && n > tp) return null;
                t = _changed.Task;
            }
            await t.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Writes happen in stream order: a writer waits until the output has reached its offset.</summary>
internal sealed class PosGate
{
    private readonly object _l = new();
    private long _pos;
    private readonly List<(long off, TaskCompletionSource tcs)> _w = new();
    public long Pos { get { lock (_l) return _pos; } }
    public Task WaitFor(long offset)
    {
        lock (_l)
        {
            if (_pos >= offset) return Task.CompletedTask;
            var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _w.Add((offset, t)); return t.Task;
        }
    }
    public void Advance(long newPos)
    {
        lock (_l)
        {
            _pos = Math.Max(_pos, newPos);
            foreach (var w in _w.Where(w => w.off <= _pos).ToList()) { w.tcs.TrySetResult(); _w.Remove(w); }
        }
    }
    public void Abort() { lock (_l) foreach (var w in _w) w.tcs.TrySetCanceled(); }
}

/// <summary>
/// Reads inline bytes and state pushes from the feed, downloads parts as they become ready (up to <c>Parallel</c> at a time),
/// verifies each against its SHA-256, writes everything in stream order (inline first, then the parts at their offsets), acks
/// each part (the server deletes it) and at the end checks the total size and the overall SHA-256.
/// </summary>
internal static class Receiver
{
    private static readonly bool Debug = Environment.GetEnvironmentVariable("R2PIPE_DEBUG") == "1";
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static void Dbg(string m) { if (Debug) Console.Error.WriteLine($"[{Clock.ElapsedMilliseconds,6} ms] {m}"); }

    public static async Task<ReceiveResult> RunAsync(IPipeApi api, IBlobs blobs, string id, ISink sink, ResumeFile resume, ReceiveOptions o, Meter meter, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tracker = new Tracker();
        var pos = new PosGate();
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var wlock = new object();
        long total = 0, inlineGot = 0; int resumed = 0, count = 0;
        bool hashValid = true;
        Exception? failure = null;

        await using var feed = await api.OpenFeedAsync(id, o.PollSeconds, linked.Token).ConfigureAwait(false);
        var gotState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pump = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var item = await feed.NextAsync(linked.Token).ConfigureAwait(false);
                    if (item is StateItem d0) Dbg($"feed state v{d0.State.Meta.Version} {d0.State.Meta.Status} total={d0.State.Meta.TotalParts} parts={d0.State.Parts.Count} [{string.Join(',', d0.State.Parts.Select(p => p.N + p.State[0].ToString()))}]"); else if (item is InlineItem d1) Dbg($"feed inline @{d1.Offset} +{d1.Data.Length}"); else Dbg("feed ended");
                    if (item == null) { if (tracker.Meta?.Status is not ("done" or "complete")) tracker.Fail("the connection to the transfer was closed"); return; }
                    if (item is StateItem si)
                    {
                        tracker.Update(si.State); gotState.TrySetResult();
                        if (si.State.Meta.Status is "aborted" or "expired") return;
                    }
                    else if (item is InlineItem ii)
                    {
                        long next = pos.Pos;
                        long end = ii.Offset + ii.Data.Length;
                        if (end <= next) continue;                         // already have it (replay overlapping a live push)
                        if (ii.Offset > next) throw new PipeException($"inline data has a gap: got offset {ii.Offset}, expected {next}");
                        int skip = (int)(next - ii.Offset);
                        var data = skip == 0 ? ii.Data : ii.Data[skip..];
                        await sink.WriteAsync(next, data, data.Length, linked.Token).ConfigureAwait(false);
                        lock (wlock) { overall.AppendData(data); total += data.Length; inlineGot += data.Length; }
                        meter.AddBytes(data.Length);
                        pos.Advance(next + data.Length);
                    }
                    // finished when the sender is done and every inline byte has arrived (they all precede the parts)
                    if (tracker.Meta is { Status: "done" or "complete", TotalParts: not null } mm && pos.Pos >= mm.InlineSize && mm.Status == "done") return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { tracker.Fail("lost the transfer: " + e.Message); gotState.TrySetResult(); }
        }, CancellationToken.None);

        await gotState.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        if (tracker.Failure != null) throw new PipeException(tracker.Failure);

        int next = 0; // parts are claimed in order: 1, 2, 3, ...
        async Task Worker()
        {
            try
            {
                while (true)
                {
                    int n = Interlocked.Increment(ref next);
                    Dbg($"worker claims part {n}");
                    var info = await tracker.WaitForPartAsync(n, linked.Token).ConfigureAwait(false);
                    if (info == null) { Dbg($"worker: no part {n}, exits"); return; }
                    if (info.State == "acked")
                    {
                        if (!resume.Done.Contains(n)) throw new PipeException($"part {n} was already received and deleted, and is not in the resume file");
                        await pos.WaitFor(info.Offset).WaitAsync(linked.Token).ConfigureAwait(false);
                        lock (wlock) { total += info.Size; count++; resumed++; hashValid = false; }
                        meter.AddBytes(info.Size); meter.PartDone(); meter.PartResumed();
                        pos.Advance(info.Offset + info.Size);
                        continue;
                    }
                    var buf = ArrayPool<byte>.Shared.Rent((int)info.Size);
                    try
                    {
                        await Retry.RunAsync(o.Attempts, async attempt =>
                        {
                            var url = attempt == 1 && info.Url != null ? new UrlResult(info.Url, "GET") : await api.GetUrlAsync(id, n, linked.Token).ConfigureAwait(false);
                            await blobs.GetAsync(url, buf, (int)info.Size, linked.Token).ConfigureAwait(false);
                            var sha = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, (int)info.Size))).ToLowerInvariant();
                            if (sha != info.Sha256) throw new IOException($"part {n}: checksum mismatch (got {sha[..12]}…, expected {info.Sha256[..12]}…)");
                            return 0;
                        }, o.Delay, linked.Token, (a, e) => { meter.Retried(); o.Info?.Invoke($"part {n}: attempt {a} failed ({e.Message}), retrying"); }).ConfigureAwait(false);
                        await pos.WaitFor(info.Offset).WaitAsync(linked.Token).ConfigureAwait(false);
                        await sink.WriteAsync(info.Offset, buf, (int)info.Size, linked.Token).ConfigureAwait(false);
                        lock (wlock) { overall.AppendData(buf, 0, (int)info.Size); total += info.Size; count++; }
                        meter.AddBytes(info.Size);
                        pos.Advance(info.Offset + info.Size);
                        await Retry.RunAsync(o.Attempts, async _ => { await api.AckAsync(id, n, linked.Token).ConfigureAwait(false); return 0; }, o.Delay, linked.Token).ConfigureAwait(false);
                        resume.Add(n);
                        meter.PartDone();
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                }
            }
            catch (Exception e) { failure ??= e; linked.Cancel(); pos.Abort(); }
        }

        var workers = Enumerable.Range(0, Math.Max(1, o.Parallel)).Select(_ => Task.Run(Worker, CancellationToken.None)).ToArray();
        var dog = Debug ? Task.Run(async () => { while (!linked.IsCancellationRequested) { await Task.Delay(10000).ConfigureAwait(false); Dbg($"watchdog: next={next} pos={pos.Pos} count={count} metaStatus={tracker.Meta?.Status} total={tracker.Meta?.TotalParts} failure={tracker.Failure} workersDone={workers.Count(w => w.IsCompleted)}"); } }) : Task.CompletedTask;
        await Task.WhenAll(workers).ConfigureAwait(false);
        Dbg("all workers finished");
        // all parts are in; the state says complete, but inline bytes may still be on their way (they come first, so they are done already)
        var meta = tracker.Meta!;
        if (failure == null && meta.TotalParts is not null && inlineGot < meta.InlineSize)
        {
            try { await pos.WaitFor(meta.InlineSize).WaitAsync(TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false); } catch (Exception e) { failure ??= new PipeException("inline data incomplete: " + e.Message); }
        }
        linked.Cancel();
        Dbg("waiting for the feed to stop");
        try { await pump.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception e) { Dbg("feed did not stop: " + e.GetType().Name); }
        ct.ThrowIfCancellationRequested();
        if (failure != null && failure is not OperationCanceledException) throw failure is ApiException or PipeException ? failure : new PipeException(failure.Message);
        if (failure != null) throw new PipeException(tracker.Failure ?? "cancelled");

        meta = tracker.Meta!;
        if (meta.TotalParts is null) throw new PipeException(tracker.Failure ?? "transfer did not complete");
        if (count != meta.TotalParts) throw new PipeException($"received {count} parts, expected {meta.TotalParts}");
        if (total != meta.TotalSize) throw new PipeException($"size mismatch: received {total}, expected {meta.TotalSize}");
        string sha256 = hashValid ? Convert.ToHexString(overall.GetHashAndReset()).ToLowerInvariant() : (await sink.HashAllAsync(ct).ConfigureAwait(false) ?? "");
        if (sha256 == "" && !hashValid) throw new PipeException("resumed into a pipe: cannot verify the overall checksum");
        if (sha256 != meta.Sha256) throw new PipeException($"overall SHA-256 mismatch: got {sha256}, expected {meta.Sha256}");
        resume.Delete();
        return new ReceiveResult(count, total, sha256, resumed);
    }
}
