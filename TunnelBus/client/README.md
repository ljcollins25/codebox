# tbus - tunnel bus client

**Download:** the latest tbus build for Windows (`tbus.exe`) and Linux (`tbus`) is the [`tbus-latest` release](https://github.com/ljcollins25/codebox/releases/tag/tbus-latest) (single-file, no .NET install needed).

`tbus` links a local port (or a remote host:port reachable from this machine) to the tunnel bus, so you can test the bus and its dashboard with any dev server:

```
tbus share 3000 --name myapp          -> https://myapp.ref12.dev/
tbus share 3000                       -> https://<machine>-3000.ref12.dev/
tbus share 192.168.1.20:8080 --name nas   (a remote target: tbus dials that host from this machine)
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
| `share <target>... [--name N]` | Foreground. Prints registering / connected / the public URL, reconnects with backoff (1 s doubling to 30 s), re-registers when the bus forgot the name (checked every 10 s) or the connection dropped. Ctrl+C unregisters (best effort). Target: `PORT`, `HOST:PORT`, `[IPV6]:PORT`, optionally `NAME=target`. Names: `[a-z0-9]+(-[a-z0-9]+)*`, max 40. |
| `list` | The router's registry: name, server port, up, URL. |
| `stop <name>` | Unregisters; a `tbus share` of that name in another process notices (marker file) and ends instead of re-registering. |
| `open <name>` | Opens `https://<name>.<domain>/`. |
| `config [--bus URL] [--domain D]` | Show/set the bus URL (default `https://ctl.ref12.dev`) and base domain (default `ref12.dev`); also env `TUNNEL_BUS_URL`, `TUNNEL_BUS_DOMAIN`. |
| `login`, `login --service-token`, `login --admin-token`, `logout` | See below. |

State lives in `%LOCALAPPDATA%\tbus` (`$TBUS_HOME` overrides): `config.json`, `secrets.json`.

## How it works: the chisel protocol is built in

tbus is one self-contained .NET app; there is **no chisel executable** (nothing is downloaded or run, no child process; Windows Defender flags chisel as a hacktool, so tbus never touches it). It implements the client side of chisel 1.10.x's protocol for reverse remotes and talks to the bus's stock chisel server:

1. **Transport:** a `ClientWebSocket` to `wss://<bus>/_chisel` with subprotocol `chisel-v3` (chisel's `ProtocolVersion`); every write is one binary message. The Access headers (`CF-Access-Client-Id/Secret`, or `cf-access-token`) are put straight on this request.
2. **SSH:** the WebSocket is the byte stream of an SSH client session (`Microsoft.DevTunnels.Ssh`, MIT): key exchange, then password auth with the registration's user and password. Optional host-key fingerprint check (chisel's base64 SHA-256 form, or the legacy MD5 prefix) is in `ChiselOptions.Fingerprint`.
3. **Config:** a `config` global request (want reply) with the JSON `{"Version":"1.10.1","Remotes":[{"LocalHost":"0.0.0.0","LocalPort":"<bus port>","LocalProto":"tcp","RemoteHost":"<host>","RemotePort":"<port>","RemoteProto":"tcp","Socks":false,"Reverse":true,"Stdio":false}]}` as raw payload. The server checks `R:0.0.0.0:<port>` against the authfile and answers success, or failure with the reason as text (shown in the error).
4. **Traffic:** for each connection to the remote's port the server opens an SSH channel of type `chisel` whose extra data is `host:port`. tbus dials the target and copies both ways. A target that refuses the connection **rejects the channel** (the dial gets 2 s before the open is answered; a slower target is accepted and closed if the dial then fails). SSH channel windows give backpressure. The SSH library has no channel EOF, so when the target closes, the channel is closed after its data (chisel's own pipe does the same); an EOF from the tunnel side is passed on as a half-close to the target.
5. **Keepalive:** a `ping` global request every 25 s (answered `pong`), and incoming `ping`s are answered. A lost connection returns to the share loop: backoff, re-register, reconnect.

Each share has its own connection because each registration has its own chisel user, limited to its own port.

Two details of chisel that Microsoft.DevTunnels.Ssh does not accept as they are, handled in `WebSocketStream` / `ChiselSshCompat` (two small reflection patches on the pinned package version 3.12.42, covered by the tests against a real chisel server): chisel's server banner is `SSH-chisel-v3-server` (no protocol version), so `2.0-` is inserted for the library while the original text is put back into the key-exchange hash; and rejecting a channel makes the library send a channel-close for the id that never opened, which Go's SSH drops the whole connection for, so that message is suppressed.

`TBUS_SSH_TRACE=1` prints the SSH library's protocol trace (no passwords) to stderr when debugging.

## Keeping secrets off the command line

Credentials never leave the process: there is no child process and no local relay. The chisel password and the Access headers are used in memory only. All output passes a redactor that masks every secret tbus knows (admin token, Access id/secret, JWT, chisel passwords).

## Login

* **Service token** (works everywhere): env, or `tbus login --service-token`.
* **Browser login**: `tbus login` shells out to **cloudflared** if it is on PATH: `cloudflared access login <bus>` opens the browser, you sign in with GitHub on Access, cloudflared's token transfer hands back a JWT; `cloudflared access token -app=<bus>` prints it; tbus stores it (DPAPI / 0600) and sends it as `cf-access-token`, ignoring it after its `exp`. I did not reimplement the token-transfer protocol: it uses an NaCl box exchange with Cloudflare's transfer service that .NET has no primitive for, and it is not a documented stable API, so cloudflared is the supported route. Without cloudflared `tbus login` explains and points to the service token. **Not exercised against the live Access app** (needs an interactive GitHub sign-in); tested with a fake cloudflared.
* **Admin token**: still required to register (env `TUNNEL_BUS_ADMIN_TOKEN` or `tbus login --admin-token`). Identity-based registration from the dashboard work had not landed when this was written, so it is not used; when it does, a browser-login-only path can replace the admin token.

## Tests

`cd TunnelBus/client/Tbus.Tests && dotnet test` (xunit). The protocol tests run against a **real chisel 1.10.1 server**: the release binary is downloaded once into a cache (`~/.local/share/tbus-test-cache`, or `$TBUS_TEST_CACHE`), SHA-256 checked, and the tests are skipped when it cannot be had and **always on Windows** (Defender blocks the executable; the tests that need it report that as the skip reason) (`TBUS_TEST_CHISEL` points at an existing binary). They cover: reverse remote round trip, 100 concurrent connections with their own data, 50 MB each way with checksums and a stalled consumer (backpressure), WebSocket through the tunnel, authfile refusal of a disallowed remote (server's reason shown) and a wrong password, a refusing target (channel rejected, tunnel stays up), half-close, keepalive both ways, and reconnect after a server restart. The share-flow tests put a fake bus (HttpListener router API plus a `/_chisel` websocket forwarder that can demand Access headers) in front of that server: register/connect/traffic/unregister, Access headers on the websocket handshake, bus restart with re-registration, backoff, several shares, `stop`, `list`/`open`, secrets absent from output. Credential store tests use DPAPI on Windows and a 0600 file elsewhere.

## Benchmark (Tbus.Bench)

`TunnelBus/client/Tbus.Bench` compares the built-in client with the chisel 1.10.1 *client* executable (benchmark only, never shipped or used by tbus) against the same chisel server: `Tbus.Bench run --chisel <path> --scope local|bus [--mb N] [--sweep]`. Local (200 MB, loopback, 4 cores): chisel client 180-190 MB/s sending / 190-220 receiving at 70-150% CPU; tbus 140-160 MB/s sending / 350-500 MB/s receiving at 130-200% CPU; connection setup 42 ms (chisel) vs 270 ms (tbus, mostly .NET start-up and JIT); small requests about 1 ms for both. Window size, WebSocket and copy buffers and GC mode moved the numbers by less than the run-to-run noise (about 10%). Through the bus the container is the limit (lite: about 5 MB/s, basic: 17-25 MB/s, for both clients, with the client under 35% of a core).
