# Tunnel bus

A general facility: services behind NAT (a runner, a laptop, a sandbox) connect **outbound**, register a name,
and are reachable through one public front door: `https://tunnel-bus.<subdomain>.workers.dev/<name>/...`.

```
provider (stock chisel client) ──wss──►  Worker ─► Durable Object ─► container ─┬─ router (Go, :8080)
browser / curl ────────────────https──►                                          └─ chisel server (:8081, --reverse)
```

* **Worker + Durable Object** (`worker/`): every request goes Worker → DO → container (`@cloudflare/containers`). Nothing is stored; no SQL is used.
* **Container** (`Dockerfile`, `router/`, instance type `lite`, 256 MiB): the router (Go, stdlib only) and a stock `chisel server --reverse --authfile`. The router starts and supervises chisel.
  Go was chosen because it is one static binary using a few MB of RAM, `httputil.ReverseProxy` handles HTTP, SSE (flush every write) and WebSocket upgrades, and `go test` runs the same on Windows and Linux.
* **Router API** (admin token as `Authorization: Bearer`):
  `POST /_api/register {"name":"x"}` → `{port,user,password,consumer,...}` (idempotent), `DELETE /_api/register/x`, `GET /_api/registry`; open: `GET /_health`.
  `/_chisel` is chisel's websocket endpoint (prefix stripped); any other `/<name>/...` is forwarded with `/<name>` stripped and `X-Forwarded-Prefix: /<name>` set. The `Host` header is preserved.
* **Authfile**: the router rewrites chisel's authfile on every change. chisel reloads it with fsnotify on *Write* events only, so the file is rewritten **in place** (an atomic rename would end the watch). A sentinel user is always present because chisel disables auth when the user list is empty. Each provider may open only `R:…:<its port>`; a separate per-name *consumer* user may only forward to that port.
* **Credentials** are an HMAC of the name keyed by the admin token, so they are stable across container restarts. Registrations themselves are live in-memory state: providers re-register after a restart (the provider scripts do this on their own).

## Deploy

```
cd TunnelBus/worker
npm ci
printf %s "$ADMIN_TOKEN" | npx wrangler secret put ADMIN_TOKEN   # random token, keep it
npx wrangler deploy                                              # needs Docker; Workers Paid plan
```
The API token needs Workers Scripts edit and Containers (Cloudchamber) edit. Only `tunnel-bus*` resources are created.
`wrangler.jsonc` uses a `new_sqlite_classes` migration because this account no longer allows key-value-backed Durable Object classes; no SQL is executed.

## Add a provider (one command)

```
export TUNNEL_BUS_ADMIN_TOKEN=...
./provider/tunnel-bus-provider.sh --bus https://tunnel-bus.<sub>.workers.dev --name myapp --port 3000
pwsh ./provider/tunnel-bus-provider.ps1 -Bus https://tunnel-bus.<sub>.workers.dev -Name myapp -Port 3000
```
The script downloads chisel 1.10.1 into a cache folder if it is not on PATH, registers, then runs
`chisel client --keepalive 25s --auth user:pass <bus>/_chisel R:<port>:localhost:<app port>`; if chisel exits it registers again and reconnects.
Names: `[a-z0-9][a-z0-9-]*`, up to 41 characters.

## Consume

* **Browser / HTTP / SSE / WebSocket:** `https://<bus>/<name>/...`. Apps see paths without the prefix; absolute links (`/assets/x.js`) in the app's HTML will not carry the prefix — use relative links or honour `X-Forwarded-Prefix` until subdomain routing exists.
* **TCP** (database, SSH, ...): use the `consumer` credentials from the register response:
  `chisel client --auth c-<name>:<password> https://<bus>/_chisel 5432:127.0.0.1:<port>` and connect to `localhost:5432`.

## Tests

`cd router && go test ./...` (Windows and Linux; paths via `filepath`, temp dirs via `t.TempDir()`).

## Limits

* One container instance, 256 MiB; ports 20000–20999 (up to ~1000 names, a config change away from more).
* Registrations are lost when the container restarts or sleeps (`sleepAfter` 30m of no *request* activity); providers re-register. See the pass-through results below for what idle chisel connections do.
* One shared admin token registers any name; per-provider isolation is chisel-level only. Any name holder can be impersonated by the admin token holder.
* Path routing only; no wildcard on workers.dev.
* Cloudflare request limits apply (e.g. request body size per plan).

## Cost

Workers Paid ≈ $5/month, plus container time while awake (`lite` is billed per 10 ms of active CPU/memory/disk — a few cents a day when mostly idle) and Worker/DO requests (WebSocket messages count as DO requests at a reduced ratio). Check the Cloudflare pricing page for current numbers.

## To do

* Custom domain with `*.bus.<domain>` subdomain routing (add a Host check in `Router.Resolve`; the Worker already forwards everything).
* Cloudflare Access in front of the custom domain; or validate `Cf-Access-Jwt-Assertion` in `worker/src/index.ts` (marked there) before forwarding. Provider chisel clients would use an Access service token header.
* Per-provider API tokens instead of one admin token.
* hexad integration (replace its dev tunnel with a bus provider).
