using System.Runtime.InteropServices;

namespace Tool2App;

/// <summary>A .NET runtime identifier such as win-x64, linux-musl-arm64 or osx.12-arm64, with its fallback chain.</summary>
public sealed class Rid : IEquatable<Rid>
{
    private static readonly string[] Arches = { "x64", "x86", "arm64", "arm", "armv6", "s390x", "ppc64le", "loongarch64", "riscv64", "wasm" };

    public string Value { get; }
    /// <summary>OS family: win, linux, linux-musl, osx, freebsd (version suffixes dropped).</summary>
    public string Os { get; }
    public string Arch { get; }
    /// <summary>The rid used in runtime/host pack ids and folders: family-arch (win10-x64 becomes win-x64).</summary>
    public string PackRid => Os + "-" + Arch;
    /// <summary>This rid first, then ever more general rids, ending in "any". Same idea as the runtime's rid graph.</summary>
    public IReadOnlyList<string> Compatible { get; }

    private Rid(string value, string os, string arch)
    {
        Value = value; Os = os; Arch = arch;
        var list = new List<string> { value };
        void Add(string s) { if (!list.Contains(s)) list.Add(s); }
        Add(os + "-" + arch);
        if (os == "linux-musl") { Add("linux-musl"); Add("linux-" + arch); Add("linux"); }
        else Add(os);
        if (os is "linux" or "linux-musl" or "osx" or "freebsd") Add("unix");
        Add("any");
        Compatible = list;
    }

    public bool IsWindows => Os == "win";
    public bool IsMac => Os == "osx";
    public string ExeName(string name) => IsWindows ? name + ".exe" : name;

    public OSPlatform OsPlatform => Os switch
    {
        "win" => OSPlatform.Windows,
        "osx" => OSPlatform.OSX,
        "freebsd" => OSPlatform.FreeBSD,
        _ => OSPlatform.Linux,
    };

    public Architecture Architecture => Arch switch
    {
        "x64" => Architecture.X64,
        "x86" => Architecture.X86,
        "arm64" => Architecture.Arm64,
        "arm" or "armv6" => Architecture.Arm,
        "s390x" => Architecture.S390x,
        "ppc64le" => Architecture.Ppc64le,
        "loongarch64" => Architecture.LoongArch64,
        "riscv64" => Architecture.RiscV64,
        "wasm" => Architecture.Wasm,
        _ => throw new ToolException($"Unsupported architecture '{Arch}'."),
    };

    /// <summary>Position of <paramref name="rid"/> in the fallback chain (0 = most specific), or -1 when it does not apply.</summary>
    public int Rank(string rid)
    {
        for (int i = 0; i < Compatible.Count; i++)
            if (string.Equals(Compatible[i], rid, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static Rid Parse(string value)
    {
        value = value.Trim().ToLowerInvariant();
        foreach (var arch in Arches.OrderByDescending(a => a.Length))
        {
            if (!value.EndsWith("-" + arch, StringComparison.Ordinal)) continue;
            var osPart = value[..^(arch.Length + 1)];
            if (osPart.Length == 0) break;
            var os = osPart.StartsWith("win", StringComparison.Ordinal) ? "win"
                : osPart.Split('.')[0];
            return new Rid(value, os, arch);
        }
        throw new ToolException($"Cannot understand runtime identifier '{value}' (expected e.g. win-x64, linux-x64, linux-musl-arm64, osx-arm64).");
    }

    public static Rid Host
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            if (os == "linux" && File.Exists("/etc/alpine-release")) os = "linux-musl";
            return Parse(os + "-" + arch);
        }
    }

    public bool Equals(Rid? other) => other is not null && other.Value == Value;
    public override bool Equals(object? obj) => Equals(obj as Rid);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;
}
