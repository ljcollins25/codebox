// pipe-bus: shared protocol helpers (pure, unit-tested).
//
// The provider's WebSocket carries the r2pipe HTTP-front protocol (JSON control + 5-byte binary frames: kind 1 request body,
// kind 3 response body) plus logical web socket streams:
//   JSON  DO -> provider  {t:"ws-open", sid, path, headers}   {t:"ws-close", sid, code}
//   JSON  provider -> DO  {t:"ws-close", sid, code}           {t:"ws-ack", sid, n}   (n bytes consumed, credit for the viewer)
//   binary [kind u8][sid u32 BE][opcode u8][payload]: kind 4 viewer -> provider, kind 5 provider -> viewer; opcode 1 text, 2 binary.

export const K_WS_IN = 4, K_WS_OUT = 5, WS_HEADER = 6;
export const WS_WINDOW = 8 * 1024 * 1024; // viewer->provider bytes allowed un-acked before the viewer is shed

export function packWs(kind: number, sid: number, opcode: number, data: Uint8Array): ArrayBuffer {
  const out = new Uint8Array(WS_HEADER + data.byteLength);
  const v = new DataView(out.buffer);
  v.setUint8(0, kind); v.setUint32(1, sid); v.setUint8(5, opcode);
  out.set(data, WS_HEADER);
  return out.buffer;
}
export function unpackWs(buf: ArrayBuffer): { kind: number; sid: number; opcode: number; data: Uint8Array } {
  if (buf.byteLength < WS_HEADER) throw new Error("short frame");
  const v = new DataView(buf);
  return { kind: v.getUint8(0), sid: v.getUint32(1), opcode: v.getUint8(5), data: new Uint8Array(buf, WS_HEADER) };
}

export const NAME_RE = /^[a-z0-9]+(-[a-z0-9]+)*$/;
export const MAX_NAME = 40;
/** The `p-` prefix is reserved: <name> is reachable as p-<name>.<domain> on the pipe bus, so no registry accepts a name that starts with it. */
export const RESERVED_PREFIX = "p-";
export const validName = (n: string) => n.length >= 1 && n.length <= MAX_NAME && NAME_RE.test(n) && !n.startsWith(RESERVED_PREFIX);

export interface HostConfig {
  /** "pipe.ref12.dev": hosts "<name>--pipe.ref12.dev" and "<prefix>--<name>--pipe.ref12.dev". */
  suffix?: string;
  /** "ref12.dev": hosts "p-<name>.ref12.dev" and "<prefix>--p-<name>.ref12.dev" (the transition form). */
  baseDomain?: string;
  /** After the cutover: plain "<name>.ref12.dev" and "<prefix>--<name>.ref12.dev" are the pipe's too (the p- form keeps working). */
  cutover?: boolean;
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
  let name = i >= 0 ? label.slice(i + 2) : label;
  if (!viaSuffix) {
    if (name.startsWith(RESERVED_PREFIX)) name = name.slice(RESERVED_PREFIX.length);
    else if (!cfg.cutover) return null; // a plain name belongs to the container bus until the cutover
  }
  const prefix = i >= 0 ? label.slice(0, i) : undefined;
  if (!validName(name) || (prefix !== undefined && prefix.length === 0)) return null;
  if (cfg.reserved?.includes(name) && !viaSuffix) return null;
  return { name, prefix };
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
