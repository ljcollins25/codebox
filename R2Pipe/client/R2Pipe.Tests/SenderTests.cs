using System.Net;
using Xunit;

namespace R2Pipe.Tests;

public class SenderTests
{
    private static SendOptions Opts(int parallel = 3, long partSize = 1024, string? resume = null) =>
        new() { Parallel = parallel, PartSize = partSize, FirstPartSize = partSize, Inline = false, Delay = Util.NoDelay, ResumeId = resume, Attempts = 4 };

    [Fact]
    public async Task Splits_the_input_into_parts_with_checksums_and_completes()
    {
        var s = new FakeServer(); var data = Util.Random(5000);
        var r = await Sender.RunAsync(s, s, new MemoryStream(data), Opts(), new Meter(), null, default);
        Assert.Equal(5, r.Parts); Assert.Equal(5000, r.Size);
        Assert.Equal(FakeServer.Hex(data), r.Sha256);
        Assert.Equal("complete", s.Meta.Status);
        Assert.Equal(FakeServer.Hex(data), s.Meta.Sha256);
        Assert.Equal(5000, s.Meta.TotalSize);
    }

    [Fact]
    public async Task A_pipe_that_returns_small_chunks_still_fills_whole_parts()
    {
        var s = new FakeServer(); var data = Util.Random(3000);
        var r = await Sender.RunAsync(s, s, new ChunkyStream(data, 77), Opts(), new Meter(), null, default);
        Assert.Equal(3, r.Parts);
        Assert.Equal(FakeServer.Hex(data), r.Sha256);
    }

    [Fact]
    public async Task An_exact_multiple_of_the_part_size_has_no_empty_last_part_and_empty_input_has_no_parts()
    {
        var s = new FakeServer();
        Assert.Equal(2, (await Sender.RunAsync(s, s, new MemoryStream(Util.Random(2048)), Opts(), new Meter(), null, default)).Parts);
        var e = new FakeServer();
        var r = await Sender.RunAsync(e, e, new MemoryStream(), Opts(), new Meter(), null, default);
        Assert.Equal(0, r.Parts); Assert.Equal("done", e.Meta.Status);
    }

    [Fact]
    public async Task Never_has_more_parts_in_flight_than_asked()
    {
        var s = new FakeServer();
        s.BeforePut = async (n, a) => await Task.Delay(30);
        await Sender.RunAsync(s, s, new MemoryStream(Util.Random(12 * 1024)), Opts(parallel: 3), new Meter(), null, default);
        Assert.InRange(s.MaxPutsInFlight, 2, 3);
        var s1 = new FakeServer(); s1.BeforePut = async (n, a) => await Task.Delay(10);
        await Sender.RunAsync(s1, s1, new MemoryStream(Util.Random(6 * 1024)), Opts(parallel: 1), new Meter(), null, default);
        Assert.Equal(1, s1.MaxPutsInFlight);
    }

    [Fact]
    public async Task A_failed_part_is_retried_with_a_fresh_url_and_the_others_are_untouched()
    {
        var s = new FakeServer(); var m = new Meter();
        s.BeforePut = (n, a) => n == 2 && a < 3 ? throw new HttpRequestException("reset") : Task.CompletedTask;
        var data = Util.Random(4 * 1024);
        var r = await Sender.RunAsync(s, s, new MemoryStream(data), Opts(), m, null, default);
        Assert.Equal(3, s.PutAttempts[2]);
        Assert.All(new[] { 1, 3, 4 }, n => Assert.Equal(1, s.PutAttempts[n]));
        Assert.Equal(2, m.Retries);
        Assert.Equal(FakeServer.Hex(data), r.Sha256);
    }

    [Fact]
    public async Task Gives_up_after_the_attempts_and_does_not_complete()
    {
        var s = new FakeServer();
        s.BeforePut = (n, a) => n == 2 ? throw new HttpRequestException("down") : Task.CompletedTask;
        await Assert.ThrowsAsync<PipeException>(() => Sender.RunAsync(s, s, new MemoryStream(Util.Random(4 * 1024)), Opts(), new Meter(), null, default));
        Assert.Equal(4, s.PutAttempts[2]);
        Assert.NotEqual("complete", s.Meta.Status);
    }

    [Fact]
    public async Task A_final_server_answer_is_not_retried()
    {
        var s = new FakeServer();
        s.FailDone = n => n == 1 ? new ApiException(HttpStatusCode.Conflict, "409 transfer is aborted") : null;
        var ex = await Assert.ThrowsAsync<ApiException>(() => Sender.RunAsync(s, s, new MemoryStream(Util.Random(2048)), Opts(), new Meter(), null, default));
        Assert.Contains("aborted", ex.Message);
        Assert.Equal(1, s.PutAttempts[1]);
    }

    [Fact]
    public async Task Resume_skips_parts_the_server_already_has_with_the_same_checksum()
    {
        var data = Util.Random(5 * 1024);
        var s = new FakeServer();
        s.Seed(1, data[..1024]);
        s.Seed(2, data[1024..2048], "acked");          // already consumed by the receiver
        s.Seed(3, Util.Random(1024, 99));              // different content: must be uploaded again
        var m = new Meter();
        var r = await Sender.RunAsync(s, s, new MemoryStream(data), Opts(resume: "fake"), m, null, default);
        Assert.Equal(new[] { 3, 4, 5 }, s.PutCalls.OrderBy(x => x).ToArray());
        Assert.Equal(2, m.Resumed);
        Assert.Equal(5, r.Parts);
        Assert.Equal(FakeServer.Hex(data), s.Meta.Sha256);
    }

    [Fact]
    public async Task Resume_of_a_finished_transfer_is_refused()
    {
        var s = new FakeServer();
        await Sender.RunAsync(s, s, new MemoryStream(Util.Random(100)), Opts(), new Meter(), null, default);
        await Assert.ThrowsAsync<PipeException>(() => Sender.RunAsync(s, s, new MemoryStream(Util.Random(100)), Opts(resume: "fake"), new Meter(), null, default));
    }
}
