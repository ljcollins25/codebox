# toolrun

Run a .NET tool straight from NuGet, in one command: download it once, run it from a cache after that. toolrun is a self-contained single-file .NET 10 app that
**carries its own runtime**, so it works on a machine with no .NET at all.

```
toolrun <package>[@version] [toolrun options] [--] <tool args...>
```

```
toolrun dotnet-outdated-tool -- --help
toolrun dotnet-counters@8.0.547301 ps
toolrun dotnet-serve -- -p 8080
toolrun --list                       # cached tools and the private runtime, with sizes
toolrun --clean dotnet-serve         # drop one tool (or everything with plain --clean)
toolrun --which dotnet-counters      # print the command line toolrun would run
```

toolrun lives in `ToolRun/` (a sibling of `Tool2App/`) and **reuses `Tool2App.Core`** for the NuGet v3/v2 clients, the package store, DotnetToolSettings.xml and runtimeconfig
reading, version selection and the apphost writer (`HostWriter`). The only change to Tool2App.Core is a `PackageStore.Description` property.

| project | what |
| --- | --- |
| `ToolRun/ToolRun` | the executable (`toolrun`), also a dotnet tool (`dotnet pack`, `dotnet tool install -g toolrun`) |
| `ToolRun/ToolRun.Tests` | xunit tests (Windows and Linux) |
| `ToolRun/ToolRun.TestChild` | a tiny real program the tests package as a tool |

**Download:** <https://github.com/ljcollins25/codebox/releases/tag/toolrun-latest> — `toolrun.exe` (win-x64), `toolrun` (linux-x64), `toolrun-osx-arm64`; single files, no .NET needed.

## Usage

Toolrun's own options are read **before the package and directly after it**. The tool's arguments start at the first token that is not a toolrun option, or after `--`
(which is dropped); from there on nothing is interpreted, so `toolrun pkg --version` asks the tool for its version. Use `--` whenever a tool argument could be mistaken for
one of ours (`toolrun pkg -- --update`). `toolrun --version` / `--help` / `--list` / `--clean` / `--which` only count before a package is named.

| option | meaning |
| --- | --- |
| `--update` | check the feed for a newer version (default without a version: **use the newest cached one, no network at all**); if the check fails offline, the cached one is used with a warning |
| `--prerelease` | allow prerelease tool and runtime versions |
| `--source <url>` | package source, repeatable. `.json` = NuGet v3, anything else v2. Default: see Feeds |
| `--runtime-source <url>` | where ASP.NET Core / WindowsDesktop / host packs come from (default: the same sources) |
| `--prefer-installed` | use an installed .NET when it has the tool's framework line (same major.minor), instead of toolrun's private runtime |
| `--runtime-version <v>` | also put this runtime version in the private runtime folder (downloaded once); the host then picks as it normally does |
| `--no-download-runtime` | never download a runtime pack (ASP.NET Core, `--runtime-version`, or a build without an embedded runtime) |
| `--no-apphost` | run `dotnet <entry.dll>` with an installed .NET's muxer instead of an apphost (implies `--prefer-installed`) |
| `--command <name>` | pick a command when a package has several (default: the one named like the package, else the first) |
| `--config <file>`, `--home <dir>` | config file, cache folder |
| `--verbose` | explain decisions (sources, versions, runtime, the final command line) |

What toolrun says goes to **stderr**, prefixed `toolrun:`, and only when it downloads or unpacks something (or with `--verbose`). The exit code is the tool's; toolrun's own
failures exit with **1** (usage errors **2**) and print `toolrun: ...`.

## How it works

The tool is run by **the real .NET host**, exactly as `dotnet tool.dll` or an apphost would run it, so deps.json, probing, native libraries, satellite assemblies, rollForward and
`AppContext.BaseDirectory` behave as they do under dotnet. toolrun's job is to put the pieces in place and start it.

1. **Resolve the tool.** `id@version` is looked up in the cache; with no version the highest cached stable version is used, and only when there is none (or with `--update`)
   is the feed asked for the latest stable (`--prerelease`: including prereleases).
2. **Install** (first use): download the `.nupkg`, pick the best `tools/<tfm>/<rid>/` asset for this machine (highest TFM, `any` or the rid fallback chain; per-RID tool packages
   `<id>.<rid>` are followed), extract it to `tools/<id>/<version>/` as `dotnet tool install` keeps it (`tools/<tfm>/<rid>/…`, the nuspec, plus `toolrun.json`). Natives of other platforms are
   dropped, the `.nupkg` is deleted, and the folder is moved into place atomically.
3. **The private dotnet root**, `<cache>/dotnet`: `host/fxr/<v>/hostfxr` and `shared/<framework>/<v>/…` in an installer's layout. It starts as **toolrun's own runtime** (same version and RID as the
   toolrun executable), unpacked once from a zip resource **embedded in the executable**: no download. ASP.NET Core and WindowsDesktop are downloaded from NuGet the first time a tool needs
   them (version = toolrun's own, which the host reaches by roll-forward) and added to the **same** root, so the host just finds them. Several versions can live side by side.
4. **The apphost.** An apphost for the tool's entry dll is created once with `HostWriter.CreateAppHost` (from the generic apphost that is embedded too, or an installed SDK's host pack, or the
   host pack from NuGet) and stored next to the dll, so the process carries the tool's name. toolrun starts it as a child with `DOTNET_ROOT` (and `DOTNET_ROOT_<ARCH>`) set to the private root.
   (The root has no `dotnet` muxer — the runtime pack does not contain one — so `dotnet exec` is not an option there; the apphost is the cleaner way anyway.)
5. **Roll-forward.** The host resolves the frameworks. A tool built for net8 or net9 on the net10 root needs roll-forward across majors; unless the tool's own `rollForward` already allows it,
   toolrun sets **`DOTNET_ROLL_FORWARD=Major`**, and *only* when the tool's framework (honouring its rollForward) is not in the root: the variable overrides the tool's runtimeconfig, so it is not set when
   the root fits. `--verbose` says which. A tool newer than toolrun's runtime (net11 on a net10 toolrun) is a clear error.
6. **Run.** Arguments, stdin/stdout/stderr, the working directory and the exit code are passed through unchanged. While the child runs toolrun ignores Ctrl+C (on a terminal the child gets it with the
   process group); SIGTERM, SIGHUP and SIGQUIT sent to toolrun are forwarded to the child, and so is SIGINT when stdin is not a terminal. A tool killed by a signal gives 128+signal. (toolrun could
   `exec()` itself away on Unix; it does not, to keep one code path with Windows.)

`--prefer-installed`: when an installed dotnet root (`DOTNET_ROOT`, `dotnet` on PATH, the default install folders) holds every framework the tool needs at its major.minor or newer patch, that root is used
instead and the private runtime is not touched. If the apphost cannot be made and that root has a `dotnet` muxer, `dotnet <entry.dll>` is run instead.

`toolrun --which pkg` does the same without downloading runtimes or writing the apphost (it only installs the tool package, which it must read) and prints
`DOTNET_ROOT=… [DOTNET_ROLL_FORWARD=Major] <apphost> <args>` (`set "…" && …` on Windows); the runtime decisions go to stderr as `# …` lines.

### Embedding the runtime: size cost

The runtime pack of toolrun's own version and RID (linux-x64, .NET 10.0.12) goes into the executable as a compressed resource, together with the 70 KB generic apphost. Measured:

| build | size | first tool run |
| --- | --- | --- |
| `-p:EmbedRuntime=false`, no compression | 74.0 MB | downloads the 40.1 MB runtime pack |
| embedded, no compression | 111.1 MB (+37.1 MB, +50%) | unpacks, no network |
| **embedded + `EnableCompressionInSingleFile` (the release default)** | **76.6 MB** (+2.6 MB over the plain build) | unpacks, no network |
| `-p:EmbedRuntime=false`, compressed | 39.4 MB | downloads the runtime pack |

The embedded zip is about 37 MB (the runtime pack itself is 40 MB as a download), but toolrun's own bundle already contains the same assemblies; compressing the bundle gives that space back, at a
start-up cost of about 45 ms (`toolrun --version`: 32 ms plain, 78 ms compressed). So embedding is the default: the executable stays about as large as the plain build and a first run needs no network for the runtime.
ASP.NET Core is **not** embedded: its pack is 12.9 MB to download (25.8 MB unpacked), used by few tools; embedding would add about 13 MB for everyone.
A build made with `dotnet build` (no runtime identifier) embeds nothing and uses the same download path.

### Cache

`%LOCALAPPDATA%\toolrun` on Windows, `~/.local/share/toolrun` on Linux and macOS (.NET's LocalApplicationData; `XDG_DATA_HOME` is honoured); `TOOLRUN_HOME` or `--home` overrides it.

```
toolrun/
  tools/<id>/<version>/        tools/<tfm>/<rid>/…, <id>.nuspec, toolrun.json, the tool's apphost (beside its entry dll)
  dotnet/                      the private dotnet root: host/fxr/<v>/, shared/Microsoft.NETCore.App/<v>/, shared/Microsoft.AspNetCore.App/<v>/ …
  apphost/                     the generic apphost template
  packages/                    downloads in flight (emptied afterwards)
  config.json                  optional
```

### Feeds

Order of precedence: `--source` flags, env `TOOLRUN_SOURCE` (`;` separated), `config.json` (`{ "sources": ["https://…"], "runtimeSources": ["https://…"] }`), the
`packageSources` of the **nearest nuget.config files** from the working directory upwards (nearer files win, `<clear/>` and disabled sources are honoured), and finally
**nuget.org: the v3 API first, and the v2 API (`https://www.nuget.org/api/v2`) when v3 fails** (some networks refuse `api.nuget.org`). nuget.org's own v3 URL in a config
keeps that fallback. With several sources the versions are the union and a download comes from a source that lists the version; a source that is down is skipped.
Runtime and host packs come from the same sources unless `--runtime-source` (or `TOOLRUN_RUNTIME_SOURCE`, config `runtimeSources`) says otherwise: a private feed that does not mirror nuget.org needs that.

## Distribution

```
dotnet publish ToolRun/ToolRun -c Release -r linux-x64 -o out      # win-x64, osx-arm64 likewise
```

Giving a runtime identifier turns on `SelfContained`, `PublishSingleFile`, compression and the embedded runtime (see the csproj): one executable, 76.6 MB for linux-x64, that needs no .NET. Build with the
.NET 10 SDK: `Microsoft.NET.HostModel` is taken from it, as in Tool2App. Cross-publishing works from Linux, including the embedded runtime and apphost for the target RID (checked for win-x64 and osx-arm64), but a macOS arm64 executable must be ad-hoc signed to run, which the SDK does only on a Mac: the release workflow builds osx-arm64 on a macOS runner. The macOS binary is not notarised.
toolrun can also be turned into a standalone app by **tool2app** (`tool2app toolrun --single-file`, once the package is on a feed), or installed as a dotnet tool (`dotnet pack ToolRun/ToolRun`; that needs .NET and embeds nothing).

## Tests

`dotnet test ToolRun/ToolRun.Tests` (101 tests, about 10 s): argument splitting, version resolution (latest stable, prerelease, explicit, `--update`), cache layout and reuse with the network
proven unused, roll-forward tables, the private root (layout, reuse, ASP.NET Core added to the same root, `--runtime-version`, `--no-download-runtime`, an embedded runtime and apphost needing no network, which
tools get `DOTNET_ROLL_FORWARD`), `--prefer-installed`, the `--which` dry run, nuget.config / config / env precedence, the v3 to v2 fallback, a multi-source feed — against a loopback NuGet v2/v3 server with fake runtime and host
packs — and **real processes through the real host**: `toolrun.dll` installs the compiled TestChild program as a tool and runs it through an apphost on a private root made of this machine's runtime
(with `DOTNET_ROOT` cleared), checking exit codes, argument fidelity, stdin/stdout/stderr, working directory, `AppContext.BaseDirectory`, entry assembly, an unhandled exception, a net8/net9 tool rolled forward onto the
net10 root, and SIGTERM/SIGINT forwarding (Unix only). Those run only where an SDK host pack exists next to the runtime (every machine that can run `dotnet test`). Paths use `Path` functions; no shell scripts are involved.

## Limits

- Tools only: packages with a `dotnet` runner command (all `dotnet tool` packages). Not a general NuGet package runner.
- Sources must be http(s) NuGet v3/v2; local folder sources and feed credentials are not supported (nuget.config local paths are skipped with a warning). No package signature or hash verification.
- The private runtime is toolrun's own version. A tool newer than it fails with a message; update toolrun, or use `--prefer-installed` with a newer .NET.
- ASP.NET Core and WindowsDesktop need one download the first time (WindowsDesktop on Windows only). Per-tool startup settings are the host's, so everything in the tool's runtimeconfig applies.
- A tool runs on a newer major than it was built for (net8 on net10) through roll-forward, as with any `DOTNET_ROLL_FORWARD=Major` run; a tool that breaks on a newer runtime needs `--prefer-installed` with its own .NET or `--runtime-version`.
- Installs are extract-then-move: on Windows a virus scanner or indexer can briefly hold the freshly extracted files, so every move (tool folder, runtime folders, apphost) is retried 10 times over about 5 s on access/IO errors, a finished install that another process put there meanwhile is accepted, and as a last resort the folder is copied instead.
- Tools shipping native executables beside the dll lose their execute bit on Linux/macOS (zip extraction); the entry apphost is fixed up.
- **macOS is untested.** Nobody has run the test suite on a Mac, so the release workflow runs the tests on Linux and Windows only and just builds osx-arm64 (plus a smoke test of the published file and a real tool, which may fail without blocking the release). Apphosts made on macOS from a real Mach-O host are ad-hoc signed by HostModel, as arm64 requires; a template that is not Mach-O is never signed.
- toolrun does not self-update, and `--update` never removes older cached versions (`--clean <id>` does).
