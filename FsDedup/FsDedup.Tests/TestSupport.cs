using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FsDedup;
using Xunit;

[assembly: SupportedOSPlatform("windows")]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FsDedup.Tests;

/// <summary>A test that needs a ReFS volume: skipped, with the reason, unless FSDEDUP_REFS_TEST_ROOT names one.</summary>
public sealed class RefsFactAttribute : FactAttribute
{
    public const string Variable = "FSDEDUP_REFS_TEST_ROOT";

    public RefsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "FsDedup needs Windows.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Needs a ReFS volume: set {Variable} to its root (create one with FsDedup/scripts/New-RefsTestVolume.ps1).";
    }
}

/// <summary>A test that runs anywhere on Windows (on the ReFS volume when the variable is set, else in the temp folder).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "FsDedup needs Windows.";
    }
}

/// <summary>A scratch folder created for one test and deleted afterwards.</summary>
public sealed class Scratch : IDisposable
{
    public string Root { get; }

    public Scratch()
    {
        var baseDir = Environment.GetEnvironmentVariable(RefsFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.GetTempPath();
        Root = Path.Combine(baseDir, "fsdedup-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Path_(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>Writes a file of pseudo-random bytes (same seed and size: same content).</summary>
    public string Write(string name, int seed, int size)
    {
        var path = Path_(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    public DedupReport Run(bool whatIf, RunHooks? hooks = null, Action<DedupOptions>? configure = null)
    {
        var o = new DedupOptions { Root = Root, WhatIf = whatIf, MinSize = 1024, Threads = 2 };
        configure?.Invoke(o);
        return new DedupEngine(o, hooks, null).Run();
    }

    /// <summary>Every file under the root with size, times, attributes and file ID, except the tool's own files.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var rel = Path.GetRelativePath(Root, f);
            if (Path.GetFileName(f).StartsWith(".fsdedup-")) continue;
            var id = Native.ReadIdentity(f);
            map[rel] = $"{id.FileId}|{id.Size}|{id.CreationTicks}|{id.WriteTicks}|{id.Attributes}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))}";
        }
        return map;
    }

    public void Dispose()
    {
        try { Directory.Delete(Native.Long(Root), true); }
        catch (Exception) { /* leave it; the volume is thrown away */ }
    }
}

internal static class Win
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newFile, string existing, IntPtr security);

    public static void HardLink(string newFile, string existing)
    {
        if (!CreateHardLinkW(newFile, existing, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static bool SameClusters(string a, string b)
    {
        using var x = Native.OpenRead(a, FileShare.ReadWrite | FileShare.Delete, false);
        using var y = Native.OpenRead(b, FileShare.ReadWrite | FileShare.Delete, false);
        var ea = Native.GetExtents(x);
        return ea.Count > 0 && ea.SequenceEqual(Native.GetExtents(y));
    }

    public static string Sddl(string path) =>
        new FileInfo(path).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Group)
            .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Group);
}
