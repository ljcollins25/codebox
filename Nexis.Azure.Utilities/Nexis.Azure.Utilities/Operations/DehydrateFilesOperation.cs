using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.ComponentModel;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using static System.Reflection.Metadata.BlobBuilder;

namespace Nexis.Azure.Utilities;

public class DehydrateOperation(IConsole Console, CancellationToken token)
{
    private static class Strings
    {
        public const string source_timestamp = "source_timestamp";
        public const string last_refresh_time = "last_refresh_time";
        public const string snapshot = "snapshot";
    }

    public required Uri SourceFilesUri;
    public required Uri TargetBlobUri;

    public static ImmutableDictionary<string, string> BaseMetadata = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
        .Add("archive_version", "1")
        ;

    public int RefreshBatches = 5;

    public bool ShouldDeleteExtraneousTargetFiles;

    // Set to zero to refresh everything
    // Set to large value to refresh nothing
    public string RefreshIntervalValue = "5d";

    public TimeSpan RefreshInterval
    {
        get
        {
            if (TimeSpan.TryParse(RefreshIntervalValue, out var ts)) return ts;
            if (TimeSpanSetting.TryParseReadableTimeSpan(RefreshIntervalValue, out ts)) return ts;

            throw new FormatException($"Unable to parse Expiry '{RefreshIntervalValue}' as timeSpan");
        }
    }

    public required string ExpiryValue = "1h";

    public DateTimeOffset Expiry
    {
        get
        {
            if (DateTime.TryParse(ExpiryValue, out var d)) return d.ToUniversalTime();
            if (DateTimeOffset.TryParse(ExpiryValue, out var dto)) return dto;
            if (TimeSpan.TryParse(ExpiryValue, out var ts)) return DateTimeOffset.UtcNow - ts;
            if (TimeSpanSetting.TryParseReadableTimeSpan(ExpiryValue, out ts)) return DateTimeOffset.UtcNow - ts;

            throw new FormatException($"Unable to parse Expiry '{ExpiryValue}' as date or TimeSpan");
        }
    }

    public bool SingleThreaded = System.Diagnostics.Debugger.IsAttached;

    // public async Task<int> RunAsync()
    // {
    //     // Get all files in Source
    //     // Get all files in Target

    //     /*
    //     * Delete any snapshots from target which are older than ttl
    //     * 1. Set metadata State=Dehydrated, SourceEtag (copied from existing metadata)
    //     * For files missing from source, delete from target optionally
    //     * For files missing from target where source last write time is after ttl,
    //     * 1. Put blob with empty block list
    //     * 2. Put blocks from source
    //     * 3. Update target metadata, State=Dehydrated
    //     * 4. Delete source content by calling CreateFile with length of file
    //     * For hydrated files in target with last modified time older than ttl, which have not been accessed recently
    //     * 1. Create snapshot
    //     * 2. Put empty block list, with snapshot id metadata and State=DehydratePending, SourceEtag (copied from existing metadata)
    //     * - This should fail if blob has new last modified time
    //     * - Hydrators should update last write time by setting blob properties
    //     * For files changed in source where last modified time is after ttl, 
    //     *
    //     */
    //     return 0;
    // }

    public async Task<int> RunAsync()
    {
        var sourceShareClient = new ShareClient(SourceFilesUri);
        var targetBlobContainer = new BlobContainerClient(TargetBlobUri);

        var sourceFiles = await GetSourceFilesAsync(sourceShareClient);
        var targetBlobs = await GetTargetBlobsAsync(targetBlobContainer);

        var maxRefreshes = (targetBlobs.Count + (RefreshBatches - 1)) / RefreshBatches;

        var refreshExpiry = (DateTimeOffset.UtcNow - RefreshInterval).ToTimeStamp();

        await Helpers.ForEachAsync(!SingleThreaded, sourceFiles.Keys.Union(targetBlobs.Keys, StringComparer.OrdinalIgnoreCase), token, async (path, token) =>
        {
            var sourceFile = sourceFiles.GetValueOrDefault(path);
            var targetBlob = targetBlobs.GetValueOrDefault(path);

            string operation = "";
            try
            {
                Console.WriteLine($"{path}: START SourceExists:{sourceFile != null}, TargetExists: {targetBlob != null}");

                if (sourceFile == null)
                {
                    operation = "deleting extraneous blob";
                    if (ShouldDeleteExtraneousTargetFiles)
                    {
                        if (targetBlob!.Properties.LastModified < Expiry)
                        {
                            var blobClient = targetBlobContainer.GetBlobClient(path);
                            var requestConditions = new BlobRequestConditions
                            {
                                IfUnmodifiedSince = targetBlob.Properties.LastModified
                            };

                            await blobClient.DeleteIfExistsAsync(conditions: requestConditions);
                        }
                    }
                }
                // target missing or out of date
                else if (targetBlob == null
                    || targetBlob.Metadata.ValueOrDefault(Strings.source_timestamp) != sourceFile.GetLastWriteTimestamp())
                {
                    var fileSize = sourceFile.FileSize!.Value;
                    operation = "dehydrating file";
                    var fileClient = sourceShareClient.GetRootDirectoryClient().GetFileClient(path);

                    var blobClient = targetBlobContainer.GetBlockBlobClient(path);

                    // Clear block list and metadata
                    await blobClient.CommitBlockListAsync([], new CommitBlockListOptions()
                    {
                        Metadata = BaseMetadata
                    },
                    cancellationToken: token);

                    // Copy content of file to blob store (block names should be sortable by order in blob)
                    const long MAX_BLOCK_SIZE = 1 << 30; // 1gb
                    var blocks = new List<string>();
                    for (long offset = 0; offset < sourceFile.FileSize!.Value; offset += MAX_BLOCK_SIZE)
                    {
                        var blockLength = Math.Min(MAX_BLOCK_SIZE, fileSize - offset);
                        await blobClient.StageBlockFromUriAsync(
                            base64BlockId: Out.Var(out var blockName, (offset / MAX_BLOCK_SIZE).ToString().PadLeft(4, '0')),
                            sourceUri: fileClient.Uri,
                            options: new StageBlockFromUriOptions()
                            {
                                SourceRange = new HttpRange(offset, blockLength),
                                // This does not seem to be supported
                                //SourceConditions = new RequestConditions()
                                //{
                                //    IfUnmodifiedSince = sourceFile.Properties.LastModified
                                //}
                            },
                            cancellationToken: token
                        );

                        blocks.Add(blockName);
                    }

                    var metadata = BaseMetadata
                        .SetItem(Strings.source_timestamp, sourceFile.GetLastWriteTimestamp())
                        .SetItem(Strings.last_refresh_time, Timestamp.Now);

                    var props = await fileClient.GetPropertiesAsync();
                    if (props.Value.GetLastWriteTimestamp() > sourceFile.GetLastWriteTimestamp())
                    {
                        // Error out if file was modified while writing the blob
                        throw new Exception($"File modified: {props.Value.GetLastWriteTimestamp()} > {sourceFile.GetLastWriteTimestamp()}");
                    }

                    // Blocks are left uncommitted intentionally so that blob is materialized on demand
                    await blobClient.SetMetadataAsync(metadata, cancellationToken: token);

                    //await blobClient.CommitBlockListAsync(blocks, new CommitBlockListOptions()
                    //{
                    //    Metadata = metadata
                    //},
                    //cancellationToken: token);

                    // Copy times from original file
                    var smbProps = props.Value.SmbProperties;
                    smbProps.FileChangedOn = Timestamp.Now.Value;

                    // Clear out content of file. And set archive time metadata in order to mark the file as archived
                    await fileClient.CreateAsync(
                        sourceFile.FileSize!.Value,
                        new ShareFileCreateOptions()
                        {
                            Metadata = metadata,
                            SmbProperties = smbProps,
                            HttpHeaders = props.Value.ToHttpHeaders()
                        });
                }
                // target dehydrated and needs refresh or hydrated needs to be dehydrated
                else
                {
                    bool hydrated = targetBlob.Properties.ContentLength > 0;
                    bool needsRefresh = false;
                    if (!hydrated)
                    {
                        // Not hydrated. Check if refresh is needed.
                        if ((targetBlob.Metadata.ValueOrDefault(Strings.last_refresh_time) ?? Timestamp.Now) < refreshExpiry
                            && (maxRefreshes-- >= 0))
                        {
                            needsRefresh = true;
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        // Hydrated, check if blob can be evicted
                        // If source file was modified fairly recently, then it cannot be evicted.
                        // Hydrators should touch source file AND target blob to indicate keep alive for the blob
                        // Need to touch blob so that we can use a blob condition to keep from overwriting the blob
                        // Need to touch source file so that other hydrators can avoid touching based on info from query of source file
                        if (sourceFile.Properties.LastModified > Expiry || targetBlob.Properties.LastModified > Expiry) return;
                    }

                    operation = needsRefresh ? "refreshing blob" : "redehydrating blob";

                    var blobClient = targetBlobContainer.GetBlockBlobClient(path);
                    var fileClient = sourceShareClient.GetRootDirectoryClient().GetFileClient(path);

                    if (needsRefresh)
                    {
                        var blocks = await blobClient.GetBlockListAsync();
                        await blobClient.CommitBlockListAsync(
                            blocks.Value.UncommittedBlocks.Select(b => b.Name).Order(),
                            cancellationToken: token);
                    }

                    var conditions = new BlobRequestConditions()
                    {
                        IfUnmodifiedSince = targetBlob.Properties.LastModified
                    };

                    var snapshot = await blobClient.CreateSnapshotAsync(conditions: conditions, cancellationToken: token);

                    var metadata = BaseMetadata.SetItems(targetBlob.Metadata)
                        .SetItems(BaseMetadata)
                        //.SetItem(Strings.last_refresh_time, Timestamp.Now)
                        .SetItem(Strings.snapshot, snapshot.Value.Snapshot)
                        .ToBuilder();

                    // Clear out blocks. Metadata is preserved
                    await blobClient.CommitBlockListAsync([], new CommitBlockListOptions()
                    {
                        Metadata = metadata,
                        Conditions = conditions
                    },
                    cancellationToken: token);

                    await fileClient.SetMetadataAsync(metadata, cancellationToken: token);

                    var snapshotClient = blobClient.WithSnapshot(snapshot.Value.Snapshot);
                    var snaphotBlocks = await snapshotClient.GetBlockListAsync(BlockListTypes.Committed, cancellationToken: token);

                    long offset = 0;
                    foreach (var block in snaphotBlocks.Value.CommittedBlocks)
                    {
                        // Stage blocks, these are uncommitted intentionally
                        // so that blob is materialized on demand
                        await blobClient.StageBlockFromUriAsync(
                            base64BlockId: block.Name,
                            sourceUri: snapshotClient.Uri,
                            options: new StageBlockFromUriOptions()
                            {
                                SourceRange = new HttpRange(offset, block.SizeLong)
                            },
                            cancellationToken: token
                        );

                        offset += block.SizeLong;
                    }

                    // Now that update is complete, remove pointer to snapshot and update refresh time
                    metadata[Strings.last_refresh_time] = Timestamp.Now;
                    metadata.Remove(Strings.snapshot);

                    // Side effect: Set the file changed time to indicate file is dehydrated.
                    // Hydrators will set this to a well-known value to indicate file is hydrated
                    await fileClient.SetMetadataAsync(metadata);
                    await blobClient.SetMetadataAsync(metadata);
                }

                Console.WriteLine($"{path}: Completed {operation}");
            }
            catch (Exception ex)
            {
                // Blob was modified since last check; safe to ignore or log as needed
                Console.Error.WriteLine($"{path}: Exception {operation}:\n{ex.Message}");
            }
        });

        await DeleteOldSnapshotsAsync(targetBlobContainer, targetBlobs);

        // Additional steps can be added here as needed

        return 0;
    }

    private async Task<Dictionary<string, ShareFileItem>> GetSourceFilesAsync(ShareClient shareClient)
    {
        var files = new Dictionary<string, ShareFileItem>();
        var dirs = new Stack<ShareDirectoryClient>();
        {
            ShareDirectoryGetFilesAndDirectoriesOptions options = new()
            {
                Traits = ShareFileTraits.All
            };

            dirs.Push(shareClient.GetRootDirectoryClient());
            while (dirs.TryPop(out var dir))
            {
                await foreach (var item in dir.GetFilesAndDirectoriesAsync(options: options))
                {
                    var path = string.IsNullOrEmpty(dir.Path?.Trim('/')) ? item.Name : $"{dir.Path}/{item.Name}";
                    if (!item.IsDirectory)
                    {
                        files[path] = item;
                    }
                    else
                    {
                        dirs.Push(shareClient.GetDirectoryClient(path));
                    }
                }
            }
        }
        return files;
    }

    private async Task<Dictionary<string, BlobItem>> GetTargetBlobsAsync(BlobContainerClient container)
    {
        var blobs = new Dictionary<string, BlobItem>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.Metadata))
        {
            blobs[blob.Name] = blob;
        }
        return blobs;
    }

    private async Task DeleteOldSnapshotsAsync(BlobContainerClient container, Dictionary<string, BlobItem> targetBlobs)
    {
        await foreach (var blobItem in container.GetBlobsAsync(BlobTraits.None, BlobStates.Snapshots))
        {
            if (blobItem.Snapshot != null && blobItem.Properties.LastModified < Expiry
                // If target blob metadata, still references snapshot, we can't delete it yet
                && (!targetBlobs.TryGetValue(blobItem.Name, out var targetItem) || targetItem.Metadata.ValueOrDefault(Strings.snapshot) != blobItem.Snapshot))
            {
                var blobClient = container.GetBlobClient(blobItem.Name).WithSnapshot(blobItem.Snapshot);

                try
                {
                    await blobClient.DeleteIfExistsAsync();
                }
                catch (RequestFailedException ex)
                {
                    // Blob was modified since last check; safe to ignore or log as needed
                    Console.Error.WriteLine($"{blobClient.Name}: Exception deleting stale snapshot: {ex.Message}");
                }
            }
        }
    }
}