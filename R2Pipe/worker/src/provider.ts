// HTTP front: visitors' requests reach a provider (r2pipe serve) over the provider's Durable Object WebSocket.
// Control and small data (the first MAX_INLINE bytes each way) travel in WebSocket frames; the bulk goes through R2 parts.
import { Env, json, err, s3Config } from "./common";
import { presign } from "./presign";

export const MAX_INLINE = 1024 * 1024;
export const REQ_PART_SIZE = 8 * 1024 * 1024;
const NAME_RE = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const HOP = new Set(["connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade", "host", "content-length"]);
/** Binary frame: u8 kind, u32 request id, payload. */
export const K_REQ_BODY = 1, K_RES_BODY = 3;

export function frame(kind: number, rid: number, data: Uint8Array): ArrayBuffer {
  const out = new Uint8Array(5 + data.byteLength);
  out[0] = kind; new DataView(out.buffer).setUint32(1, rid); out.set(data, 5);
  return out.buffer;
}

/** The provider name for this host ("<name>--<suffix>"), or null. */
export function providerNameFromHost(host: string, env: Env): string | null {
  const suf = env.HTTP_HOST_SUFFIX;
  if (!suf || !host.endsWith("--" + suf)) return null;
  const name = host.slice(0, -(suf.length + 2));
  return NAME_RE.test(name) && name.length <= 40 ? name : null;
}

export async function handleHttpFront(req: Request, env: Env, url: URL): Promise<Response | null> {
  let name = providerNameFromHost(url.hostname, env);
  let path = url.pathname;
  let prefix = "";
  if (!name) {
    const m = url.pathname.match(/^\/(?:p|_serve)\/([a-z0-9-]{1,40})(\/.*)?$/);
    if (!m) return null;
    name = m[1]; path = m[2] ?? "/"; prefix = "/p/" + name;
    if (!NAME_RE.test(name)) return err(400, "bad provider name");
    if (url.pathname.startsWith("/_serve/")) { // the provider's own WebSocket
      if (req.headers.get("upgrade") !== "websocket") return err(426, "websocket expected");
      return env.PROVIDER.get(env.PROVIDER.idFromName(name)).fetch(new Request("https://do/ws", req));
    }
  }
  const headers = new Headers(req.headers);
  headers.delete("cf-access-client-secret"); headers.delete("cf-access-client-id");
  headers.delete("cf-access-jwt-assertion");
  if (env.ADMIN_TOKEN && headers.get("authorization") === "Bearer " + env.ADMIN_TOKEN) headers.delete("authorization");
  headers.set("x-forwarded-host", url.host); headers.set("x-forwarded-proto", url.protocol.slice(0, -1));
  if (prefix) headers.set("x-forwarded-prefix", prefix);
  headers.set("x-r2pipe-path", path + url.search);
  headers.set("x-r2pipe-base", url.origin);
  return env.PROVIDER.get(env.PROVIDER.idFromName(name)).fetch(new Request("https://do/request", { method: req.method, headers, body: req.body, duplex: "half" } as RequestInit));
}

interface Pending {
  rid: number;
  name: string;
  writable: WritableStreamDefaultWriter<Uint8Array>;
  resolve: (r: Response) => void;
  responded: boolean;
  chain: Promise<void>;
  parts: Map<number, string>;
  ahead: Map<number, Promise<R2ObjectBody | null>>;
  nextPart: number;
  expectParts: number | null;
  keys: string[];
  pump: Promise<void> | null;
  waiter: (() => void) | null;
  closed: boolean;
  bytes: number;
}

export class Provider implements DurableObject {
  private pending = new Map<number, Pending>();
  private nextRid = 1;
  constructor(private ctx: DurableObjectState, private env: Env) {}

  private socket(): WebSocket | null { return this.ctx.getWebSockets("provider")[0] ?? null; }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === "/ws") {
      for (const old of this.ctx.getWebSockets("provider")) { try { old.close(4000, "replaced by a new provider"); } catch { /* */ } }
      const pair = new WebSocketPair();
      this.ctx.acceptWebSocket(pair[1], ["provider"]);
      return new Response(null, { status: 101, webSocket: pair[0] });
    }
    if (url.pathname === "/request") return this.request(req);
    return err(404, "not found");
  }

  private async request(req: Request): Promise<Response> {
    const ws = this.socket();
    if (!ws) return json({ error: "no provider is connected for this name" }, 502);
    const rid = this.nextRid++;
    const ts = new TransformStream<Uint8Array, Uint8Array>();
    const result = new Promise<Response>((resolve) => {
      const p: Pending = { rid, name: "", writable: ts.writable.getWriter(), resolve, responded: false, chain: Promise.resolve(), parts: new Map(), ahead: new Map(), nextPart: 1, expectParts: null, keys: [], pump: null, waiter: null, closed: false, bytes: 0 };
      p.pump = this.pumpParts(p);
      this.pending.set(rid, p);
      p.resolve = (r) => resolve(r);
      (p as any).readable = ts.readable;
    });
    const p = this.pending.get(rid)!;
    const headers: [string, string][] = [];
    req.headers.forEach((v, k) => { if (!HOP.has(k) && !k.startsWith("x-r2pipe-") && !k.startsWith("cf-") && k !== "x-forwarded-for") headers.push([k, v]); });
    ws.send(JSON.stringify({ t: "req", rid, method: req.method, path: req.headers.get("x-r2pipe-path") ?? "/", headers, hasBody: req.body != null }));
    // the request body goes on in the background: inline first, then R2 parts
    this.uploadBody(p, ws, req).catch((e) => this.fail(p, 502, "request body: " + (e as Error).message));
    const timeout = new Promise<Response>((res) => setTimeout(() => res(json({ error: "provider did not answer in time" }, 504)), 90_000));
    const r = await Promise.race([result, timeout]);
    if (!p.responded) this.fail(p, 504, "timeout");
    return r;
  }

  private async uploadBody(p: Pending, ws: WebSocket, req: Request) {
    const rid = p.rid;
    if (!req.body) { ws.send(JSON.stringify({ t: "req-end", rid, parts: 0 })); return; }
    const reader = req.body.getReader();
    let inline = 0, part = 0, buf: Uint8Array[] = [], bufLen = 0;
    const cfg = s3Config(this.env);
    const flush = async () => {
      if (!bufLen) return;
      if (!cfg) throw new Error("request bodies over 1 MiB need presigned mode");
      const merged = new Uint8Array(bufLen); let o = 0; for (const b of buf) { merged.set(b, o); o += b.length; }
      buf = []; bufLen = 0;
      const key = `h/${rid}-${crypto.randomUUID().slice(0, 8)}/${++part}`;
      await this.env.BUCKET.put(key, merged);
      p.keys.push(key);
      ws.send(JSON.stringify({ t: "req-part", rid, n: part, size: merged.length, url: await presign(cfg, "GET", key, 3600) }));
    };
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      if (p.closed) { await reader.cancel(); return; }
      let v: Uint8Array = value;
      if (inline < MAX_INLINE) {
        const take = Math.min(v.length, MAX_INLINE - inline);
        ws.send(frame(K_REQ_BODY, rid, v.subarray(0, take)));
        inline += take; v = v.subarray(take);
      }
      if (v.length) { buf.push(v); bufLen += v.length; if (bufLen >= REQ_PART_SIZE) await flush(); }
    }
    await flush();
    ws.send(JSON.stringify({ t: "req-end", rid, parts: part }));
  }

  /** Streams R2 parts into the visitor's response in order, as they are announced. */
  private async pumpParts(p: Pending) {
    try {
      for (;;) {
        if (p.closed) return;
        const g = p.parts.get(p.nextPart);
        if (!g) {
          if (p.expectParts != null && p.nextPart > p.expectParts) { await p.writable.close(); p.closed = true; this.finish(p); return; }
          await new Promise<void>((r) => { p.waiter = r; });
          continue;
        }
        await p.chain; // the inline bytes come first
        const o = await (p.ahead.get(p.nextPart) ?? this.env.BUCKET.get(g));
        p.ahead.delete(p.nextPart);
        const nk = p.parts.get(p.nextPart + 1);
        if (nk) p.ahead.set(p.nextPart + 1, this.env.BUCKET.get(nk)); // open the next part while this one streams
        if (!o) throw new Error("part " + p.nextPart + " is missing in R2");
        await o.body.pipeTo(new WritableStream({ write: (c) => p.writable.write(c) }));
        p.parts.delete(p.nextPart); p.nextPart++;
      }
    } catch (e) {
      this.fail(p, 502, (e as Error).message);
    }
  }

  private wake(p: Pending) { const w = p.waiter; p.waiter = null; w?.(); }

  private fail(p: Pending, status: number, message: string) {
    if (p.closed) return;
    p.closed = true;
    if (!p.responded) { p.responded = true; p.resolve(json({ error: message }, status)); }
    else p.writable.abort(new Error(message)).catch(() => {});
    try { this.socket()?.send(JSON.stringify({ t: "abort", rid: p.rid })); } catch { /* */ }
    this.finish(p);
  }

  private finish(p: Pending) {
    this.pending.delete(p.rid);
    this.wake(p);
    if (p.keys.length) this.ctx.waitUntil(this.env.BUCKET.delete(p.keys).catch(() => {}));
    p.keys = [];
  }

  async webSocketMessage(ws: WebSocket, msg: string | ArrayBuffer) {
    if (typeof msg !== "string") {
      const v = new DataView(msg);
      if (v.getUint8(0) !== K_RES_BODY) return;
      const p = this.pending.get(v.getUint32(1));
      if (!p || p.closed) return;
      const data = new Uint8Array(msg, 5);
      p.chain = p.chain.then(async () => { if (!p.closed) { try { await p.writable.write(data.slice()); p.bytes += data.length; } catch { this.fail(p, 499, "visitor went away"); } } });
      return;
    }
    if (msg === "ping") { ws.send("pong"); return; }
    let m: any;
    try { m = JSON.parse(msg); } catch { return; }
    const p: Pending | undefined = this.pending.get(m.rid);
    if (m.t === "urls") { // batch of presigned PUT URLs for the response parts
      const cfg = s3Config(this.env);
      if (!p || !cfg) { ws.send(JSON.stringify({ t: "urls", rid: m.rid, urls: [], error: "presigned mode is not configured" })); return; }
      const urls: Array<{ n: number; url: string }> = [];
      for (let i = 0; i < Math.min(m.count | 0, 16); i++) {
        const key = this.keyFor(p, m.from + i);
        urls.push({ n: m.from + i, url: await presign(cfg, "PUT", key, 3600) });
      }
      ws.send(JSON.stringify({ t: "urls", rid: m.rid, urls }));
      return;
    }
    if (!p) return;
    if (m.t === "res") {
      p.chain = p.chain.then(() => {
        if (p.responded) return;
        p.responded = true;
        const h = new Headers();
        for (const [k, v] of m.headers as [string, string][]) { if (!HOP.has(k.toLowerCase())) { try { h.append(k, v); } catch { /* bad header */ } } }
        if (typeof m.length === "number") h.set("content-length", String(m.length));
        const nullBody = [101, 204, 205, 304].includes(m.status);
        if (nullBody) { p.resolve(new Response(null, { status: m.status, headers: h })); p.closed = true; p.writable.close().catch(() => {}); this.finish(p); }
        else p.resolve(new Response((p as any).readable, { status: m.status, headers: h }));
      });
    } else if (m.t === "part") {
      p.chain = p.chain.then(() => {
        const key = this.keyFor(p, m.n);
        p.parts.set(m.n, key); // read lazily, in order: only one R2 body is open at a time
        p.keys.push(key);
        this.wake(p);
      });
    } else if (m.t === "end") {
      p.chain = p.chain.then(async () => {
        p.expectParts = m.parts | 0;
        if (!p.responded) { p.responded = true; p.resolve(new Response(null, { status: 502 })); }
        this.wake(p);
        if (p.expectParts === 0 && !p.closed) { p.closed = true; await p.writable.close().catch(() => {}); this.finish(p); }
      });
    } else if (m.t === "error") {
      this.fail(p, 502, String(m.message ?? "provider error"));
    }
  }

  private keyFor(p: Pending, n: number): string { return `h/${p.rid}-${this.ctx.id.toString().slice(0, 8)}/${n}`; }

  webSocketClose(ws: WebSocket, code: number) {
    try { ws.close(code); } catch { /* */ }
    for (const p of [...this.pending.values()]) this.fail(p, 502, "provider disconnected");
  }
}
