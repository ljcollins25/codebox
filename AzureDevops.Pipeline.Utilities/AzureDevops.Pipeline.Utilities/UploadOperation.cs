using System.Buffers;
using System.CommandLine;
using System.Diagnostics;
using System.Threading;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.VisualStudio.Services.Commerce;

namespace AzureDevops.Pipeline.Utilities;

public class UploadOperation(IConsole Console, CancellationToken token)
{
    public required FileInfo Source;

    public required Uri TargetUri;

    public bool Overwrite;

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"Uploading '{Source.FullName}' to '{TargetUri.Scrub()}'");

        var blob = new BlockBlobClient(TargetUri);

        using var sourceStream = Source.OpenRead();

        var totalLength = Source.Length;
        long copied = 0;
        var remaining = totalLength;

        BlockBlobOpenWriteOptions? options = null;

        if (!Overwrite)
        {
            options = new BlockBlobOpenWriteOptions()
            {
                OpenConditions = new BlobRequestConditions()
                {
                    IfNoneMatch = ETag.All
                }
            };
        }

        using var targetStream = await blob.OpenWriteAsync(overwrite: true, options);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(new Memory<byte>(buffer), token)) != 0)
            {
                await targetStream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), token);

                copied += bytesRead;
                var percentage = (copied * 100.0) / totalLength;
                Console.WriteLine($"Copied ({percentage.Truncate(1)}%) {copied} of {totalLength} in {watch.Elapsed}");

                watch.Restart();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }


        return 0;
    }
}