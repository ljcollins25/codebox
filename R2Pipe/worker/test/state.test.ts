import { describe, it, expect } from "vitest";
import { TransferCore, Kv, Objects, HttpError, partKey } from "../src/state";

function fakes() {
  const store = new Map<string, unknown>();
  const kv: Kv = {
    async get(k) { return store.get(k) as any; },
    async put(k, v) { store.set(k, structuredClone(v)); },
    async delete(k) { store.delete(k); },
    async list(p) { return new Map([...store].filter(([k]) => k.startsWith(p)).map(([k, v]) => [k, structuredClone(v)])) as any; },
    async deleteAll() { store.clear(); },
  };
  const objs = new Map<string, number>();
  const objects: Objects = {
    async delete(keys) { for (const k of keys) objs.delete(k); },
    async head(k) { return objs.has(k) ? { size: objs.get(k)!, etag: "etag-" + k } : null; },
  };
  let t = 1_000_000;
  const clock = { now: () => t, advance: (ms: number) => { t += ms; } };
  const core = new TransferCore(kv, objects, clock.now, 24 * 3600_000, 600_000);
  return { store, objs, core, clock };
}
const H = "a".repeat(64);
const MB = 1024 * 1024;
const code = async (p: Promise<unknown>) => { try { await p; return 0; } catch (e) { return (e as HttpError).status ?? -1; } };

const offs = new Map<string, number>();
async function upload(f: ReturnType<typeof fakes>, id: string, n: number, size: number, sha = H, offset?: number) {
  f.objs.set(partKey(id, n), size);
  const o = offset ?? (n === 1 ? 0 : (offs.get(id + (n - 1)) ?? 0));
  offs.set(id + n, o + size);
  return f.core.partReady(n, { offset: o, size, sha256: sha });
}

describe("transfer state machine", () => {
  it("creates an open transfer with an expiry and validates part size", async () => {
    const f = fakes();
    const m = await f.core.create({ id: "abc", name: "x.bin", size: 10, partSize: 4 * MB, mode: "binding" });
    expect(m.status).toBe("open");
    expect(m.expiresAt).toBe(f.clock.now() + 24 * 3600_000);
    expect(await code(f.core.create({ id: "abc", mode: "binding" }))).toBe(409);
    const g = fakes();
    expect(await code(g.core.create({ id: "q", partSize: 10, mode: "binding" }))).toBe(400);
    expect(await code(g.core.create({ id: "q", size: 10000 * 2 * MB, partSize: 1 * MB, mode: "binding" }))).toBe(400);
  });

  it("part ready checks R2, is idempotent, and rejects size mismatch", async () => {
    const f = fakes();
    await f.core.create({ id: "t1", mode: "presigned", partSize: MB });
    expect(await code(f.core.partReady(1, { offset: 0, size: 5, sha256: H }))).toBe(409); // not in R2
    f.objs.set(partKey("t1", 1), 5);
    expect(await code(f.core.partReady(1, { offset: 0, size: 6, sha256: H }))).toBe(409);
    const r = await f.core.partReady(1, { offset: 0, size: 5, sha256: H });
    expect(r.events).toEqual([{ type: "part", n: 1, offset: 0, size: 5, sha256: H, etag: "etag-" + partKey("t1", 1) }]);
    expect((await f.core.partReady(1, { offset: 0, size: 5, sha256: H })).events).toEqual([]);
    expect(await code(f.core.partReady(2, { offset: 5, size: 5, sha256: "zz" }))).toBe(400);
  });

  it("ack deletes the object and refuses further downloads", async () => {
    const f = fakes();
    await f.core.create({ id: "t2", mode: "binding", partSize: MB });
    await upload(f, "t2", 1, 7);
    expect((await f.core.allowDownload(1)).part.size).toBe(7);
    expect((await f.core.ack(1)).events).toEqual([{ type: "ack", n: 1 }]);
    expect(f.objs.has(partKey("t2", 1))).toBe(false);
    expect(await code(f.core.allowDownload(1))).toBe(410);
    expect((await f.core.ack(1)).events).toEqual([]);
    expect(await code(f.core.ack(9))).toBe(404);
    expect(await code(f.core.allowUpload(1))).toBe(409);
  });

  it("complete needs every part, matching size; done once everything is acked", async () => {
    const f = fakes();
    await f.core.create({ id: "t3", mode: "binding", partSize: MB, size: 12 });
    await upload(f, "t3", 1, 8);
    await upload(f, "t3", 3, 4);
    expect(await code(f.core.complete({ parts: 3, size: 12, sha256: H }))).toBe(409); // part 2 missing
    await upload(f, "t3", 2, 1);
    expect(await code(f.core.complete({ parts: 3, size: 13, sha256: H }))).toBe(409); // wrong total
    const c = await f.core.complete({ parts: 3, size: 13 - 1, sha256: H }).catch((e) => e);
    expect(c.status).toBe(409); // 8+1+4 = 13, not 12: also differs from the announced size
    const f2 = fakes();
    await f2.core.create({ id: "t4", mode: "binding", partSize: MB });
    await upload(f2, "t4", 1, 8); await upload(f2, "t4", 2, 1);
    const ok = await f2.core.complete({ parts: 2, size: 9, sha256: H });
    expect(ok.meta.status).toBe("complete");
    expect(await code(f2.core.allowUpload(1))).toBe(409);
    await f2.core.ack(1);
    expect((await f2.core.ack(2)).events.map((e) => e.type)).toEqual(["ack", "done"]);
    expect((await f2.core.meta()).status).toBe("done");
  });

  it("an empty transfer completes at once; a part beyond the total is refused", async () => {
    const f = fakes();
    await f.core.create({ id: "t5", mode: "binding", partSize: MB });
    const r = await f.core.complete({ parts: 0, size: 0, sha256: H });
    expect(r.meta.status).toBe("done");
  });

  it("abort deletes the unconsumed objects and blocks further work", async () => {
    const f = fakes();
    await f.core.create({ id: "t6", mode: "binding", partSize: MB });
    await upload(f, "t6", 1, 3); await upload(f, "t6", 2, 3);
    await f.core.ack(1);
    expect((await f.core.abort()).events).toEqual([{ type: "abort" }]);
    expect(f.objs.size).toBe(0);
    expect(await code(f.core.allowUpload(3))).toBe(409);
    expect(await code(f.core.allowDownload(2))).toBe(410);
    expect((await f.core.abort()).events).toEqual([]);
  });

  it("expiry wipes objects and storage only when due", async () => {
    const f = fakes();
    const m = await f.core.create({ id: "t7", mode: "binding", partSize: MB });
    await upload(f, "t7", 1, 3);
    f.clock.advance(3600_000);
    expect((await f.core.expireIfDue()).next).toBe(m.expiresAt);
    expect(f.objs.size).toBe(1);
    f.clock.advance(24 * 3600_000);
    const r = await f.core.expireIfDue();
    expect(r).toEqual({ next: null, events: [{ type: "expired" }] });
    expect(f.objs.size).toBe(0);
    expect(f.store.size).toBe(0);
    expect(await code(f.core.meta())).toBe(404);
  });

  it("a finished transfer expires sooner (grace period)", async () => {
    const f = fakes();
    await f.core.create({ id: "t8", mode: "binding", partSize: MB });
    await upload(f, "t8", 1, 3);
    await f.core.complete({ parts: 1, size: 3, sha256: H });
    await f.core.ack(1);
    const m = await f.core.meta();
    expect(m.status).toBe("done");
    expect(m.expiresAt).toBe(f.clock.now() + 600_000);
  });

  it("version grows with every change (long poll)", async () => {
    const f = fakes();
    await f.core.create({ id: "t9", mode: "binding", partSize: MB });
    const v0 = (await f.core.meta()).version;
    await upload(f, "t9", 1, 3);
    expect((await f.core.meta()).version).toBeGreaterThan(v0);
  });
});
