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

**Client app:** `TunnelBus/client` is `tbus`, a .NET command-line client (`tbus share 3000 --name myapp`, `list`, `stop`, `open`), the easy way to link a local port; see its README.

## Consume

* **Browser / HTTP / SSE / WebSocket:** `https://<bus>/<name>/...`. Apps see paths without the prefix; absolute links (`/assets/x.js`) in the app's HTML will not carry the prefix — use relative links or honour `X-Forwarded-Prefix` until subdomain routing exists.
* **TCP** (database, SSH, ...): use the `consumer` credentials from the register response:
  `chisel client --auth c-<name>:<password> https://<bus>/_chisel 5432:127.0.0.1:<port>` and connect to `localhost:5432`.

## Host routing (own domain)

Provider hosts are single labels directly under the base domain:

| Host | Goes to | Provider sees |
|---|---|---|
| `hexad.ref12.dev` | provider `hexad`, path unchanged | `Host` and `X-Forwarded-Host` = the public host |
| `3000--hexad.ref12.dev` | provider `hexad` | same, plus `X-Bus-Host-Prefix: 3000` |
| `anything--hexad.ref12.dev` | provider `hexad` | prefix = `anything` |

The provider name is whatever follows the **last** `--`; everything left of it passes through (read it from `X-Forwarded-Host` or `X-Bus-Host-Prefix`), so hexad can send `3000--hexad.<base>` to its own sandbox port 3000. Names therefore may not contain `--` (`[a-z0-9]+(-[a-z0-9]+)*`, max 40). Deeper hosts (`a.hexad.ref12.dev`) are not routed. `workers.dev` keeps path routing (`/<name>/`): the router uses host routing only when `BUS_BASE_DOMAIN` is set and the request's host is under it.
The control host (`BUS_CONTROL_HOST`, `ctl.<base>` by default) serves `/_api`, `/_chisel`, `/_health`; use it as the provider's `--bus` URL. The Worker sends the public host to the router in `X-Bus-Host` (overwriting any client value). Config: `BUS_BASE_DOMAIN`, `BUS_CONTROL_HOST`, `BUS_LABEL_SUFFIX` in `worker/wrangler.jsonc` `vars`.

**Recommended: a dedicated zone, names directly under it** (`hexad.ref12.dev`, `3000--hexad.ref12.dev`, `ctl.ref12.dev`).
Why (Cloudflare docs): Universal SSL covers the apex and first-level subdomains (`*.ref12.dev`) only; `*.bus.ref12.dev` needs Advanced Certificate Manager (extra monthly cost). Single-label hosts under the zone need only a proxied wildcard DNS record, one Worker route `*.ref12.dev/*` and the free Universal certificate. A wildcard DNS record would also match deeper names, but the certificate would not cover them, which is why the scheme uses `--` and not dots. Any plan can create and proxy wildcard DNS records.

**Shared zone** (other sites live there): Worker route hostnames may begin with `*` or `*.` but cannot have a wildcard in the middle (`ref12.dev/*.jpg` and infix wildcards are invalid), and a wildcard may be followed by text (`*-bus.ref12.dev/*` is a valid pattern). So set `BUS_LABEL_SUFFIX=-bus`: hosts are `hexad-bus.ref12.dev`, `3000--hexad-bus.ref12.dev`, `ctl-bus.ref12.dev`, covered by the route `*-bus.ref12.dev/*` and still by Universal SSL. Exact DNS records and more specific routes win over the wildcard record and route (the most specific pattern wins), so existing sites keep working. The alternative is ACM with `*.bus.ref12.dev`.

### ref12.dev (checked read-only with the API token, 2026-10-05)

The zone is **empty and dedicated** (Free plan, active, no DNS records; the token cannot list Worker routes yet, it lacks Zone → Workers Routes). So the recommended scheme applies as is: **names directly under the zone, no suffix**:
`hexad.ref12.dev`, `3000--hexad.ref12.dev`, control host `ctl.ref12.dev`; one proxied wildcard record `*`, one route `*.ref12.dev/*`, one Access application `*.ref12.dev`, free Universal SSL.
`BUS_BASE_DOMAIN=ref12.dev` and `BUS_CONTROL_HOST=ctl.ref12.dev` go in `wrangler.jsonc` (`domain-setup --write-config` sets them). Nothing has been created in the zone yet. If the zone later hosts other sites, switch to the `-bus` suffix scheme described above (the previously checked ref12.dev zone was shared, with tunnels and Email Routing records, which is why the suffix was proposed there).

### Live configuration (2026-10-05)

Base domain `ref12.dev` (dedicated zone, empty before). Wildcard `AAAA *.ref12.dev -> 100::` (proxied), route `*.ref12.dev/*` -> `tunnel-bus`, control host `ctl.ref12.dev`. Access: team `laxid.cloudflareaccess.com`, application `tunnel-bus` for `*.ref12.dev` with GitHub as the only login method, an allow policy for one email and a service-token policy (token `tunnel-bus-hexad`); `ACCESS_REQUIRED=true`. workers.dev is closed (403) because it carries no Access JWT.

### Setup

```
export CLOUDFLARE_API_TOKEN=...   # Zone:Read, DNS:Edit, Workers Routes:Edit (on the zone)
node scripts/domain-setup.mjs --zone ref12.dev [--suffix -bus] --dry-run
node scripts/domain-setup.mjs --zone ref12.dev [--suffix -bus] --write-config   # sets BUS_* in worker/wrangler.jsonc
cd worker && npx wrangler deploy
```
The script adds a proxied wildcard `AAAA * -> 100::` (placeholder origin; the Worker answers every request) and the route, and refuses to touch a route owned by another Worker. Manual equivalent: DNS → add a proxied `*` record; Workers → tunnel-bus → Settings → Domains & Routes → Add route `*.ref12.dev/*`.

## Cloudflare Access

Access sits in front of the zone pattern; the Worker verifies the `Cf-Access-Jwt-Assertion` JWT again (signature from `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`, issuer, `aud`, `exp`), so a route that bypasses Access, or workers.dev, cannot reach the bus when `ACCESS_REQUIRED=true`. **Nothing is exempt**, not even `/_health` or the registration API: they need a service token like any other caller (the admin token stays a second factor on `/_api`). Config: `ACCESS_REQUIRED`, `ACCESS_TEAM_DOMAIN`, `ACCESS_AUD`.

**You do first (dashboard):**
1. Zero Trust: dashboard → Zero Trust → choose a team name and plan (free plan: up to 50 users). This creates `<team>.cloudflareaccess.com`.
2. Settings → Authentication → Login methods → add one (One-time PIN needs no setup; GitHub needs an OAuth app).
3. Give the API token these permissions (on top of Workers and Containers edit it has): **Account → Access: Apps and Policies → Edit**, **Account → Access: Service Tokens → Edit**, **Account → Access: Organizations, Identity Providers, and Groups → Read**, **Zone → Zone → Read**, **Zone → DNS → Edit**, **Zone → Workers Routes → Edit**.
4. Then:
   ```
   node scripts/access-setup.mjs --zone ref12.dev --email you@ref12.dev --out hexad-token.json [--suffix -bus] --write-config
   ```
   It creates the service token (id/secret only to the file, mode 0600), an "allow me" policy, a service-token (`non_identity`) policy, and one self-hosted application for `*.ref12.dev` (or `*-bus.ref12.dev`) that returns 401 to programs instead of a login redirect. Load `hexad-token.json` into hexad secret variables, delete the file, then set `ACCESS_REQUIRED` to `"true"` and `wrangler deploy`.

**Providers and programs** send the service token: set `CF_ACCESS_CLIENT_ID` and `CF_ACCESS_CLIENT_SECRET`; the provider scripts add the headers to the registration call and pass `--header` to chisel. Browsers log in through Access.

## Tests

`cd router && go test ./...` (Windows and Linux; paths via `filepath`, temp dirs via `t.TempDir()`; the provider-script tests run the bash/PowerShell scripts in dry-run mode and skip a missing interpreter). `cd worker && npm test` (JWT verification with a local signing key and fake certs endpoint). `node --test scripts/scripts.test.mjs` (setup scripts against a fake Cloudflare API).

## Limits

* One container instance, 256 MiB; ports 20000–20999 (up to ~1000 names, a config change away from more).
* Registrations are lost when the container restarts or sleeps (`sleepAfter` 30m of no *request* activity); providers re-register. See the pass-through results below for what idle chisel connections do.
* One shared admin token registers any name; per-provider isolation is chisel-level only. Any name holder can be impersonated by the admin token holder.
* Path routing only; no wildcard on workers.dev.
* Cloudflare request limits apply (e.g. request body size per plan).

## Cost

Workers Paid ≈ $5/month, plus container time while awake (`lite` is billed per 10 ms of active CPU/memory/disk — a few cents a day when mostly idle) and Worker/DO requests (WebSocket messages count as DO requests at a reduced ratio). Check the Cloudflare pricing page for current numbers.

## To do

* Run `domain-setup` and `access-setup` once the domain, Zero Trust org and token permissions exist; flip `ACCESS_REQUIRED`.
* Per-provider API tokens instead of one admin token.
* hexad integration (replace its dev tunnel with a bus provider using service-token headers).

## Measured pass-through results (2026-10-05, from a GitHub Actions runner)

Deployed at `https://tunnel-bus.ref12cf.workers.dev`, test app behind a stock chisel 1.10.1 client.
* Page: 200 through the bus. WebSocket: 50/50 echo round trips (median 138 ms, max 153 ms), and a message after 60 s idle on the same socket worked.
* SSE: 138 s stream, 45 events at 3 s intervals, no stall (max gap 3008 ms); the stream stayed open the whole time.
* Idle: chisel client left idle (keepalive 25 s) for 10 min; afterwards `/demo/ping` still returned 200 and the bus uptime showed no restart.
* Kill chisel client, restart via the provider script (re-register + reconnect): routing back (200) about 1–2 s after the new client connected (first request 502 while down).
* Latency of a small request (40 requests, new TLS connection each, curl): direct 0.3 ms; via the bus median 212 ms, p90 262 ms (the Worker's own `/_health` is 133 ms median, so ~80 ms is Worker→DO→container→chisel→client).
