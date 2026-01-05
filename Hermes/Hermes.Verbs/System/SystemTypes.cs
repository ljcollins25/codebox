using Hermes.Core;

namespace Hermes.Verbs.System;

// sys.machineInfo
public sealed class SysMachineInfoArgs
{
}

public sealed class SysMachineInfoResult : VerbResult
{
    public required string OperatingSystem { get; init; }
    public required int CpuCount { get; init; }
    public required long TotalMemoryBytes { get; init; }
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
}

public sealed class DiskInfo
{
    public required string Name { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }
}
