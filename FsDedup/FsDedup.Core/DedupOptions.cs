namespace FsDedup;

/// <summary>Everything the command line can say.</summary>
public sealed class DedupOptions
{
    public const long DefaultMinSize = 64 * 1024;

    public required string Root { get; set; }
    public bool WhatIf { get; set; }
    public long MinSize { get; set; } = DefaultMinSize;
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 4);
    public string? CachePath { get; set; }
    public bool Verbose { get; set; }
    public bool Json { get; set; }

    /// <summary>"clone" is the only mode implemented; "hardlink" is reserved (see <see cref="IDedupStrategy"/>).</summary>
    public string Mode { get; set; } = "clone";
}

/// <summary>Test seams: called between the steps of a replacement so a test can change a file at an exact moment.</summary>
public sealed class RunHooks
{
    /// <summary>Before the duplicate is opened for replacement (it is not yet held; writers can still get in).</summary>
    public Action<string>? AfterHash { get; set; }

    /// <summary>After the byte-for-byte verification, before the final re-check (the duplicate is held open here).</summary>
    public Action<string>? BeforeSwap { get; set; }

    /// <summary>Replaces the strategy's per-original reference limit (to test group splitting with small groups).</summary>
    public int? MaxReferencesOverride { get; set; }

}
