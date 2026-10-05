import { HttpError, TransferCore, Event, Meta, Part, Objects } from "./state";
import { presign } from "./presign";
import { Env, json, err, s3Config, kvOf, packInline, unpackInline } from "./common";

type Role = "send" | "recv";

export class Transfer implements DurableObject {
  private core: TransferCore;
  private waiters: Array<() => void> = [];
  constructor(private ctx: DurableObjectState, private env: Env) {
    const bucket = env.BUCKET;
    const objects: Objects = {
      delete: (keys) => bucket.delete(keys),
      head: async (k) => { const h = await bucket.head(k); return h ? { size: h.size, etag: h.etag } : null; },
    };
    this.core = new TransferCore(kvOf(ctx.storage), objects, () => Date.now(), Number(env.TRANSFER_TTL_SECONDS ?? 86400) * 1000);
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
    return m.mode === "presigned" && cfg ? presign(cfg, "GET", key, 3600) : `${base}/t/${m.id}/parts/${n}/data`;
  }
  private async putUrl(m: Meta, n: number, base: string): Promise<string> {
    const key = await this.core.allowUpload(n);
    const cfg = s3Config(this.env);
    return m.mode === "presigned" && cfg ? presign(cfg, "PUT", key, 3600) : `${base}/t/${m.id}/parts/${n}/data`;
  }

  async fetch(req: Request): Promise<Response> {
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
      snap = await this.core.snapshot().catch(() => snap);
    }
    return json({ meta: snap.meta, parts: snap.parts });
  }

  async webSocketMessage(ws: WebSocket, msg: string | ArrayBuffer) {
    if (typeof msg === "string") { if (msg === "ping") ws.send("pong"); return; }
    if (!this.ctx.getTags(ws).includes("send")) return;
    // forward first (lowest latency), then store for late joiners; an invalid chunk is reported to the sender
    for (const r of this.sockets("recv")) { try { r.send(msg); } catch { /* closed */ } }
    try {
      const { offset, data } = unpackInline(msg);
      await this.core.inlineAppend(offset, data);
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

/** One instance lists the open transfers (for `r2pipe ls`). Plain key-value storage. */
export class Registry implements DurableObject {
  constructor(private ctx: DurableObjectState) {}
  async fetch(req: Request): Promise<Response> {
    const p = new URL(req.url).pathname;
    if (p === "/upsert") {
      const b = (await req.json()) as any;
      // Upserts from parallel acks can arrive out of order: keep the newest version and leave a tombstone for finished transfers
      const old = (await this.ctx.storage.get("t:" + b.id)) as any;
      if (old && typeof b.version === "number" && typeof old.version === "number" && b.version <= old.version) return json({ ok: true, stale: true });
      await this.ctx.storage.put("t:" + b.id, b.remove ? { id: b.id, removed: true, version: b.version ?? 0, expiresAt: b.expiresAt ?? Date.now() + 3600_000 } : b);
      return json({ ok: true });
    }
    const all = [...(await this.ctx.storage.list({ prefix: "t:" })).values()] as any[];
    const cutoff = Date.now();
    return json({ transfers: all.filter((x) => !x.removed && x.expiresAt > cutoff).sort((a, b) => b.createdAt - a.createdAt) });
  }
}
