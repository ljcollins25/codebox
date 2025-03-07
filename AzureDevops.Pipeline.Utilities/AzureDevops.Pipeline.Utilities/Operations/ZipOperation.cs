using System.CommandLine;
using System.IO.Compression;

namespace AzureDevops.Pipeline.Utilities;

public class ZipOperation(IConsole Console, CancellationToken token)
{
    public required FileInfo Source;

    public required FileInfo Target;

    public CompressionLevel CompressionLevel = CompressionLevel.Fastest;

    public bool Overwrite;

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"Compressing '{Source.FullName}' to '{Target.FullName}'");

        {
            using var sourceStream = Source.OpenRead();
            using var targetFileStream = Target.Open(Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite);
            using var targetStream = new GZipStream(targetFileStream, CompressionLevel, leaveOpen: true);

            await sourceStream.CopyToAsync(targetStream, Console, token);
        }

        Source.Refresh();
        Target.Refresh();
        Console.WriteLine($"Compressed '{Source.FullName}' to '{Target.FullName}' ({Source.Length} => {Target.Length})");


        return 0;
    }
}