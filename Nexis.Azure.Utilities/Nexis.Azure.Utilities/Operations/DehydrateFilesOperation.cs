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

public class DehydrateOperation(IConsole Console, CancellationToken token) : DriveOperationBase(Console, token)
{
    public int RefreshBatches = 5;

    public long? MinDehydrationSize;

    //public bool Force;

    // Set to zero to delete ephemeral snapshots immediately
    public TimeSpan EphemeralSnapshotDeleteDelay = ParseTimeSpan("5m");

    // Set to zero to refresh everything
    // Set to large value to refresh nothing
    public TimeSpan RefreshInterval = ParseTimeSpan("5d");

    // Set to zero to force staging of active blobs
    public required DateTimeOffset Expiry = ParsePastDateTimeOffset("1h");

    public async Task<int> RunAsync()
    {
        BlobContainerClient targetBlobContainer = GetTargetContainerAndPrefix(out var prefix);

        var targetBlobs = await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata | BlobTraits.Tags, prefix: prefix, cancellationToken: token)
            .OrderBy(b => b.Name).ToListAsync();
        var snapshotPolicy = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        var maxRefreshes = (targetBlobs.Count + (RefreshBatches - 1)) / RefreshBatches;

        var refreshExpiry = (DateTimeOffset.UtcNow - RefreshInterval).ToTimeStamp();

        await Helpers.ForEachAsync(!SingleThreaded, FilterDirectories(targetBlobs), token, async (blob, token) =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            var readTags = blob.Tags().ToImmutableDictionary();
            Exception? ex = null;
            var path = blob.Name;
            var entry = BlobDataEntry.From(blob);

            string operation = "";
            BlobState state = GetBlobState(blob);
            var logPrefix = $"[{state.ToString().PadRight(15, ' ')}] {GetName(path)}";
            try
            {
                bool requireHydrated = entry.EffectiveSize <= MinDehydrationSize;
                if (readTags.TryGetValue(Strings.snapshot, out var snapshotId))
                {
                    // Retain referenced snapshots
                    snapshotPolicy[snapshotId] = DateTime.MaxValue;
                }

                Console.WriteLine($"{logPrefix}: (Snapshot={snapshotId})");

                if (state == BlobState.active && requireHydrated)
                {
                    operation = "under dehydration threshold";
                    return;
                }
                else if (state == BlobState.active && (blob.Properties.ContentLength == 0 || blob.Properties.LastModified > Expiry))
                {
                    // Blob is committed and has zero length or was modified recently
                    // Leave active for now
                    operation = "recently active";
                    return;
                }
                else if (state == BlobState.ghost
                    && Out.Var<Timestamp?>(out var lastRefreshTime, (readTags!.ValueOrDefault(Strings.last_refresh_time) ?? Timestamp.Zero)) > refreshExpiry)
                {
                    if ((blob.Metadata()!.ValueOrDefault(Strings.dirty_time) ?? Timestamp.Zero) > lastRefreshTime)
                    {
                        // The rclone FS marked the file dirty (i.e. has some leftover uncommitted blocks), need to refresh
                    }
                    else if (!requireHydrated)
                    {
                        // File is ghosted and sufficiently recently refreshed
                        operation = "up to date";
                        return;
                    }
                }

                var blobClient = targetBlobContainer.GetBlockBlobClient(path);
                var fileSize = blob.Properties.ContentLength!.Value;

                // NOTE: We don't use <= here because MinDehydrationSize may be null in which case we can
                // always dehydrate
                operation = state == BlobState.active
                    ? "dehydrating file"
                    : "refreshing file";

                string tagCondition;
                if (readTags.TryGetValue(Strings.last_access, out var lastTouch))
                {
                    if (Timestamp.Parse(lastTouch) > Expiry)
                    {
                        operation = "recently touched";
                        return;
                    }

                    tagCondition = $"{Strings.last_access} = '{lastTouch}'";
                }
                else
                {
                    tagCondition = $"{Strings.last_access} = null";
                }

                // Create snapshot
                var conditions = new BlobRequestConditions()
                {
                    IfMatch = blob.Properties.ETag,
                    TagConditions = tagCondition
                };

                //var headers = new BlobHttpHeaders()
                //{
                //    CacheControl = blob.Properties.CacheControl,
                //    ContentDisposition = blob.Properties.ContentDisposition,
                //    ContentEncoding = blob.Properties.ContentEncoding,
                //    ContentHash = blob.Properties.ContentHash,
                //    ContentLanguage = blob.Properties.ContentLanguage,
                //    ContentType = blob.Properties.ContentType,
                //};

                snapshotId = null;
                Timestamp operationTimestamp = Timestamp.Now;

                // active, snapshot and stage blocks
                // transitioning, stage blocks from snapshot
                // ghosted (but expired), commit blocks with operation prefix

                if (state == BlobState.active)
                {
                    // no precommit needed
                }
                else if (state == BlobState.transitioning)
                {
                    snapshotId = blob.Tags[Strings.snapshot];

                    if (snapshotId != null)
                    {
                        var snapshotExists = await blobClient.WithSnapshot(snapshotId).ExistsAsync(cancellationToken: token);
                        if (!snapshotExists)
                        {
                            state = BlobState.ghost;
                            readTags = readTags.SetItem(Strings.block_prefix, readTags.GetValueOrDefault(Strings.block_prefix, ""));
                            snapshotId = null;
                        }
                    }
                }

                if (state == BlobState.ghost)
                {
                    // Needs precommit
                    var blockPrefix = readTags[Strings.block_prefix];
                    var currentBlockList = await blobClient.GetBlockListAsync();
                    var blocksToCommit = currentBlockList.Value.UncommittedBlocks
                        .Where(b => b.Name.StartsWith(blockPrefix))
                        .OrderBy(b => b.Name)
                        .ToList();

                    var totalSize = blocksToCommit.Sum(b => (long?)b.SizeLong) ?? 0;
                    var expectedSize = readTags.GetValueOrDefault(Strings.size, "0");
                    Contract.Assert(totalSize.ToString() == readTags[Strings.size], $"actual size ({totalSize.ToString()}) != expected size ({expectedSize})");

                    // Change to active state
                    var crsp = await blobClient.CommitBlockListAsync(
                        base64BlockIds: blocksToCommit.Select(b => b.Name),
                        options: new CommitBlockListOptions()
                        {
                            Tags = RemoveCustomKeys(readTags, BlobState.active),
                            Conditions = conditions,
                            Metadata = blob.Metadata,
                            //HttpHeaders = headers
                        },
                        cancellationToken: token);

                    conditions.IfMatch = crsp.Value.ETag;

                    if (requireHydrated)
                    {
                        operation = "hydrating file";
                        return;
                    }
                }

                bool isEmphemeralSnapshot = false;
                if (snapshotId == null)
                {
                    var snapshot = await blobClient.CreateSnapshotAsync(conditions: conditions, cancellationToken: token);
                    snapshotId = snapshot.Value.Snapshot;
                    isEmphemeralSnapshot = true;
                }

                // Retain referenced snapshots
                snapshotPolicy[snapshotId] = DateTime.MaxValue;

                var snapshotClient = blobClient.WithSnapshot(snapshotId);
                var snapshotLength = await snapshotClient.GetPropertiesAsync(cancellationToken: token).ThenAsync(r => r.Value.ContentLength);
                fileSize = snapshotLength;

                var tags = RemoveCustomKeys(readTags, BlobState.transitioning)
                    .SetItem(Strings.size, snapshotLength.ToString())
                    .SetItem(Strings.block_prefix, operationTimestamp.ToBlockIdPrefix())
                    .SetItem(Strings.snapshot, snapshotId);

                // Clear block list and set Transitioning tag
                var commitResponse = await blobClient.CommitBlockListAsync([], new CommitBlockListOptions()
                {
                    Tags = tags,
                    Conditions = conditions,
                    Metadata = blob.Metadata
                    //HttpHeaders = headers
                },
                cancellationToken: token);

                // Copy content of file to blob store (block names should be sortable by order in blob)
                const long MAX_BLOCK_SIZE = 1 << 30; // 1gb
                var blocks = new List<string>();
                int blockId = 0;
                for (long offset = 0; offset < snapshotLength; offset += MAX_BLOCK_SIZE)
                {
                    var blockName = operationTimestamp.ToBlockId(blockId++);
                    var blockLength = Math.Min(MAX_BLOCK_SIZE, snapshotLength - offset);
                    await blobClient.StageBlockFromUriAsync(
                        base64BlockId: blockName,
                        sourceUri: snapshotClient.Uri,
                        options: new StageBlockFromUriOptions()
                        {
                            SourceRange = new HttpRange(offset, blockLength),
                        },
                        cancellationToken: token
                    );

                    blocks.Add(blockName);
                }

                tags = RemoveCustomKeys(readTags, BlobState.ghost)
                    .SetItem(Strings.block_prefix, operationTimestamp.ToBlockIdPrefix())
                    .SetItem(Strings.size, snapshotLength.ToString())
                    .SetItem(Strings.last_refresh_time, Timestamp.Now);

                // Blocks are left uncommitted intentionally so that blob is materialized on demand

                Console.WriteLine($"{logPrefix}: Finalizing ghosting. Tag condition = \"{tagCondition}\"");
                await blobClient.SetTagsAsync(tags, new BlobRequestConditions()
                {
                    //IfMatch = commitResponse.Value.ETag
                    TagConditions = tagCondition
                }, cancellationToken: token);

                if (isEmphemeralSnapshot)
                {
                    // Post-processing will wait some interval before deleting the ephemeral snapshot
                    // snapshotPolicy[snapshotId] = DateTime.UtcNow + EphemeralSnapshotDeleteDelay;
                }

                if (isEmphemeralSnapshot)
                {
                    try
                    {
                        await snapshotClient.DeleteAsync(cancellationToken: token);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine($"{logPrefix}: Failed to delete snapshot {operation}:\n{exception.Message}");
                    }
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

        await DeleteOldSnapshotsAsync(targetBlobContainer, prefix, snapshotPolicy);

        return 0;
    }

    private BlobState GetBlobState(BlobItem blob)
    {
        if (blob.Properties.ContentLength != 0)
        {
            return BlobState.active;
        }
        else if (blob.Tags().TryGetValue(Strings.state, out var stateValue)
            && Enum.TryParse<BlobState>(stateValue, out var state))
        {
            return state;
        }

        return BlobState.active;
    }

    private async Task DeleteOldSnapshotsAsync(BlobContainerClient container, string? prefix, IReadOnlyDictionary<string, DateTime> snapshotPolicy)
    {
        var snapshots = await container.GetBlobsAsync(BlobTraits.None, BlobStates.Snapshots | BlobStates.Version, prefix: prefix)
            .Where(b => b.Snapshot.IsNonEmpty() || b.VersionId.IsNonEmpty())
            .ToListAsync();
        await Helpers.ForEachAsync(!SingleThreaded, snapshots, token, async (blobItem, token) =>
        {
            string result = "Success";
            var blobClient = container.GetBlobClient(blobItem.Name);
            var prefix = GetName(blobClient.Name);
            string operation = "skipped";
            try
            {

                if (blobItem.VersionId is { } versionId && !string.IsNullOrEmpty(versionId))
                {
                    blobClient = blobClient.WithVersion(versionId);
                    prefix += $"?version={versionId}";
                }
                else if (blobItem.Snapshot is { } snapshotId && !string.IsNullOrEmpty(snapshotId))
                {
                    blobClient = blobClient.WithSnapshot(snapshotId);
                    prefix += $"?snapshot={snapshotId}";

                    if (snapshotPolicy.TryGetValue(snapshotId, out var retainedUntil))
                    {
                        if (retainedUntil == DateTime.MaxValue)
                        {
                            operation = "skipped due to reference or failed transition";
                            return;
                        }

                        var delay = retainedUntil - DateTime.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            Console.WriteLine($"{prefix}: Waiting {operation} for {delay} before cleaning up snapshot");
                            await Task.Delay(delay);
                            Console.WriteLine($"{prefix}: Waited {operation} for {delay} before cleaning up snapshot");
                        }
                    }

                    // Snapshot policy supercedes this check (namely for ephemeral staging snapshots and snapshots still referenced by base blob)
                    if (blobItem.Properties.LastModified > Expiry)
                    {
                        operation = "skipped due to last modified time";
                        return;
                    }
                }
                else
                {
                    return;
                }
                operation = "deleting";
                await blobClient.DeleteIfExistsAsync();
            }
            catch (Exception ex)
            {
                result = $"Failure:\n{ex}";
            }
            finally
            {
                Console.WriteLine($"{prefix}: Completed {operation}. Result = {result}");
            }
        });
    }
}