using System.Text.Json.Serialization;

namespace FsDedup;

public sealed class DedupReport
{
    public string Root { get; set; } = "";
    public string Mode { get; set; } = "clone";
    public bool WhatIf { get; set; }
    public string FileSystem { get; set; } = "";
    public long ClusterSize { get; set; }

    public long FilesScanned { get; set; }
    public SortedDictionary<string, long> Skipped { get; set; } = new();
    public long FilesHashed { get; set; }
    public long FilesHashedFromCache { get; set; }
    public long BytesHashed { get; set; }
    public double HashSeconds { get; set; }
    public double HashMBPerSecond => HashSeconds > 0 ? BytesHashed / 1048576.0 / HashSeconds : 0;

    public int Groups { get; set; }
    public long DuplicateFiles { get; set; }
    public long AlreadyShared { get; set; }
    public long FilesReplaced { get; set; }
    public long FilesToReplace { get; set; }

    /// <summary>Logical bytes of the files replaced (what-if: that would be replaced), rounded up to whole clusters.</summary>
    public long BytesFreed { get; set; }
    public long FreeSpaceBefore { get; set; }
    public long FreeSpaceAfter { get; set; }

    public List<string> Recovered { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<GroupReport> GroupList { get; set; } = new();
    public double ReplaceSeconds { get; set; }
    public double ElapsedSeconds { get; set; }
}

public sealed class GroupReport
{
    public long Size { get; set; }
    public string Hash { get; set; } = "";
    public string Original { get; set; } = "";
    public List<string> Duplicates { get; set; } = new();
    public List<string> AlreadyShared { get; set; } = new();
    [JsonIgnore] public int Index { get; set; }
}
