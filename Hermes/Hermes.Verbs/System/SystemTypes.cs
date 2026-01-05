using System.ComponentModel;
using Hermes.Core;

namespace Hermes.Verbs.System;

/// <summary>
/// Arguments for the sys.machineInfo Verb.
/// </summary>
[Description("Get information about the host machine.")]
public sealed class SysMachineInfoArgs
{
}

/// <summary>
/// Result of the sys.machineInfo Verb.
/// </summary>
[Description("Information about the host machine.")]
public sealed class SysMachineInfoResult : VerbResult
{
    /// <summary>
    /// The operating system name and version.
    /// </summary>
    [Description("The operating system name and version string.")]
    public required string OperatingSystem { get; init; }

    /// <summary>
    /// The number of logical CPU cores.
    /// </summary>
    [Description("The number of logical CPU cores available on the machine.")]
    public required int CpuCount { get; init; }

    /// <summary>
    /// The total physical memory in bytes.
    /// </summary>
    [Description("The total physical memory available on the machine, in bytes.")]
    public required long TotalMemoryBytes { get; init; }

    /// <summary>
    /// Information about mounted disks.
    /// </summary>
    [Description("Information about the mounted disks/drives on the machine.")]
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
}

/// <summary>
/// Information about a disk/drive.
/// </summary>
[Description("Information about a single disk or drive.")]
public sealed class DiskInfo
{
    /// <summary>
    /// The name or mount point of the disk.
    /// </summary>
    [Description("The name or mount point of the disk (e.g., 'C:\\' on Windows or '/dev/sda1' on Linux).")]
    public required string Name { get; init; }

    /// <summary>
    /// The total size of the disk in bytes.
    /// </summary>
    [Description("The total capacity of the disk, in bytes.")]
    public required long TotalBytes { get; init; }

    /// <summary>
    /// The free space on the disk in bytes.
    /// </summary>
    [Description("The available free space on the disk, in bytes.")]
    public required long FreeBytes { get; init; }
}
