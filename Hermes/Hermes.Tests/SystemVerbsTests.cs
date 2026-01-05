using System.Runtime.InteropServices;
using Hermes.Verbs.System;
using Xunit;

namespace Hermes.Tests;

public class SystemVerbsTests
{
    [Fact]
    public void SysMachineInfo_ReturnsValidInfo()
    {
        var result = SystemHandlers.MachineInfo(new SysMachineInfoArgs());

        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.OperatingSystem);
        Assert.True(result.CpuCount > 0);
        Assert.True(result.TotalMemoryBytes > 0);
        Assert.NotNull(result.Disks);
    }

    [Fact]
    [Trait("Category", "Platform")]
    public void SysMachineInfo_HasAtLeastOneDisk()
    {
        var result = SystemHandlers.MachineInfo(new SysMachineInfoArgs());

        Assert.NotEmpty(result.Disks);
        var firstDisk = result.Disks[0];
        Assert.NotEmpty(firstDisk.Name);
        Assert.True(firstDisk.TotalBytes > 0);
    }
}
