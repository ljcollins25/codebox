# tool2app

Turn a .NET tool package into a standalone app: an executable that runs the tool without `dotnet tool install`, and
(by default) without .NET installed on the machine.

```
tool2app <package>[@version] [--rid win-x64] [--single-file] [--framework-dependent] [-o out]
```

The input is a NuGet tool package id (latest stable version unless `@version` is given), a local `.nupkg`, or an extracted
package folder. Run `tool2app --help` for every option.

## Usage

```
dotnet run --project Tool2App/Tool2App.Cli -- dotnet-counters -o counters          # self-contained folder for this machine
dotnet run --project Tool2App/Tool2App.Cli -- dotnet-counters --single-file        # one executable
dotnet run --project Tool2App/Tool2App.Cli -- dotnet-serve --framework-dependent   # small apphost, needs .NET installed
dotnet run --project Tool2App/Tool2App.Cli -- dotnet-outdated-tool --rid win-x64 --single-file -o outdated-win   # cross-build
dotnet run --project Tool2App/Tool2App.Cli -- ./Some.Tool.1.2.3.nupkg --rid linux-musl-x64
```

(or `dotnet publish Tool2App/Tool2App.Cli -c Release` and use the `tool2app` it produces, or `dotnet pack` and
`dotnet tool install -g tool2app`: the project is also a dotnet tool.)

| option | meaning |
| --- | --- |
| `--rid <rid>` | target runtime identifier, default this machine. Cross-building works in both directions (win-x64 from Linux and back) because only files are assembled; nothing is executed. |
| `--single-file` | one executable (a bundle) instead of a folder |
| `--framework-dependent` | no runtime included: a small apphost plus the tool's files (or, with `--single-file`, one small file); needs .NET installed |
| `-o`, `--force` | output folder (default `./<command>-<rid>`); `--force` replaces a non-empty one |
| `--runtime-version 8.0.12` | ship exactly this runtime instead of the latest patch of the tool's major.minor |
| `--command`, `--tfm` | choose a command (packages with several) or a `tools/<tfm>` folder (default: the highest) |
| `--prerelease` | allow prerelease tool and runtime versions |
| `--feed`, `--feed-kind v2\|v3\|auto`, `--cache` | package source and download cache; env `TOOL2APP_FEED`, `TOOL2APP_FEED_KIND`, `TOOL2APP_CACHE` |
| `--extract-all`, `--native-beside`, `--compress` | single file: extract everything at start-up / leave native libraries next to the exe / compress the bundle |
| `--patch-resources` | Windows only (see Limits) |

### Feeds

The default feed is nuget.org: the **v3** API first, and if that fails (some networks refuse `api.nuget.org`) the **v2** API
(`https://www.nuget.org/api/v2`: `FindPackagesById()` OData for versions, `/package/<id>/<version>` for the download).
`--feed <url>` selects one feed with no fallback; a URL ending in `.json` is v3, anything else v2, or force it with
`--feed-kind`. Downloads (the tool package, runtime packs, host packs) are cached in
`%LOCALAPPDATA%\tool2app\packages` (`~/.local/share/tool2app/packages` on Linux), by id and version.

## How it works

1. **Read the package** (`PackageContent`: zip or folder). `tools/<tfm>/<any|rid>/DotnetToolSettings.xml` gives the command name and
   entry point. Only folders whose rid is `any` or in the target's rid fallback chain (`linux-x64`, `linux`, `unix`, `any`) count; the highest
   TFM wins, then the most specific rid. Packages that only point at per-RID packages (`<RuntimeIdentifierPackages>`) are followed to `<id>.<rid>`.
2. **Find the runtime.** The entry dll's `.runtimeconfig.json` lists the shared frameworks (`framework` or `frameworks`):
   Microsoft.NETCore.App and possibly Microsoft.AspNetCore.App or Microsoft.WindowsDesktop.App. A web tool lists only AspNetCore, which itself needs
   NETCore.App, so dependencies are added. Without a runtimeconfig the tool's TFM decides.
   The version is **the latest stable patch of the tool's major.minor** on the feed (a tool built for net6 or net8 gets that runtime, not today's);
   if that line does not exist the tool's `rollForward` decides (`Minor`, `Major`, `Latest*` allow a later line, `Disable` fails); `--runtime-version` overrides it.
3. **Fetch the pieces from NuGet for the rid:** `Microsoft.NETCore.App.Runtime.<rid>`, `Microsoft.AspNetCore.App.Runtime.<rid>`,
   `Microsoft.WindowsDesktop.App.Runtime.<rid>` (Windows rids only) and `Microsoft.NETCore.App.Host.<rid>`.
   Note: **`singlefilehost` is in the Host pack** (`runtimes/<rid>/native/singlefilehost`, next to `apphost`), not in the runtime pack.
4. **Lay out the app** (self-contained):
   - the tool's files, minus `DotnetToolSettings.xml` and minus `runtimes/<rid>/` folders of other platforms;
   - the runtime packs' `runtimes/<rid>/lib/<tfm>/**` and `native/**` at the root (this includes `libhostfxr`/`libhostpolicy`, which is how an apphost finds its runtime);
   - **`<tool>.runtimeconfig.json`** rewritten: `framework`/`frameworks`, `rollForward` replaced by `includedFrameworks` (name and exact version), everything else (tfm, `configProperties`) kept;
   - **`<tool>.deps.json` merged properly** rather than dropped. The host uses only what deps.json lists, so the runtime must be in it. The tool's target
     `.NETCoreApp,Version=v8.0` becomes `.NETCoreApp,Version=v8.0/<rid>` (and the `runtimeTarget`), and each framework pack's own
     `Microsoft.NETCore.App.deps.json` entries and libraries are added. The tool's RID-specific assets (`runtimeTargets` such as `runtimes/linux-x64/native/libX.so`)
     that apply to the target (most specific rid wins) are moved to the app root and listed as plain runtime/native assets, which is how the SDK lays out a RID-specific publish, so the host needs no rid graph.
     Dropping deps.json would also work (with no deps.json the host puts every dll of the folder on the app path) but then rid-specific native libraries and satellite assemblies are not found; so the file is kept, and generated for single-file when a tool has none;
   - the **apphost** (`HostWriter.CreateAppHost`) renamed to the command name (`.exe` for Windows) and bound to the entry dll.
5. **`--single-file`:** the same layout is fed to `Bundler` with `singlefilehost` (bound to the entry dll first, as the SDK does), self-contained like
   `PublishSingleFile`+`SelfContained`. The runtime's own native libraries are built into `singlefilehost` and are not shipped (and removed from the bundled deps.json).
   **Native libraries of the tool** are bundled and extracted on first start (`IncludeNativeLibrariesForSelfExtract`; extraction folder `~/.net`, or `DOTNET_BUNDLE_EXTRACT_BASE_DIR`);
   `--native-beside` leaves them next to the exe instead. `--extract-all` is `IncludeAllContentForSelfExtract`: every file is extracted, so `Assembly.Location` is a real path.
   Tools that load assemblies by path (nbgv does) need it. Debug symbols and XML docs are not copied; native binaries of other operating systems inside the package (a Windows shim in a Linux build) are dropped.
6. **`--framework-dependent`:** apphost bound to the entry dll plus the tool's files with their runtimeconfig and deps untouched (rid-specific assets are picked by the host at run time);
   with `--single-file` the bundle uses the apphost and is small.

### Microsoft.NET.HostModel

`HostWriter.CreateAppHost` and `Bundler` come from `Microsoft.NET.HostModel.dll`. The NuGet package of that name stops at `5.0.0-preview.1`
(the user's older `Dotnet.Portable.Runner` references 3.1.16), whose Bundler cannot write the bundle format current hosts read. The SDK ships a current build
beside msbuild, so `Tool2App.Core.csproj` references **the DLL of the SDK that builds it**:
`<Reference Include="Microsoft.NET.HostModel"><HintPath>$(MSBuildToolsPath)/Microsoft.NET.HostModel.dll</HintPath><Private>true</Private></Reference>`
(`MSBuildToolsPath` is `<dotnet root>/sdk/<version>/` on Linux and Windows; `Private` copies it to the output so the tool runs without an SDK). With the .NET 10 SDK this is HostModel 10.0.12,
and it can write bundles for older runtimes (the bundle version follows the runtime's major.minor).

## Proven on real tools (Linux runner, results in `.hexad/out/RESULTS.md` of the session branch)

Each output was run with `--version` inside `mcr.microsoft.com/dotnet/runtime-deps:8.0` (glibc, libssl, libicu, **no .NET**).

| tool | shape | runtime shipped | self-contained folder | `--single-file` | works |
| --- | --- | --- | --- | --- | --- |
| dotnet-counters 10.0.745401 | net8.0, native Windows/other-OS content | NETCore 8.0.31 (tool asks 8.0.0, rollForward Major) | 87 MB, 224 files | 68.2 MB exe, 1 file | yes / yes |
| nbgv 3.10.94 | net10.0, libgit2 | NETCore 10.0.12 | 92 MB | 86.2 MB exe, needs `--extract-all` (loads assemblies by path) | yes / yes (with `--extract-all`; without it nbgv fails with `Path "" is not an absolute path`) |
| dotnet-outdated-tool 4.8.1 | net9.0 | NETCore 9.0.20 | 81 MB | 75.4 MB exe | yes / yes |
| dotnet-serve 1.10.194 | net8.0 ASP.NET Core | NETCore + AspNetCore 8.0.31 | 100 MB, 330 files | 96.4 MB exe | yes / yes; also served `GET /` with 200 from the container |
| dotnet-ef 6.0.36 | net6.0, older runtime, built through the v2 feed only (`--feed https://www.nuget.org/api/v2`) | NETCore 6.0.36 | 70 MB | 61.7 MB exe | yes / yes |

Framework-dependent: dotnet-counters 17 MB folder, dotnet-serve `--framework-dependent --single-file` 4 MB: both run in `dotnet/aspnet:8.0`, and in a container without .NET they print
"You must install .NET to run this application", as expected. `--single-file --compress` roughly halves the size (dotnet-outdated 75 MB to 38 MB, started fine).

## Tests

`dotnet test Tool2App/Tool2App.Tests`: DotnetToolSettings and runtimeconfig reading, framework/version/rid selection, runtimeconfig rewriting, deps.json merging,
the v2 and v3 feed clients against a loopback HTTP server (`HttpListener`, paging, fallback, 404s, cache), and end-to-end conversions of a tiny tool package
built in the test (self-contained folder, single file, framework-dependent, a win-x64 cross-build, per-RID tool packages) against fake runtime/host packs served by that server.
No network, no shell scripts, only `Path` functions: meant to pass on Windows as well as Linux.

## Limits

- **Signing:** nothing is signed. A Windows exe built here has no Authenticode signature; macOS output is not code-signed unless tool2app runs on macOS
  (osx-arm64 binaries must be signed to run: `codesign -s - <file>` after a Linux build).
- **No trimming, no ReadyToRun, no NativeAOT:** assemblies are the ones in the tool package; apps are as big as an untrimmed self-contained publish. `--compress` helps the single file.
- **Windows resource patching:** the exe keeps the apphost's own resources (no icon or version info of the tool) and uses the console subsystem. `--patch-resources` copies the version resources from
  the entry dll, and only works when tool2app itself runs on Windows (HostModel uses the Win32 resource API); elsewhere it is skipped with a warning.
- **Single file:** the app runs from the bundle, so tools that use `Assembly.Location` or read their own files next to the exe need `--extract-all`. The runtime's diagnostics natives
  (`libmscordaccore`, `createdump`...) are not included in a single-file build.
- Per-OS testing: only linux-x64 outputs were run. The win-x64 outputs are cross-built, checked as PE files and as bundles, not executed here.
- Tools whose runtimeconfig is already self-contained are rejected. Frameworks other than the three above have no runtime pack here. WindowsDesktop tools need a Windows rid.
- The tool's own deps are copied as shipped: if two versions of one assembly exist (tool and framework) the host's usual "highest version wins" applies.
