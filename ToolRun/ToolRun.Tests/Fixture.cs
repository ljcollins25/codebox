using System.Runtime.InteropServices;
using System.Text;
using Tool2App;
using Tool2App.Tests;

namespace ToolRun.Tests;

/// <summary>A loopback NuGet server with tool packages and fake runtime/host packs, a temp toolrun home, and helpers.</summary>
internal sealed class Fixture : IDisposable
{
    private const string AppHostPlaceholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    public readonly FakeNuGetServer Server = new();
    public readonly string Work = TestFiles.NewTempDir();
    public readonly Rid Rid = Rid.Host;
    /// <summary>major.minor of the runtime running the tests: toolrun's "own" line.</summary>
    public readonly string OwnLine;
    public string OwnVersion => OwnLine + ".3";
    public string HomeDir => Path.Combine(Work, "home");
    public ToolRunHome Home => new(HomeDir);
    public List<string> Info { get; } = new();

    public Fixture()
    {
        // runtime packs: 8.0.x and 9.0.x lines
        var run = Environment.Version;
        OwnLine = $"{run.Major}.{run.Minor}";
        foreach (var v in new[] { "8.0.1", "8.0.3", "8.0.2", "8.0.4-preview.1", "9.0.0", OwnLine + ".1", OwnLine + ".3", OwnLine + ".2" })
        {
            Server.Add("Microsoft.NETCore.App.Runtime." + Rid.PackRid, v, RuntimePack(v));
            Server.Add("Microsoft.AspNetCore.App.Runtime." + Rid.PackRid, v, AspNetPack(v));
            Server.Add("Microsoft.NETCore.App.Host." + Rid.PackRid, v, HostPack());
        }
    }

    public void Dispose() { Server.Dispose(); try { Directory.Delete(Work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public string HostfxrName => Rid.IsWindows ? "hostfxr.dll" : Rid.IsMac ? "libhostfxr.dylib" : "libhostfxr.so";
    public string CoreclrName => Rid.IsWindows ? "coreclr.dll" : Rid.IsMac ? "libcoreclr.dylib" : "libcoreclr.so";

    private byte[] RuntimePack(string version)
    {
        var rid = Rid.PackRid;
        var deps = "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v8.0/" + rid + "\",\"signature\":\"\"},\"targets\":{\".NETCoreApp,Version=v8.0\":{}},\"libraries\":{}}";
        return TestFiles.Zip(new Dictionary<string, byte[]>
        {
            [$"runtimes/{rid}/lib/net8.0/System.Private.CoreLib.dll"] = TestFiles.Bytes("corelib " + version),
            [$"runtimes/{rid}/lib/net8.0/Microsoft.NETCore.App.deps.json"] = TestFiles.Bytes(deps),
            [$"runtimes/{rid}/native/{CoreclrName}"] = TestFiles.Bytes("coreclr " + version),
            [$"runtimes/{rid}/native/{HostfxrName}"] = TestFiles.Bytes("hostfxr " + version),
        });
    }

    private byte[] AspNetPack(string version)
    {
        var rid = Rid.PackRid;
        return TestFiles.Zip(new Dictionary<string, byte[]>
        {
            [$"runtimes/{rid}/lib/net8.0/Microsoft.AspNetCore.dll"] = TestFiles.Bytes("aspnet " + version),
            [$"runtimes/{rid}/lib/net8.0/Microsoft.AspNetCore.App.runtimeconfig.json"] = TestFiles.Bytes("{}"),
        });
    }

    private byte[] HostPack()
    {
        var ext = Rid.IsWindows ? ".exe" : "";
        var apphost = Encoding.ASCII.GetBytes("APPHOST-STUB\0" + AppHostPlaceholder + "\0").Concat(new byte[64]).ToArray();
        return TestFiles.Zip(new Dictionary<string, byte[]> { [$"runtimes/{Rid.PackRid}/native/apphost{ext}"] = apphost });
    }

    /// <summary>The compiled TestChild program (a real tool entry point) or a stand-in managed dll.</summary>
    public static string ChildDir => AppContext.BaseDirectory;

    public byte[] ToolPackage(string id, string version, string cmd = "tiny-cmd", string fw = "Microsoft.NETCore.App", string fwVersion = "8.0.0", string? rollForward = null, bool real = false)
    {
        var rc = "{\"runtimeOptions\":{\"tfm\":\"net8.0\",\"framework\":{\"name\":\"" + fw + "\",\"version\":\"" + fwVersion + "\"}"
            + (rollForward is null ? "" : ",\"rollForward\":\"" + rollForward + "\"") + "}}";
        const string p = "tools/net8.0/any/";
        var entries = new Dictionary<string, byte[]>
        {
            [id + ".nuspec"] = TestFiles.Bytes($"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{id}</id><version>{version}</version></metadata></package>"),
            [p + "DotnetToolSettings.xml"] = TestFiles.Bytes($"<DotNetCliTool Version=\"1\"><Commands><Command Name=\"{cmd}\" EntryPoint=\"tiny.dll\" Runner=\"dotnet\"/></Commands></DotNetCliTool>"),
            [p + "tiny.dll"] = real ? File.ReadAllBytes(Path.Combine(ChildDir, "ToolRun.TestChild.dll")) : TestFiles.Bytes("not really a dll " + version),
            [p + "tiny.runtimeconfig.json"] = TestFiles.Bytes(rc),
            [p + "runtimes/linux-x64/native/libfoo.so"] = TestFiles.Bytes("linux"),
            [p + "runtimes/win-x64/native/foo.dll"] = TestFiles.Bytes("windows"),
            [p + "runtimes/osx-arm64/native/libfoo.dylib"] = TestFiles.Bytes("mac"),
        };
        if (real)
        {
            var deps = Path.Combine(ChildDir, "ToolRun.TestChild.deps.json");
            if (File.Exists(deps)) entries[p + "tiny.deps.json"] = File.ReadAllBytes(deps);
        }
        return TestFiles.Zip(entries);
    }

    public void AddTool(string id, string version, string cmd = "tiny-cmd", string fw = "Microsoft.NETCore.App", string fwVersion = "8.0.0", string? rollForward = null, bool real = false)
        => Server.Add(id, version, ToolPackage(id, version, cmd, fw, fwVersion, rollForward, real));

    public string Fake(string name) => Path.Combine(Work, name);

    /// <summary>A fake installed dotnet root: host/fxr and shared/&lt;framework&gt;/&lt;version&gt; folders.</summary>
    public string FakeDotnet(string name, params (string fw, string version)[] runtimes)
    {
        var root = Path.Combine(Work, name);
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "9.0.0"));
        foreach (var (fw, v) in runtimes) Directory.CreateDirectory(Path.Combine(root, "shared", fw, v));
        return root;
    }

    public ToolRunApp App(string[] args, IReadOnlyList<string>? systemRoots = null, Func<IReadOnlyList<string>, Tool2App.IPackageFeed>? feeds = null, Dictionary<string, string?>? env = null)
    {
        var o = CliParser.Parse(args);
        if (o.Sources.Count == 0 && feeds is null) o.Sources.Add(Server.V2Url);
        var app = new ToolRunApp(o, Home, feeds, k => env is not null && env.TryGetValue(k, out var v) ? v : null, cwd: Work, rid: Rid, info: Info.Add);
        app.SystemRoots = () => systemRoots ?? Array.Empty<string>();
        return app;
    }

    public int Requests(string fragment) => Server.Count(fragment);
}
