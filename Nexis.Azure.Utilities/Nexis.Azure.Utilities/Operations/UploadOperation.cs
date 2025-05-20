using System.Buffers;
using System.CommandLine;
using System.Diagnostics;
using System.Threading;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Nexis.Azure.Utilities;

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

        await sourceStream.CopyToAsync(targetStream, Console, token);

        return 0;
    }
}