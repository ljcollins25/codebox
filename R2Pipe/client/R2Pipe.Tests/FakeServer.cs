using System.Net;
using System.Security.Cryptography;

namespace R2Pipe.Tests;

/// <summary>An in-memory stand-in for the Worker and R2, close enough to the real state machine for scheduling tests.</summary>
internal sealed class FakeServer : IPipeApi, IBlobs
{
    private readonly object _l = new();
    private readonly Dictionary<int, byte[]> _objects = new();
    private readonly Dictionary<int, PartInfo> _parts = new();
    private MetaInfo _meta;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _putsInFlight, _getsInFlight;
    public int MaxPutsInFlight, MaxGetsInFlight;
    public readonly List<string> Log = new();
    public readonly Dictionary<int, int> PutAttempts = new(), GetAttempts = new();
    public readonly List<int> PutCalls = new();
    /// <summary>Called before each PUT/GET of a part with the attempt number; may throw or delay.</summary>
    public Func<int, int, Task>? BeforePut, BeforeGet;
    /// <summary>May alter the bytes returned by a GET (corruption).</summary>
    public Action<int, int, byte[]>? CorruptGet;
    public Func<int, Exception?>? FailDone;

    public FakeServer(long partSize = 1024)
    {
        _meta = new MetaInfo("fake", "f.bin", null, partSize, "presigned", "open", null, null, 0, null, 0, 0, 0);
    }
    public MetaInfo Meta { get { lock (_l) return _meta; } }
    public int ObjectCount { get { lock (_l) return _objects.Count; } }
    public void Seed(int n, byte[] data, string state = "ready")
    {
        lock (_l) { _objects[n] = data; _parts[n] = new PartInfo(n, (n - 1) * 1024L, data.Length, Hex(data), "etag", state); Bump(); }
    }
    public static string Hex(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
    private void Bump() { _meta = _meta with { Version = _meta.Version + 1 }; PushState(); var o = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); o.TrySetResult(); }

    public Task<CreateResult> CreateAsync(string? name, long? size, long partSize, string? mode, CancellationToken ct)
    {
        lock (_l) { _meta = _meta with { PartSize = partSize, Name = name ?? "f.bin", Size = size }; }
        return Task.FromResult(new CreateResult("fake", "presigned", partSize, name, size));
    }
    public readonly List<FeedItem> FeedLog = new();
    public bool InlineAvailable = true;
    public int PutUrlBatches;
    public long InlineStored;
    public readonly List<(long off, int len)> InlineSends = new();
    private void PushState() { FeedLog.Add(new StateItem(new StateResult(_meta, _parts.Values.OrderBy(p => p.N).ToList()))); }
    public Task<List<PutUrl>> PutUrlsAsync(string id, int from, int count, CancellationToken ct)
    {
        lock (_l) PutUrlBatches++;
        return Task.FromResult(Enumerable.Range(from, count).Select(k => new PutUrl(k, "mem://put/" + k)).ToList());
    }
    public Task<UrlResult> PutUrlAsync(string id, int n, CancellationToken ct) => Task.FromResult(new UrlResult("mem://put/" + n, "PUT"));
    public Task<UrlResult> GetUrlAsync(string id, int n, CancellationToken ct)
    {
        lock (_l)
        {
            if (!_parts.TryGetValue(n, out var p)) throw new ApiException(HttpStatusCode.NotFound, "404 part not ready");
            if (p.State == "acked") throw new ApiException(HttpStatusCode.Gone, "410 part already acked and deleted");
            return Task.FromResult(new UrlResult("mem://get/" + n, "GET", p.Size, p.Sha256));
        }
    }

    public async Task PutAsync(UrlResult url, byte[] data, int length, CancellationToken ct)
    {
        int n = int.Parse(url.Url[(url.Url.LastIndexOf('/') + 1)..]);
        int attempt; int cur;
        lock (_l) { PutAttempts[n] = attempt = PutAttempts.GetValueOrDefault(n) + 1; PutCalls.Add(n); cur = ++_putsInFlight; MaxPutsInFlight = Math.Max(MaxPutsInFlight, cur); Log.Add("put-start " + n); }
        try
        {
            if (BeforePut != null) await BeforePut(n, attempt);
            await Task.Yield();
            lock (_l) _objects[n] = data.AsSpan(0, length).ToArray();
        }
        finally { lock (_l) { _putsInFlight--; Log.Add("put-end " + n); } }
    }

    public async Task GetAsync(UrlResult url, byte[] buffer, int expected, CancellationToken ct)
    {
        int n = int.Parse(url.Url[(url.Url.LastIndexOf('/') + 1)..]);
        int attempt; int cur; byte[] src;
        lock (_l) { GetAttempts[n] = attempt = GetAttempts.GetValueOrDefault(n) + 1; cur = ++_getsInFlight; MaxGetsInFlight = Math.Max(MaxGetsInFlight, cur); Log.Add("get-start " + n); }
        try
        {
            if (BeforeGet != null) await BeforeGet(n, attempt);
            await Task.Yield();
            lock (_l) src = _objects.TryGetValue(n, out var s) ? s.ToArray() : throw new ApiException(HttpStatusCode.NotFound, "404 gone");
            CorruptGet?.Invoke(n, attempt, src);
            src.AsSpan(0, expected).CopyTo(buffer);
        }
        finally { lock (_l) { _getsInFlight--; Log.Add("get-end " + n); } }
    }

    public Task DoneAsync(string id, int n, long offset, long size, string sha256, CancellationToken ct)
    {
        if (FailDone?.Invoke(n) is { } ex) throw ex;
        lock (_l)
        {
            if (!_objects.TryGetValue(n, out var o)) throw new ApiException(HttpStatusCode.Conflict, "409 part object not found in R2");
            if (o.Length != size) throw new ApiException(HttpStatusCode.Conflict, "409 size mismatch");
            _parts[n] = new PartInfo(n, offset, size, sha256, "etag", "ready"); Bump(); Log.Add("done " + n);
        }
        return Task.CompletedTask;
    }

    public Task AckAsync(string id, int n, CancellationToken ct)
    {
        lock (_l)
        {
            _objects.Remove(n);
            _parts[n] = _parts[n] with { State = "acked" };
            Log.Add("ack " + n);
            if (_meta.Status == "complete" && _parts.Values.All(p => p.State == "acked")) _meta = _meta with { Status = "done" };
            Bump();
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string id, int parts, long size, long inline, string sha256, CancellationToken ct)
    {
        lock (_l)
        {
            if (inline != InlineStored) throw new ApiException(HttpStatusCode.Conflict, "409 inline mismatch");
            _meta = _meta with { Status = parts == 0 ? "done" : "complete", TotalParts = parts, TotalSize = size, InlineSize = inline, Sha256 = sha256 };
            Log.Add("complete"); Bump();
        }
        return Task.CompletedTask;
    }

    public Task AbortAsync(string id, CancellationToken ct) { lock (_l) { _meta = _meta with { Status = "aborted" }; Bump(); } return Task.CompletedTask; }

    public async Task<StateResult> StateAsync(string id, long since, int waitSeconds, CancellationToken ct)
    {
        Task? wait = null;
        lock (_l) { if (waitSeconds > 0 && _meta.Version <= since && _meta.Status is "open" or "complete") wait = _changed.Task; }
        if (wait != null) { try { await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct)); } catch (OperationCanceledException) { throw; } }
        ct.ThrowIfCancellationRequested();
        lock (_l) return new StateResult(_meta, _parts.Values.OrderBy(p => p.N).ToList());
    }

    public Task<List<ListEntry>> ListAsync(CancellationToken ct) => Task.FromResult(new List<ListEntry>());

    public Task<IReceiveFeed> OpenFeedAsync(string id, int pollSeconds, CancellationToken ct)
    {
        lock (_l)
        {
            // like the Durable Object: the state first, then the inline bytes stored so far, then live updates
            int start = FeedLog.Count;
            var first = new StateItem(new StateResult(_meta, _parts.Values.OrderBy(p => p.N).ToList()));
            return Task.FromResult<IReceiveFeed>(new FakeFeed(this, first, InlineLog.ToList(), start));
        }
    }
    public readonly List<InlineItem> InlineLog = new();

    public Task<IInlineSender?> OpenInlineSenderAsync(string id, CancellationToken ct) =>
        Task.FromResult<IInlineSender?>(InlineAvailable ? new FakeInline(this) : null);

    internal sealed class FakeInline(FakeServer s) : IInlineSender
    {
        public Task SendAsync(long offset, byte[] data, int length, CancellationToken ct)
        {
            lock (s._l)
            {
                if (offset != s.InlineStored) throw new ApiException(HttpStatusCode.Conflict, "409 inline offset");
                var item = new InlineItem(offset, data.AsSpan(0, length).ToArray());
                s.InlineLog.Add(item); s.FeedLog.Add(item); s.InlineSends.Add((offset, length)); s.InlineStored += length; s._meta = s._meta with { InlineSize = s.InlineStored };
                var o = s._changed; s._changed = new(TaskCreationOptions.RunContinuationsAsynchronously); o.TrySetResult();
            }
            return Task.CompletedTask;
        }
        public Task FlushAsync(long total, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeFeed(FakeServer s, StateItem first, List<InlineItem> replay, int index) : IReceiveFeed
    {
        private int _stage; private int _r;
        public async Task<FeedItem?> NextAsync(CancellationToken ct)
        {
            if (_stage == 0) { _stage = 1; return first; }
            if (_stage == 1 && _r < replay.Count) return replay[_r++];
            _stage = 2;
            while (true)
            {
                Task t;
                lock (s._l)
                {
                    while (index < s.FeedLog.Count)
                    {
                        var it = s.FeedLog[index++];
                        if (it is InlineItem ii && replay.Any(x => x.Offset == ii.Offset)) continue;
                        return it;
                    }
                    t = s._changed.Task;
                }
                await t.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A stream that hands out data in small chunks, like a pipe.</summary>
internal sealed class ChunkyStream(byte[] data, int chunk) : Stream
{
    private int _pos;
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n); _pos += n; return n;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

internal sealed class MemSink : ISink
{
    public readonly MemoryStream Data = new();
    public readonly List<long> Offsets = new();
    public Task WriteAsync(long offset, byte[] data, int length, CancellationToken ct)
    {
        lock (Data) { Offsets.Add(offset); Data.Position = offset; Data.Write(data, 0, length); }
        return Task.CompletedTask;
    }
    public Task<string?> HashAllAsync(CancellationToken ct) => Task.FromResult<string?>(FakeServer.Hex(Data.ToArray()));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Util
{
    public static byte[] Random(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    public static Task NoDelay(TimeSpan t, CancellationToken c) => Task.CompletedTask;
}
