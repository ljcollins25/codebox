using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
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

    public long BlockSize = 1 << 27; // 128mb

    public int ThreadCount = SingleThreaded ? 1 : 4;

    public IDictionary<string, FileInfo> GetFiles()
    {
        LocalSourcePath = Path.GetFullPath(Path.Combine(LocalSourcePath, RelativePath ?? string.Empty));
        var rootPath = File.Exists(LocalSourcePath) ? Path.GetDirectoryName(LocalSourcePath) : LocalSourcePath;
        rootPath = rootPath!.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

        string getPath(string path)
        {
            if (!string.IsNullOrEmpty(RelativePath))
            {
                path = Path.Combine(RelativePath, path).Replace('\\', '/');
            }

            return path;
        }

        var files = new DirectoryInfo(rootPath).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.FullName.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => getPath(f.FullName.Substring(rootPath.Length).Replace('\\', '/')), f => f)
            ;

        return files;
    }

    public async Task<int> RunAsync()
    {
        if (!string.IsNullOrEmpty(RelativePath) && RelativePath.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            // If RelativePath is a full path, we need to remove the LocalSourcePath prefix
            RelativePath = RelativePath.Substring(LocalSourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var files = GetFiles();

        Url targetRoot = Uri;
        if (!string.IsNullOrEmpty(RelativePath))
        {
            if (File.Exists(LocalSourcePath))
            {
                RelativePath = Path.GetDirectoryName(RelativePath) ?? string.Empty;
            }

            targetRoot = targetRoot.Combine(RelativePath.Replace('\\', '/'));
            Uri = targetRoot;
            Console.Out.WriteLine($"Target URI: {Uri}");
        }

        BlobContainerClient targetBlobContainer = GetTargetContainerAndPrefix(out var prefix);

        var targetBlobs = FilterDirectories(await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata | BlobTraits.Tags, prefix: prefix, cancellationToken: token)
            .ToListAsync())
            .ToImmutableDictionary(b => b.Name);

        foreach (var entry in files)
        {
            var path = entry.Key;
            var file = entry.Value;
            Timestamp fileLastModifiedTime = file.LastWriteTimeUtc;
            var fileLength = file.Length;
            var blob = targetBlobs.GetValueOrDefault(path);

            var blobMtime = blob?.Metadata()?.ValueOrDefault<string, string>(Strings.mtime_metadata);
            Timestamp? blobLastModifiedTime = blobMtime
                ?? (Timestamp?)blob?.Properties.LastModified;

            var logPrefix = $"{GetName(path)}";
            if (blobLastModifiedTime != null && blobLastModifiedTime >= fileLastModifiedTime)
            {
                Console.WriteLine($"{logPrefix}: Skipping blob last modified time ({blobLastModifiedTime}) >= ({fileLastModifiedTime}) file last modified time");
                files.Remove(path);
            }
        }

        Console.Out.WriteLine($"Target blobs Length={targetBlobs.Count}, FirstKey={targetBlobs.FirstOrDefault().Key}");


        Timestamp operationTimestamp = Timestamp.Now;

        var totalLength = files.Sum(f => f.Value.Length);
        var copiedBytes = 0L;
        var completedBytes = 0L;
        Stopwatch totalWatch = Stopwatch.StartNew();

        await Helpers.ForEachAsync(ThreadCount, files, token, async (entry, token) =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            Exception? ex = null;
            var path = entry.Key;
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
                if (blobLastModifiedTime != null && blobLastModifiedTime >= fileLastModifiedTime)
                {
                    operation = $"Skipping blob last modified time ({blobLastModifiedTime}) >= ({fileLastModifiedTime}) file last modified time";
                    return;
                }

                operation = $"Copying Length = {fileLength}. Last Modified Time = {fileLastModifiedTime}. Blob Last Modified = {blobLastModifiedTime} ({blobMtime})";
                // Copy content of file to blob store (block names should be sortable by order in blob)
                var blocks = new List<string>();
                int blockId = 0;
                var blobClient = targetBlobContainer.GetBlockBlobClient(path);
                using (var fileStream = File.Open(file.FullName, FileMode.Open))
                using (var segmentStream = new StreamSegment(fileStream))
                {
                    for (long offset = 0; offset < fileLength; offset += BlockSize)
                    {
                        var blockName = operationTimestamp.ToBlockId(blockId++);
                        var blockLength = Math.Min(BlockSize, fileLength - offset);
                        segmentStream.Adjust(offset, blockLength);
                        segmentStream.Position = 0;
                        await blobClient.StageBlockAsync(
                            base64BlockId: blockName,
                            content: segmentStream,
                            cancellationToken: token
                        );

                        Interlocked.Add(ref copiedBytes, blockLength);

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
            catch (TaskCanceledException)
            {
                operation = "Operation was cancelled.";
            }
            catch (OperationCanceledException)
            {
                operation = "Operation was cancelled.";
            }
            catch (Exception exception)
            {
                ex = exception;
            }
            finally
            {
                var result = ex == null ? "Success" : $"Failure\n\n{ex}\n\n";
                var completed = Interlocked.Add(ref completedBytes, fileLength);
                var percent = (completed * 100) / totalLength;
                var elapsed = watch.Elapsed;
                var totalElapsed = totalWatch.Elapsed;
                var avgSpeed = copiedBytes / totalElapsed.TotalSeconds;
                var remainingBytes = totalLength - completed;
                var estimatedSeconds = avgSpeed > 0 ? remainingBytes / avgSpeed : 0;
                var eta = TimeSpan.FromSeconds(estimatedSeconds);
                Console.WriteLine($"{logPrefix}: [{percent}% {totalLength} bytes (est {eta:g})] Completed {operation} in {watch.Elapsed}. Result = {result}");
            }
        });

        return 0;
    }
}