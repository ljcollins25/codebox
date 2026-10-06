# r2pipe

A bulk data pipe between two machines that can only connect **outbound**. **Cloudflare R2** carries the bytes (no egress fees, no Worker or container CPU on the data path), a **Worker + Durable Object** does the signaling and carries the first bytes. A sibling of the tunnel bus (`TunnelBus/`); it reuses its domain (`ref12.dev`) and its Cloudflare Access application (`*.ref12.dev`, GitHub login for browsers, service token for programs).

```
sender ──ws: first ≤1 MiB inline──► Transfer DO ──ws──► receiver          (control + first bytes: one DO round trip)
sender ──PUT part N (presigned)──► R2 ◄──GET part N (presigned)── receiver (bulk: straight to R2's S3 endpoint)
sender ──"part N done"──► DO ──push state + GET URL──► receiver ──ack N──► DO deletes the part
```

## Parts

| | |
|---|---|
| `worker/` | Worker `r2pipe` (TypeScript, `aws4fetch`), Durable Objects `Transfer` (one per transfer), `Registry` (list for `ls`), `Provider` (HTTP front). Plain DO key-value storage, no SQL. |
| `client/R2Pipe` | .NET 10 single-file CLI `r2pipe` (win-x64 and linux-x64) |
| `client/R2Pipe.Tests` | xUnit: part scheduling, slow start, inline, resume, checksums, secrets (DPAPI test runs on Windows only) |
| `bench/bench.sh` | send + recv in two processes on one machine, prints one JSON line |

## Setup

```
cd R2Pipe/worker && npm ci && npx vitest run && npx tsc -p .
# bucket (name r2pipe) and a 24 h lifecycle rule (objects and unfinished multipart uploads) were created through the API
export CLOUDFLARE_API_TOKEN=... CLOUDFLARE_ACCOUNT_ID=...      # token: Workers Scripts edit, R2 edit, zone Workers Routes edit
npx wrangler deploy
# secrets (never printed):
#   R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY   -> presigned mode; without them the Worker runs in binding mode
#   ADMIN_TOKEN                                             -> bearer token for workers.dev testing (Access cannot reach workers.dev)
```
**R2 S3 credentials from an account API token** (Cloudflare's documented scheme): access key id = the token's id (`GET /accounts/<id>/tokens/verify`), secret access key = lowercase hex SHA-256 of the token value. The token needs R2 read/write on the bucket. Alternatively create a dedicated "Object Read & Write" R2 token scoped to the bucket in the dashboard and use its key pair.
Routes (zone `ref12.dev`): `pipe.ref12.dev/*` and `*--pipe.ref12.dev/*` → `r2pipe`; the existing wildcard DNS record covers both names. Access protects both (the Worker verifies the JWT as well). Programs send `CF-Access-Client-Id/Secret`.

## Use

```
export CF_ACCESS_CLIENT_ID=... CF_ACCESS_CLIENT_SECRET=...       # or: r2pipe login --client-id .. --client-secret ..  (DPAPI on Windows, mode 0600 elsewhere)
r2pipe send big.tar --name big.tar            # prints the id (stdout) and the receive command (stderr); '-' reads stdin
r2pipe recv <id> -o big.tar                   # '-o -' writes to stdout; no -o: the sender's file name
r2pipe ls                                     # open transfers
r2pipe abort <id>
r2pipe send big.tar --resume <id>             # continue an interrupted send (parts the server holds with the same SHA-256 are skipped)
r2pipe serve 3000 --name myapp                # HTTP front, see below
```
Options: `--parallel N` (default 4; 8 is better on a fast link), `--part-size 32M` (the largest part), `--mode presigned|binding`, `--quiet`, `--stats file.json`. `R2PIPE_URL` selects the Worker (default `https://pipe.ref12.dev`).
A killed receiver resumes from `<out>.r2pipe` (the list of parts already written and acked); it verifies the whole output file's SHA-256 at the end.

## How it works

* **Create** `POST /t {name,size,partSize,mode}` → id. State (`open → complete → done`, or `aborted`/`expired`) lives in the Transfer DO; an alarm wipes everything after 24 h (and the bucket lifecycle rule is the backstop).
* **Inline first bytes.** The sender opens a WebSocket to the DO (`/t/<id>/ws?role=send`) and sends up to 1 MiB as binary frames (8-byte big-endian offset + bytes, 256 KiB each). The DO forwards each frame to the receivers' sockets before storing it (the store allows late joiners to replay; the sender gets an `inline-ack`). Messages are far below the DO WebSocket limit (32 MiB documented); I did not probe the limit. A transfer of ≤ 1 MiB never touches R2.
* **Parts.** The rest is cut into parts that **start small and double** (1, 2, 4 … up to `--part-size`), each at an exact stream offset (the first part starts where the inline data ends; `complete` checks that the parts are contiguous, that the sizes add up, and that the inline length matches). PUT URLs are requested 8 at a time; a retry asks for a fresh URL. The sender reports each part (`done`: offset, size, SHA-256); the DO checks the object in R2 with the binding (`head`: size must match) and pushes it, **with its presigned GET URL**, to the receiver's WebSocket.
* **Receiver.** Opens the WebSocket (state + inline replay + pushes; it falls back to long polling `/state?since=V&wait=S` if it cannot connect), downloads up to N parts at once into pooled buffers, verifies each SHA-256, writes in stream order, acks (the DO deletes the object), and at the end checks the total size and the overall SHA-256 (taken over the stream in order).
* **Modes.** *presigned*: clients talk to `<account>.r2.cloudflarestorage.com` directly (UNSIGNED-PAYLOAD query signing, 1 h; the Access headers are never sent to R2). *binding*: parts go through the Worker (`PUT/GET /t/<id>/parts/<n>/data`, R2 binding). Parts are separate objects, not an S3 multipart upload: multipart parts cannot be read before `CompleteMultipartUpload`, which would break streaming.
* **Auth.** Access JWT (verified in the Worker: signature, issuer, audience, expiry) or `Authorization: Bearer <ADMIN_TOKEN>`; `/_health` is open on workers.dev only.

## HTTP front (a container-free tunnel for HTTP)

`r2pipe serve <port> --name <n>` keeps a WebSocket to the Provider DO `<n>`. A visitor's request to `https://<n>--pipe.ref12.dev/<path>` (or `https://pipe.ref12.dev/p/<n>/<path>`, also on workers.dev with the bearer token) goes Worker → Provider DO → provider as a JSON frame (method, path, headers; `Host` and Access credentials are stripped; `X-Forwarded-Host/Proto/Prefix` are set). A request body streams: the first 1 MiB inline, the rest uploaded to R2 by the Worker in 8 MiB parts (presigned GET URLs go to the provider). The provider calls the local app, answers with `res` (status, headers, length), the first 1 MiB of the body inline, then R2 parts (1 MiB doubling to 32 MiB, up to `--parallel` uploading at once; PUT URLs requested over the same socket). The Worker streams the response to the visitor: the inline bytes first, then each part read through the R2 binding in order (the next one is opened while the current streams), and deletes the objects at the end. Not done: WebSocket and SSE through this path (and trailers, `Expect`, HTTP/2 push). SSE could be a stream of inline chunks flushed as they arrive (no R2); WebSockets framed messages through the DO (kind byte + connection id + opcode), both ways. Limits seen: request-to-first-header timeout is 90 s in the DO; a visitor that disconnects aborts the request on the provider; a provider reconnect fails the requests in flight (502).

## Measurements

Runner: GitHub Actions ubuntu-latest (2026-10-05), 1 GiB random file, send and recv in two processes at once on the runner (the data goes runner → R2 → runner), 32 MiB largest part, slow start. Single runs; the link, not r2pipe, is the variable (the numbers moved ±30 % between runs).

**Pipeline throughput** (1 GiB ÷ wall time of send+recv, MB/s = 10^6 bytes/s):

| mode | 1 in flight | 4 | 8 |
|---|---|---|---|
| presigned (before inline/slow start, workers.dev) | 22.0 | 55.5 | **106.4** (send 153, recv 148) |
| presigned (final, workers.dev) | – | 58.8 | 76.6 (send 111, recv 93) |
| presigned (final, `pipe.ref12.dev` through Access) | 16.2 | 59.6 | **92.7** (send 131, recv 127) |
| binding (through the Worker, before inline) | 15.4 | 38.8 | 63.8 (final code, 8 in flight) |

The pipeline figure is lower than either side's speed because the receiver lags the sender by the first parts and both share the runner's link; each side alone ran 70–150 MB/s. About 100 MB/s is what this runner reaches with 8 in flight; I did not get a clean win from the slow start on throughput (final presigned 8-parallel runs were 77–93 vs 106 before), so treat the difference as run-to-run noise plus the extra small parts; it was not isolated.

**Time to first byte (receiver)**, from `recv` start: before 1.8–4.4 s (it waited for the first 8–32 MiB part to be uploaded, announced and fetched). After (inline over the DO WebSocket + 1 MiB first part): **187–285 ms** (target < 0.5 s met), and ≈ 1.0–1.2 s from the *sender's* process start. I did not instrument the stages separately; known contributions: .NET start 0.09 s, `POST /t` ≈ 0.3 s from the runner (creating the DO), the receiver's state fetch + WebSocket handshake, one DO hop for the first inline frame. Binding mode has the same first byte (it also uses the inline path).

**HTTP front** (`r2pipe serve` against `python -m http.server` on the runner, visitor = curl on the same runner, i.e. runner → Cloudflare → runner, so the figures include two long hops):

| | r2pipe | tunnel bus (figures from the task: basic) |
|---|---|---|
| small GET, 100 requests (workers.dev) | median 105 ms, p90 124 ms | ≈ 140–155 ms |
| small GET, 100 requests (`demo--pipe.ref12.dev`, through Access) | median 111 ms, p90 141 ms | |
| 25 MB download | 2.0–2.6 s (≈ 10–13 MB/s) | not given |
| 1 GB download | 46 s (≈ 23 MB/s), first byte 0.12–0.17 s, SHA verified | ≈ 18–25 MB/s |

So small requests are somewhat faster than the bus and bulk is **on par with the bus, not better**: the Worker reads the response parts strictly in order through a single stream, and a 1 GiB response is 36 parts, each with a PUT, a notification and a GET round trip. With a first attempt that opened all parts' R2 bodies at once the stream stalled and was cancelled (request failed at 80 MiB); with one body at a time it ran 72 s, with one-ahead prefetch 46 s. A browser or client that can use `r2pipe recv` should do that for big files instead.

**Cost of a run** (R2 operations). One 1 GiB transfer with 32 MiB parts is ≈ 38 parts: 38 PutObject (Class A) + 38 GetObject + 38 HeadObject by the Worker (Class B) + 38 deletes (free) ≈ **38 Class A + 76 Class B** ≈ $0.0002 at list prices ($4.50 / M A, $0.36 / M B), within the free tier (1 M A, 10 M B per month). Storage is transient (parts live seconds to minutes; 24 h cap). Egress is free. This session's 17 one-GiB runs plus the HTTP tests were about 1,000 A and 2,000 B operations. Worker requests and Durable Object requests are billed separately (a few hundred per GiB).

## Cloudflare K2 (evaluated on paper; not available to this token)

The API token has no K2 permission (`GET /accounts/<id>/k2/streams` → `10000 Authentication error`; K2 needs "K2 Config Write/Produce/Consume" and Workers Paid). Nothing was created or enabled. From the docs: a stream is a durable ordered log; consumers read through **subscriptions** that track a position (`earliest`/`latest`), **poll** `POST /subscriptions/<id>/consume` for leased batches (5 min lease, ack/nack, at-least-once), several subscriptions read independently; limits 30 MB/s produce, record ≈ 1 MB, 5 MB per produce, 10 MB per consume, retention 1 h – 30 days, 20 streams and 10 GB per account; beta is not billed, planned $0.04/GB produced and consumed, $0.02/GB-month. Consequences for r2pipe: a late or resumed receiver could replay "part ready / ack" events, but the Transfer DO already replays the full state (parts, inline bytes) on connect, and the DO WebSocket pushes in one hop whereas K2 consumers poll (latency not measured); small payloads through K2 would cost $0.08/GB where R2 is free, and the inline path through the DO already covers tiny transfers. **Recommendation: keep the DO.** If a durable audit log or fan-out to many receivers is wanted later, add K2 as a write-only log beside the DO (hybrid) once the beta is available on the account; not worth it for this prototype.

## Limits and notes

* Max 10,000 parts (so part size must be ≥ size/10,000); stdin of unknown length works up to that.
* Inline data ≤ 1 MiB per transfer; the sender must send it before any part.
* Binding mode: a request body through the Worker is capped by the plan's request size limit (100 MB on Free/Pro, more on higher plans); use ≤ 64 MiB parts there.
* The state machine and presigning are unit-tested (vitest); the Durable Object classes and the WebSocket paths were exercised against the deployed Worker, not under Miniflare.
* The receiver acks only after a verified write; a part that was acked cannot be fetched again (a second receiver must join before the acks, or resume from its own `.r2pipe` file). Several receivers on one transfer therefore compete for the acks; fan-out is not supported.
* `recv` into a pipe cannot resume (it cannot re-hash earlier output).

## Performance work (branch r2pipe-perf, 2026-10-05/06)

**Read this first: the cross-machine data is thin and noisy.** The single-machine sweep is solid (3 runs per cell). The two-runner (Linux ⇄ Windows) benchmark was disrupted: a runner outage, a transfer the Windows side missed (L2W p8-k1, p32-k1: no number), and two real client hangs found on the way (a receiver and a `serve`-less `ls` poll stuck on a request with no timeout, and a registry race that left finished transfers listed). Those were fixed in 04185d2f (30 s timeout on every signaling request, 45 s stall timeout per part PUT/GET with retry on a fresh URL, Transfer DO serializes requests). Not every cell has three clean runs; one run is shown where that is all there is.

### 1. Parallelism sweep (one runner, send+recv at once, 1 GiB, presigned, pipeline MB/s = 1 GiB ÷ wall time; 3 runs: min–max)

| in flight | part size | pipeline MB/s | send MB/s | recv MB/s |
|---|---|---|---|---|
| 8 | 32M | 47.1–51.5 | 61–82 | 62–71 |
| 16 | 32M | 56–73 (one run 18, receiver stalled: the hang above) | 90–142 | 83–129 |
| 32 | 32M | 51–76 | 88–166 | 72–138 |
| 16 | 8M | 60–72 | 89–111 | 83–106 |
| 16 | 16M | 71–80 | 124–153 | 104–122 |
| 16 | 64M | 52–72 | 86–123 | 69–106 |

URL batch size (16 in flight, 32M parts): batch 4 → 53–76, batch 32 → 55–71: no effect, so batch size is not the limit (cap raised to 128 anyway). Client: 4 cores, 677 MB RSS, one core ~90 % busy in recv at 16 in flight (SHA-256 + copy), so a single process stops scaling around 130–160 MB/s on this 4-core runner; run-to-run spread (±30 %) is larger than the difference between 16 and 32 in flight. Scaling stops at about 16 in flight: beyond that there is no gain, and the limits are the shared runner link (both processes on one NIC) and client CPU, not signaling (WebSocket pushes carry the GET URLs) and not R2.

**Chosen defaults:** 16 in flight, 16 MiB largest part, URL batch 16 (slow start 1, 2, 4 … MiB unchanged), inline 1 MiB. Before: 4 / 32 MiB / 8.

### 2. HTTP front prefetch

The Provider DO now reads up to `HTTP_PREFETCH` (6) parts / `HTTP_PREFETCH_BYTES` (64 MiB) ahead from R2 into memory while the current part is written to the visitor, in order; a failed or cancelled visitor stops the window and deletes the parts. `serve` uploads response parts 16 at once (parts up to 32 MiB).

| download via `<name>--pipe` / `/p/<name>` (curl on the same runner) | before | after (3 runs) |
|---|---|---|
| 25 MB | 2.0–2.6 s | 1.8–2.5 s (10–14 MB/s; part count and round trips dominate, not bandwidth) |
| 1 GB | 46 s (23 MB/s) | 24–29 s (**37–45 MB/s**), first byte 0.2–0.3 s |
| tunnel bus (figures from the task, basic) | 18–25 MB/s | |

`--parallel 8` and `16` for `serve` gave the same 1 GB time. About 2× the bus for 1 GB; 25 MB is limited by per-part round trips.

### 3. Two machines (Linux runner ⇄ Windows runner, 1 GiB, 16M parts)

| direction | in flight | runs (MB/s) | seconds |
|---|---|---|---|
| Linux sends, Windows receives | 8 | 51.1, 53.3, 45.0, 46.8 (build before the fixes) | 20–24 |
| Linux sends, Windows receives | 16 | 86.0, 72.9, 26.3, 96.1, 58 | 12–41 |
| Linux sends, Windows receives | 32 | no result (missed) | |
| Linux sends (send side only, stats of the sender) | 8 / 16 / 32 | 49–54 / 30, 89, 61 / 32, 37 | |
| Windows sends, Linux receives | 8 | 27.9, 30.3, 30.4 | 35–38 |
| Windows sends, Linux receives | 16 | 30.5, 28.7, 27.7 | 35–39 |
| Windows sends, Linux receives | 32 | 30.7, 30.0, 29.1 | 35–37 |

What it shows: Windows → Linux is flat at about 30 MB/s regardless of 8, 16 or 32 in flight, so that direction is limited by the Windows runner's upload, not by r2pipe or R2. Linux → Windows scales from about 50 (8) to 70–95 (16) but swings 26–96 between identical runs, which is the Windows runner's download variance; all runs verified SHA-256 and the Windows client passed 48/48 tests. First byte on the receiver: 94–330 ms in both directions. No retries in any run on the final build.

### 4. What limits throughput now

1. The link of the weaker runner (about 30 MB/s up from the Windows runner; 50–95 MB/s down), then
2. a single client process at about 130–160 MB/s (hash + copy on 4 cores) when the link is not the limit,
3. for the HTTP front: one ordered stream through one Durable Object with a 64 MiB window, about 40 MB/s,
4. not: URL batch size, signaling latency, R2 itself.

**A future cross-machine benchmark** should be driven by one coordinator that starts both sides with per-step timeouts and a shared run id (so a missed transfer fails that cell instead of waiting for it), and writes one result file per cell. Or measure each side against R2 on its own (upload to R2, then download from R2, one machine at a time), which removes the pairing and the waiting altogether.
