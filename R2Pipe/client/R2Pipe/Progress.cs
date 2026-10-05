using System.Diagnostics;

namespace R2Pipe;

/// <summary>Counters shared by the workers, plus the throughput line.</summary>
internal sealed class Meter
{
    private long _bytes, _parts, _retries, _resumed;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _firstByteMs = -1;
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Parts => Interlocked.Read(ref _parts);
    public long Retries => Interlocked.Read(ref _retries);
    public long Resumed => Interlocked.Read(ref _resumed);
    public TimeSpan Elapsed => _sw.Elapsed;
    public long FirstByteMs => Interlocked.Read(ref _firstByteMs);
    public void AddBytes(long n) { Interlocked.Add(ref _bytes, n); Interlocked.CompareExchange(ref _firstByteMs, _sw.ElapsedMilliseconds, -1); }
    public void PartDone() => Interlocked.Increment(ref _parts);
    public void Retried() => Interlocked.Increment(ref _retries);
    public void PartResumed() => Interlocked.Increment(ref _resumed);
    public double MBps => Bytes / 1e6 / Math.Max(_sw.Elapsed.TotalSeconds, 1e-6);

    public string Line(string verb, long? total) =>
        $"{verb} {Fmt(Bytes)}{(total is > 0 ? $" / {Fmt(total.Value)} ({100.0 * Bytes / total.Value:0}%)" : "")}  {Parts} parts  {MBps:0.0} MB/s  {_sw.Elapsed:mm\\:ss}" + (Retries > 0 ? $"  {Retries} retries" : "");

    public static string Fmt(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GiB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MiB" : $"{b / 1024.0:0.0} KiB";

    /// <summary>Prints a status line to stderr every second until cancelled.</summary>
    public async Task ReportAsync(string verb, long? total, TextWriter w, bool tty, CancellationToken ct)
    {
        var every = tty ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        try
        {
            while (true)
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
                if (tty) w.Write("\r" + Line(verb, total) + "   "); else w.WriteLine(Line(verb, total));
            }
        }
        catch (OperationCanceledException) { }
        if (tty) w.Write("\r");
    }
}
