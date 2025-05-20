using System.CommandLine;
using System.IO.Compression;
using Azure.Storage.Blobs.Specialized;

namespace Nexis.Azure.Utilities;

public class DehydrateOperation(IConsole Console, CancellationToken token)
{
    public required string Source;

    public required Uri Target;

    public async Task<int> RunAsync()
    {
        // Get all files in Source
        // Get all files in Target

        /*
        * Delete any snapshots from target which are older than ttl
        * 1. Set metadata State=Dehydrated, SourceEtag (copied from existing metadata)
        * For files missing from source, delete from target optionally
        * For files missing from target where source last write time is after ttl,
        * 1. Put blob with empty block list
        * 2. Put blocks from source
        * 3. Update target metadata, State=Dehydrated
        * 4. Delete source content by calling CreateFile with length of file
        * For hydrated files in target with last modified time older than ttl, which have not been accessed recently
        * 1. Create snapshot
        * 2. Put empty block list, with snapshot id metadata and State=DehydratePending, SourceEtag (copied from existing metadata)
        * - This should fail if blob has new last modified time
        * - Hydrators should update last write time by setting blob properties
        * For files changed in source where last modified time is after ttl, 
        *
        */
        return 0;
    }

    public interface ISourceStore
    {
        public Task CopyToBlobStorage(BlockBlobClient client);
    }
}