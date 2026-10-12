import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Provider } from "../src/provider";
import { BusProvider } from "../src/bus/provider";
import type { Env } from "../src/common";

class Socket {
  static OPEN = 1;
  readyState = 1;
  attachment: any = null;
  sent: any[] = [];
  closes: number[] = [];
  throwSend = false;
  onSend?: (m: any) => void;
  send(m: any) {
    if (this.throwSend || this.readyState !== 1) throw new Error("Can't call WebSocket send() after close().");
    const decoded = typeof m === "string" && m.startsWith("{") ? JSON.parse(m) : m;
    this.sent.push(decoded);
    this.onSend?.(decoded);
  }
  close(code: number) { this.closes.push(code); this.readyState = 2; }
  serializeAttachment(a: any) { this.attachment = structuredClone(a); }
  deserializeAttachment() { return structuredClone(this.attachment); }
}
const asWs = (s: Socket) => s as unknown as WebSocket;
const request = () => new Request("https://do/request", { method: "POST", headers: { "x-r2pipe-path": "/daemon/join" } });

function fixture(kind: "base" | "bus" = "base") {
  const sockets: Socket[] = [], tags = new Map<Socket, string[]>(), background: Promise<any>[] = [];
  const events: any[] = [];
  const storage = new Map<string, any>();
  const ctx = {
    setWebSocketAutoResponse: vi.fn(),
    getWebSocketAutoResponseTimestamp: (s: WebSocket) => new Date((s as any).attachment?.connectedAt ?? Date.now()),
    getWebSockets: (tag?: string) => sockets.filter((s) => !tag || tags.get(s)?.includes(tag)).map(asWs),
    getTags: (s: WebSocket) => tags.get(s as unknown as Socket) ?? [],
    acceptWebSocket: (s: WebSocket, t: string[]) => { sockets.push(s as unknown as Socket); tags.set(s as unknown as Socket, t); },
    storage: { get: async (k: string) => k === "name" ? "hexad-project" : storage.get(k), put: async (k: string | Record<string, any>, v: any) => { if (typeof k === "string") storage.set(k, structuredClone(v)); else for (const [key, value] of Object.entries(k)) storage.set(key, structuredClone(value)); }, delete: async (k: string | string[]) => { for (const key of Array.isArray(k) ? k : [k]) storage.delete(key); } },
    blockConcurrencyWhile: (f: () => Promise<any>) => { const p = f(); background.push(p); return p; },
    waitUntil: (p: Promise<any>) => background.push(p),
    id: { toString: () => "fixture" },
  } as unknown as DurableObjectState;
  const env = {
    BUCKET: { delete: vi.fn(async () => {}) },
    BUSREG: { idFromName: (n: string) => n, get: () => ({ fetch: async (_u: string, i: any) => { events.push(JSON.parse(i.body)); return new Response(null); } }) },
  } as unknown as Env;
  let provider: Provider;
  const add = (s: Socket, t = ["provider"]) => { sockets.push(s); tags.set(s, t); s.serializeAttachment({id: crypto.randomUUID(), connectedAt: Date.now(), retired: false}); if (t.includes("provider") && s.readyState === 1) { storage.set("providerSlot", s.deserializeAttachment()); storage.set("currentId", s.attachment.id); tags.set(s, [...t, "conn-" + s.attachment.id]); } return s; };
  const reply = (s: Socket, rid: number) => provider.webSocketMessage(asWs(s), JSON.stringify({ t: "res", rid, status: 204, headers: [] }));
  const autoReply = (s: Socket) => { s.onSend = (m) => { if (m.t === "req") void reply(s, m.rid); }; };
  return { ctx, env, sockets, events, background, storage, add, reply, autoReply,
    get p() { if (!provider) provider = kind === "bus" ? new BusProvider(ctx, env) : new Provider(ctx, env); return provider; },
    reconstruct() { provider = kind === "bus" ? new BusProvider(ctx, env) : new Provider(ctx, env); },
    async ready() { if (!provider) provider = kind === "bus" ? new BusProvider(ctx, env) : new Provider(ctx, env); await Promise.all(background); },
    count() { return (provider as any).pending.size; } };
}

beforeEach(() => {
  vi.stubGlobal("WebSocket", Socket);
  vi.stubGlobal("WebSocketRequestResponsePair", class { constructor(public request: string, public response: string) {} });
  vi.stubGlobal("WebSocketPair", class { 0 = new Socket(); 1 = new Socket(); });
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe("provider socket lifecycle", () => {
  it("skips a closed first socket and forwards on the open socket", async () => {
    const f = fixture(); const old = f.add(new Socket()); old.readyState = 3;
    const live = f.add(new Socket()); f.autoReply(live);
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(204);
    expect(old.sent).toEqual([]); expect(f.count()).toBe(0);
  });
  it.each([0, 2, 3])("does not select readyState %s", async (state) => {
    const f = fixture(); f.add(new Socket()).readyState = state;
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(503); expect(f.count()).toBe(0);
  });
  it("persists retirement across DO reconstruction even if a runtime still lists it OPEN", async () => {
    const f = fixture(); const old = f.add(new Socket());
    const initial = f.p; await f.ready();
    initial.webSocketClose(asWs(old), 1006); old.readyState = 1;
    const live = f.add(new Socket()); f.autoReply(live);
    f.reconstruct();
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(204);
    expect(old.attachment.retired).toBe(true); expect(old.sent).toEqual([]);
  });
  it("turns the first send race into 502 and releases the pending pump/writer", async () => {
    const f = fixture(); const s = f.add(new Socket()); s.throwSend = true;
    let pending: any;
    s.send = () => { pending = [...(f.p as any).pending.values()][0]; throw new Error("closed"); };
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(502);
    expect(f.count()).toBe(0); expect(pending.closed).toBe(true);
    await pending.pump;
    expect(s.attachment.retired).toBe(true);
  });
  it("a late close/error from the old provider cannot fail a new request", async () => {
    const f = fixture("bus"); const old = f.add(new Socket()); old.readyState = 3;
    const live = f.add(new Socket());
    const provider = f.p; await f.ready();
    const running = provider.fetch(request());
    expect(f.count()).toBe(1);
    f.p.webSocketClose(asWs(old), 1006);
    (f.p as BusProvider).webSocketError(asWs(old));
    expect(f.count()).toBe(1);
    expect(f.events.some((e) => e.connected === false)).toBe(false);
    await f.reply(live, live.sent[0].rid);
    expect((await running).status).toBe(204);
  });
  it("rejects late response frames from a replaced provider", async () => {
    const f = fixture(); const old = f.add(new Socket()); old.readyState = 3;
    const live = f.add(new Socket()); const provider = f.p; await f.ready();
    const running = provider.fetch(request());
    const rid = live.sent[0].rid;
    await f.reply(old, rid); expect(f.count()).toBe(1);
    await f.reply(live, rid); expect((await running).status).toBe(204);
  });
  it("send failure in request-body forwarding cleans up and cancels the timer", async () => {
    vi.useFakeTimers();
    const f = fixture(); const s = f.add(new Socket());
    s.onSend = (m) => { if (m.t === "req") s.throwSend = true; };
    await f.ready();
    const result = await f.p.fetch(request());
    expect(result.status).toBe(502); expect(f.count()).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });
  it("clears a successful request timer", async () => {
    vi.useFakeTimers();
    const f = fixture(); const s = f.add(new Socket()); f.autoReply(s);
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(204);
    expect(vi.getTimerCount()).toBe(0);
  });
  it("provider loss marks the bus offline and closes its viewers", async () => {
    const f = fixture("bus");
    const s = f.add(new Socket()); const v = f.add(new Socket(), ["v7"]);
    const provider = f.p;
    await f.ready();
    await f.ready();
    provider.webSocketClose(asWs(s), 1006);
    await f.ready();
    expect(v.readyState).toBe(2); expect(f.events).toContainEqual({ name: "hexad-project", connected: false });
    expect(await (await f.p.fetch(new Request("https://do/stats"))).json()).toMatchObject({ connected: false });
  });
  it("WebSocket upgrade send failure returns 502, retires provider and closes the accepted viewer", async () => {
    const f = fixture("bus"); const s = f.add(new Socket()); s.throwSend = true;
    await f.ready();
    const r = await f.p.fetch(new Request("https://do/request", { headers: { upgrade: "websocket" } }));
    expect(r.status).toBe(503); expect(s.attachment.retired).toBe(true);
    expect(f.sockets[1].readyState).toBe(2);
  });
  it("has no fallback after the sole slot fails to send", async () => {
    const f = fixture(); const old = f.add(new Socket()); old.attachment.retired = true;
    const live = f.add(new Socket()); live.throwSend = true;
    const provider = f.p;
    await f.ready();
    expect((await provider.fetch(request())).status).toBe(502);
    expect((await provider.fetch(request())).status).toBe(503);
    expect(old.sent).toEqual([]);
  });
  it("expires half-open slots lazily from the auto-response timestamp", async () => {
    const f = fixture(); const live = f.add(new Socket());
    live.attachment.connectedAt = Date.now() - 60_001;
    const provider = f.p;
    await f.ready();
    expect((await provider.fetch(request())).status).toBe(503);
    expect(live.attachment.retired).toBe(true);
    expect(f.count()).toBe(0);
  });
  it("restores exactly one provider on wake and excludes retired OPEN sockets", async () => {
    const f = fixture(); const old = f.add(new Socket()); old.attachment.retired = true;
    const live = f.add(new Socket()); f.reconstruct(); f.autoReply(live);
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(204);
    expect(old.sent).toEqual([]);
    expect((f.ctx.setWebSocketAutoResponse as any)).toHaveBeenCalled();
  });
  it("fresh object restores the persisted slot, not a newer unmatched OPEN socket", async () => {
    const f = fixture();
    const right = f.add(new Socket());
    const saved = structuredClone(f.storage.get("providerSlot"));
    const wrong = f.add(new Socket()); wrong.attachment.connectedAt += 1000;
    f.storage.set("providerSlot", saved); f.storage.set("currentId", saved.id);
    f.reconstruct(); await f.ready(); f.autoReply(right);
    expect((await f.p.fetch(request())).status).toBe(204);
    expect(wrong.sent).toEqual([]);
    expect(f.storage.get("providerSlot").id).toBe(right.attachment.id);
  });
  it("replacement retires old providers and does not retain their pending requests", async () => {
    // Node's Response forbids 101; shim only that platform response, not socket logic.
    const NativeResponse = Response;
    vi.stubGlobal("Response", class extends NativeResponse {
      constructor(body?: any, init?: any) {
        super(body, init?.status === 101 ? { status: 200 } : init);
        if (init?.status === 101) Object.defineProperty(this, "status", { value: 101 });
      }
    });
    const f = fixture("bus"); const old = f.add(new Socket()); const viewer = f.add(new Socket(), ["v8"]);
    const provider = f.p; await f.ready();
    const running = provider.fetch(request());
    const accepted = await f.p.fetch(new Request("https://do/ws"));
    expect(accepted.status).toBe(101);
    expect((await running).status).toBe(502); expect(f.count()).toBe(0);
    expect(old.attachment.retired).toBe(true); expect(viewer.readyState).toBe(2);
    const live = f.sockets[2]; f.autoReply(live);
    f.p.webSocketClose(asWs(old), 1000);
    await f.ready();
    expect((await f.p.fetch(request())).status).toBe(204);
  });
});
