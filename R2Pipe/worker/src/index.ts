import { authenticate } from "./auth";
import { HttpError, Kv, Objects, TransferCore, Event, Meta, Snapshot } from "./state";
import { presign, S3Config } from "./presign";

export interface Env {
  BUCKET: R2Bucket;
  TRANSFER: DurableObjectNamespace;
  REGISTRY: DurableObjectNamespace;
  ACCESS_TEAM_DOMAIN?: string;
  ACCESS_AUD?: string;
  ADMIN_TOKEN?: string;
  R2_ACCOUNT_ID?: string;
  R2_ACCESS_KEY_ID?: string;
  R2_SECRET_ACCESS_KEY?: string;
  R2_BUCKET_NAME?: string;
  TRANSFER_TTL_SECONDS?: string;
}

const json = (v: unknown, status = 200) => new Response(JSON.stringify(v), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });
const err = (status: number, message: string) => json({ error: message }, status);

export function s3Config(env: Env): S3Config | null {
  if (!env.R2_ACCOUNT_ID || !env.R2_ACCESS_KEY_ID || !env.R2_SECRET_ACCESS_KEY) return null;
  return { accountId: env.R2_ACCOUNT_ID, accessKeyId: env.R2_ACCESS_KEY_ID, secretAccessKey: env.R2_SECRET_ACCESS_KEY, bucket: env.R2_BUCKET_NAME ?? "r2pipe" };
}

function newId(): string {
  const b = crypto.getRandomValues(new Uint8Array(12));
  return [...b].map((x) => "abcdefghijklmnopqrstuvwxyz234567"[x & 31]).join("");
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === "/_health") return json({ ok: true, presigned: s3Config(env) != null });
    const a = await authenticate(req, env);
    if (!a.ok) return err(403, `forbidden: ${a.reason}`);
    try {
      const m = url.pathname.match(/^\/t(?:\/([a-z2-7]{12}))?(\/.*)?$/);
      if (!m) return url.pathname === "/" ? json({ name: "r2pipe", presigned: s3Config(env) != null, api: "/t" }) : err(404, "not found");
      const id = m[1], rest = m[2] ?? "";
      if (!id) {
        if (req.method === "GET" && rest === "") return env.REGISTRY.get(env.REGISTRY.idFromName("registry")).fetch("https://do/list");
        if (req.method === "POST" && rest === "") {
          const body = (await req.json().catch(() => ({}))) as { name?: string; size?: number; partSize?: number; mode?: string };
          const want = body.mode === "binding" ? "binding" : body.mode === "presigned" ? "presigned" : undefined;
          if (want === "presigned" && !s3Config(env)) return err(400, "presigned mode is not configured on this Worker (no R2 S3 credentials)");
          const mode = want ?? (s3Config(env) ? "presigned" : "binding");
          const nid = newId();
          const stub = env.TRANSFER.get(env.TRANSFER.idFromName(nid));
          return stub.fetch("https://do/create", { method: "POST", body: JSON.stringify({ ...body, id: nid, mode, creator: a.who, base: url.origin }) });
        }
        return err(405, "method not allowed");
      }
      const stub = env.TRANSFER.get(env.TRANSFER.idFromName(id));
      const fwd = new Request(`https://do${rest || "/state"}${url.search}`, req);
      return await stub.fetch(fwd, { headers: new Headers([...req.headers, ["x-r2pipe-base", url.origin]]) } as RequestInit);
    } catch (e) {
      if (e instanceof HttpError) return err(e.status, e.message);
      return err(500, (e as Error).message);
    }
  },
};

function kvOf(storage: DurableObjectStorage): Kv {
  return {
    get: (k) => storage.get(k) as any,
    put: (k, v) => storage.put(k, v as any),
    delete: async (k) => { await storage.delete(k); },
    list: async (p) => (await storage.list({ prefix: p })) as any,
    deleteAll: () => storage.deleteAll(),
  };
}

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

  private async publish(events: Event[], meta?: Meta) {
    for (const ev of events) {
      const s = JSON.stringify(ev);
      for (const ws of this.ctx.getWebSockets()) { try { ws.send(s); } catch { /* closed */ } }
    }
    const w = this.waiters; this.waiters = []; for (const f of w) f();
    const m = meta ?? (events.length ? await this.core.meta().catch(() => null) : null);
    if (m) {
      const reg = this.env.REGISTRY.get(this.env.REGISTRY.idFromName("registry"));
      const done = m.status === "aborted" || m.status === "done";
      await reg.fetch("https://do/upsert", { method: "POST", body: JSON.stringify({ id: m.id, name: m.name, size: m.size, totalSize: m.totalSize, status: m.status, createdAt: m.createdAt, expiresAt: m.expiresAt, mode: m.mode, remove: done }) }).catch(() => {});
    }
  }

  private urls(base: string, id: string) {
    return { id, state: `${base}/t/${id}`, ws: `${base.replace(/^http/, "ws")}/t/${id}/ws`, recv: `r2pipe recv ${id}` };
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
        return json({ ...this.urls(b.base, m.id), mode: m.mode, partSize: m.partSize, name: m.name, size: m.size, expiresAt: m.expiresAt });
      }
      if (p === "/ws") {
        if (req.headers.get("upgrade") !== "websocket") return err(426, "websocket expected");
        await this.core.meta();
        const pair = new WebSocketPair();
        this.ctx.acceptWebSocket(pair[1]);
        const snap = await this.core.snapshot();
        pair[1].send(JSON.stringify({ type: "hello", meta: snap.meta, parts: snap.parts.filter((x) => x.state === "ready") }));
        return new Response(null, { status: 101, webSocket: pair[0] });
      }
      const pm = p.match(/^\/parts\/(\d+)\/(put-url|get-url|done|ack|data)$/);
      if (p === "/state" && req.method === "GET") return this.state(url);
      if (p === "/complete" && req.method === "POST") {
        const r = await this.core.complete((await req.json()) as any);
        await this.publish(r.events, r.meta);
        return json({ ok: true, status: r.meta.status });
      }
      if ((p === "/abort" && req.method === "POST") || (p === "/" && req.method === "DELETE")) {
        const r = await this.core.abort();
        await this.publish(r.events);
        return json({ ok: true });
      }
      if (pm) {
        const n = Number(pm[1]), what = pm[2];
        if (what === "put-url" && req.method === "POST") {
          const key = await this.core.allowUpload(n);
          const m = await this.core.meta();
          const cfg = s3Config(this.env);
          if (m.mode === "presigned" && cfg) return json({ method: "PUT", url: await presign(cfg, "PUT", key, 3600) });
          return json({ method: "PUT", url: `${base}/t/${m.id}/parts/${n}/data`, viaWorker: true });
        }
        if (what === "get-url" && req.method === "POST") {
          const { key, part } = await this.core.allowDownload(n);
          const m = await this.core.meta();
          const cfg = s3Config(this.env);
          const o = { size: part.size, sha256: part.sha256, etag: part.etag };
          if (m.mode === "presigned" && cfg) return json({ method: "GET", url: await presign(cfg, "GET", key, 3600), ...o });
          return json({ method: "GET", url: `${base}/t/${m.id}/parts/${n}/data`, viaWorker: true, ...o });
        }
        if (what === "done" && req.method === "POST") {
          const r = await this.core.partReady(n, (await req.json()) as any);
          await this.publish(r.events);
          return json({ ok: true, part: r.part });
        }
        if (what === "ack" && req.method === "POST") {
          const r = await this.core.ack(n);
          await this.publish(r.events);
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

  webSocketMessage(ws: WebSocket, msg: string | ArrayBuffer) {
    if (msg === "ping") ws.send("pong");
  }
  webSocketClose(ws: WebSocket, code: number) { try { ws.close(code); } catch { /* */ } }

  async alarm() {
    const r = await this.core.expireIfDue();
    if (r.next) await this.ctx.storage.setAlarm(r.next);
    else await this.env.REGISTRY.get(this.env.REGISTRY.idFromName("registry")).fetch("https://do/upsert", { method: "POST", body: JSON.stringify({ id: this.ctx.id.name ?? "", remove: true }) }).catch(() => {});
  }
}

/** One instance lists the open transfers (for `r2pipe ls`). Plain key-value storage. */
export class Registry implements DurableObject {
  constructor(private ctx: DurableObjectState) {}
  async fetch(req: Request): Promise<Response> {
    const p = new URL(req.url).pathname;
    if (p === "/upsert") {
      const b = (await req.json()) as any;
      if (b.remove) await this.ctx.storage.delete("t:" + b.id);
      else await this.ctx.storage.put("t:" + b.id, b);
      return json({ ok: true });
    }
    const all = [...(await this.ctx.storage.list({ prefix: "t:" })).values()] as any[];
    const cutoff = Date.now();
    return json({ transfers: all.filter((x) => x.expiresAt > cutoff).sort((a, b) => b.createdAt - a.createdAt) });
  }
}
