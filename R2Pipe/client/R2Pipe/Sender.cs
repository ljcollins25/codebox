using System.Buffers;
using System.Security.Cryptography;

namespace R2Pipe;

internal sealed class SendOptions
{
    public string? Name { get; init; }
    public long? Size { get; init; }
    /// <summary>The largest part. Parts start small and double (slow start) up to this size.</summary>
    public long PartSize { get; init; } = 32L << 20;
    public long FirstPartSize { get; init; } = 1L << 20;
    public int Parallel { get; init; } = 4;
    public string? Mode { get; init; }
    public int Attempts { get; init; } = 5;
    /// <summary>Send the first bytes (up to <see cref="InlineMax"/>) over the WebSocket instead of through R2.</summary>
    public bool Inline { get; init; } = true;
    public long InlineMax { get; init; } = 1L << 20;
    public int InlineChunk { get; init; } = 256 * 1024;
    /// <summary>PUT URLs are fetched this many parts at a time.</summary>
    public int UrlBatch { get; init; } = 8;
    /// <summary>Continue an earlier transfer: parts the server already has with the same checksum are not uploaded again.</summary>
    public string? ResumeId { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public Action<string>? Info { get; init; }
}

internal sealed record SendResult(string Id, int Parts, long Size, string Sha256, long Inline);

/// <summary>Part sizes: <c>first</c>, then doubling, capped at <c>max</c>.</summary>
internal static class PartPlan
{
    public static long SizeOf(int n, long first, long max)
    {
        if (first >= max) return max;
        long s = first;
        for (int i = 1; i < n && s < max; i++) s <<= 1;
        return Math.Min(s, max);
    }
}

/// <summary>Hands out PUT URLs, a batch per round trip.</summary>
internal sealed class UrlCache(IPipeApi api, string id, int batch)
{
    private readonly object _l = new();
    private readonly Dictionary<int, Task<string>> _urls = new();
    public Task<string> GetAsync(int n, CancellationToken ct)
    {
        lock (_l)
        {
            if (_urls.TryGetValue(n, out var t)) return t;
            var batchTask = api.PutUrlsAsync(id, n, batch, ct);
            for (int i = 0; i < batch; i++)
            {
                int k = n + i;
                _urls[k] = Wait(batchTask, k);
            }
            return _urls[n];
        }
    }
    private static async Task<string> Wait(Task<List<PutUrl>> t, int k)
    {
        var list = await t.ConfigureAwait(false);
        return list.First(u => u.N == k).Url;
    }
    public void Forget(int n) { lock (_l) _urls.Remove(n); }
}

/// <summary>
/// The first bytes go inline over the WebSocket (the receiver sees them at once); the rest is cut into parts (small first, then
/// bigger) and uploaded up to <c>Parallel</c> at a time. The overall SHA-256 is taken over the input as it is read (so stdin
/// works), the per-part SHA-256 is reported with each finished part. A failed part is retried with a fresh URL.
/// </summary>
internal static class Sender
{
    public static async Task<SendResult> RunAsync(IPipeApi api, IBlobs blobs, Stream input, SendOptions o, Meter meter, Action<CreateResult>? onCreated, CancellationToken ct)
    {
        string id;
        Dictionary<int, PartInfo> have = new();
        long resumeInline = 0;
        if (o.ResumeId != null)
        {
            var st = await api.StateAsync(o.ResumeId, -1, 0, ct).ConfigureAwait(false);
            if (st.Meta.Status != "open") throw new PipeException($"transfer {o.ResumeId} is {st.Meta.Status}, cannot resume");
            id = o.ResumeId; resumeInline = st.Meta.InlineSize;
            foreach (var p in st.Parts) have[p.N] = p;
            onCreated?.Invoke(new CreateResult(id, st.Meta.Mode, st.Meta.PartSize, st.Meta.Name, st.Meta.Size));
        }
        else
        {
            var c = await api.CreateAsync(o.Name, o.Size, o.PartSize, o.Mode, ct).ConfigureAwait(false);
            id = c.Id;
            onCreated?.Invoke(c);
        }
        long maxPart = o.PartSize;
        if (maxPart > int.MaxValue / 2) throw new PipeException("part size too large");
        var urls = new UrlCache(api, id, o.UrlBatch);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0; int n = 0; bool eof = false;

        // 1. the first bytes, inline
        IInlineSender? inl = (o.Inline && resumeInline == 0 && o.ResumeId == null) ? await api.OpenInlineSenderAsync(id, ct).ConfigureAwait(false) : null;
        var chunk = new byte[o.InlineChunk];
        if (resumeInline > 0)
        {
            long left = resumeInline;
            while (left > 0)
            {
                int r = await input.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)), ct).ConfigureAwait(false);
                if (r == 0) throw new PipeException("input is shorter than the inline data already stored; cannot resume");
                overall.AppendData(chunk, 0, r); left -= r; total += r;
            }
        }
        if (inl != null)
        {
            await using var _ = inl;
            while (total < o.InlineMax)
            {
                int r = await input.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, o.InlineMax - total)), ct).ConfigureAwait(false);
                if (r == 0) { eof = true; break; }
                await inl.SendAsync(total, chunk, r, ct).ConfigureAwait(false);
                overall.AppendData(chunk, 0, r); total += r;
                meter.AddBytes(r);
            }
            await inl.FlushAsync(total, ct).ConfigureAwait(false);
        }
        long inlineTotal = total;

        // 2. the rest as parts
        var gate = new SemaphoreSlim(Math.Max(1, o.Parallel));
        var tasks = new List<Task>();
        Exception? failure = null;
        try
        {
            while (!eof)
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                int want = (int)PartPlan.SizeOf(n + 1, o.FirstPartSize, maxPart);
                var buf = ArrayPool<byte>.Shared.Rent(want);
                int len;
                try { len = await FillAsync(input, buf, want, linked.Token).ConfigureAwait(false); }
                catch { ArrayPool<byte>.Shared.Return(buf); gate.Release(); throw; }
                if (len == 0) { ArrayPool<byte>.Shared.Return(buf); gate.Release(); break; }
                n++;
                long offset = total;
                total += len;
                overall.AppendData(buf, 0, len);
                var sha = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, len))).ToLowerInvariant();
                int part = n;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (have.TryGetValue(part, out var h) && h.Sha256 == sha && h.Size == len && h.Offset == offset) { meter.AddBytes(len); meter.PartDone(); meter.PartResumed(); return; }
                        await UploadAsync(api, blobs, urls, id, part, offset, buf, len, sha, o, meter, linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) { failure ??= e; linked.Cancel(); }
                    finally { ArrayPool<byte>.Shared.Return(buf); gate.Release(); }
                }, CancellationToken.None));
                if (len < want) break;
                if (failure != null) break;
            }
        }
        catch (OperationCanceledException) when (failure != null) { }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (failure != null) { ct.ThrowIfCancellationRequested(); throw failure is ApiException or PipeException ? failure : new PipeException("upload failed: " + failure.Message); }
        ct.ThrowIfCancellationRequested();

        var all = Convert.ToHexString(overall.GetHashAndReset()).ToLowerInvariant();
        await Retry.RunAsync(o.Attempts, async _ => { await api.CompleteAsync(id, n, total, inlineTotal, all, ct).ConfigureAwait(false); return 0; }, o.Delay, ct).ConfigureAwait(false);
        return new SendResult(id, n, total, all, inlineTotal);
    }

    private static async Task UploadAsync(IPipeApi api, IBlobs blobs, UrlCache urls, string id, int n, long offset, byte[] buf, int len, string sha, SendOptions o, Meter meter, CancellationToken ct)
    {
        await Retry.RunAsync(o.Attempts, async attempt =>
        {
            string url;
            if (attempt == 1) url = await urls.GetAsync(n, ct).ConfigureAwait(false);
            else url = (await api.PutUrlAsync(id, n, ct).ConfigureAwait(false)).Url;   // a retry asks for a fresh URL
            await blobs.PutAsync(new UrlResult(url, "PUT"), buf, len, ct).ConfigureAwait(false);
            await api.DoneAsync(id, n, offset, len, sha, ct).ConfigureAwait(false);
            return 0;
        }, o.Delay, ct, (a, e) => { meter.Retried(); o.Info?.Invoke($"part {n}: attempt {a} failed ({e.Message}), retrying"); }).ConfigureAwait(false);
        meter.AddBytes(len);
        meter.PartDone();
    }

    internal static async Task<int> FillAsync(Stream s, byte[] buf, int want, CancellationToken ct)
    {
        int got = 0;
        while (got < want)
        {
            int r = await s.ReadAsync(buf.AsMemory(got, want - got), ct).ConfigureAwait(false);
            if (r == 0) break;
            got += r;
        }
        return got;
    }
}
