// pipe-bus Provider DO: one per registered name. Extends the r2pipe HTTP front (inline first bytes, R2 parts, streaming)
// with web socket relay (logical streams on the provider's socket), per-name rate limits and stream caps.
import { Provider } from "../provider";
import type { Env } from "../common";
import { json } from "../common";
import { K_WS_IN, K_WS_OUT, WS_WINDOW, TokenBucket, packWs, unpackWs } from "./common";

const HOP_WS = new Set(["connection", "upgrade", "sec-websocket-key", "sec-websocket-version", "sec-websocket-extensions", "host", "content-length"]);

export class BusProvider extends Provider {
  private bucket: TokenBucket;
  private name = "";
  private unacked = new Map<number, number>();
  private maxStreams: number;
  private maxWs: number;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.bucket = new TokenBucket(Number(env.BUS_RATE ?? 50), Number(env.BUS_BURST ?? 100));
    this.maxStreams = Number(env.BUS_MAX_STREAMS ?? 64);
    this.maxWs = Number(env.BUS_MAX_WS ?? 32);
    this.nextRid = (Math.floor(Date.now() / 1000) & 0x3fffffff) + 1; // sids/rids stay unique across hibernation
    ctx.blockConcurrencyWhile(async () => { this.name = (await ctx.storage.get<string>("name")) ?? ""; });
  }

  private viewers(): WebSocket[] { return this.ctx.getWebSockets().filter((w) => this.ctx.getTags(w).some((t) => t[0] === "v")); }
  private sidOf(ws: WebSocket): number | null { const t = this.ctx.getTags(ws).find((x) => x[0] === "v"); return t ? Number(t.slice(1)) : null; }
  private registry(): DurableObjectStub { return this.env.BUSREG!.get(this.env.BUSREG!.idFromName("registry")); }
  private note(connected: boolean) {
    if (!this.name) return;
    this.ctx.waitUntil(this.registry().fetch("https://do/connected", { method: "POST", body: JSON.stringify({ name: this.name, connected }) }).then((r) => r.body?.cancel()).catch(() => {}));
  }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const n = req.headers.get("x-bus-name");
    if (n && n !== this.name) { this.name = n; await this.ctx.storage.put("name", n); }
    if (url.pathname === "/revoke") {
      for (const w of this.ctx.getWebSockets()) { try { w.close(this.ctx.getTags(w).includes("provider") ? 4001 : 1001, "revoked"); } catch { /* */ } }
      for (const p of [...this.pending.values()]) this.fail(p, 502, "revoked");
      return json({ ok: true });
    }
    if (url.pathname === "/ws") {
      const r = await super.fetch(req);
      if (r.status === 101) this.note(true);
      return r;
    }
    if (url.pathname === "/stats") return json({ pending: this.pending.size, viewers: this.viewers().length, connected: this.socket() != null });
    return super.fetch(req);
  }

  protected admit(): Response | null {
    if (!this.bucket.take()) return new Response(JSON.stringify({ error: "rate limit for this name exceeded" }), { status: 429, headers: { "retry-after": "1", "content-type": "application/json" } });
    if (this.pending.size >= this.maxStreams) return new Response(JSON.stringify({ error: "too many concurrent requests for this name" }), { status: 429, headers: { "retry-after": "1", "content-type": "application/json" } });
    return null;
  }

  protected async request(req: Request): Promise<Response> {
    if (!this.socket()) return new Response(null, { status: 503, headers: { "x-bus-offline": "1" } }); // the route turns this into the offline answer
    if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") return super.request(req);
    const ws = this.socket();
    if (!ws) return json({ error: "no provider is connected for this name" }, 502);
    const refused = this.admit();
    if (refused) return refused;
    if (this.viewers().length >= this.maxWs) return json({ error: "too many web sockets for this name" }, 429);
    const sid = this.nextRid++;
    const headers: [string, string][] = [];
    req.headers.forEach((v, k) => { if (!HOP_WS.has(k) && !k.startsWith("x-r2pipe-") && !k.startsWith("cf-") && k !== "x-forwarded-for" && k !== "sec-websocket-protocol") headers.push([k, v]); });
    const protocols = (req.headers.get("sec-websocket-protocol") ?? "").split(",").map((s) => s.trim()).filter(Boolean);
    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], ["v" + sid]);
    ws.send(JSON.stringify({ t: "ws-open", sid, path: req.headers.get("x-r2pipe-path") ?? "/", headers, protocols }));
    const h = new Headers();
    if (protocols[0]) h.set("sec-websocket-protocol", protocols[0]);
    return new Response(null, { status: 101, webSocket: pair[0], headers: h });
  }

  async webSocketMessage(ws: WebSocket, msg: string | ArrayBuffer) {
    const sid = this.sidOf(ws);
    if (sid != null) { // a viewer's message goes to the provider
      const prov = this.socket();
      if (!prov) { try { ws.close(1013, "no provider"); } catch { /* */ } return; }
      const bytes = typeof msg === "string" ? new TextEncoder().encode(msg) : new Uint8Array(msg);
      const un = (this.unacked.get(sid) ?? 0) + bytes.length;
      if (un > WS_WINDOW) { try { ws.close(1013, "too much unread data for the provider"); } catch { /* */ } return; }
      this.unacked.set(sid, un);
      prov.send(packWs(K_WS_IN, sid, typeof msg === "string" ? 1 : 2, bytes));
      return;
    }
    if (typeof msg !== "string") {
      if (new DataView(msg).getUint8(0) === K_WS_OUT) {
        const f = unpackWs(msg);
        const v = this.ctx.getWebSockets("v" + f.sid)[0];
        if (!v) { try { this.socket()?.send(JSON.stringify({ t: "ws-close", sid: f.sid, code: 1001 })); } catch { /* */ } return; }
        try { if (f.opcode === 1) v.send(new TextDecoder().decode(f.data)); else v.send(f.data.slice()); } catch { /* the viewer is gone */ }
        return;
      }
    } else if (msg.startsWith("{")) {
      let m: any; try { m = JSON.parse(msg); } catch { return; }
      if (m.t === "ws-ack") { const left = (this.unacked.get(m.sid) ?? 0) - (m.n | 0); if (left > 0) this.unacked.set(m.sid, left); else this.unacked.delete(m.sid); return; }
      if (m.t === "ws-close") {
        const v = this.ctx.getWebSockets("v" + m.sid)[0];
        this.unacked.delete(m.sid);
        if (v) { try { v.close(Number(m.code) >= 1000 && Number(m.code) < 5000 && m.code !== 1005 && m.code !== 1006 ? m.code : 1000, "provider closed"); } catch { /* */ } }
        return;
      }
    }
    return super.webSocketMessage(ws, msg);
  }

  webSocketClose(ws: WebSocket, code: number) {
    const sid = this.sidOf(ws);
    if (sid != null) {
      this.unacked.delete(sid);
      try { ws.close(code === 1005 || code === 1006 ? 1000 : code); } catch { /* */ }
      try { this.socket()?.send(JSON.stringify({ t: "ws-close", sid, code })); } catch { /* */ }
      return;
    }
    super.webSocketClose(ws, code);
    if (!this.socket()) {
      this.note(false);
      for (const v of this.viewers()) { try { v.close(1001, "provider went away"); } catch { /* */ } }
    }
  }

  webSocketError(ws: WebSocket) { this.webSocketClose(ws, 1006); }
}
