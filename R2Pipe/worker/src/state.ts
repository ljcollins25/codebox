// Transfer state machine. Pure logic over a small storage interface so it runs in the Durable Object and in plain tests.
// Storage is the DO's key-value API (no SQL tables of our own).

export interface Kv {
  get<T>(key: string): Promise<T | undefined>;
  put(key: string, value: unknown): Promise<void>;
  delete(key: string): Promise<void>;
  list<T>(prefix: string): Promise<Map<string, T>>;
  deleteAll(): Promise<void>;
}
export interface Objects {
  /** Delete parts (R2 keys). */
  delete(keys: string[]): Promise<void>;
  /** Size and etag of an object, or null when absent. */
  head(key: string): Promise<{ size: number; etag: string } | null>;
}
export type Status = "open" | "complete" | "done" | "aborted" | "expired";
export interface Meta {
  id: string;
  name: string;
  size: number | null;
  partSize: number;
  mode: "presigned" | "binding";
  status: Status;
  createdAt: number;
  expiresAt: number;
  totalParts: number | null;
  totalSize: number | null;
  sha256: string | null;
  version: number;
  creator?: string;
}
export interface Part {
  n: number;
  size: number;
  sha256: string;
  etag: string;
  state: "ready" | "acked";
  at: number;
}
export type Event =
  | { type: "part"; n: number; size: number; sha256: string; etag: string }
  | { type: "ack"; n: number }
  | { type: "complete"; parts: number; size: number; sha256: string }
  | { type: "done" }
  | { type: "abort" }
  | { type: "expired" };

export class HttpError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export const MAX_PARTS = 10000;
export const partKey = (id: string, n: number) => `t/${id}/${String(n).padStart(6, "0")}`;

export interface Snapshot { meta: Meta; parts: Part[] }

export class TransferCore {
  constructor(
    private kv: Kv,
    private objects: Objects,
    private now: () => number = () => Date.now(),
    private ttlMs = 24 * 3600 * 1000,
    private doneGraceMs = 10 * 60 * 1000,
  ) {}

  async meta(): Promise<Meta> {
    const m = await this.kv.get<Meta>("meta");
    if (!m) throw new HttpError(404, "no such transfer");
    return m;
  }
  private async save(m: Meta) { m.version++; await this.kv.put("meta", m); }

  async create(p: { id: string; name?: string; size?: number | null; partSize?: number; mode: "presigned" | "binding"; creator?: string }): Promise<Meta> {
    if (await this.kv.get("meta")) throw new HttpError(409, "transfer exists");
    const partSize = p.partSize ?? 32 * 1024 * 1024;
    if (!Number.isInteger(partSize) || partSize < 1024 * 1024 || partSize > 512 * 1024 * 1024) throw new HttpError(400, "partSize must be 1 MiB .. 512 MiB");
    if (p.size != null && (!Number.isInteger(p.size) || p.size < 0)) throw new HttpError(400, "bad size");
    if (p.size != null && Math.ceil(p.size / partSize) > MAX_PARTS) throw new HttpError(400, `more than ${MAX_PARTS} parts; raise partSize`);
    const t = this.now();
    const m: Meta = {
      id: p.id, name: (p.name ?? p.id).slice(0, 200), size: p.size ?? null, partSize, mode: p.mode, status: "open",
      createdAt: t, expiresAt: t + this.ttlMs, totalParts: null, totalSize: null, sha256: null, version: 0, creator: p.creator,
    };
    await this.save(m);
    return m;
  }

  private async open(): Promise<Meta> {
    const m = await this.meta();
    if (m.status !== "open") throw new HttpError(409, `transfer is ${m.status}`);
    return m;
  }
  private checkN(m: Meta, n: number) {
    if (!Number.isInteger(n) || n < 1 || n > MAX_PARTS) throw new HttpError(400, "bad part number");
    if (m.totalParts != null && n > m.totalParts) throw new HttpError(400, "part number beyond total");
  }

  /** The sender may upload while the transfer is open. Returns the R2 key. */
  async allowUpload(n: number): Promise<string> {
    const m = await this.open();
    this.checkN(m, n);
    const old = await this.kv.get<Part>(`p:${n}`);
    if (old?.state === "acked") throw new HttpError(409, "part already consumed");
    return partKey(m.id, n);
  }

  /** Sender reports a finished part; the object is checked in R2. */
  async partReady(n: number, r: { size: number; sha256: string; etag?: string }): Promise<{ part: Part; events: Event[] }> {
    const m = await this.open();
    this.checkN(m, n);
    if (!Number.isInteger(r.size) || r.size < 0) throw new HttpError(400, "bad size");
    if (!/^[0-9a-f]{64}$/.test(r.sha256 ?? "")) throw new HttpError(400, "sha256 must be 64 lowercase hex characters");
    const head = await this.objects.head(partKey(m.id, n));
    if (!head) throw new HttpError(409, "part object not found in R2");
    if (head.size !== r.size) throw new HttpError(409, `part size mismatch: reported ${r.size}, stored ${head.size}`);
    const old = await this.kv.get<Part>(`p:${n}`);
    if (old && old.sha256 === r.sha256 && old.size === r.size) return { part: old, events: [] }; // idempotent retry
    if (old?.state === "acked") throw new HttpError(409, "part already consumed");
    const part: Part = { n, size: r.size, sha256: r.sha256, etag: head.etag, state: "ready", at: this.now() };
    await this.kv.put(`p:${n}`, part);
    await this.save(m);
    return { part, events: [{ type: "part", n, size: part.size, sha256: part.sha256, etag: part.etag }] };
  }

  /** Receiver wants to download part n. */
  async allowDownload(n: number): Promise<{ key: string; part: Part }> {
    const m = await this.meta();
    if (m.status === "aborted" || m.status === "expired") throw new HttpError(410, `transfer ${m.status}`);
    const p = await this.kv.get<Part>(`p:${n}`);
    if (!p) throw new HttpError(404, "part not ready");
    if (p.state === "acked") throw new HttpError(410, "part already acked and deleted");
    return { key: partKey(m.id, n), part: p };
  }

  /** Receiver confirms it has the part and verified it; the object is deleted. */
  async ack(n: number): Promise<{ events: Event[] }> {
    const m = await this.meta();
    if (m.status === "aborted" || m.status === "expired") throw new HttpError(410, `transfer ${m.status}`);
    const p = await this.kv.get<Part>(`p:${n}`);
    if (!p) throw new HttpError(404, "part not ready");
    if (p.state === "acked") return { events: [] };
    await this.objects.delete([partKey(m.id, n)]);
    p.state = "acked";
    await this.kv.put(`p:${n}`, p);
    const events: Event[] = [{ type: "ack", n }];
    if (m.status === "complete" && (await this.allAcked(m))) {
      m.status = "done";
      m.expiresAt = Math.min(m.expiresAt, this.now() + this.doneGraceMs);
      events.push({ type: "done" });
    }
    await this.save(m);
    return { events };
  }

  private async allAcked(m: Meta): Promise<boolean> {
    const parts = await this.kv.list<Part>("p:");
    if (m.totalParts == null || parts.size < m.totalParts) return false;
    for (const p of parts.values()) if (p.state !== "acked") return false;
    return true;
  }

  async complete(c: { parts: number; size: number; sha256: string }): Promise<{ meta: Meta; events: Event[] }> {
    const m = await this.open();
    if (!Number.isInteger(c.parts) || c.parts < 0 || c.parts > MAX_PARTS) throw new HttpError(400, "bad parts");
    if (!/^[0-9a-f]{64}$/.test(c.sha256 ?? "")) throw new HttpError(400, "sha256 must be 64 lowercase hex characters");
    const parts = await this.kv.list<Part>("p:");
    const missing: number[] = [];
    let total = 0;
    for (let n = 1; n <= c.parts; n++) { const p = parts.get(`p:${n}`); if (!p) missing.push(n); else total += p.size; }
    if (missing.length) throw new HttpError(409, `missing parts: ${missing.slice(0, 20).join(",")}`);
    for (const p of parts.values()) if (p.n > c.parts) throw new HttpError(409, `part ${p.n} is beyond the declared ${c.parts}`);
    if (total !== c.size) throw new HttpError(409, `size mismatch: parts add up to ${total}, declared ${c.size}`);
    if (m.size != null && m.size !== c.size) throw new HttpError(409, `size mismatch: announced ${m.size}, declared ${c.size}`);
    m.status = "complete"; m.totalParts = c.parts; m.totalSize = c.size; m.sha256 = c.sha256;
    const events: Event[] = [{ type: "complete", parts: c.parts, size: c.size, sha256: c.sha256 }];
    if (await this.allAcked(m) || c.parts === 0) { m.status = "done"; m.expiresAt = Math.min(m.expiresAt, this.now() + this.doneGraceMs); events.push({ type: "done" }); }
    await this.save(m);
    return { meta: m, events };
  }

  async abort(): Promise<{ events: Event[] }> {
    const m = await this.meta();
    if (m.status === "aborted" || m.status === "expired") return { events: [] };
    await this.purgeObjects();
    m.status = "aborted"; m.expiresAt = Math.min(m.expiresAt, this.now() + this.doneGraceMs);
    await this.save(m);
    return { events: [{ type: "abort" }] };
  }

  /** Alarm: wipe when due. Returns the next alarm time, or null when everything is gone. */
  async expireIfDue(): Promise<{ next: number | null; events: Event[] }> {
    const m = await this.kv.get<Meta>("meta");
    if (!m) return { next: null, events: [] };
    if (this.now() < m.expiresAt) return { next: m.expiresAt, events: [] };
    await this.purgeObjects();
    await this.kv.deleteAll();
    return { next: null, events: [{ type: "expired" }] };
  }

  private async purgeObjects() {
    const m = await this.meta();
    const parts = await this.kv.list<Part>("p:");
    const keys = [...parts.values()].filter((p) => p.state === "ready").map((p) => partKey(m.id, p.n));
    for (let i = 0; i < keys.length; i += 500) await this.objects.delete(keys.slice(i, i + 500));
  }

  async snapshot(): Promise<Snapshot> {
    const meta = await this.meta();
    const parts = [...(await this.kv.list<Part>("p:")).values()].sort((a, b) => a.n - b.n);
    return { meta, parts };
  }
}
