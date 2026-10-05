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
            Meta = s.Meta;
            foreach (var p in s.Parts) _parts[p.N] = p;
            if (s.Meta.Status is "aborted" or "expired") Failure = $"transfer {s.Meta.Status}";
            var old = _changed; _changed = NewTcs(); old.TrySetResult();
        }
    }
    public void Fail(string why) { lock (_l) { Failure ??= why; var o = _changed; _changed = NewTcs(); o.TrySetResult(); } }

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

internal sealed class OrderGate(int first)
{
    private readonly object _l = new();
    private int _cur = first;
    private readonly Dictionary<int, TaskCompletionSource> _w = new();
    public Task WaitTurn(int n)
    {
        lock (_l)
        {
            if (n <= _cur) return Task.CompletedTask;
            var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _w[n] = t; return t.Task;
        }
    }
    public void Done(int n)
    {
        lock (_l)
        {
            _cur = n + 1;
            if (_w.Remove(_cur, out var t)) t.TrySetResult();
        }
    }
    public void Abort() { lock (_l) foreach (var t in _w.Values) t.TrySetCanceled(); }
}

/// <summary>
/// Downloads parts as they become ready, up to <c>Parallel</c> at a time, in order of part number (so memory is bounded by
/// parallel × part size). Each part is checked against its SHA-256, written in order, then acked (the server deletes it).
/// At the end the total size and the overall SHA-256 are checked.
/// </summary>
internal static class Receiver
{
    public static async Task<ReceiveResult> RunAsync(IPipeApi api, IBlobs blobs, string id, ISink sink, ResumeFile resume, ReceiveOptions o, Meter meter, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tracker = new Tracker();
        var first = await api.StateAsync(id, -1, 0, linked.Token).ConfigureAwait(false);
        if (first.Meta.Status is "aborted" or "expired") throw new PipeException($"transfer is {first.Meta.Status}");
        tracker.Update(first);
        long partSize = first.Meta.PartSize;

        var poll = Task.Run(async () =>
        {
            long since = first.Meta.Version;
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    StateResult st;
                    try { st = await Retry.RunAsync(o.Attempts, _ => api.StateAsync(id, since, o.PollSeconds, linked.Token), o.Delay, linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    since = st.Meta.Version;
                    tracker.Update(st);
                    if (st.Meta.Status is "aborted" or "expired" or "done") return;
                }
            }
            catch (Exception e) { tracker.Fail("lost the transfer state: " + e.Message); }
        }, CancellationToken.None);

        int next = 0; // parts are claimed in order: 1, 2, 3, ...
        var gate = new OrderGate(1);
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0; int resumed = 0, count = 0;
        bool hashValid = true;
        Exception? failure = null;
        var wlock = new object();

        async Task Worker()
        {
            try
            {
                while (true)
                {
                    int n = Interlocked.Increment(ref next);
                    var info = await tracker.WaitForPartAsync(n, linked.Token).ConfigureAwait(false);
                    if (info == null) return;
                    if (info.State == "acked")
                    {
                        if (!resume.Done.Contains(n)) throw new PipeException($"part {n} was already received and deleted, and is not in the resume file");
                        await gate.WaitTurn(n).ConfigureAwait(false);
                        lock (wlock) { total += info.Size; count++; resumed++; hashValid = false; }
                        meter.AddBytes(info.Size); meter.PartDone(); meter.PartResumed();
                        gate.Done(n);
                        continue;
                    }
                    var buf = ArrayPool<byte>.Shared.Rent((int)info.Size);
                    try
                    {
                        await Retry.RunAsync(o.Attempts, async _ =>
                        {
                            var url = await api.GetUrlAsync(id, n, linked.Token).ConfigureAwait(false);
                            await blobs.GetAsync(url, buf, (int)info.Size, linked.Token).ConfigureAwait(false);
                            var sha = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, (int)info.Size))).ToLowerInvariant();
                            if (sha != info.Sha256) throw new IOException($"part {n}: checksum mismatch (got {sha[..12]}…, expected {info.Sha256[..12]}…)");
                            return 0;
                        }, o.Delay, linked.Token, (a, e) => { meter.Retried(); o.Info?.Invoke($"part {n}: attempt {a} failed ({e.Message}), retrying"); }).ConfigureAwait(false);
                        await gate.WaitTurn(n).ConfigureAwait(false);
                        await sink.WriteAsync((n - 1) * partSize, buf, (int)info.Size, linked.Token).ConfigureAwait(false);
                        lock (wlock) { overall.AppendData(buf, 0, (int)info.Size); total += info.Size; count++; }
                        meter.AddBytes(info.Size);
                        gate.Done(n);
                        await Retry.RunAsync(o.Attempts, async _ => { await api.AckAsync(id, n, linked.Token).ConfigureAwait(false); return 0; }, o.Delay, linked.Token).ConfigureAwait(false);
                        resume.Add(n);
                        meter.PartDone();
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                }
            }
            catch (Exception e) { failure ??= e; linked.Cancel(); gate.Abort(); }
        }

        var workers = Enumerable.Range(0, Math.Max(1, o.Parallel)).Select(_ => Task.Run(Worker, CancellationToken.None)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        linked.Cancel();
        await poll.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (failure != null && failure is not OperationCanceledException) throw failure is ApiException or PipeException ? failure : new PipeException(failure.Message);
        if (failure != null) throw new PipeException(tracker.Failure ?? "cancelled");

        var meta = tracker.Meta!;
        if (meta.TotalParts is null) throw new PipeException("transfer did not complete");
        if (count != meta.TotalParts) throw new PipeException($"received {count} parts, expected {meta.TotalParts}");
        if (total != meta.TotalSize) throw new PipeException($"size mismatch: received {total}, expected {meta.TotalSize}");
        string sha256 = hashValid ? Convert.ToHexString(overall.GetHashAndReset()).ToLowerInvariant() : (await sink.HashAllAsync(ct).ConfigureAwait(false) ?? "");
        if (sha256 == "" && !hashValid) throw new PipeException("resumed into a pipe: cannot verify the overall checksum");
        if (sha256 != meta.Sha256) throw new PipeException($"overall SHA-256 mismatch: got {sha256}, expected {meta.Sha256}");
        resume.Delete();
        return new ReceiveResult(count, total, sha256, resumed);
    }
}
