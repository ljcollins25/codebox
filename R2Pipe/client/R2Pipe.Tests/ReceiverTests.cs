using Xunit;

namespace R2Pipe.Tests;

public class ReceiverTests
{
    private static ReceiveOptions Opts(int parallel = 3) => new() { Parallel = parallel, Delay = Util.NoDelay, Attempts = 3, PollSeconds = 1 };
    private static SendOptions SOpts(int parallel = 3) => new() { Parallel = parallel, PartSize = 1024, Delay = Util.NoDelay };

    private static async Task<(ReceiveResult, MemSink)> Roundtrip(FakeServer s, byte[] data, int sp = 3, int rp = 3, MemSink? sink = null, ResumeFile? resume = null)
    {
        sink ??= new MemSink();
        var send = Sender.RunAsync(s, s, new MemoryStream(data), SOpts(sp), new Meter(), null, default);
        var recv = Receiver.RunAsync(s, s, "fake", sink, resume ?? new ResumeFile(null, "fake"), Opts(rp), new Meter(), default);
        await send;
        return (await recv.WaitAsync(TimeSpan.FromSeconds(20)), sink);
    }

    [Fact]
    public async Task Receives_what_was_sent_in_order_and_acks_every_part()
    {
        var s = new FakeServer(); var data = Util.Random(9 * 1024 + 123);
        var (r, sink) = await Roundtrip(s, data);
        Assert.Equal(10, r.Parts);
        Assert.Equal(data, sink.Data.ToArray());
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 1024L), sink.Offsets);   // written in order
        Assert.Equal(0, s.ObjectCount);                                                  // everything deleted
        Assert.Equal("done", s.Meta.Status);
    }

    [Fact]
    public async Task Never_downloads_more_parts_at_once_than_asked()
    {
        var s = new FakeServer(); s.BeforeGet = async (n, a) => await Task.Delay(20);
        await Roundtrip(s, Util.Random(16 * 1024), sp: 4, rp: 2);
        Assert.True(s.MaxGetsInFlight <= 2, "gets in flight: " + s.MaxGetsInFlight);
    }

    [Fact]
    public async Task Streams_the_receiver_gets_part_1_while_the_sender_is_still_uploading()
    {
        var s = new FakeServer();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.BeforePut = async (n, a) => { if (n == 4) await gate.Task; };   // the last part is held back
        var data = Util.Random(4 * 1024);
        var sink = new MemSink();
        var send = Sender.RunAsync(s, s, new MemoryStream(data), SOpts(4), new Meter(), null, default);
        var recv = Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(null, "fake"), Opts(), new Meter(), default);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && !s.Log.Contains("ack 3")) await Task.Delay(10);
        Assert.Contains("ack 3", s.Log);               // parts 1..3 were consumed...
        Assert.DoesNotContain("complete", s.Log);      // ...before the sender finished
        gate.SetResult();
        await send; await recv.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(data, sink.Data.ToArray());
    }

    [Fact]
    public async Task A_corrupted_download_is_detected_and_fetched_again()
    {
        var s = new FakeServer(); var m = new Meter();
        s.CorruptGet = (n, a, bytes) => { if (n == 2 && a == 1) bytes[5] ^= 0xFF; };
        var data = Util.Random(3 * 1024);
        var (r, sink) = await Roundtrip(s, data);
        Assert.Equal(2, s.GetAttempts[2]);
        Assert.Equal(data, sink.Data.ToArray());
    }

    [Fact]
    public async Task Persistent_corruption_fails_and_nothing_wrong_is_acked()
    {
        var s = new FakeServer();
        s.CorruptGet = (n, a, bytes) => { if (n == 2) bytes[0] ^= 1; };
        var sink = new MemSink();
        var send = Sender.RunAsync(s, s, new MemoryStream(Util.Random(3 * 1024)), SOpts(), new Meter(), null, default);
        var ex = await Assert.ThrowsAsync<PipeException>(() => Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(null, "fake"), Opts(), new Meter(), default).WaitAsync(TimeSpan.FromSeconds(10)));
        await send;
        Assert.Contains("checksum", ex.Message);
        Assert.DoesNotContain("ack 2", s.Log);
    }

    [Fact]
    public async Task A_wrong_overall_checksum_is_reported()
    {
        var s = new FakeServer(); var data = Util.Random(2048);
        await Sender.RunAsync(s, s, new MemoryStream(data), SOpts(), new Meter(), null, default);
        await s.CompleteAsync("fake", 2, 2048, new string('0', 64), default);   // the sender lied about the total
        var ex = await Assert.ThrowsAsync<PipeException>(() => Receiver.RunAsync(s, s, "fake", new MemSink(), new ResumeFile(null, "fake"), Opts(), new Meter(), default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("overall SHA-256", ex.Message);
    }

    [Fact]
    public async Task A_receiver_that_dies_resumes_from_the_resume_file_and_still_verifies_the_whole()
    {
        var s = new FakeServer(); var data = Util.Random(6 * 1024);
        await Sender.RunAsync(s, s, new MemoryStream(data), SOpts(), new Meter(), null, default);
        var statePath = Path.Combine(Path.GetTempPath(), "r2pipe-test-" + Guid.NewGuid().ToString("N") + ".r2pipe");
        try
        {
            var sink = new MemSink();
            s.BeforeGet = (n, a) => n == 4 ? throw new HttpRequestException("network down") : Task.CompletedTask;   // dies at part 4
            await Assert.ThrowsAsync<PipeException>(() => Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(statePath, "fake"), Opts(1), new Meter(), default));
            var done = new ResumeFile(statePath, "fake").Done;
            Assert.Equal(new[] { 1, 2, 3 }, done.OrderBy(x => x).ToArray());

            s.BeforeGet = null; s.GetAttempts.Clear();
            var m = new Meter();
            var r = await Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(statePath, "fake"), Opts(2), m, default);
            Assert.Equal(3, r.Resumed);
            Assert.Equal(new[] { 4, 5, 6 }, s.GetAttempts.Keys.OrderBy(x => x).ToArray());   // 1..3 not downloaded again
            Assert.Equal(data, sink.Data.ToArray());
            Assert.False(File.Exists(statePath));
        }
        finally { if (File.Exists(statePath)) File.Delete(statePath); }
    }

    [Fact]
    public async Task An_acked_part_that_is_not_in_the_resume_file_is_an_error()
    {
        var s = new FakeServer(); var data = Util.Random(2048);
        await Sender.RunAsync(s, s, new MemoryStream(data), SOpts(), new Meter(), null, default);
        await s.AckAsync("fake", 1, default);
        var ex = await Assert.ThrowsAsync<PipeException>(() => Receiver.RunAsync(s, s, "fake", new MemSink(), new ResumeFile(null, "fake"), Opts(), new Meter(), default));
        Assert.Contains("already received", ex.Message);
    }

    [Fact]
    public async Task An_aborted_transfer_stops_the_receiver()
    {
        var s = new FakeServer();
        var recv = Receiver.RunAsync(s, s, "fake", new MemSink(), new ResumeFile(null, "fake"), Opts(), new Meter(), default);
        await Task.Delay(100);
        await s.AbortAsync("fake", default);
        var ex = await Assert.ThrowsAsync<PipeException>(() => recv.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("aborted", ex.Message);
    }

    [Fact]
    public async Task A_late_receiver_picks_up_everything_still_there()
    {
        var s = new FakeServer(); var data = Util.Random(4 * 1024);
        await Sender.RunAsync(s, s, new MemoryStream(data), SOpts(), new Meter(), null, default);
        var sink = new MemSink();
        await Receiver.RunAsync(s, s, "fake", sink, new ResumeFile(null, "fake"), Opts(), new Meter(), default);
        Assert.Equal(data, sink.Data.ToArray());
    }
}
