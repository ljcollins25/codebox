using System.Diagnostics;
using System.Runtime.InteropServices;
using Tool2App;
using Xunit;

namespace ToolRun.Tests;

/// <summary>A toolrun home whose private dotnet root is made of this machine's installed runtime (linked, or copied where links are not allowed) and a real apphost, so toolrun.dll can run real tools through the real host.</summary>
internal static class RealHome
{
    public static readonly Lazy<string?> Dir = new(Create);

    public static string DotnetRoot() =>
        new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent!.Parent!.Parent!.FullName;

    private static string? Create()
    {
        var rid = Rid.Host;
        var root = DotnetRoot();
        var version = Environment.Version.ToString();
        var fx = Path.Combine(root, "shared", "Microsoft.NETCore.App", version);
        var fxr = Path.Combine(root, "host", "fxr", version);
        var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Host." + rid.PackRid);
        var apphost = Directory.Exists(packs)
            ? Directory.EnumerateDirectories(packs).Select(d => Path.Combine(d, "runtimes", rid.PackRid, "native", rid.ExeName("apphost"))).FirstOrDefault(File.Exists)
            : null;
        if (!Directory.Exists(fx) || !Directory.Exists(fxr) || apphost is null) return null;   // no SDK host pack here: the tests below pass trivially

        var home = Path.Combine(Path.GetTempPath(), "toolrun-realhome-" + Guid.NewGuid().ToString("N"));
        DirLink(fx, Path.Combine(home, "dotnet", "shared", "Microsoft.NETCore.App", version));
        DirLink(fxr, Path.Combine(home, "dotnet", "host", "fxr", version));
        Directory.CreateDirectory(Path.Combine(home, "apphost"));
        File.Copy(apphost, Path.Combine(home, "apphost", rid.PackRid + "-" + rid.ExeName("apphost")));
        return home;
    }

    private static void DirLink(string target, string link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        Copy(target, link);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

/// <summary>
/// toolrun.dll as a process, running the compiled TestChild program as a tool through an apphost and the real host (hostfxr/hostpolicy) on the private root,
/// with DOTNET_ROOT cleared in toolrun's own environment so nothing installed can leak in.
/// </summary>
public class ProcessTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    private static string? Home => RealHome.Dir.Value;

    private Process Start(string[] toolrunArgs, string id = "Tiny.Tool", string fwVersion = "10.0.0", string? workDir = null, string? rollForward = null)
    {
        _f.AddTool(id, "1.0.0", real: true, fwVersion: fwVersion, rollForward: rollForward);
        var psi = new ProcessStartInfo(Path.Combine(RealHome.DotnetRoot(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
            WorkingDirectory = workDir ?? _f.Work,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "toolrun.dll"));
        psi.ArgumentList.Add(id.ToLowerInvariant());
        foreach (var a in new[] { "--source", _f.Server.V2Url, "--home", Home!, "--verbose" }) psi.ArgumentList.Add(a);
        foreach (var a in toolrunArgs) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_ROOT"] = "";
        psi.Environment["DOTNET_ROOT_X64"] = "";
        psi.Environment["DOTNET_ROOT_ARM64"] = "";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        return Process.Start(psi)!;
    }

    private (int code, string stdout, string stderr) Run(string[] args, string? stdin = null, string? workDir = null, string id = "Tiny.Tool", string fwVersion = "10.0.0", string? rollForward = null)
    {
        using var p = Start(args, id, fwVersion, workDir, rollForward);
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(120_000), "toolrun did not finish");
        return (p.ExitCode, o.Result, e.Result);
    }

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(123)]
    public void TheToolsExitCodeIsToolrunsExitCode(int code)
    {
        if (Home is null) return;
        var r = Run(new[] { "--", "exit", code.ToString() });
        Assert.Equal(code, r.code);
        Assert.Contains("real .NET host", r.stderr);
    }

    [Fact]
    public void ArgumentsArriveUnchanged()
    {
        if (Home is null) return;
        var r = Run(new[] { "--", "args", "--help", "--source", "x y", "", "a\"b", "-", "--" });
        Assert.Equal(0, r.code);
        Assert.Equal(new[] { "<--help>", "<--source>", "<x y>", "<>", "<a\"b>", "<->", "<-->" }, Lines(r.stdout));
    }

    [Fact]
    public void ArgumentsWithoutDoubleDashAlsoPassThrough()
    {
        if (Home is null) return;
        var r = Run(new[] { "args", "--version" });
        Assert.Equal("<--version>", r.stdout.Trim());
    }

    [Fact]
    public void StdinGoesInAndStdoutAndStderrComeOutSeparately()
    {
        if (Home is null) return;
        var r = Run(new[] { "--", "echo" }, stdin: "hello\nworld\n");
        Assert.Equal("hello\nworld\n", r.stdout.Replace("\r\n", "\n"));
        Assert.Contains("err-line", r.stderr);
        Assert.DoesNotContain("err-line", r.stdout);
    }

    [Fact]
    public void TheWorkingDirectoryIsInherited()
    {
        if (Home is null) return;
        var work = Path.Combine(_f.Work, "some dir");
        Directory.CreateDirectory(work);
        var r = Run(new[] { "--", "cwd" }, workDir: work);
        Assert.Equal(Path.GetFullPath(work), Path.GetFullPath(r.stdout.Trim()));
    }

    [Fact]
    public void TheHostRunsTheToolOnThePrivateRootAndAppContextBaseDirectoryIsTheToolsFolder()
    {
        if (Home is null) return;
        Assert.Equal(Path.Combine(Home, "dotnet"), Run(new[] { "--", "env", "DOTNET_ROOT" }).stdout.Trim());
        var b = Run(new[] { "--", "basedir" }).stdout.Trim();
        var toolDir = Path.Combine(Home, "tools", "tiny.tool", "1.0.0", "tools", "net8.0", "any");
        Assert.Equal(Path.GetFullPath(toolDir).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal("ToolRun.TestChild", Run(new[] { "--", "entry" }).stdout.Trim());
    }

    [Theory]
    [InlineData("8.0.0", null)]          // default policy, net8 on a net10 root: only works because toolrun sets DOTNET_ROLL_FORWARD=Major
    [InlineData("9.0.0", "Minor")]
    public void AnOlderToolRunsOnTheNewerPrivateRuntimeThroughRollForward(string fwVersion, string? policy)
    {
        if (Home is null) return;
        var id = "Old.Tool" + fwVersion.Replace(".", "");
        var r = Run(new[] { "--", "env", "DOTNET_ROLL_FORWARD" }, id: id, fwVersion: fwVersion, rollForward: policy);
        Assert.Equal(0, r.code);
        Assert.Equal("Major", r.stdout.Trim());
        Assert.Contains("DOTNET_ROLL_FORWARD=Major", r.stderr);   // and the verbose line says so
        // the real host really ran it on the net10 runtime
        Assert.Equal(0, Run(new[] { "--", "args", "ok" }, id: id, fwVersion: fwVersion, rollForward: policy).code);
    }

    [Fact]
    public void TheToolOfTheRootsOwnLineGetsNoOverride()
    {
        if (Home is null) return;
        var r = Run(new[] { "--", "env", "DOTNET_ROLL_FORWARD" });
        Assert.Equal("<unset>", r.stdout.Trim());
    }

    [Fact]
    public void AnUnhandledExceptionIsTheToolsOwn()
    {
        if (Home is null) return;
        var r = Run(new[] { "--", "throw" });
        Assert.NotEqual(0, r.code);
        Assert.Contains("boom from the tool", r.stderr);
    }

    [Fact]
    public void ToolrunErrorsExitWith1AndSayWhoSpoke()
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(RealHome.DotnetRoot(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "toolrun.dll"), "no.such.tool", "--source", _f.Server.V2Url, "--home", _f.HomeDir },
            RedirectStandardError = true, UseShellExecute = false,
        })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(1, p.ExitCode);
        Assert.StartsWith("toolrun:", err);
    }

    [Fact]
    public void WhichPrintsTheCommandLineWithoutRunningIt()
    {
        if (Home is null) return;
        Run(new[] { "--", "exit", "0" });   // installs the tool
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(RealHome.DotnetRoot(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "toolrun.dll"), "--which", "tiny.tool", "--source", _f.Server.V2Url, "--home", Home, "--", "a b", "c" },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        var o = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("DOTNET_ROOT=", o.Replace("set \"", ""));
        Assert.Contains("tiny-cmd", o);
        Assert.Contains("a b", o);
        Assert.Single(Lines(o));
    }

    [Fact]
    public void SigtermSentToToolrunReachesTheTool()
    {
        if (Home is null || OperatingSystem.IsWindows()) return;
        using var p = Start(new[] { "--", "wait-signal" });
        Assert.Equal("ready", p.StandardOutput.ReadLine());
        Assert.True(Signals.Send(p.Id, Signals.SIGTERM));
        Assert.True(p.WaitForExit(30_000));
        Assert.Equal(42, p.ExitCode);   // the tool's own handler ran and its exit code came back through toolrun
    }

    [Fact]
    public void SigintSentToToolrunReachesTheToolWhenNotOnATerminal()
    {
        if (Home is null || OperatingSystem.IsWindows()) return;
        using var p = Start(new[] { "--", "wait-signal" });
        Assert.Equal("ready", p.StandardOutput.ReadLine());
        Assert.True(Signals.Send(p.Id, Signals.SIGINT));
        Assert.True(p.WaitForExit(30_000));
        Assert.Equal(43, p.ExitCode);
    }

    [Fact]
    public void ChildRunnerReturnsExitCodesAndPassesArgs()
    {
        var plan = new LaunchPlan
        {
            FileName = Path.Combine(RealHome.DotnetRoot(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            Args = { Path.Combine(AppContext.BaseDirectory, "ToolRun.TestChild.dll"), "exit", "9" },
        };
        Assert.Equal(9, ChildRunner.Run(plan));
        Assert.Throws<ToolException>(() => ChildRunner.Run(new LaunchPlan { FileName = Path.Combine(_f.Work, "does-not-exist") }));
    }
}
