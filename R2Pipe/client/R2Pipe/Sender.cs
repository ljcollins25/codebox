using System.Buffers;
using System.Security.Cryptography;

namespace R2Pipe;

internal sealed class SendOptions
{
    public string? Name { get; init; }
    public long? Size { get; init; }
    public long PartSize { get; init; } = 32L << 20;
    public int Parallel { get; init; } = 4;
    public string? Mode { get; init; }
    public int Attempts { get; init; } = 5;
    /// <summary>Continue an earlier transfer: parts the server already has with the same checksum are not uploaded again.</summary>
    public string? ResumeId { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public Action<string>? Info { get; init; }
}

internal sealed record SendResult(string Id, int Parts, long Size, string Sha256);

/// <summary>
/// Reads the input in order into parts and uploads up to <c>Parallel</c> of them at once. The overall SHA-256 is taken over the
/// input as it is read (so stdin works), the per-part SHA-256 is reported with each finished part. A failed part is retried
/// with a fresh URL; the others carry on.
/// </summary>
internal static class Sender
{
    public static async Task<SendResult> RunAsync(IPipeApi api, IBlobs blobs, Stream input, SendOptions o, Meter meter, Action<CreateResult>? onCreated, CancellationToken ct)
    {
        string id; long partSize;
        Dictionary<int, PartInfo> have = new();
        if (o.ResumeId != null)
        {
            var st = await api.StateAsync(o.ResumeId, -1, 0, ct).ConfigureAwait(false);
            if (st.Meta.Status != "open") throw new PipeException($"transfer {o.ResumeId} is {st.Meta.Status}, cannot resume");
            id = o.ResumeId; partSize = st.Meta.PartSize;
            foreach (var p in st.Parts) have[p.N] = p;
            onCreated?.Invoke(new CreateResult(id, st.Meta.Mode, partSize, st.Meta.Name, st.Meta.Size));
        }
        else
        {
            var c = await api.CreateAsync(o.Name, o.Size, o.PartSize, o.Mode, ct).ConfigureAwait(false);
            id = c.Id; partSize = c.PartSize;
            onCreated?.Invoke(c);
        }
        if (partSize > int.MaxValue / 2) throw new PipeException("part size too large");
        int ps = (int)partSize;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new SemaphoreSlim(Math.Max(1, o.Parallel));
        var tasks = new List<Task>();
        Exception? failure = null;
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0; int n = 0;

        try
        {
            while (true)
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                var buf = ArrayPool<byte>.Shared.Rent(ps);
                int len;
                try { len = await FillAsync(input, buf, ps, linked.Token).ConfigureAwait(false); }
                catch { ArrayPool<byte>.Shared.Return(buf); gate.Release(); throw; }
                if (len == 0) { ArrayPool<byte>.Shared.Return(buf); gate.Release(); break; }
                n++;
                total += len;
                overall.AppendData(buf, 0, len);
                var sha = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, len))).ToLowerInvariant();
                int part = n;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (have.TryGetValue(part, out var h) && h.Sha256 == sha && h.Size == len) { meter.AddBytes(len); meter.PartDone(); meter.PartResumed(); return; }
                        await UploadAsync(api, blobs, id, part, buf, len, sha, o, meter, linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) { failure ??= e; linked.Cancel(); }
                    finally { ArrayPool<byte>.Shared.Return(buf); gate.Release(); }
                }, CancellationToken.None));
                if (len < ps) break;
                if (failure != null) break;
            }
        }
        catch (OperationCanceledException) when (failure != null) { }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (failure != null) { ct.ThrowIfCancellationRequested(); throw failure is ApiException or PipeException ? failure : new PipeException("upload failed: " + failure.Message); }
        ct.ThrowIfCancellationRequested();

        var all = Convert.ToHexString(overall.GetHashAndReset()).ToLowerInvariant();
        await Retry.RunAsync(o.Attempts, async _ => { await api.CompleteAsync(id, n, total, all, ct).ConfigureAwait(false); return 0; }, o.Delay, ct).ConfigureAwait(false);
        return new SendResult(id, n, total, all);
    }

    private static async Task UploadAsync(IPipeApi api, IBlobs blobs, string id, int n, byte[] buf, int len, string sha, SendOptions o, Meter meter, CancellationToken ct)
    {
        await Retry.RunAsync(o.Attempts, async attempt =>
        {
            var url = await api.PutUrlAsync(id, n, ct).ConfigureAwait(false);
            await blobs.PutAsync(url, buf, len, ct).ConfigureAwait(false);
            await api.DoneAsync(id, n, len, sha, ct).ConfigureAwait(false);
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
