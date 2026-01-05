namespace Hermes.Verbs.System;

/// <summary>
/// Unified system Verb handlers. All sys.* operations are implemented here.
/// </summary>
public static class SystemHandlers
{
    /// <summary>
    /// sys.machineInfo - Get system information.
    /// </summary>
    public static SysMachineInfoResult MachineInfo(SysMachineInfoArgs args)
    {
        var disks = new List<DiskInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady)
            {
                disks.Add(new DiskInfo
                {
                    Name = drive.Name,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace
                });
            }
        }

        return new SysMachineInfoResult
        {
            OperatingSystem = Environment.OSVersion.ToString(),
            CpuCount = Environment.ProcessorCount,
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Disks = disks
        };
    }
}
