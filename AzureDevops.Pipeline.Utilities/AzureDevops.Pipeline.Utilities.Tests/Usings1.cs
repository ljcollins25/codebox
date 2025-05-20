interface IDownloadableFile
{
    /// <summary>
    /// Uniquely identifies a metafile. Files which same Uid should have
    /// </summary>
    public string Uid { get; }

    public long Size { get; }

    Task DownloadRangeAsync(long offset, int length, IWritableBuffer target);
}

public interface IWritableBuffer
{
    Stream GetStream();

    Memory<byte> Memory { get; }
}

interface IFileWriter
{
    Task WriteAsync(long offset, IReadOnlyList<ReadOnlyMemory<byte>> bytes);
}

interface IFastDownlaoder
{
    Task DownloadToAsync(IDownloadableFile file, IFileWriter writer, FastDownloaderSettings? settings = null);
}

public record FastDownloaderSettings(
    int DownloadConcurrency,
    int WriteConcurrency);

public interface IBackedFile
{
    long Size { get; }

    string Uid { get; }

    Task DownloadRangeAsync(long offset, int length, Memory<byte> target);

    Task WriteAsync(long offset, IEnumerable<ReadOnlyMemory<byte>> bytes);
}

/// <summary>
/// Metafile is synthesized by concatenating all the files from the file providers. Order of files
/// in metafile should be consistent.
/// </summary>
class CloudTestCompositeMetafile(IReadOnlyList<IBackedFile> files) : IDownloadableFile, IFileWriter
{
    /// <summary>
    /// Aggregate *hash* of all constituent files from file providers. If hash is not available for a file,
    /// perhaps the hash of its absolute uri (excluding any parameters that might change for the same content such as sas uri parameters)
    /// </summary>
    public string Uid { get; } = Helpers.HashCombine(files.Select(f => f.Uid));

    /// <summary>
    /// Aggregate sum of all constituent files from file providers
    /// </summary>
    public long Size { get; } = files.Sum(f => f.Size);

    /// <summary>
    /// Downloads all file ranges overlapping the given byte range from their respective file providers.
    /// NOTE: It should avoid downloading full constituent file if range download is supported.
    /// </summary>
    public async Task DownloadRangeAsync(long offset, int length, IWritableBuffer target)
    {
        var memory = target.Memory;
        int relativeOffset = 0;
        foreach (var e in GetOverlappingFiles(offset, length))
        {
            var count = (int)e.length;
            await e.file.DownloadRangeAsync(e.offset, count, memory.Slice(relativeOffset, count)); 
        }
    }

    /// <summary>
    /// Writes files all constituent file ranges overapping with the given range.
    /// </summary>
    public async Task WriteAsync(long offset, IReadOnlyList<ReadOnlyMemory<byte>> bytes)
    {
        (int index, int memoryOffset) cursor = default;
        foreach (var e in GetOverlappingFiles(offset, bytes.Sum(b => (long)b.Length)))
        {
            await e.file.WriteAsync(e.offset, GetMemoryRanges(ref cursor, bytes, e.length));
        }
    }

    private IEnumerable<ReadOnlyMemory<byte>> GetMemoryRanges(ref (int index, int memoryOffset) cursor, IReadOnlyList<ReadOnlyMemory<byte>> bytes, long length) => throw new NotImplementedException();

    private IEnumerable<(long offset, long length, IBackedFile file)> GetOverlappingFiles(long offset, long length) => throw new NotImplementedException();
}

public static class Helpers
{
    public static string HashCombine(IEnumerable<string> hashes) => throw new NotImplementedException();

    public static IEnumerable<long> RollingSum(IEnumerable<long> values)
    {
        long sum = 0;
        foreach (var value in values)
        {
            sum += value;
            yield return sum;
        }
    }
}