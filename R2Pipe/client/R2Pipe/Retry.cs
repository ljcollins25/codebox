namespace R2Pipe;

internal static class Retry
{
    /// <summary>Runs <paramref name="op"/> up to <paramref name="attempts"/> times; final API errors (4xx) and cancellation are not retried.</summary>
    public static async Task<T> RunAsync<T>(int attempts, Func<int, Task<T>> op, Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct, Action<int, Exception>? onRetry = null)
    {
        delay ??= (t, c) => Task.Delay(t, c);
        for (int a = 1; ; a++)
        {
            try { return await op(a).ConfigureAwait(false); }
            catch (Exception e) when (!ct.IsCancellationRequested && a < attempts && e is not ApiException { Fatal: true } && e is not PipeException)
            {
                onRetry?.Invoke(a, e);
                await delay(TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, a - 1), 8000)), ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A failure that retrying cannot fix (for example a bad checksum on the whole transfer).</summary>
internal sealed class PipeException(string message) : Exception(message);
