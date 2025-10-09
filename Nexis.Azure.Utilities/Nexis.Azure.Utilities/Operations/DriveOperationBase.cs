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

public class DriveOperationBase(IConsole Console, CancellationToken token)
{
    public required Uri Uri;

    protected static ImmutableDictionary<string, string> BaseTags = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
        .Add("archive_version", "1")
        ;

    public static bool SingleThreaded = System.Diagnostics.Debugger.IsAttached;

    public BlobContainerClient GetTargetContainerAndPrefix(out string? prefix, Uri? explicitUri = null)
    {
        var rootBlobClient = new BlobClient(explicitUri ?? Uri);
        var targetBlobContainer = rootBlobClient.GetParentBlobContainerClient();
        var suffix = "/";
        if (rootBlobClient.Name.TrimEnd('/') != "" && rootBlobClient.Exists(cancellationToken: token))
        {
            suffix = "";
        }
        prefix = !string.IsNullOrEmpty(Out.Var(out var rootPath, rootBlobClient.Name.TrimEnd('/')))
            ? rootPath + suffix
            : null;

        return targetBlobContainer;
    }

    protected enum BlobState
    {
        pending,
        ghost,
        transitioning,
        active
    }

    protected static ImmutableDictionary<string, string> RemoveCustomKeys(
        IEnumerable<KeyValuePair<string, string>> source,
        BlobState state)
    {
        return BaseTags
            .SetItems(source)
            .RemoveRange(source.Select(s => s.Key).Where(k => k.StartsWith(Strings.tagPrefix)))
            .SetItem(Strings.state, state.ToString());
    }

    private static Regex regex = new Regex(@"\[\[(?<displayName>[^\]]+)\]\].*");

    protected static string GetName(string blobName)
    {
        var m = regex.Match(blobName);
        if (m.Success)
        {
            return m.Groups["displayName"].Value;
        }

        return blobName;
    }

    protected BlobState GetBlobState(BlobItem blob)
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
}