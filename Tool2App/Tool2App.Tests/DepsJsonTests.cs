using System.Text.Json.Nodes;
using Tool2App;
using Xunit;

namespace Tool2App.Tests;

public class DepsJsonTests
{
    private const string ToolDeps = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0", "signature": "" },
          "compilationOptions": {},
          "targets": {
            ".NETCoreApp,Version=v8.0": {
              "tiny/1.0.0": { "dependencies": { "Native.Lib": "2.0.0" }, "runtime": { "tiny.dll": {} } },
              "Native.Lib/2.0.0": {
                "runtime": { "lib/net8.0/Native.Lib.dll": { "assemblyVersion": "2.0.0.0", "fileVersion": "2.0.0.0" } },
                "resources": { "lib/net8.0/fr/Native.Lib.resources.dll": { "locale": "fr" } },
                "runtimeTargets": {
                  "runtimes/linux-x64/native/libfoo.so": { "rid": "linux-x64", "assetType": "native" },
                  "runtimes/win-x64/native/foo.dll": { "rid": "win-x64", "assetType": "native" },
                  "runtimes/osx-arm64/native/libfoo.dylib": { "rid": "osx-arm64", "assetType": "native" },
                  "runtimes/unix/lib/net8.0/Native.Unix.dll": { "rid": "unix", "assetType": "runtime" },
                  "runtimes/linux/lib/net8.0/Native.Unix.dll": { "rid": "linux", "assetType": "runtime" },
                  "runtimes/win/lib/net8.0/Native.Win.dll": { "rid": "win", "assetType": "runtime" }
                }
              }
            }
          },
          "libraries": {
            "tiny/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Native.Lib/2.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-x", "path": "native.lib/2.0.0" }
          }
        }
        """;

    private static JsonObject Framework(string packId, string version, string rid, string coreLibVersion = "8.0.0.0") => JsonNode.Parse($$"""
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0/{{rid}}", "signature": "" },
          "targets": {
            ".NETCoreApp,Version=v8.0": {},
            ".NETCoreApp,Version=v8.0/{{rid}}": {
              "{{packId}}/{{version}}": {
                "runtime": { "System.Private.CoreLib.dll": { "assemblyVersion": "{{coreLibVersion}}", "fileVersion": "8.0.3126.1" } },
                "native": { "libcoreclr.so": { "fileVersion": "0.0.0.0" } }
              }
            }
          },
          "libraries": { "{{packId}}/{{version}}": { "type": "package", "serviceable": true, "sha512": "", "path": "{{packId.ToLowerInvariant()}}/{{version}}" } }
        }
        """)!.AsObject();

    private static string AppDir()
    {
        var d = TestFiles.NewTempDir();
        foreach (var f in new[] { "runtimes/linux-x64/native/libfoo.so", "runtimes/win-x64/native/foo.dll", "runtimes/unix/lib/net8.0/Native.Unix.dll", "runtimes/linux/lib/net8.0/Native.Unix.dll", "runtimes/win/lib/net8.0/Native.Win.dll" })
            TestFiles.Write(Path.Combine(d, f.Replace('/', Path.DirectorySeparatorChar)), f);
        return d;
    }

    private static Tfm Net8() { Tfm.TryParse("net8.0", out var t); return t; }

    [Fact]
    public void Target_becomes_rid_specific_and_runtime_target_points_at_it()
    {
        var app = AppDir();
        var merged = DepsJson.MergeSelfContained(DepsJson.Parse(ToolDeps), new[] { Framework("Microsoft.NETCore.App.Runtime.linux-x64", "8.0.31", "linux-x64") }, Rid.Parse("linux-x64"), Net8(), app, true);
        Assert.Equal(".NETCoreApp,Version=v8.0/linux-x64", (string?)merged["runtimeTarget"]!["name"]);
        var targets = merged["targets"]!.AsObject();
        Assert.Equal(new[] { ".NETCoreApp,Version=v8.0/linux-x64" }, targets.Select(t => t.Key));
        var t0 = targets[".NETCoreApp,Version=v8.0/linux-x64"]!.AsObject();
        Assert.True(t0.ContainsKey("tiny/1.0.0"));
        Assert.True(t0.ContainsKey("Microsoft.NETCore.App.Runtime.linux-x64/8.0.31"));
        Assert.True(merged["libraries"]!.AsObject().ContainsKey("Microsoft.NETCore.App.Runtime.linux-x64/8.0.31"));
        Assert.True(merged["libraries"]!.AsObject().ContainsKey("Native.Lib/2.0.0"));
        Assert.NotNull(t0["Microsoft.NETCore.App.Runtime.linux-x64/8.0.31"]!["native"]);
    }

    [Fact]
    public void Rid_assets_for_the_target_are_flattened_with_the_most_specific_rid_and_foreign_ones_dropped()
    {
        var app = AppDir();
        var merged = DepsJson.MergeSelfContained(DepsJson.Parse(ToolDeps), Array.Empty<JsonObject>(), Rid.Parse("linux-x64"), Net8(), app, true);
        var lib = merged["targets"]![".NETCoreApp,Version=v8.0/linux-x64"]!["Native.Lib/2.0.0"]!.AsObject();
        Assert.False(lib.ContainsKey("runtimeTargets"));
        Assert.Equal(new[] { "libfoo.so" }, lib["native"]!.AsObject().Select(n => n.Key));
        var runtime = lib["runtime"]!.AsObject().Select(n => n.Key).ToList();
        Assert.Contains("lib/net8.0/Native.Lib.dll", runtime);   // untouched
        Assert.Contains("Native.Unix.dll", runtime);              // from runtimes/linux (more specific than unix)
        Assert.DoesNotContain("Native.Win.dll", runtime);
        Assert.Contains("lib/net8.0/fr/Native.Lib.resources.dll", lib["resources"]!.AsObject().Select(n => n.Key));
        Assert.Equal("runtimes/linux/lib/net8.0/Native.Unix.dll", File.ReadAllText(Path.Combine(app, "Native.Unix.dll")));
        Assert.Equal("runtimes/linux-x64/native/libfoo.so", File.ReadAllText(Path.Combine(app, "libfoo.so")));
        Assert.False(File.Exists(Path.Combine(app, "foo.dll")));
    }

    [Fact]
    public void Windows_target_takes_the_win_assets()
    {
        var app = AppDir();
        var merged = DepsJson.MergeSelfContained(DepsJson.Parse(ToolDeps), Array.Empty<JsonObject>(), Rid.Parse("win-x64"), Net8(), app, true);
        Assert.Equal(".NETCoreApp,Version=v8.0/win-x64", (string?)merged["runtimeTarget"]!["name"]);
        var lib = merged["targets"]![".NETCoreApp,Version=v8.0/win-x64"]!["Native.Lib/2.0.0"]!.AsObject();
        Assert.Equal(new[] { "foo.dll" }, lib["native"]!.AsObject().Select(n => n.Key));
        Assert.Contains("Native.Win.dll", lib["runtime"]!.AsObject().Select(n => n.Key));
        Assert.DoesNotContain("Native.Unix.dll", lib["runtime"]!.AsObject().Select(n => n.Key));
    }

    [Fact]
    public void Single_file_drops_the_runtime_packs_native_entries()
    {
        var merged = DepsJson.MergeSelfContained(DepsJson.Parse(ToolDeps), new[] { Framework("Microsoft.NETCore.App.Runtime.linux-x64", "8.0.31", "linux-x64") }, Rid.Parse("linux-x64"), Net8(), AppDir(), includeRuntimeNative: false);
        var pack = merged["targets"]![".NETCoreApp,Version=v8.0/linux-x64"]!["Microsoft.NETCore.App.Runtime.linux-x64/8.0.31"]!.AsObject();
        Assert.False(pack.ContainsKey("native"));
        Assert.True(pack.ContainsKey("runtime"));
    }

    [Fact]
    public void Several_frameworks_merge_and_the_first_entry_wins_on_clashes()
    {
        var merged = DepsJson.MergeSelfContained(DepsJson.Parse(ToolDeps), new[]
        {
            Framework("Microsoft.NETCore.App.Runtime.linux-x64", "8.0.31", "linux-x64"),
            Framework("Microsoft.AspNetCore.App.Runtime.linux-x64", "8.0.31", "linux-x64"),
        }, Rid.Parse("linux-x64"), Net8(), AppDir(), true);
        var t = merged["targets"]![".NETCoreApp,Version=v8.0/linux-x64"]!.AsObject();
        Assert.True(t.ContainsKey("Microsoft.NETCore.App.Runtime.linux-x64/8.0.31"));
        Assert.True(t.ContainsKey("Microsoft.AspNetCore.App.Runtime.linux-x64/8.0.31"));
    }

    [Fact]
    public void Without_a_tool_deps_file_an_app_entry_is_generated()
    {
        var merged = DepsJson.MergeSelfContained(null, new[] { Framework("Microsoft.NETCore.App.Runtime.linux-x64", "8.0.31", "linux-x64") }, Rid.Parse("linux-x64"), Net8(), AppDir(), false,
            new[] { "hello.dll", "Helper.dll" }, "hello");
        var t = merged["targets"]![".NETCoreApp,Version=v8.0/linux-x64"]!.AsObject();
        Assert.Equal(new[] { "hello.dll", "Helper.dll" }, t["hello/1.0.0"]!["runtime"]!.AsObject().Select(n => n.Key));
        Assert.Equal("project", (string?)merged["libraries"]!["hello/1.0.0"]!["type"]);
    }

    [Fact]
    public void Synthesized_framework_deps_list_the_packs_files()
    {
        var d = DepsJson.SynthesizeFramework("Microsoft.NETCore.App.Runtime.linux-x64", "3.1.32", Rid.Parse("linux-x64"), Net8(), new[] { "A.dll" }, new[] { "libcoreclr.so" });
        var e = d["targets"]!.AsObject().Single().Value!["Microsoft.NETCore.App.Runtime.linux-x64/3.1.32"]!.AsObject();
        Assert.Equal("A.dll", e["runtime"]!.AsObject().Single().Key);
        Assert.Equal("libcoreclr.so", e["native"]!.AsObject().Single().Key);
    }

    [Fact]
    public void Native_code_for_other_operating_systems_is_recognised()
    {
        var dir = TestFiles.NewTempDir();
        string Make(string name, params byte[] head) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, head.Concat(new byte[16]).ToArray()); return p; }
        var elf = Make("a.so", 0x7f, (byte)'E', (byte)'L', (byte)'F');
        var pe = Make("a.dll", (byte)'M', (byte)'Z', 0x90, 0);
        var mach = Make("a.dylib", 0xCF, 0xFA, 0xED, 0xFE);
        var text = Make("a.txt", (byte)'h', (byte)'i', (byte)'!', (byte)'!');
        var linux = Rid.Parse("linux-x64"); var win = Rid.Parse("win-x64"); var mac = Rid.Parse("osx-arm64");
        Assert.False(ForeignNative.Is(elf, linux)); Assert.True(ForeignNative.Is(elf, win)); Assert.True(ForeignNative.Is(elf, mac));
        Assert.True(ForeignNative.Is(pe, linux)); Assert.False(ForeignNative.Is(pe, win));
        Assert.True(ForeignNative.Is(mach, linux)); Assert.False(ForeignNative.Is(mach, mac));
        Assert.False(ForeignNative.Is(text, linux));
        Assert.False(ForeignNative.Is(typeof(Rid).Assembly.Location, linux));   // a managed dll is portable
    }

    [Fact]
    public void Managed_check_tells_dlls_from_native_files()
    {
        Assert.True(DepsJson.IsManaged(typeof(Rid).Assembly.Location));
        var f = Path.Combine(TestFiles.NewTempDir(), "native.so");
        File.WriteAllBytes(f, new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3 });
        Assert.False(DepsJson.IsManaged(f));
    }
}

