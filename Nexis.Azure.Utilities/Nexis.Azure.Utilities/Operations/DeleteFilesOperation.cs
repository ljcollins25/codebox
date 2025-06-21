using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;

namespace Nexis.Azure.Utilities;

public class DeleteFilesOperation(IConsole Console, CancellationToken token) : DriveOperationBase(Console, token)
{
    public bool DryRun = true;

    public async Task<int> RunAsync()
    {
        BlobContainerClient targetBlobContainer = GetTargetContainerAndPrefix(out var prefix);

        var targetBlobs = await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata | BlobTraits.Tags, prefix: prefix, cancellationToken: token)
            .OrderBy(b => b.Name).ToListAsync();

        await Helpers.ForEachAsync(!SingleThreaded, targetBlobs, token, async (blob, token) =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            var entry = BlobDataEntry.From(blob);
            var readTags = blob.Tags().ToImmutableDictionary();
            Exception? ex = null;
            var path = blob.Name;
            var blobClient = targetBlobContainer.GetBlockBlobClient(path);

            string operation = DryRun ? "Delete (dry run)" : "Delete";
            BlobState state = GetBlobState(blob);
            var logPrefix = $"[{state.ToString().PadRight(15, ' ')}] {GetName(path)}";
            try
            {
                Console.WriteLine($"{logPrefix}: Started {operation} (Snapshot={entry.Snapshot})");
                if (!DryRun)
                {
                    await blobClient.DeleteAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: token);
                }
            }
            catch (Exception exception)
            {
                ex = exception;
            }
            finally
            {
                var result = ex == null ? "Success" : $"Failure\n\n{ex}\n\n";
                Console.WriteLine($"{logPrefix}: Completed {operation} in {watch.Elapsed}. Result = {result}");
            }
        });

        return 0;
    }
}