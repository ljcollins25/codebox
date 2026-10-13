import { HttpError, TransferCore, Event, Meta, Part, Objects, MAX_TRANSFER_MS } from "./state";
import { presign } from "./presign";
import { Env, json, err, s3Config, kvOf, packInline, unpackInline } from "./common";

type Role = "send" | "recv";
export function transferUrlSeconds(m: Meta, now = Date.now()): number {
  const remaining = Math.floor((Math.min(m.expiresAt, m.createdAt + MAX_TRANSFER_MS) - now) / 1000);
  if (remaining < 1) throw new HttpError(410, "transfer expired");
  return Math.min(3600, remaining);
}


export class Transfer implements DurableObject {
  private core: TransferCore;
  private waiters: Array<() => void> = [];
  constructor(private ctx: DurableObjectState, private env: Env) {
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    const bucket = env.BUCKET;
    const objects: Objects = {
      delete: (keys) => bucket.delete(keys),
      head: async (k) => { const h = await bucket.head(k); return h ? { size: h.size, etag: h.etag } : null; },
    };
    this.core = new TransferCore(kvOf(ctx.storage), objects, () => Date.now(), Number(env.TRANSFER_TTL_SECONDS ?? 3600) * 1000);
  }

  private sockets(role: Role) { return this.ctx.getWebSockets(role); }

  /** Tell everybody who listens: the new meta and the parts that changed (a part may carry its GET URL). */
  private async publish(changed: Part[], meta: Meta, urls?: Map<number, string>) {
    const msg = JSON.stringify({ type: "state", meta, parts: changed.map((p) => ({ ...p, url: urls?.get(p.n) })) });
    for (const ws of this.ctx.getWebSockets()) { try { ws.send(msg); } catch { /* closed */ } }
    const w = this.waiters; this.waiters = []; for (const f of w) f();
    const done = meta.status === "aborted" || meta.status === "done";
    await this.env.REGISTRY.get(this.env.REGISTRY.idFromName("registry")).fetch("https://do/upsert", { method: "POST", body: JSON.stringify({ id: meta.id, name: meta.name, size: meta.size, totalSize: meta.totalSize, status: meta.status, createdAt: meta.createdAt, expiresAt: meta.expiresAt, mode: meta.mode, version: meta.version, remove: done }) }).catch(() => {});
  }

  private async getUrl(m: Meta, key: string, n: number, base: string): Promise<string> {
    const cfg = s3Config(this.env);
    return m.mode === "presigned" && cfg ? presign(cfg, "GET", key, transferUrlSeconds(m)) : `${base}/t/${m.id}/parts/${n}/data`;
  }
  private async putUrl(m: Meta, n: number, base: string): Promise<string> {
    const key = await this.core.allowUpload(n);
    const cfg = s3Config(this.env);
    return m.mode === "presigned" && cfg ? presign(cfg, "PUT", key, transferUrlSeconds(m)) : `${base}/t/${m.id}/parts/${n}/data`;
  }

  private tail: Promise<unknown> = Promise.resolve();
  /** The state machine reads, awaits (R2) and writes the meta: run requests one at a time so parallel acks cannot overwrite each other. */
  fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === "/ws" || url.pathname === "/state" || url.pathname.endsWith("/data")) return this.fetchInner(req);
    const run = this.tail.then(() => this.fetchInner(req), () => this.fetchInner(req));
    this.tail = run.catch(() => {});
    return run;
  }

  private async fetchInner(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const base = req.headers.get("x-r2pipe-base") ?? "";
    try {
      const p = url.pathname;
      if (p === "/create" && req.method === "POST") {
        const b = (await req.json()) as any;
        const m = await this.core.create(b);
        await this.ctx.storage.setAlarm(m.expiresAt);
        await this.publish([], m);
        return json({ id: m.id, ws: `${b.base.replace(/^http/, "ws")}/t/${m.id}/ws`, mode: m.mode, partSize: m.partSize, name: m.name, size: m.size, expiresAt: m.expiresAt });
      }
      if (p === "/ws") return this.upgrade(req, url);
      if (p === "/state" && req.method === "GET") return this.state(url);
      if (p === "/complete" && req.method === "POST") {
        const r = await this.core.complete((await req.json()) as any);
        await this.publish([], r.meta);
        return json({ ok: true, status: r.meta.status });
      }
      if ((p === "/abort" && req.method === "POST") || (p === "/" && req.method === "DELETE")) {
        const r = await this.core.abort();
        await this.publish([], await this.core.meta());
        return json({ ok: true });
      }
      if (p === "/put-urls" && req.method === "POST") { // batch: URLs for parts from..from+count-1
        const b = (await req.json()) as { from: number; count: number };
        const m = await this.core.meta();
        const count = Math.min(Math.max(1, b.count | 0), 128);
        const urls: Array<{ n: number; url: string }> = [];
        for (let i = 0; i < count; i++) urls.push({ n: b.from + i, url: await this.putUrl(m, b.from + i, base) });
        return json({ method: "PUT", urls });
      }
      if (p === "/get-urls" && req.method === "POST") {
        const b = (await req.json()) as { parts: number[] };
        const m = await this.core.meta();
        const urls: Array<{ n: number; url: string; size: number; sha256: string; offset: number }> = [];
        for (const n of b.parts.slice(0, 32)) {
          const { key, part } = await this.core.allowDownload(n);
          urls.push({ n, url: await this.getUrl(m, key, n, base), size: part.size, sha256: part.sha256, offset: part.offset });
        }
        return json({ urls });
      }
      const pm = p.match(/^\/parts\/(\d+)\/(put-url|get-url|done|ack|data)$/);
      if (pm) {
        const n = Number(pm[1]), what = pm[2];
        const m = await this.core.meta();
        if (what === "put-url" && req.method === "POST") return json({ method: "PUT", url: await this.putUrl(m, n, base) });
        if (what === "get-url" && req.method === "POST") {
          const { key, part } = await this.core.allowDownload(n);
          return json({ method: "GET", url: await this.getUrl(m, key, n, base), size: part.size, sha256: part.sha256, offset: part.offset });
        }
        if (what === "done" && req.method === "POST") {
          const r = await this.core.partReady(n, (await req.json()) as any);
          if (r.events.length) {
            const mm = await this.core.meta();
            const key = (await this.core.allowDownload(n)).key;
            await this.publish([r.part], mm, new Map([[n, await this.getUrl(mm, key, n, base)]]));
          }
          return json({ ok: true, part: r.part });
        }
        if (what === "ack" && req.method === "POST") {
          const r = await this.core.ack(n);
          if (r.events.length) { const snap = await this.core.snapshot(); await this.publish(snap.parts.filter((x) => x.n === n), snap.meta); }
          return json({ ok: true });
        }
        if (what === "data") { // binding mode: the bytes pass through the Worker
          if (req.method === "PUT") {
            const key = await this.core.allowUpload(n);
            if (!req.body) throw new HttpError(400, "empty body");
            const o = await this.env.BUCKET.put(key, req.body);
            return json({ etag: o?.etag, size: o?.size });
          }
          if (req.method === "GET") {
            const { key } = await this.core.allowDownload(n);
            const o = await this.env.BUCKET.get(key);
            if (!o) throw new HttpError(404, "object missing");
            return new Response(o.body, { headers: { "content-length": String(o.size), etag: o.etag } });
          }
        }
      }
      return err(404, "not found");
    } catch (e) {
      if (e instanceof HttpError) return err(e.status, e.message);
      return err(500, (e as Error).message);
    }
  }

  /** WebSocket. recv: gets the state, the inline bytes so far (replay), then pushes. send: pushes inline frames (offset + bytes). */
  private async upgrade(req: Request, url: URL): Promise<Response> {
    if (req.headers.get("upgrade") !== "websocket") return err(426, "websocket expected");
    const role: Role = url.searchParams.get("role") === "send" ? "send" : "recv";
    const m = await this.core.meta();
    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [role]);
    const snap = await this.core.snapshot();
    const urls = new Map<number, string>();
    if (role === "recv") {
      const base = req.headers.get("x-r2pipe-base") ?? "";
      for (const p of snap.parts) if (p.state === "ready") urls.set(p.n, await this.getUrl(snap.meta, `t/${m.id}/${String(p.n).padStart(6, "0")}`, p.n, base));
    }
    pair[1].send(JSON.stringify({ type: "state", meta: snap.meta, parts: snap.parts.map((x) => ({ ...x, url: urls.get(x.n) })) }));
    if (role === "recv") for (const c of await this.core.inlineChunks()) pair[1].send(packInline(c.offset, c.data));
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  /** GET /state?since=V&wait=S : the snapshot, held until the version moves past V (long poll). */
  private async state(url: URL): Promise<Response> {
    const since = Number(url.searchParams.get("since") ?? -1);
    const wait = Math.min(Number(url.searchParams.get("wait") ?? 0), 55);
    let snap = await this.core.snapshot();
    if (wait > 0 && snap.meta.version <= since && (snap.meta.status === "open" || snap.meta.status === "complete")) {
      await new Promise<void>((res) => { const t = setTimeout(res, wait * 1000); this.waiters.push(() => { clearTimeout(t); res(); }); });
      snap = await this.core.snapshot();
    }
    return json({ meta: snap.meta, parts: snap.parts });
  }

  async webSocketMessage(ws: WebSocket, msg: string | ArrayBuffer) {
    if (typeof msg === "string") { if (msg === "ping") ws.send("pong"); return; }
    if (!this.ctx.getTags(ws).includes("send")) return;
    // Validate lifetime and store before forwarding: an expired sender cannot
    // bypass the one-hour contract by keeping its WebSocket connected.
    try {
      const { offset, data } = unpackInline(msg);
      await this.core.inlineAppend(offset, data);
      for (const r of this.sockets("recv")) { try { r.send(msg); } catch { /* closed */ } }
      ws.send(JSON.stringify({ type: "inline-ack", size: (await this.core.meta()).inlineSize }));
    } catch (e) {
      ws.send(JSON.stringify({ type: "error", message: (e as Error).message }));
    }
  }
  webSocketClose(ws: WebSocket, code: number) { try { ws.close(code); } catch { /* */ } }

  async alarm() {
    const r = await this.core.expireIfDue();
    if (r.next) await this.ctx.storage.setAlarm(r.next);
  }
}

/** One instance lists open transfers; bounded pruning runs only on writes. */
export class Registry implements DurableObject {
  private tail: Promise<unknown> = Promise.resolve();
  constructor(private ctx: DurableObjectState) {}
  fetch(req: Request): Promise<Response> {
    const run = this.tail.then(() => this.fetchInner(req), () => this.fetchInner(req));
    this.tail = run.catch(() => {});
    return run;
  }
  private async prune(now: number) {
    const startAfter = await this.ctx.storage.get<string>("pruneCursor");
    const rows = await this.ctx.storage.list<any>({ prefix: "t:", limit: 128, ...(startAfter ? { startAfter } : {}) });
    const expired: string[] = [];
    for (const [key, row] of rows) {
      const absolute = Number.isFinite(row.createdAt) ? row.createdAt + MAX_TRANSFER_MS : row.expiresAt;
      const until = row.removed ? (row.pruneAfter ?? absolute + 600_000) : Math.min(row.expiresAt, absolute);
      if (Number.isFinite(until) && now >= until) expired.push(key);
    }
    if (expired.length) await this.ctx.storage.delete(expired);
    if (rows.size === 128) await this.ctx.storage.put("pruneCursor", [...rows.keys()].at(-1)!);
    else await this.ctx.storage.delete("pruneCursor");
  }
  private async fetchInner(req: Request): Promise<Response> {
    const p = new URL(req.url).pathname, now = Date.now();
    if (p === "/upsert") {
      const b = (await req.json()) as any;
      await this.prune(now);
      if (typeof b.id !== "string" || !Number.isFinite(b.createdAt) || !Number.isFinite(b.expiresAt) || !Number.isFinite(b.version))
        return err(400, "invalid registry row");
      const absolute = b.createdAt + MAX_TRANSFER_MS;
      // After tombstones are pruned, delayed old writes still cannot resurrect.
      if (now >= Math.min(b.expiresAt, absolute)) return json({ ok: true, stale: true });
      const key = "t:" + b.id, old = await this.ctx.storage.get<any>(key);
      if (old && b.version <= old.version) return json({ ok: true, stale: true });
      await this.ctx.storage.put(key, b.remove
        ? { id: b.id, removed: true, version: b.version, createdAt: b.createdAt,
            expiresAt: Math.min(b.expiresAt, absolute), pruneAfter: absolute + 600_000 }
        : { ...b, expiresAt: Math.min(b.expiresAt, absolute) });
      return json({ ok: true });
    }
    const all = [...(await this.ctx.storage.list({ prefix: "t:" })).values()] as any[];
    return json({ transfers: all.filter(x => !x.removed && now < Math.min(x.expiresAt,
      Number.isFinite(x.createdAt) ? x.createdAt + MAX_TRANSFER_MS : x.expiresAt)).sort((a, b) => b.createdAt - a.createdAt) });
  }
}
