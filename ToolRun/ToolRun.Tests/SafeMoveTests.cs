using System.Text;
using Tool2App;
using Tool2App.Tests;
using Xunit;

namespace ToolRun.Tests;

public class SafeMoveTests : IDisposable
{
    private readonly string _dir = TestFiles.NewTempDir();
    public void Dispose()
    {
        SafeMove.DirMover.Value = null; SafeMove.Sleeper.Value = null; SafeMove.StepMs.Value = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name, string file = "a.txt", string text = "x")
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, file), text);
        return d;
    }

    [Fact]
    public void ATransientLockIsRetriedWithGrowingPauses()
    {
        var src = Folder("src"); var dst = Path.Combine(_dir, "dst");
        int calls = 0; var pauses = new List<TimeSpan>();
        SafeMove.DirMover.Value = (s, t) => { if (++calls <= 3) throw new UnauthorizedAccessException("Access to the path is denied."); Directory.Move(s, t); };
        SafeMove.Sleeper.Value = pauses.Add;
        SafeMove.Dir(src, dst, () => false);
        Assert.Equal(4, calls);
        Assert.True(File.Exists(Path.Combine(dst, "a.txt")));
        Assert.False(Directory.Exists(src));
        Assert.Equal(new[] { 100, 200, 300 }, pauses.Select(p => (int)p.TotalMilliseconds).ToArray());
    }

    [Fact]
    public void IOExceptionsAreRetriedToo()
    {
        var src = Folder("src"); var dst = Path.Combine(_dir, "dst");
        int calls = 0;
        SafeMove.DirMover.Value = (s, t) => { if (++calls == 1) throw new IOException("being used by another process"); Directory.Move(s, t); };
        SafeMove.Sleeper.Value = _ => { };
        SafeMove.Dir(src, dst, () => false);
        Assert.Equal(2, calls);
        Assert.True(Directory.Exists(dst));
    }

    [Fact]
    public void TenTriesAboutFiveSecondsThenACopyAsALastResort()
    {
        var src = Folder("src"); var dst = Path.Combine(_dir, "dst");
        int calls = 0; var total = TimeSpan.Zero;
        SafeMove.DirMover.Value = (_, _) => { calls++; throw new UnauthorizedAccessException("denied"); };
        SafeMove.Sleeper.Value = p => total += p;
        SafeMove.Dir(src, dst, () => false);
        Assert.Equal(SafeMove.Tries, calls);
        Assert.InRange(total.TotalSeconds, 4, 6);             // 100 + 200 ... + 900 ms
        Assert.True(File.Exists(Path.Combine(dst, "a.txt")));  // copied, so the install still succeeds
    }

    [Fact]
    public void AFinishedTargetFromAnotherProcessCountsAsInstalled()
    {
        var src = Folder("src", text: "mine"); var dst = Folder("dst", text: "theirs");
        int calls = 0;
        SafeMove.DirMover.Value = (_, _) => { calls++; throw new IOException("target exists"); };
        SafeMove.Sleeper.Value = _ => throw new InvalidOperationException("no waiting expected");
        SafeMove.Dir(src, dst, () => File.Exists(Path.Combine(dst, "a.txt")));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(dst, "a.txt")));   // theirs is kept
        Assert.False(Directory.Exists(src));
        Assert.Equal(0, calls);                                                 // not even tried
    }

    [Fact]
    public void AnIncompleteTargetIsNotTrusted()
    {
        var src = Folder("src", text: "good"); var dst = Folder("dst", file: "partial.tmp");
        SafeMove.DirMover.Value = (_, _) => throw new IOException("target exists");
        SafeMove.Sleeper.Value = _ => { };
        SafeMove.Dir(src, dst, () => File.Exists(Path.Combine(dst, "a.txt")));
        Assert.Equal("good", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.False(File.Exists(Path.Combine(dst, "partial.tmp")));
    }

    [Fact]
    public void FilesAreRetriedAndReplaced()
    {
        var src = Path.Combine(_dir, "new.exe"); var dst = Path.Combine(_dir, "tool.exe");
        File.WriteAllText(src, "new"); File.WriteAllText(dst, "old");
        SafeMove.Sleeper.Value = _ => { };
        SafeMove.File(src, dst);
        Assert.Equal("new", File.ReadAllText(dst));
    }

    [Fact]
    public void ARealHeldFileHandleOnWindowsIsWaitedOut()
    {
        if (!OperatingSystem.IsWindows()) return;   // elsewhere an open file does not block a directory move
        var src = Folder("src"); var dst = Path.Combine(_dir, "dst");
        var held = new FileStream(Path.Combine(src, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        SafeMove.StepMs.Value = 40;
        var release = Task.Run(async () => { await Task.Delay(250); held.Dispose(); });
        SafeMove.Dir(src, dst, () => false);
        release.Wait();
        Assert.True(File.Exists(Path.Combine(dst, "a.txt")));
    }
}

public class TransientLockInstallTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() { SafeMove.DirMover.Value = null; SafeMove.Sleeper.Value = null; _f.Dispose(); }

    private static Action<string, string> Flaky(int failures, Action<int>? count = null)
    {
        int n = 0;
        return (s, t) => { count?.Invoke(++n); if (n <= failures) throw new UnauthorizedAccessException($"Access to the path '{s}' is denied."); Directory.Move(s, t); };
    }

    [Fact]
    public async Task AToolInstallSurvivesALockedFolder()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        SafeMove.DirMover.Value = Flaky(4); SafeMove.Sleeper.Value = _ => { };
        var tool = await _f.App(new[] { "tiny.tool" }).ResolveToolAsync();
        Assert.True(File.Exists(Path.Combine(tool.Dir, "toolrun.json")));
    }

    [Fact]
    public async Task ADownloadedAspNetCoreRuntimeSurvivesALockedFolder()
    {
        _f.AddTool("Web.Tool", "1.0.0", cmd: "web", fw: "Microsoft.AspNetCore.App");
        SafeMove.DirMover.Value = Flaky(3); SafeMove.Sleeper.Value = _ => { };
        var plan = await _f.App(new[] { "web.tool" }, Array.Empty<string>()).PrepareAsync(false);
        Assert.True(File.Exists(Path.Combine(plan.RuntimeRoot, "shared", "Microsoft.AspNetCore.App", _f.OwnVersion, "Microsoft.AspNetCore.dll")));
        Assert.Empty(Directory.EnumerateDirectories(plan.RuntimeRoot, ".*"));      // no temp folders left behind
    }

    [Fact]
    public async Task TheEmbeddedRuntimeUnpackSurvivesALockedFolder()
    {
        _f.AddTool("Tiny.Tool", "1.0.0", fwVersion: "8.0.0");
        var zip = TestFiles.Zip(new Dictionary<string, byte[]>
        {
            ["shared/Microsoft.NETCore.App/10.0.88/System.Private.CoreLib.dll"] = TestFiles.Bytes("c"),
            ["host/fxr/10.0.88/" + _f.HostfxrName] = TestFiles.Bytes("h"),
        });
        EmbeddedAssets.Override.Value = n => n == EmbeddedAssets.RuntimeZip ? new MemoryStream(zip) : null;
        try
        {
            SafeMove.DirMover.Value = Flaky(2); SafeMove.Sleeper.Value = _ => { };
            var plan = await _f.App(new[] { "tiny.tool" }, Array.Empty<string>()).PrepareAsync(false);
            Assert.True(File.Exists(Path.Combine(plan.RuntimeRoot, "shared", "Microsoft.NETCore.App", "10.0.88", "System.Private.CoreLib.dll")));
            Assert.True(File.Exists(Path.Combine(plan.RuntimeRoot, "host", "fxr", "10.0.88", _f.HostfxrName)));
        }
        finally { EmbeddedAssets.Override.Value = null; }
    }

    [Fact]
    public async Task ARunnerThatWonTheRaceIsAcceptedWhenItsInstallIsComplete()
    {
        _f.AddTool("Tiny.Tool", "1.0.0");
        // another toolrun finishes the same install while ours is being moved
        var home = _f.Home;
        SafeMove.Sleeper.Value = _ => { };
        SafeMove.DirMover.Value = (s, t) =>
        {
            Directory.CreateDirectory(t);
            File.WriteAllText(Path.Combine(t, "toolrun.json"), File.ReadAllText(Path.Combine(s, "toolrun.json")));
            throw new IOException("Cannot create a file when that file already exists.");
        };
        var tool = await _f.App(new[] { "tiny.tool" }).ResolveToolAsync();
        Assert.Equal("1.0.0", tool.Meta.Version);
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(tool.Dir)!, ".*"));
    }
}
