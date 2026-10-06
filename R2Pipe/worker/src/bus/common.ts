// pipe-bus: shared protocol helpers (pure, unit-tested).
//
// Provider WebSocket = text frames (JSON control) + binary frames: [kind u8][stream id u32 BE][seq u32 BE][payload].
// For web socket relay frames "seq" carries the opcode (1 text, 2 binary).

export const K_REQ_BODY = 1; //   DO -> provider: request body bytes inline (seq = order)
export const K_RES_BODY = 3; //   provider -> DO: response body bytes inline (seq = order)
export const K_WS_IN = 4; //      DO -> provider: a message from the viewer's web socket (seq = opcode)
export const K_WS_OUT = 5; //     provider -> DO: a message for the viewer's web socket (seq = opcode)
export const HEADER = 9;

export const INLINE_MAX = 1024 * 1024; // first bytes of a body travel inline
export const RES_WINDOW = 4 * 1024 * 1024; // inline response bytes the provider may have un-acked per stream (credit)
export const WS_WINDOW = 8 * 1024 * 1024; // viewer->provider web socket bytes allowed un-acked before the viewer is shed

export function packFrame(kind: number, sid: number, seq: number, data: Uint8Array): ArrayBuffer {
  const out = new Uint8Array(HEADER + data.byteLength);
  const v = new DataView(out.buffer);
  v.setUint8(0, kind); v.setUint32(1, sid); v.setUint32(5, seq);
  out.set(data, HEADER);
  return out.buffer;
}

export function unpackFrame(buf: ArrayBuffer): { kind: number; sid: number; seq: number; data: Uint8Array } {
  if (buf.byteLength < HEADER) throw new Error("short frame");
  const v = new DataView(buf);
  return { kind: v.getUint8(0), sid: v.getUint32(1), seq: v.getUint32(5), data: new Uint8Array(buf, HEADER) };
}

export const NAME_RE = /^[a-z0-9]+(-[a-z0-9]+)*$/;
export const MAX_NAME = 40;
export const validName = (n: string) => n.length >= 1 && n.length <= MAX_NAME && NAME_RE.test(n);

export interface HostConfig {
  /** "pipe.ref12.dev": hosts "<name>--pipe.ref12.dev" and "<prefix>--<name>--pipe.ref12.dev". */
  suffix?: string;
  /** "ref12.dev" (cutover): hosts "<name>.ref12.dev" and "<prefix>--<name>.ref12.dev". */
  baseDomain?: string;
  /** Labels under baseDomain that are not providers. */
  reserved?: string[];
}

/** Which provider a viewer's Host header names, and the "<prefix>" of "<prefix>--<name>", if any. */
export function parseViewerHost(host: string, cfg: HostConfig): { name: string; prefix?: string } | null {
  host = host.toLowerCase().replace(/:\d+$/, "");
  let label: string | null = null, viaSuffix = false;
  if (cfg.suffix && host.endsWith("--" + cfg.suffix.toLowerCase())) { label = host.slice(0, -(cfg.suffix.length + 2)); viaSuffix = true; }
  else if (cfg.baseDomain && host.endsWith("." + cfg.baseDomain.toLowerCase())) {
    const l = host.slice(0, -(cfg.baseDomain.length + 1));
    if (!l.includes(".")) label = l;
  }
  if (!label) return null;
  const i = label.lastIndexOf("--");
  const name = i >= 0 ? label.slice(i + 2) : label;
  const prefix = i >= 0 ? label.slice(0, i) : undefined;
  if (!validName(name) || (prefix !== undefined && prefix.length === 0)) return null;
  if (cfg.reserved?.includes(name) && !viaSuffix) return null;
  return { name, prefix };
}

/** Reassembles items that may arrive out of order (response parts finish in any order) and hands them out in sequence. */
export class SeqBuffer<T> {
  private items = new Map<number, T>();
  private next: number;
  private last: number | null = null;
  constructor(first = 1) { this.next = first; }
  put(seq: number, item: T) { if (seq >= this.next) this.items.set(seq, item); }
  setLast(last: number) { this.last = last; }
  /** The next in-order item, or undefined when it has not arrived. */
  take(): T | undefined {
    const it = this.items.get(this.next);
    if (it === undefined) return undefined;
    this.items.delete(this.next); this.next++;
    return it;
  }
  peek(offset = 0): T | undefined { return this.items.get(this.next + offset); }
  get finished(): boolean { return this.last !== null && this.next > this.last; }
  get pending(): number { return this.items.size; }
  get nextSeq(): number { return this.next; }
}

/** Per-name request rate limit. */
export class TokenBucket {
  private tokens: number; private at: number;
  constructor(private rate: number, private burst: number, now = Date.now()) { this.tokens = burst; this.at = now; }
  take(now = Date.now()): boolean {
    this.tokens = Math.min(this.burst, this.tokens + ((now - this.at) / 1000) * this.rate);
    this.at = now;
    if (this.tokens < 1) return false;
    this.tokens -= 1;
    return true;
  }
}

export const hex = (b: ArrayBuffer | Uint8Array) => [...new Uint8Array(b instanceof Uint8Array ? b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength) : b)].map((x) => x.toString(16).padStart(2, "0")).join("");
export async function sha256Hex(s: string): Promise<string> { return hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s))); }
export function mintToken(): string { return "pb_" + hex(crypto.getRandomValues(new Uint8Array(24))); }
export function timingEqual(a: string, b: string): boolean {
  const ea = new TextEncoder().encode(a), eb = new TextEncoder().encode(b);
  let d = ea.length ^ eb.length;
  for (let i = 0; i < Math.max(ea.length, eb.length); i++) d |= (ea[i] ?? 0) ^ (eb[i] ?? 0);
  return d === 0;
}
