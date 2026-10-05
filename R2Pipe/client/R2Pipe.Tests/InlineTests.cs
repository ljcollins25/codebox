using Xunit;

namespace R2Pipe.Tests;

public class InlineTests
{
    private static SendOptions Opts(long first = 1024, long max = 8192, long inlineMax = 3000, int chunk = 1000, int parallel = 3, bool inline = true) =>
        new() { Parallel = parallel, PartSize = max, FirstPartSize = first, Inline = inline, InlineMax = inlineMax, InlineChunk = chunk, UrlBatch = 4, Delay = Util.NoDelay };
    private static ReceiveOptions ROpts(int p = 3) => new() { Parallel = p, Delay = Util.NoDelay, PollSeconds = 1 };

    private static async Task<(SendResult, MemSink)> Run(FakeServer s, byte[] data, SendOptions o)
    {
        var sink = new MemSink();
        var send = Sender.RunAsync(s, s, new MemoryStream(data), o, new Meter(), null, default);
        var recv = Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(null, "fake"), ROpts(), new Meter(), default);
        var r = await send;
        await recv.WaitAsync(TimeSpan.FromSeconds(20));
        return (r, sink);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2999)] [InlineData(3000)] [InlineData(3001)] [InlineData(10_000)] [InlineData(40_000)]
    public async Task Inline_prefix_then_parts_reassemble_exactly_at_every_boundary(int size)
    {
        var s = new FakeServer(); var data = Util.Random(size, size + 1);
        var (r, sink) = await Run(s, data, Opts());
        Assert.Equal(data, sink.Data.ToArray());
        Assert.Equal(Math.Min(size, 3000), r.Inline);
        Assert.Equal(FakeServer.Hex(data), r.Sha256);
        Assert.Equal(0, s.ObjectCount);
    }

    [Fact]
    public async Task A_small_transfer_never_touches_the_data_plane()
    {
        var s = new FakeServer(); var data = Util.Random(2500);
        var (r, sink) = await Run(s, data, Opts());
        Assert.Equal(0, r.Parts);
        Assert.Empty(s.PutCalls);
        Assert.Equal(0, s.PutUrlBatches);
        Assert.Equal(data, sink.Data.ToArray());
        Assert.Equal(new[] { (0L, 1000), (1000L, 1000), (2000L, 500) }, s.InlineSends);   // offsets are exact
    }

    [Fact]
    public async Task Parts_start_where_the_inline_data_ends_and_slow_start_doubles()
    {
        var s = new FakeServer(); var data = Util.Random(3000 + 1024 + 2048 + 4096 + 8192 + 100);
        var (r, sink) = await Run(s, data, Opts());
        var parts = s.Meta; 
        var st = await s.StateAsync("fake", -1, 0, default);
        Assert.Equal(new long[] { 3000, 4024, 6072, 10168, 18360 }, st.Parts.Select(p => p.Offset).ToArray());
        Assert.Equal(new long[] { 1024, 2048, 4096, 8192, 100 }, st.Parts.Select(p => p.Size).ToArray());
        Assert.Equal(data, sink.Data.ToArray());
    }

    [Theory]
    [InlineData(1, 1, 8, new long[] { 1, 2, 4, 8, 8, 8 })]
    [InlineData(1, 32, 32, new long[] { 1, 2, 4, 8, 16, 32, 32 })]
    [InlineData(32, 32, 32, new long[] { 32, 32 })]
    [InlineData(4, 3, 3, new long[] { 3, 3 })]
    public void Part_plan(long first, long unused, long max, long[] expected)
    {
        for (int n = 1; n <= expected.Length; n++) Assert.Equal(expected[n - 1], PartPlan.SizeOf(n, first, max));
    }

    [Fact]
    public async Task Put_urls_are_fetched_in_batches()
    {
        var s = new FakeServer();
        await Run(s, Util.Random(3000 + 9 * 1024), Opts(first: 1024, max: 1024));
        Assert.Equal(3, s.PutUrlBatches);   // 9 parts / batch of 4
    }

    [Fact]
    public async Task Without_the_websocket_everything_goes_through_parts()
    {
        var s = new FakeServer { InlineAvailable = false }; var data = Util.Random(5000);
        var (r, sink) = await Run(s, data, Opts());
        Assert.Equal(0, r.Inline);
        Assert.NotEmpty(s.PutCalls);
        Assert.Equal(data, sink.Data.ToArray());
    }

    [Fact]
    public async Task A_receiver_that_joins_late_replays_the_inline_bytes_before_the_parts()
    {
        var s = new FakeServer(); var data = Util.Random(9000);
        await Sender.RunAsync(s, s, new MemoryStream(data), Opts(), new Meter(), null, default);
        var sink = new MemSink();
        await Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(null, "fake"), ROpts(), new Meter(), default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(data, sink.Data.ToArray());
    }

    [Fact]
    public async Task The_receiver_has_the_first_bytes_before_any_part_is_uploaded()
    {
        var s = new FakeServer();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.BeforePut = async (n, a) => await hold.Task;     // R2 is "slow"
        var data = Util.Random(3000 + 4000);
        var sink = new MemSink();
        var send = Sender.RunAsync(s, s, new MemoryStream(data), Opts(), new Meter(), null, default);
        var recv = Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(null, "fake"), ROpts(), new Meter(), default);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && sink.Data.Length < 3000) await Task.Delay(5);
        Assert.Equal(3000, sink.Data.Length);
        Assert.Empty(s.Log.Where(l => l.StartsWith("done")));
        hold.SetResult();
        await send; await recv.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(data, sink.Data.ToArray());
    }
}
