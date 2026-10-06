import { describe, it, expect } from "vitest";
import { parseViewerHost, packWs, unpackWs, TokenBucket, validName, mintToken, sha256Hex, K_WS_IN } from "../src/bus/common";
import { RegistryCore, RegistryError, cleanMeta } from "../src/bus/registry";
import type { Kv } from "../src/state";

function memKv(): Kv {
  const m = new Map<string, unknown>();
  return {
    get: async (k) => m.get(k) as any, put: async (k, v) => { m.set(k, JSON.parse(JSON.stringify(v))); }, delete: async (k) => { m.delete(k); },
    list: async (p) => new Map([...m].filter(([k]) => k.startsWith(p))) as any, deleteAll: async () => m.clear(),
  };
}

describe("hosts", () => {
  const cfg = { suffix: "pipe.ref12.dev", baseDomain: "ref12.dev", reserved: ["ctl", "pipe"] };
  it("parses names and prefixes", () => {
    expect(parseViewerHost("app--pipe.ref12.dev", cfg)).toEqual({ name: "app", prefix: undefined });
    expect(parseViewerHost("3000--app--pipe.ref12.dev", cfg)).toEqual({ name: "app", prefix: "3000" });
    expect(parseViewerHost("app.ref12.dev", cfg)).toEqual({ name: "app", prefix: undefined });
    expect(parseViewerHost("3000--app.ref12.dev:443", cfg)).toEqual({ name: "app", prefix: "3000" });
  });
  it("refuses reserved, nested and bad hosts", () => {
    expect(parseViewerHost("ctl.ref12.dev", cfg)).toBeNull();
    expect(parseViewerHost("a.b.ref12.dev", cfg)).toBeNull();
    expect(parseViewerHost("evil.com", cfg)).toBeNull();
    expect(parseViewerHost("--app--pipe.ref12.dev", cfg)).toBeNull();
    expect(parseViewerHost("app.ref12.dev", { suffix: "pipe.ref12.dev" })).toBeNull();
  });
  it("validates names", () => { expect(validName("a-b1")).toBe(true); for (const n of ["", "A", "a--b", "-a", "a-", "x".repeat(41)]) expect(validName(n)).toBe(false); });
});

describe("frames and limits", () => {
  it("round-trips web socket frames", () => {
    const f = unpackWs(packWs(K_WS_IN, 7, 2, new Uint8Array([1, 2, 3])));
    expect([f.kind, f.sid, f.opcode, [...f.data]]).toEqual([K_WS_IN, 7, 2, [1, 2, 3]]);
    expect(() => unpackWs(new ArrayBuffer(3))).toThrow();
  });
  it("rate limits per bucket", () => {
    const b = new TokenBucket(10, 3, 0);
    expect([b.take(0), b.take(0), b.take(0), b.take(0)]).toEqual([true, true, true, false]);
    expect(b.take(150)).toBe(true);
    expect(b.take(150)).toBe(false);
  });
});

describe("registry and tokens", () => {
  it("returns the token once and stores only its hash", async () => {
    const kv = memKv(); const r = new RegistryCore(kv, () => 1000);
    const { token } = await r.register("app", { description: "d", kind: "web", session: { name: "s", hexad: "h" } });
    expect(token).toMatch(/^pb_[0-9a-f]{48}$/);
    const rec = await kv.get<any>("n:app");
    expect(JSON.stringify(rec)).not.toContain(token);
    expect(rec.tokenHash).toBe(await sha256Hex(token));
    expect(await r.verify("app", token)).toBe(true);
  });
  it("a token is valid for one name only", async () => {
    const r = new RegistryCore(memKv());
    const a = await r.register("a", {}), b = await r.register("b", {});
    expect(await r.verify("a", b.token)).toBe(false);
    expect(await r.verify("b", a.token)).toBe(false);
    expect(await r.verify("nope", a.token)).toBe(false);
    expect(await r.verify("a", "")).toBe(false);
  });
  it("re-registering revokes the old token; unregistering revokes all", async () => {
    const r = new RegistryCore(memKv());
    const t1 = (await r.register("a", {})).token, t2 = (await r.register("a", {})).token;
    expect(await r.verify("a", t1)).toBe(false); expect(await r.verify("a", t2)).toBe(true);
    expect(await r.remove("a")).toBe(true); expect(await r.verify("a", t2)).toBe(false); expect(await r.remove("a")).toBe(false);
  });
  it("validates names and metadata", async () => {
    const r = new RegistryCore(memKv());
    await expect(r.register("Bad Name", {})).rejects.toBeInstanceOf(RegistryError);
    expect(() => cleanMeta({ description: "x".repeat(201) })).toThrow();
    expect(() => cleanMeta({ kind: "Bad Kind" })).toThrow();
    expect(() => cleanMeta({ sessionUrl: "javascript:1" })).toThrow();
  });
  it("updates metadata, clears with empty string, tracks connections and lists dashboard rows", async () => {
    let t = 1000; const r = new RegistryCore(memKv(), () => t);
    await r.register("a", { description: "one", label: "L", kind: "web" });
    await r.update("a", { description: "two", label: "" });
    t = 2000; await r.setConnected("a", true); t = 3000; await r.setConnected("a", false); t = 4000; await r.setConnected("a", true);
    const rows = await r.list((n) => ({ host: n + "--pipe.ref12.dev", prefixedHost: "<prefix>--" + n, path: "/p/" + n + "/" }));
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ name: "a", up: true, description: "two", kind: "web", reconnects: 1, bus: "pipe", host: "a--pipe.ref12.dev" });
    expect(rows[0].label).toBeUndefined();
    expect(JSON.stringify(rows)).not.toContain("tokenHash");
    expect(await r.update("zzz", { description: "x" })).toBeNull();
  });
  it("mintToken is random", () => { expect(mintToken()).not.toBe(mintToken()); });
});
