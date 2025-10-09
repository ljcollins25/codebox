// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs.Models;

namespace Nexis.Azure.Utilities;

public record struct BlobRelativePath(string Name, Uncomparable<string?> Prefix)
{
    public BlobRelativePath(string path, string? prefix)
        : this(path.Substring(prefix?.Length ?? 0).TrimStart('/'), Prefix: new(prefix))
    {
    }

    public string GetFullName(string? prefix) => string.IsNullOrEmpty(prefix) ? Name : string.Concat(prefix, Name);

    public string FullName => GetFullName(Prefix);
}

public record struct Uncomparable<T>(T Value) : IEquatable<Uncomparable<T>>, IComparable<Uncomparable<T>>
{
    public int CompareTo(Uncomparable<T> other)
    {
        return 0;
    }

    public bool Equals(Uncomparable<T> other)
    {
        return true;
    }

    public override int GetHashCode()
    {
        return 0;
    }

    public static implicit operator T(Uncomparable<T> u) => u.Value;

    //public static implicit operator Uncomparable<T>(T value) => new(value);
}

/// <summary>
/// Accesses metadata about a semaphore or proxy peer entry in a strongly-typed manner
/// </summary>
/// <param name="Metadata">the underlying metadata dictionary</param>
/// <param name="Data">the blob info</param>
public record struct BlobDataEntry(IDictionary<string, string> Metadata, BlobDataEntry.BlobData Data)
{
    [JsonIgnore]
    public long EffectiveSize { get => GetValueOrDefault(Strings.size, Data.Size); set => SetValue(value); }

    public long EffectivePageSize => EffectiveSize.RoundUpToMultiple(512);


    [JsonIgnore]
    public Timestamp? Snapshot { get => GetValueOrDefault(Strings.snapshot); set => SetValue(value); }

    public void SetValue(MetadataValue value, [CallerMemberName] string key = null!)
    {
        if (!string.IsNullOrEmpty(value.StringValue))
        {
            Metadata[key] = value;
        }
    }

    public MetadataValue GetValueOrDefault([CallerMemberName] string key = null!, MetadataValue defaultValue = default)
    {
        if (Metadata?.TryGetValue(key, out var stringValue) == true)
        {
            return stringValue;
        }
        else
        {
            return defaultValue;
        }
    }

    public static BlobDataEntry From(BlobItem item)
    {
        var metadata = EmptyStringMap.SetItems(item.Metadata()).SetItems(item.Tags()).ToBuilder();
        return new BlobDataEntry(metadata, item);
    }

    public record struct BlobData(string Name, long Size, DateTimeOffset LastModified, bool StoreEffectiveTimes = false)
    {
        public DateTimeOffset LastModified { get; set; } = LastModified;

        public static implicit operator BlobData(BlobItem value) => new BlobData(Name: value.Name, value.Properties.ContentLength ?? -1, LastModified: GetLastModifiedTime(value));
    }

    internal static DateTimeOffset? ParseSnapshotTime(string? snapshotId)
    {
        const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        return snapshotId == null
            ? null
            : DateTimeOffset.ParseExact(snapshotId, Format, null);
    }

    internal static DateTimeOffset GetLastModifiedTime(BlobItem blobItem)
    {
        return ParseSnapshotTime(blobItem.Snapshot) ?? blobItem.Properties.LastModified!.Value;
    }

    internal static DateTimeOffset GetLastModifiedTime(BlobSnapshotInfo blobItem)
    {
        return ParseSnapshotTime(blobItem.Snapshot)!.Value;
    }
}