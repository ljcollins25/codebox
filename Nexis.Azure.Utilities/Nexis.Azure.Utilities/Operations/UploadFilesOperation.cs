﻿using System;
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
using System.Runtime.InteropServices;
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

    public string? RelativePath;

    public long BlockSizeMb = 128; // 128mb

    public long BlockSize => BlockSizeMb << 20;

    public int ThreadCount = SingleThreaded ? 1 : 4;

    public bool UpdateTimestamps = false;

    public bool Force = true;

    public List<string> ExcludedExtensions = [".marker"];

    public List<string> RequiredInfixes = [];

    public bool IncludeDirectory = false;

    public IDictionary<string, FileInfo> GetFiles()
    {
        LocalSourcePath = Path.GetFullPath(Path.Combine(LocalSourcePath, RelativePath ?? string.Empty));
        var rootPath = File.Exists(LocalSourcePath) ? Path.GetDirectoryName(LocalSourcePath) : LocalSourcePath;

        var relativePath = RelativePath;
        if (!string.IsNullOrEmpty(RelativePath) && File.Exists(LocalSourcePath))
        {
            relativePath = Path.GetDirectoryName(relativePath) ?? string.Empty;
        }

        string getPath(string path)
        {
            if (!string.IsNullOrEmpty(relativePath))
            {
                path = Path.Combine(relativePath, path).Replace('\\', '/');
            }

            return path;
        }

        var searchPath = rootPath;
        if (IncludeDirectory)
        {
            rootPath = Path.GetDirectoryName(rootPath);
        }

        rootPath = rootPath!.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

        var files = new DirectoryInfo(searchPath).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.FullName.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
            .Where(f => !ExcludedExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => RequiredInfixes.All(i => f.Name.Contains(i, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableSortedDictionary(f => getPath(f.FullName.Substring(rootPath.Length).Replace('\\', '/')), f => f, StringComparer.OrdinalIgnoreCase)
            .ToBuilder()
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
        Contract.Assert(files.Count != 0);

        Url targetRoot = Uri;
        if (!string.IsNullOrEmpty(RelativePath))
        {
            targetRoot = targetRoot.Combine(RelativePath.Replace('\\', '/'));
            Uri = targetRoot;
            Console.Out.WriteLine($"Target URI: {Uri}");
        }

        BlobContainerClient targetBlobContainer = GetTargetContainerAndPrefix(out var prefix);

        if (string.IsNullOrEmpty(RelativePath))
        {
            files = files.ToDictionary(f => prefix + f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        }

        var targetBlobs = !Force
            ? FilterDirectories(await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata | BlobTraits.Tags, prefix: prefix, cancellationToken: token)
                .ToListAsync())
                .ToImmutableDictionary(b => b.Name)
            : ImmutableDictionary<string, BlobItem>.Empty;

        await Helpers.ForEachAsync(ThreadCount, files.ToImmutableSortedDictionary(StringComparer.OrdinalIgnoreCase), token, async (entry, token) =>
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
            var op = "Skipped";
            if (blobLastModifiedTime != null && (blobLastModifiedTime < fileLastModifiedTime || (UpdateTimestamps && blobLastModifiedTime != fileLastModifiedTime)))
            {
                op = "Updated";
                try
                {
                    var bc = targetBlobContainer.GetBlockBlobClient(blob!.Name);
                    await bc.SetMetadataAsync(blob.Metadata().ToImmutableDictionary()
                            .SetItem(Strings.mtime_metadata, fileLastModifiedTime),
                        new BlobRequestConditions()
                        {
                            IfMatch = blob.Properties.ETag
                        },
                        cancellationToken: token);
                }
                catch (Exception ex)
                {
                    op = $"Failed\n{ex}\n";
                }
                lock (targetBlobs)
                {
                    files.Remove(path);
                }
            }

            Console.WriteLine($"{logPrefix}: {op} blob last modified time ({blobLastModifiedTime}) > ({fileLastModifiedTime}) file last modified time");

        });



        Timestamp operationTimestamp = Timestamp.Now;

        var fileCount = files.Count;
        int completedFiles = 0;
        var totalLength = files.Sum(f => f.Value.Length);
        var copiedBytes = 0L;
        var completedBytes = 0L;
        Stopwatch totalWatch = Stopwatch.StartNew();
        Console.Out.WriteLine($"Target blobs Length={targetBlobs.Count}, FirstKey={targetBlobs.FirstOrDefault().Key}");

        Console.Out.WriteLine($"Total files to upload: {fileCount}, Total Length: {totalLength} bytes, Block Size: {BlockSize} bytes, Threads: {ThreadCount}");
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

            void printStatus(long length, string operation)
            {
                var result = ex == null ? "Success" : $"Failure\n\n{ex}\n\n";
                var completed = Interlocked.Add(ref completedBytes, length);
                var percent = (completed * 100) / totalLength;
                var elapsed = watch.Elapsed;
                var totalElapsed = totalWatch.Elapsed;
                var avgSpeed = copiedBytes / totalElapsed.TotalSeconds;
                var remainingBytes = totalLength - completed;
                var estimatedSeconds = avgSpeed > 0 ? remainingBytes / avgSpeed : 0;
                var eta = TimeSpan.FromSeconds(estimatedSeconds);
                Console.WriteLine($"{logPrefix}: [{completedFiles}/{fileCount} {percent}% {totalLength} bytes (est {eta:g})] Completed {operation} in {watch.Elapsed}. Result = {result}");
            }

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
                var totalBlocks = (fileLength + (BlockSize - 1)) / BlockSize;
                using (var fileStream = File.Open(file.FullName, FileMode.Open))
                using (var segmentStream = new StreamSegment(fileStream))
                {
                    for (long offset = 0; offset < fileLength; offset += BlockSize)
                    {
                        var bid = blockId;
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

                        printStatus(blockLength, $"Uploaded block {bid}/{totalBlocks}");
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
                Interlocked.Increment(ref completedFiles);
                printStatus(0, operation);
            }
        });

        return 0;
    }
}