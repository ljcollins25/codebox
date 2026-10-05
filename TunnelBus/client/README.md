# tbus - tunnel bus client

**Download:** the latest tbus build for Windows (`tbus.exe`) and Linux (`tbus`) is the [`tbus-latest` release](https://github.com/ljcollins25/codebox/releases/tag/tbus-latest) (single-file, no .NET install needed).

`tbus` links a local port (or a remote host:port reachable from this machine) to the tunnel bus, so you can test the bus and its dashboard with any dev server:

```
tbus share 3000 --name myapp          -> https://myapp.ref12.dev/
tbus share 3000                       -> https://<machine>-3000.ref12.dev/
tbus share 192.168.1.20:8080 --name nas   (a remote target: chisel's reverse remote dials that host from this machine)
tbus share web=3000 api=3001          several shares in one process
tbus list | tbus stop <name> | tbus open <name>
```

## Install (Windows, PowerShell)

Published as a **self-contained single file** (the same convention as the Hermes CLI: net10.0, `PublishSingleFile`, `SelfContained`), about 39 MB, no .NET needed on the target. A framework-dependent dotnet tool is available with `-p:_IsPacking=true`.

```powershell
dotnet publish TunnelBus/client/Tbus -c Release -r win-x64 -o $env:LOCALAPPDATA\tbus\bin
[Environment]::SetEnvironmentVariable('Path', $env:Path + ";$env:LOCALAPPDATA\tbus\bin", 'User')   # then open a new terminal
tbus login --service-token      # prompts (no echo) for CF-Access-Client-Id and -Secret; stored with DPAPI
tbus login --admin-token        # prompts for the bus admin token; stored with DPAPI
tbus share 3000 --name myapp
```
Linux/macOS: `-r linux-x64|linux-arm64|osx-x64|osx-arm64`; secrets go into a 0600 file instead of DPAPI (no keychain dependency).
Alternatively skip `login` and set `TUNNEL_BUS_ADMIN_TOKEN`, `CF_ACCESS_CLIENT_ID`, `CF_ACCESS_CLIENT_SECRET`; environment wins over stored values.

## Commands

| Command | |
|---|---|
| `share <target>... [--name N]` | Foreground. Prints registering / connected / the public URL, reconnects with backoff (1 s doubling to 30 s), re-registers when the bus forgot the name (checked every 10 s) or chisel died. Ctrl+C unregisters (best effort). Target: `PORT`, `HOST:PORT`, `[IPV6]:PORT`, optionally `NAME=target`. Names: `[a-z0-9]+(-[a-z0-9]+)*`, max 40. |
| `list` | The router's registry: name, server port, up, URL. |
| `stop <name>` | Unregisters; a `tbus share` of that name in another process notices (marker file) and ends instead of re-registering. |
| `open <name>` | Opens `https://<name>.<domain>/`. |
| `config [--bus URL] [--domain D]` | Show/set the bus URL (default `https://ctl.ref12.dev`) and base domain (default `ref12.dev`); also env `TUNNEL_BUS_URL`, `TUNNEL_BUS_DOMAIN`. |
| `login`, `login --service-token`, `login --admin-token`, `logout` | See below. |

State lives in `%LOCALAPPDATA%\tbus` (`$TBUS_HOME` overrides): `config.json`, `secrets.json`, `chisel/1.10.1/<os_arch>/`.

## chisel

`TBUS_CHISEL` (a path), else `chisel` on PATH, else downloaded once from the pinned release v1.10.1 for this OS/architecture, **SHA-256 checked** against hashes pinned in the source (a mismatch is refused, nothing is cached), and cached in the state folder. Each share runs its own chisel client because each registration has its own chisel user, limited to its own port.

## Keeping secrets off the command line

* chisel gets its credentials through its `AUTH` environment variable, never `--auth`. It also does not inherit `TUNNEL_BUS_ADMIN_TOKEN` or `CF_ACCESS_*`.
* chisel only takes headers as `--header` arguments, so tbus runs a **loopback relay** (like hexad's bus transport, `BusRelay`): chisel is started with `http://127.0.0.1:<port>/<24 random hex>/_chisel`; the relay answers only below that random prefix, rewrites the request head (path, `Host`, strips any client `cf-access-*` header, adds the Access headers) and then pipes the WebSocket bytes to the bus over TLS. The registration/list/unregister calls are made by tbus itself with the headers on the request. Result: no secret in any process's argument list.
* All output passes a redactor that masks every secret tbus knows (admin token, Access id/secret, JWT, chisel passwords).

## Login

* **Service token** (works everywhere): env, or `tbus login --service-token`.
* **Browser login**: `tbus login` shells out to **cloudflared** if it is on PATH: `cloudflared access login <bus>` opens the browser, you sign in with GitHub on Access, cloudflared's token transfer hands back a JWT; `cloudflared access token -app=<bus>` prints it; tbus stores it (DPAPI / 0600) and sends it as `cf-access-token`, ignoring it after its `exp`. I did not reimplement the token-transfer protocol: it uses an NaCl box exchange with Cloudflare's transfer service that .NET has no primitive for, and it is not a documented stable API, so cloudflared is the supported route. Without cloudflared `tbus login` explains and points to the service token. **Not exercised against the live Access app** (needs an interactive GitHub sign-in); tested with a fake cloudflared.
* **Admin token**: still required to register (env `TUNNEL_BUS_ADMIN_TOKEN` or `tbus login --admin-token`). Identity-based registration from the dashboard work had not landed when this was written, so it is not used; when it does, a browser-login-only path can replace the admin token.

## Tests

`cd TunnelBus/client/Tbus.Tests && dotnet test` (xunit): argument parsing, a fake bus (HttpListener: register, registry, unregister), a fake chisel (a shell script, a `.cmd` on Windows), register/connect/re-register after a bus restart, backoff, several shares, `stop`, secrets absent from chisel args/environment/output, credential round-trips (DPAPI on Windows, 0600 file on Unix), relay header injection, chisel download verification. Paths are built with `Path` functions; the Windows variants (DPAPI, `.cmd` fakes) have not been run here: the runner is Linux.
