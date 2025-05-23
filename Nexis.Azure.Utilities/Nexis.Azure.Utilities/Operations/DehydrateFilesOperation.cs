using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.ComponentModel;
using System.Diagnostics;
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
        public const string metadataPrefix = "ghostd_";
        public const string last_refresh_time = $"{metadataPrefix}refresh_time";
        public const string snapshot = $"{metadataPrefix}snapshot";
        public const string state = $"{metadataPrefix}state";
        public const string block_prefix = $"{metadataPrefix}_block_prefix";
        public const string size = $"{metadataPrefix}size";
    }

    public required Uri Uri;

    public static ImmutableDictionary<string, string> BaseMetadata = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
        .Add("archive_version", "1")
        ;

    public int RefreshBatches = 5;

    public bool ShouldDeleteExtraneousTargetFiles;

    // Set to zero to delete ephemeral snapshots immediately
    public string EphemeralSnapshotDeleteDelayValue = "5m";

    public TimeSpan EphemeralSnapshotDeleteDelay
    {
        get
        {
            if (TimeSpan.TryParse(EphemeralSnapshotDeleteDelayValue, out var ts)) return ts;
            if (TimeSpanSetting.TryParseReadableTimeSpan(EphemeralSnapshotDeleteDelayValue, out ts)) return ts;

            throw new FormatException($"Unable to parse Expiry '{EphemeralSnapshotDeleteDelayValue}' as timeSpan");
        }
    }

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

    private enum BlobState
    {
        ghosted,
        transitioning,
        active
    }

    private static ImmutableDictionary<string, string> GetStrippedMetadata(IEnumerable<KeyValuePair<string, string>> source, BlobState state)
    {
        return BaseMetadata
            .SetItems(source)
            .RemoveRange(source.Select(s => s.Key).Where(k => k.StartsWith(Strings.metadataPrefix)))
            .SetItem(Strings.state, state.ToString());
    }

    public async Task<int> RunAsync()
    {
        var targetBlobContainer = new BlobContainerClient(Uri);
        var targetBlobs = await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata)
            .OrderBy(b => b.Name).ToListAsync();
        var snapshotPolicy = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        var maxRefreshes = (targetBlobs.Count + (RefreshBatches - 1)) / RefreshBatches;

        var refreshExpiry = (DateTimeOffset.UtcNow - RefreshInterval).ToTimeStamp();

        await Helpers.ForEachAsync(!SingleThreaded, targetBlobs, token, async (blob, token) =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            Exception ex = null;
            var path = blob.Name;

            string operation = "";

            BlobState state = GetBlobState(blob);
            var prefix = $"[{state.ToString().PadRight(15, ' ')}] {path}";
            try
            {
                Console.WriteLine($"{prefix}: START (State={state})");

                if (blob.Metadata.TryGetValue(Strings.snapshot, out var snapshotId))
                {
                    // Retain referenced snapshots
                    snapshotPolicy[snapshotId] = DateTime.MaxValue;
                }

                if (state == BlobState.ghosted && (blob.Metadata.ValueOrDefault(Strings.last_refresh_time) ?? Timestamp.Zero) > refreshExpiry)
                {
                    // File is ghosted and sufficiently recently refreshed
                    operation = "up to date";
                    return;
                }
                else if (state == BlobState.active && (blob.Properties.ContentLength == 0 || blob.Properties.LastModified > Expiry))
                {
                    // Blob is committed and has zero length or was modified recently
                    // Leave active for now
                    operation = "recently active";
                    return;
                }

                var blobClient = targetBlobContainer.GetBlockBlobClient(path);
                var fileSize = blob.Properties.ContentLength!.Value;

                operation = state == BlobState.active
                    ? "dehydrating file"
                    : "refreshing file";

                // Create snapshot
                var conditions = new BlobRequestConditions()
                {
                    IfMatch = blob.Properties.ETag
                };

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
                    snapshotId = blob.Metadata[Strings.snapshot];
                }
                else if (state == BlobState.ghosted)
                {
                    // Needs precommit
                    var blockPrefix = blob.Metadata[Strings.block_prefix];
                    var currentBlockList = await blobClient.GetBlockListAsync();
                    var blocksToCommit = currentBlockList.Value.UncommittedBlocks
                        .Select(b => b.Name)
                        .Where(b => b.StartsWith(blockPrefix))
                        .Order()
                        .ToList();

                    // Change to active state
                    var crsp = await blobClient.CommitBlockListAsync(
                        base64BlockIds: blocksToCommit,
                        metadata: GetStrippedMetadata(blob.Metadata, BlobState.active),
                        conditions: conditions,
                        cancellationToken: token);

                    conditions.IfMatch = crsp.Value.ETag;
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

                var metadata = GetStrippedMetadata(blob.Metadata, BlobState.transitioning)
                    .SetItem(Strings.size, snapshotLength.ToString())
                    .SetItem(Strings.snapshot, snapshotId);

                // Clear block list and set Transitioning metadata
                var commitResponse = await blobClient.CommitBlockListAsync([], new CommitBlockListOptions()
                {
                    Metadata = metadata,
                    Conditions = conditions
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
                            SourceRange = new HttpRange(offset, blockLength)
                        },
                        cancellationToken: token
                    );

                    blocks.Add(blockName);
                }

                metadata = GetStrippedMetadata(blob.Metadata, BlobState.ghosted)
                    .SetItem(Strings.block_prefix, operationTimestamp.ToBlockIdPrefix())
                    .SetItem(Strings.size, snapshotLength.ToString())
                    .SetItem(Strings.last_refresh_time, Timestamp.Now);

                // Blocks are left uncommitted intentionally so that blob is materialized on demand
                await blobClient.SetMetadataAsync(metadata, new BlobRequestConditions()
                {
                    IfMatch = commitResponse.Value.ETag
                }, cancellationToken: token);

                if (isEmphemeralSnapshot)
                {
                    // Post-processing will wait some interval before deleting the ephemeral snapshot
                    snapshotPolicy[snapshotId] = DateTime.UtcNow + EphemeralSnapshotDeleteDelay;
                }

                //try
                //{
                //    //await snapshotClient.DeleteAsync(cancellationToken: token);
                //}
                //catch (Exception exception)
                //{
                //    Console.WriteLine($"{prefix}: Failed to delete snapshot {operation}:\n{exception.Message}");
                //}
            }
            catch (Exception exception)
            {
                ex = exception;
            }
            finally
            {
                var result = ex == null ? "Success" : $"Failure\n{ex}";
                Console.WriteLine($"{prefix}: Completed {operation}. Result = {result}");
            }
        });

        await DeleteOldSnapshotsAsync(targetBlobContainer, snapshotPolicy);

        return 0;
    }

    private BlobState GetBlobState(BlobItem blob)
    {
        if (blob.Properties.ContentLength != 0)
        {
            return BlobState.active;
        }
        else if (blob.Metadata.TryGetValue(Strings.state, out var stateValue)
            && Enum.TryParse<BlobState>(stateValue, out var state))
        {
            return state;
        }

        return BlobState.active;
    }

    private async Task DeleteOldSnapshotsAsync(BlobContainerClient container, Dictionary<string, DateTime> snapshotPolicy)
    {
        var snapshots = await container.GetBlobsAsync(BlobTraits.None, BlobStates.Snapshots | BlobStates.Version)
            .ToListAsync();
        await Helpers.ForEachAsync(!SingleThreaded, snapshots, token, async (blobItem, token) =>
        {
            var blobClient = container.GetBlobClient(blobItem.Name);
            var prefix = blobClient.Name;
            string operation = "skipped";

            if (blobItem.VersionId is { } versionId && !string.IsNullOrEmpty(versionId))
            {
                blobClient = blobClient.WithVersion(versionId);
                prefix += $"?version={versionId}";
            }
            else if (blobItem.Snapshot is { } snapshotId && string.IsNullOrEmpty(snapshotId))
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

            string result = "Success";
            try
            {
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