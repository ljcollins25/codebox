using System.Text;
using System.Text.Json.Nodes;
using Tool2App;
using Xunit;

namespace Tool2App.Tests;

/// <summary>
/// Builds a tiny tool package and fake runtime/host packs, serves them from a loopback NuGet v2 server and runs the whole conversion.
/// No network and no real runtime needed; the apphosts are stand-ins that contain the placeholders HostModel looks for.
/// </summary>
public class EndToEndTests : IDisposable
{
    private const string AppHostPlaceholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
    private static readonly byte[] BundleSignature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    };

    private readonly FakeNuGetServer _server = new();
    private readonly string _work = TestFiles.NewTempDir();
    private readonly string _toolPackage;
    private readonly Tool2AppBuilder _builder;
    private readonly List<string> _log = new();

    public EndToEndTests()
    {
        foreach (var rid in new[] { "linux-x64", "win-x64" })
        {
            foreach (var v in new[] { "8.0.1", "8.0.3", "8.0.2", "8.0.4-preview.1", "9.0.0" })
            {
                _server.Add("Microsoft.NETCore.App.Runtime." + rid, v, RuntimePack(rid, v));
                _server.Add("Microsoft.NETCore.App.Host." + rid, v, HostPack(rid));
            }
        }
        _toolPackage = Path.Combine(_work, "tiny.1.2.3.nupkg");
        File.WriteAllBytes(_toolPackage, TinyTool());
        var store = new PackageStore(new V2Feed(_server.V2Url), Path.Combine(_work, "cache")) { Log = _log.Add };
        _builder = new Tool2AppBuilder(store, _log.Add);
    }

    public void Dispose() { _server.Dispose(); try { Directory.Delete(_work, true); } catch (IOException) { } }

    private static byte[] RuntimePack(string rid, string version)
    {
        var nativeName = rid.StartsWith("win") ? "coreclr.dll" : "libcoreclr.so";
        var tfmDeps = """
            {"runtimeTarget":{"name":".NETCoreApp,Version=v8.0/@RID@","signature":""},
             "targets":{".NETCoreApp,Version=v8.0":{},".NETCoreApp,Version=v8.0/@RID@":{"Microsoft.NETCore.App.Runtime.@RID@/@VER@":{
               "runtime":{"System.Private.CoreLib.dll":{"assemblyVersion":"8.0.0.0","fileVersion":"8.0.1.1"}},
               "native":{"@NATIVE@":{"fileVersion":"0.0.0.0"}}}}},
             "libraries":{"Microsoft.NETCore.App.Runtime.@RID@/@VER@":{"type":"package","serviceable":true,"sha512":"","path":"microsoft.netcore.app.runtime.@RID@/@VER@"}}}
            """.Replace("@RID@", rid).Replace("@VER@", version).Replace("@NATIVE@", nativeName);
        return TestFiles.Zip(new Dictionary<string, byte[]>
        {
            [$"runtimes/{rid}/lib/net8.0/System.Private.CoreLib.dll"] = ManagedBytes(),
            [$"runtimes/{rid}/lib/net8.0/Microsoft.NETCore.App.deps.json"] = TestFiles.Bytes(tfmDeps),
            [$"runtimes/{rid}/lib/net8.0/Microsoft.NETCore.App.runtimeconfig.json"] = TestFiles.Bytes("{}"),
            [$"runtimes/{rid}/native/{nativeName}"] = TestFiles.Bytes("coreclr " + version),
        });
    }

    private static byte[] ManagedBytes() => File.ReadAllBytes(typeof(Rid).Assembly.Location);   // a real managed assembly, so the bundler treats it as one
    private static byte[] Elf(string text) => new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    private static byte[] HostPack(string rid)
    {
        var ext = rid.StartsWith("win") ? ".exe" : "";
        // both real hosts carry both placeholders: the app path (apphost) and the bundle header offset (single-file)
        byte[] Host(string name) => Encoding.ASCII.GetBytes(name + "-STUB\0" + AppHostPlaceholder + "\0").Concat(new byte[8]).Concat(BundleSignature).Concat(new byte[64]).ToArray();
        var apphost = Host("APPHOST");
        var single = Host("SINGLEFILEHOST");
        return TestFiles.Zip(new Dictionary<string, byte[]>
        {
            [$"runtimes/{rid}/native/apphost{ext}"] = apphost,
            [$"runtimes/{rid}/native/singlefilehost{ext}"] = single,
        });
    }

    private static byte[] TinyTool()
    {
        var managed = File.ReadAllBytes(typeof(Rid).Assembly.Location);   // any managed assembly will do as the entry point
        const string deps = """
            {"runtimeTarget":{"name":".NETCoreApp,Version=v8.0","signature":""},"compilationOptions":{},
             "targets":{".NETCoreApp,Version=v8.0":{
               "tiny/1.2.3":{"dependencies":{"Native.Lib":"2.0.0"},"runtime":{"tiny.dll":{}}},
               "Native.Lib/2.0.0":{"runtime":{"lib/net8.0/Native.Lib.dll":{}},
                 "resources":{"lib/net8.0/fr/Native.Lib.resources.dll":{"locale":"fr"}},
                 "runtimeTargets":{
                   "runtimes/linux-x64/native/libfoo.so":{"rid":"linux-x64","assetType":"native"},
                   "runtimes/win-x64/native/foo.dll":{"rid":"win-x64","assetType":"native"},
                   "runtimes/osx-arm64/native/libfoo.dylib":{"rid":"osx-arm64","assetType":"native"}}}}},
             "libraries":{"tiny/1.2.3":{"type":"project","serviceable":false,"sha512":""},
               "Native.Lib/2.0.0":{"type":"package","serviceable":true,"sha512":"x","path":"native.lib/2.0.0"}}}
            """;
        const string rc = """{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"},"rollForward":"Major","configProperties":{"System.GC.Concurrent":false}}}""";
        const string settings = """<DotNetCliTool Version="1"><Commands><Command Name="tiny-cmd" EntryPoint="tiny.dll" Runner="dotnet"/></Commands></DotNetCliTool>""";
        const string nuspec = """<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Tiny</id><version>1.2.3</version></metadata></package>""";
        const string p = "tools/net8.0/any/";
        return TestFiles.Zip(new Dictionary<string, byte[]>
        {
            ["Tiny.nuspec"] = TestFiles.Bytes(nuspec),
            [p + "DotnetToolSettings.xml"] = TestFiles.Bytes(settings),
            [p + "tiny.dll"] = managed,
            [p + "tiny.runtimeconfig.json"] = TestFiles.Bytes(rc),
            [p + "tiny.deps.json"] = TestFiles.Bytes(deps),
            [p + "Native.Lib.dll"] = managed,
            [p + "fr/Native.Lib.resources.dll"] = managed,
            [p + "runtimes/linux-x64/native/libfoo.so"] = Elf("linux native"),
            [p + "runtimes/win-x64/native/foo.dll"] = TestFiles.Bytes("windows native"),
            [p + "runtimes/osx-arm64/native/libfoo.dylib"] = TestFiles.Bytes("mac native"),
        });
    }

    private Task<BuildResult> Build(string rid, Action<BuildOptions>? tweak = null, string? name = null)
    {
        var o = new BuildOptions { Rid = Rid.Parse(rid), OutputDir = Path.Combine(_work, name ?? ("out-" + Guid.NewGuid().ToString("N"))) };
        tweak?.Invoke(o);
        using var pkg = PackageContent.Open(_toolPackage);
        return _builder.BuildAsync(pkg, o);
    }

    private static string P(string dir, string rel) => Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Self_contained_folder_for_linux()
    {
        var r = await Build("linux-x64");
        var dir = Path.GetDirectoryName(r.ExePath)!;
        Assert.Equal("tiny-cmd", Path.GetFileName(r.ExePath));
        Assert.Equal(new[] { "Microsoft.NETCore.App 8.0.3" }, r.Frameworks);       // latest stable patch, not 8.0.4-preview.1, not 9.0.0
        Assert.Equal("Tiny", r.ToolId);

        // tool files + runtime files side by side; rid-specific natives flattened; foreign rids gone
        Assert.True(File.Exists(P(dir, "tiny.dll")));
        Assert.True(File.Exists(P(dir, "fr/Native.Lib.resources.dll")));
        Assert.True(File.Exists(P(dir, "System.Private.CoreLib.dll")));
        Assert.True(File.Exists(P(dir, "libcoreclr.so")));
        Assert.Contains("linux native", File.ReadAllText(P(dir, "libfoo.so")));
        Assert.False(Directory.Exists(P(dir, "runtimes")));                            // flattened: nothing left under runtimes/
        Assert.False(File.Exists(P(dir, "foo.dll")));
        Assert.False(File.Exists(P(dir, "runtimes/win-x64/native/foo.dll")));
        Assert.False(File.Exists(P(dir, "runtimes/osx-arm64/native/libfoo.dylib")));
        Assert.False(File.Exists(P(dir, "DotnetToolSettings.xml")));
        Assert.False(File.Exists(P(dir, "Microsoft.NETCore.App.deps.json")));

        // runtimeconfig: self-contained form
        var opts = JsonNode.Parse(File.ReadAllText(P(dir, "tiny.runtimeconfig.json")))!["runtimeOptions"]!.AsObject();
        Assert.False(opts.ContainsKey("framework"));
        Assert.Equal("8.0.3", (string?)opts["includedFrameworks"]![0]!["version"]);
        Assert.False((bool)opts["configProperties"]!["System.GC.Concurrent"]!);

        // deps: merged
        var deps = JsonNode.Parse(File.ReadAllText(P(dir, "tiny.deps.json")))!.AsObject();
        Assert.Equal(".NETCoreApp,Version=v8.0/linux-x64", (string?)deps["runtimeTarget"]!["name"]);
        Assert.Contains("Microsoft.NETCore.App.Runtime.linux-x64/8.0.3", deps["libraries"]!.AsObject().Select(l => l.Key));
        Assert.Contains("libfoo.so", deps["targets"]![".NETCoreApp,Version=v8.0/linux-x64"]!["Native.Lib/2.0.0"]!["native"]!.AsObject().Select(l => l.Key));

        // apphost bound to the entry dll, placeholder gone
        var host = Encoding.ASCII.GetString(File.ReadAllBytes(r.ExePath));
        Assert.DoesNotContain(AppHostPlaceholder, host);
        Assert.Contains("tiny.dll", host);
        if (!OperatingSystem.IsWindows()) Assert.True((File.GetUnixFileMode(r.ExePath) & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public async Task Explicit_runtime_version_is_used_as_given()
    {
        var r = await Build("linux-x64", o => o.RuntimeVersion = "8.0.1");
        Assert.Equal(new[] { "Microsoft.NETCore.App 8.0.1" }, r.Frameworks);
        await Assert.ThrowsAsync<ToolException>(() => Build("linux-x64", o => o.RuntimeVersion = "8.0.77"));
    }

    [Fact]
    public async Task Cross_build_for_windows_gives_an_exe_with_windows_natives()
    {
        var r = await Build("win-x64");
        var dir = Path.GetDirectoryName(r.ExePath)!;
        Assert.Equal("tiny-cmd.exe", Path.GetFileName(r.ExePath));
        Assert.Equal("windows native", File.ReadAllText(P(dir, "foo.dll")));
        Assert.False(File.Exists(P(dir, "libfoo.so")));
        Assert.True(File.Exists(P(dir, "coreclr.dll")));
        Assert.Contains("tiny.dll", Encoding.ASCII.GetString(File.ReadAllBytes(r.ExePath)));
    }

    [Fact]
    public async Task Single_file_bundles_managed_files_and_native_libraries()
    {
        var r = await Build("linux-x64", o => o.SingleFile = true);
        var dir = Path.GetDirectoryName(r.ExePath)!;
        Assert.Equal(new[] { "tiny-cmd" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));   // really one file
        Assert.True(Executables.IsBundle(r.ExePath, out var offset));
        Assert.True(offset > 0);
        var bytes = Encoding.Latin1.GetString(File.ReadAllBytes(r.ExePath));
        Assert.Contains("System.Private.CoreLib.dll", bytes);
        Assert.Contains("tiny.dll", bytes);
        Assert.Contains("libfoo.so", bytes);                    // native library embedded for self-extraction
        Assert.DoesNotContain("libcoreclr.so", bytes);          // the runtime's own natives live in singlefilehost
        Assert.Contains("includedFrameworks", bytes);           // runtimeconfig embedded
        Assert.Contains("Microsoft.NETCore.App.Runtime.linux-x64/8.0.3", bytes);
    }

    [Fact]
    public async Task Single_file_can_leave_native_libraries_beside_the_exe()
    {
        var r = await Build("linux-x64", o => { o.SingleFile = true; o.NativeBeside = true; });
        var dir = Path.GetDirectoryName(r.ExePath)!;
        Assert.Contains("linux native", File.ReadAllText(P(dir, "libfoo.so")));
    }

    [Fact]
    public async Task Windows_single_file_cross_build()
    {
        var r = await Build("win-x64", o => o.SingleFile = true);
        Assert.True(Executables.IsBundle(r.ExePath, out _));
        Assert.EndsWith("tiny-cmd.exe", r.ExePath);
    }

    [Fact]
    public async Task Framework_dependent_keeps_the_original_config_and_adds_no_runtime()
    {
        var r = await Build("linux-x64", o => o.FrameworkDependent = true);
        var dir = Path.GetDirectoryName(r.ExePath)!;
        Assert.False(File.Exists(P(dir, "System.Private.CoreLib.dll")));
        Assert.False(File.Exists(P(dir, "libcoreclr.so")));
        var opts = JsonNode.Parse(File.ReadAllText(P(dir, "tiny.runtimeconfig.json")))!["runtimeOptions"]!.AsObject();
        Assert.Equal("Microsoft.NETCore.App", (string?)opts["framework"]!["name"]);
        Assert.True(File.Exists(P(dir, "runtimes/win-x64/native/foo.dll")));    // untouched: the host picks at run time
        Assert.Contains("tiny.dll", Encoding.ASCII.GetString(File.ReadAllBytes(r.ExePath)));
    }

    [Fact]
    public async Task Framework_dependent_single_file()
    {
        var r = await Build("linux-x64", o => { o.FrameworkDependent = true; o.SingleFile = true; });
        Assert.True(Executables.IsBundle(r.ExePath, out _));
    }

    [Fact]
    public async Task Existing_output_needs_force()
    {
        var r = await Build("linux-x64", name: "fixed");
        var dir = Path.GetDirectoryName(r.ExePath)!;
        var ex = await Assert.ThrowsAsync<ToolException>(() => Build("linux-x64", name: "fixed"));
        Assert.Contains("--force", ex.Message);
        await Build("linux-x64", o => o.Force = true, "fixed");
        Assert.True(File.Exists(P(dir, "tiny.dll")));
    }

    [Fact]
    public async Task Works_from_an_extracted_folder_too()
    {
        var folder = Path.Combine(_work, "extracted");
        System.IO.Compression.ZipFile.ExtractToDirectory(_toolPackage, folder);
        using var pkg = PackageContent.Open(folder);
        var r = await _builder.BuildAsync(pkg, new BuildOptions { Rid = Rid.Parse("linux-x64"), OutputDir = Path.Combine(_work, "from-folder") });
        Assert.True(File.Exists(r.ExePath));
    }

    [Fact]
    public async Task Pointer_package_with_rid_specific_tool_packages_fetches_the_one_for_the_rid()
    {
        _server.Add("Tiny.linux-x64", "1.2.3", TinyTool());
        var pointer = TestFiles.Zip(new Dictionary<string, byte[]>
        {
            ["Tiny.nuspec"] = TestFiles.Bytes("""<?xml version="1.0"?><package><metadata><id>Tiny</id><version>1.2.3</version></metadata></package>"""),
            ["tools/net8.0/any/DotnetToolSettings.xml"] = TestFiles.Bytes("""<DotNetCliTool Version="2"><RuntimeIdentifierPackages><RuntimeIdentifierPackage RuntimeIdentifier="linux-x64" Id="Tiny.linux-x64"/><RuntimeIdentifierPackage RuntimeIdentifier="win-x64" Id="Tiny.win-x64"/></RuntimeIdentifierPackages></DotNetCliTool>"""),
        });
        var folder = Path.Combine(_work, "pointer");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Tiny.1.2.3.nupkg");
        File.WriteAllBytes(file, pointer);
        using var pkg = PackageContent.Open(file);
        var r = await _builder.BuildAsync(pkg, new BuildOptions { Rid = Rid.Parse("linux-x64"), OutputDir = Path.Combine(_work, "pointer-out") });
        Assert.True(File.Exists(P(Path.GetDirectoryName(r.ExePath)!, "tiny.dll")));
        var ex = await Assert.ThrowsAsync<ToolException>(() => _builder.BuildAsync(pkg, new BuildOptions { Rid = Rid.Parse("osx-arm64"), OutputDir = Path.Combine(_work, "pointer-mac") }));
        Assert.Contains("none for osx-arm64", ex.Message);
    }

    [Fact]
    public async Task Windows_desktop_tools_cannot_target_linux()
    {
        // a tool whose runtimeconfig asks for WindowsDesktop
        var rc = """{"runtimeOptions":{"tfm":"net8.0-windows","frameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.0"},{"name":"Microsoft.WindowsDesktop.App","version":"8.0.0"}]}}""";
        var folder = Path.Combine(_work, "wd");
        System.IO.Compression.ZipFile.ExtractToDirectory(_toolPackage, folder);
        File.WriteAllText(P(folder, "tools/net8.0/any/tiny.runtimeconfig.json"), rc);
        using var pkg = PackageContent.Open(folder);
        var ex = await Assert.ThrowsAsync<ToolException>(() => _builder.BuildAsync(pkg, new BuildOptions { Rid = Rid.Parse("linux-x64"), OutputDir = Path.Combine(_work, "wd-out") }));
        Assert.Contains("Windows", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_work, "wd-out")));   // nothing half-built is left behind
    }
}

