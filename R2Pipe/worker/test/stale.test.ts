import { describe, it, expect } from "vitest";
import { RegistryCore } from "../src/bus/registry";
import { offlineResponse, ago } from "../src/bus/offline";
import { graceMs } from "../src/bus/do";
import type { Kv } from "../src/state";

function memKv(): Kv {
  const m = new Map<string, unknown>();
  return { get: async (k: string) => m.get(k) as any, put: async (k: string, v: unknown) => { m.set(k, v); }, delete: async (k: string) => { m.delete(k); },
    list: async (p: string) => new Map([...m].filter(([k]) => k.startsWith(p))) } as unknown as Kv;
}
const H = 3600 * 1000;
const hostFor = (n: string) => ({ host: n, prefixedHost: n, path: "/p/" + n + "/" });

describe("stale names", () => {
  it("a name with no provider past the grace period is removed on lookup", async () => {
    let t = 1_000_000; const r = new RegistryCore(memKv(), () => t, 24 * H);
    await r.register("live", {}); t += 1000; await r.setConnected("live", true); t += 5000; await r.setConnected("live", false);
    t += 23 * H; expect(await r.info("live")).toMatchObject({ name: "live", connected: false });
    t += 2 * H; expect(await r.get("live")).toBeNull();
    expect(await r.info("live")).toBeNull(); expect(await r.verify("live", "x")).toBe(false);
  });
  it("is removed on listing too, and counts from registration when it never connected", async () => {
    let t = 0; const r = new RegistryCore(memKv(), () => t, 24 * H);
    await r.register("never", {}); t = 10 * H; await r.register("fresh", {});
    t = 25 * H; const rows = await r.list(hostFor);
    expect(rows.map((x) => x.name)).toEqual(["fresh"]);
  });
  it("a connected name never expires; the grace restarts at disconnect", async () => {
    let t = 0; const r = new RegistryCore(memKv(), () => t, 24 * H);
    await r.register("a", {}); await r.setConnected("a", true);
    t = 100 * H; expect(await r.get("a")).not.toBeNull();
    await r.setConnected("a", false); t += 23 * H; expect(await r.get("a")).not.toBeNull();
    t += 2 * H; expect(await r.get("a")).toBeNull();
  });
  it("offline rows say how long and when the name expires", async () => {
    let t = 0; const r = new RegistryCore(memKv(), () => t, 24 * H);
    await r.register("a", {}); await r.setConnected("a", true); t = 2 * H; await r.setConnected("a", false); t = 5 * H;
    const [row] = await r.list(hostFor);
    expect(row).toMatchObject({ up: false, offlineSeconds: 3 * 3600, expiresAt: new Date(26 * H).toISOString(), lastSeen: new Date(2 * H).toISOString() });
    await r.setConnected("a", true); expect((await r.list(hostFor))[0]).toMatchObject({ offlineSeconds: 0, expiresAt: null });
  });
  it("grace 0 keeps names forever; BUS_STALE_HOURS sets the grace", async () => {
    let t = 0; const r = new RegistryCore(memKv(), () => t, 0);
    await r.register("a", {}); t = 1e12; expect(await r.get("a")).not.toBeNull();
    expect(graceMs({ BUS_STALE_HOURS: "2" } as any)).toBe(2 * H); expect(graceMs({} as any)).toBe(24 * H); expect(graceMs({ BUS_STALE_HOURS: "0" } as any)).toBe(0); expect(graceMs({ BUS_STALE_HOURS: "x" } as any)).toBe(24 * H);
  });
});

describe("offline answer", () => {
  const now = Date.parse("2026-01-02T00:00:00Z"), seen = "2026-01-01T22:00:00.000Z";
  const info = { name: "live", lastSeen: seen };
  it("browsers get a small HTML page with 503", async () => {
    const res = offlineResponse(new Request("https://p-live.ref12.dev/", { headers: { accept: "text/html,application/xhtml+xml" } }), info, "https://ctl.ref12.dev/", now);
    expect(res.status).toBe(503); expect(res.headers.get("content-type")).toContain("text/html");
    const body = await res.text();
    expect(body).toContain("live"); expect(body).toContain("registered on the pipe bus, but its provider is offline".replace("r", "R"));
    expect(body).toContain("2 h ago"); expect(body).toContain('href="https://ctl.ref12.dev/"');
  });
  it("escapes the name", async () => {
    const res = offlineResponse(new Request("https://x/", { headers: { accept: "text/html" } }), { name: "<b>", lastSeen: null }, "/", now);
    const body = await res.text(); expect(body).not.toContain("<b>"); expect(body).toContain("never connected");
  });
  it("other clients get JSON with name, bus and lastSeen, status 503", async () => {
    for (const accept of [undefined, "application/json", "*/*"]) {
      const res = offlineResponse(new Request("https://p-live.ref12.dev/", accept ? { headers: { accept } } : {}), info, "/", now);
      expect(res.status).toBe(503); expect(res.headers.get("content-type")).toContain("json");
      expect(await res.json()).toEqual({ error: "no provider is connected for this name", name: "live", bus: "pipe", lastSeen: seen });
    }
  });
  it("a POST from a browser still gets JSON", async () => {
    const res = offlineResponse(new Request("https://x/", { method: "POST", headers: { accept: "text/html" } }), info, "/", now);
    expect(res.headers.get("content-type")).toContain("json");
  });
  it("ago", () => { expect(ago(null, now)).toBe("never connected"); expect(ago(new Date(now - 90_000).toISOString(), now)).toBe("1 min ago"); expect(ago(new Date(now - 3 * 86400_000).toISOString(), now)).toBe("3 d ago"); });
});

describe("unknown names", () => {
  it("are not found (the route answers 404): unknown and expired both read as null", async () => {
    const r = new RegistryCore(memKv(), () => 0, 24 * H);
    expect(await r.info("nobody")).toBeNull(); expect(await r.get("nobody")).toBeNull();
  });
});
