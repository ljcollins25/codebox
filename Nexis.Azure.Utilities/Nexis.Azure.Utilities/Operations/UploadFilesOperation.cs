using System;
using System.Buffers;
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
using DotNext.IO;

namespace Nexis.Azure.Utilities;

public class UploadFilesOperation(IConsole Console, CancellationToken token) : DriveOperationBase(Console, token)
{
    public required string LocalSourcePath;

    public string RelativePath;

    public IReadOnlyDictionary<string, FileInfo> GetFiles()
    {
        LocalSourcePath = Path.GetFullPath(Path.Combine(LocalSourcePath, RelativePath ?? string.Empty));
        var rootPath = File.Exists(LocalSourcePath) ? Path.GetDirectoryName(LocalSourcePath) : LocalSourcePath;
        rootPath = rootPath!.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

        var files = new DirectoryInfo(rootPath).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.FullName.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
            .ToImmutableSortedDictionary(f => f.FullName.Substring(rootPath.Length).Replace('\\', '/'), f => f)
            ;

        return files;
    }

    public async Task<int> RunAsync()
    {
        var files = GetFiles();

        Url targetRoot = Uri;
        if (!string.IsNullOrEmpty(RelativePath))
        {
            if (File.Exists(LocalSourcePath))
            {
                RelativePath = Path.GetDirectoryName(RelativePath) ?? string.Empty;
            }
            
            targetRoot = targetRoot.Combine(Uri.EscapeUriString(RelativePath.Replace('\\', '/')));
            Uri = targetRoot;
        }

        BlobContainerClient targetBlobContainer = GetTargetContainerAndPrefix(out var prefix);

        var targetBlobs = FilterDirectories(await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata | BlobTraits.Tags, prefix: prefix, cancellationToken: token)
            .ToListAsync()).ToImmutableDictionary(b => b.Name);


        Timestamp operationTimestamp = Timestamp.Now;

        var totalLength = files.Sum(f => f.Value.Length);
        var copiedBytes = 0L;

        await Helpers.ForEachAsync(SingleThreaded ? 1 : 4, files, token, async (entry, token) =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            Exception? ex = null;
            var path = entry.Key;
            if (!string.IsNullOrEmpty(RelativePath))
            {
                path = Path.Combine(RelativePath, path).Replace('\\', '/');
            }
            var file = entry.Value;
            Timestamp fileLastModifiedTime = file.LastWriteTimeUtc;
            var fileLength = file.Length;
            var blob = targetBlobs.GetValueOrDefault(path);

            var blobMtime = blob?.Metadata()?.ValueOrDefault<string, string>(Strings.mtime_metadata);
            Timestamp? blobLastModifiedTime = blobMtime
                ?? (Timestamp?)blob?.Properties.LastModified;

            var logPrefix = $"{GetName(path)}";
            string operation = "";

            try
            {
                if (blobLastModifiedTime !=  null && blobLastModifiedTime >= fileLastModifiedTime)
                {
                    operation = $"Skipping blob last modified time ({blobLastModifiedTime}) >= ({fileLastModifiedTime}) file last modified time";
                    return;
                }


                operation = $"Copying Length = {fileLength}. Last Modified Time = {fileLastModifiedTime}. Blob Last Modified = {blobLastModifiedTime} ({blobMtime})";
                // Copy content of file to blob store (block names should be sortable by order in blob)
                const long MAX_BLOCK_SIZE = 1 << 28; // 256mb
                var blocks = new List<string>();
                int blockId = 0;
                var blobClient = targetBlobContainer.GetBlockBlobClient(Helpers.UriCombine(prefix, path));
                using (var fileStream = File.Open(file.FullName, FileMode.Open))
                using (var segmentStream = new StreamSegment(fileStream))
                {
                    for (long offset = 0; offset < fileLength; offset += MAX_BLOCK_SIZE)
                    {
                        var blockName = operationTimestamp.ToBlockId(blockId++);
                        var blockLength = Math.Min(MAX_BLOCK_SIZE, fileLength - offset);
                        segmentStream.Adjust(offset, blockLength);
                        segmentStream.Position = 0;
                        await blobClient.StageBlockAsync(
                            base64BlockId: blockName,
                            content: segmentStream,
                            cancellationToken: token
                        );

                        blocks.Add(blockName);
                    }
                }

                var commitResponse = await blobClient.CommitBlockListAsync(
                    blocks,
                    new CommitBlockListOptions()
                    {
                        Metadata = EmptyStringMap
                            .Add(Strings.mtime_metadata, fileLastModifiedTime)
                    },
                    cancellationToken: token);
            }
            catch (Exception exception)
            {
                ex = exception;
            }
            finally
            {
                var result = ex == null ? "Success" : $"Failure\n\n{ex}\n\n";
                var completed = Interlocked.Add(ref copiedBytes, fileLength);
                var percent = (completed * 100) / totalLength;
                Console.WriteLine($"{logPrefix}: [{percent}% {totalLength} bytes] Completed {operation} in {watch.Elapsed}. Result = {result}");
            }
        });

        return 0;
    }
}