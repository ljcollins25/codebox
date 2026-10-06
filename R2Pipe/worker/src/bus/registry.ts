// Names, per-name provider tokens (stored as SHA-256, never in clear) and the dashboard metadata. Plain DO key-value storage.
import type { Kv } from "../state";
import { mintToken, sha256Hex, timingEqual, validName } from "./common";

export interface Session { name?: string; id?: string; hexad?: string }
export interface Meta { description?: string; label?: string; owner?: string; kind?: string; session?: Session; sessionUrl?: string }
export interface Record_ extends Meta {
  name: string;
  tokenHash: string;
  registeredAt: number;
  updatedAt: number;
  connected: boolean;
  connectedSince: number;
  lastSeen: number;
  reconnects: number;
  everConnected: boolean;
}
export class RegistryError extends Error { constructor(public status: number, m: string) { super(m); } }

const KIND_RE = /^[a-z0-9][a-z0-9._-]*$/;

/** Validates metadata the same way the tbus client and the old router do (lengths from tbus ShareMeta). */
export function cleanMeta(m: Record<string, unknown>): Meta {
  const out: Meta = {};
  const str = (k: string, max: number) => {
    const v = m[k];
    if (v === undefined || v === null) return undefined;
    if (typeof v !== "string") throw new RegistryError(400, `${k} must be a string`);
    if (v.length > max) throw new RegistryError(400, `${k} is longer than ${max} characters`);
    return v.trim();
  };
  const d = str("description", 200), l = str("label", 60), o = str("owner", 100) ?? str("source", 100), k = str("kind", 32);
  if (d !== undefined) out.description = d;
  if (l !== undefined) out.label = l;
  if (o !== undefined) out.owner = o;
  if (k !== undefined) { if (k && !KIND_RE.test(k)) throw new RegistryError(400, "bad kind"); out.kind = k; }
  const s = m["session"];
  if (s && typeof s === "object") {
    const ss = s as Record<string, unknown>; const sess: Session = {};
    for (const [key, max] of [["name", 60], ["id", 80], ["hexad", 100]] as const) {
      const v = ss[key]; if (v === undefined) continue;
      if (typeof v !== "string" || v.length > max) throw new RegistryError(400, `session.${key} too long`);
      sess[key] = v;
    }
    out.session = sess;
  }
  const su = str("sessionUrl", 300);
  if (su !== undefined) { if (su && !/^https?:\/\//.test(su)) throw new RegistryError(400, "sessionUrl must be http(s)"); out.sessionUrl = su; }
  return out;
}

export const DEFAULT_GRACE_MS = 24 * 3600 * 1000;

export class RegistryCore {
  /** graceMs: a name whose provider has been away longer than this is removed (lazily, on lookup and listing). 0 = never. */
  constructor(private kv: Kv, private now: () => number = () => Date.now(), private graceMs: number = DEFAULT_GRACE_MS) {}

  /** When the name last had a provider: the last connect or disconnect, else the registration itself. */
  static lastActive(r: Record_): number { return Math.max(r.lastSeen || 0, r.registeredAt || 0); }
  private stale(r: Record_): boolean { return this.graceMs > 0 && !r.connected && this.now() - RegistryCore.lastActive(r) > this.graceMs; }
  /** Removes every stale name; returns their names. */
  async sweep(): Promise<string[]> {
    const gone: string[] = [];
    for (const r of (await this.kv.list<Record_>("n:")).values()) if (this.stale(r)) { await this.kv.delete("n:" + r.name); gone.push(r.name); }
    return gone;
  }

  /** Registers (or re-registers) a name: a NEW token every time, so the previous holder is cut off. The clear token is returned once. */
  async register(name: string, meta: Record<string, unknown>): Promise<{ token: string; record: Record_ }> {
    if (!validName(name)) throw new RegistryError(400, "bad name: lowercase letters, digits and single hyphens, up to 40 characters");
    const token = mintToken();
    const old = await this.kv.get<Record_>("n:" + name);
    const t = this.now();
    const rec: Record_ = {
      ...(old ?? { registeredAt: t, connected: false, connectedSince: 0, lastSeen: 0, reconnects: 0, everConnected: false }),
      name, tokenHash: await sha256Hex(token), updatedAt: t,
      ...cleanMeta(meta),
    };
    if (!old) rec.registeredAt = t;
    // a fresh registration replaces the metadata the previous one set
    if (old) { for (const k of ["description", "label", "owner", "kind", "session", "sessionUrl"] as const) if (!(k in meta) && !(k === "owner" && "source" in meta)) delete rec[k]; }
    await this.kv.put("n:" + name, rec);
    return { token, record: rec };
  }

  async update(name: string, meta: Record<string, unknown>): Promise<Record_ | null> {
    const rec = await this.kv.get<Record_>("n:" + name);
    if (!rec) return null;
    const m = cleanMeta(meta);
    // an empty string clears a field
    for (const [k, v] of Object.entries(m)) { if (v === "") delete (rec as any)[k]; else (rec as any)[k] = v; }
    rec.updatedAt = this.now();
    await this.kv.put("n:" + name, rec);
    return rec;
  }

  async remove(name: string): Promise<boolean> {
    if (!(await this.kv.get("n:" + name))) return false;
    await this.kv.delete("n:" + name);
    return true;
  }

  /** Lookup; a stale name is removed here and reads as unknown. */
  async get(name: string): Promise<Record_ | null> {
    const r = (await this.kv.get<Record_>("n:" + name)) ?? null;
    if (r && this.stale(r)) { await this.kv.delete("n:" + name); return null; }
    return r;
  }

  /** What the offline page and JSON say about a registered name (null: unknown or expired). */
  async info(name: string): Promise<{ name: string; connected: boolean; lastSeen: string | null; registeredAt: string } | null> {
    const r = await this.get(name);
    if (!r) return null;
    return { name: r.name, connected: r.connected, lastSeen: r.lastSeen ? new Date(r.lastSeen).toISOString() : null, registeredAt: new Date(r.registeredAt).toISOString() };
  }

  async verify(name: string, token: string): Promise<boolean> {
    const rec = await this.kv.get<Record_>("n:" + name);
    if (!rec || !token) return false;
    return timingEqual(await sha256Hex(token), rec.tokenHash);
  }

  async setConnected(name: string, connected: boolean): Promise<void> {
    const rec = await this.kv.get<Record_>("n:" + name);
    if (!rec) return;
    const t = this.now();
    if (connected) { if (rec.everConnected) rec.reconnects++; rec.everConnected = true; rec.connected = true; rec.connectedSince = t; rec.lastSeen = t; }
    else { rec.connected = false; rec.lastSeen = t; rec.connectedSince = 0; }
    await this.kv.put("n:" + name, rec);
  }

  async touch(name: string): Promise<void> {
    const rec = await this.kv.get<Record_>("n:" + name);
    if (rec) { rec.lastSeen = this.now(); await this.kv.put("n:" + name, rec); }
  }

  /** The dashboard rows: the same fields the old router lists (without credentials, port is 0), never the token hash. */
  async list(hostFor: (name: string) => { host: string; prefixedHost: string; path: string }): Promise<any[]> {
    await this.sweep();
    const all = [...(await this.kv.list<Record_>("n:")).values()];
    const t = this.now();
    return all.sort((a, b) => (a.name < b.name ? -1 : 1)).map((r) => ({
      name: r.name, port: 0, up: r.connected, connected: r.connected, connectedSince: r.connected ? new Date(r.connectedSince).toISOString() : null,
      registeredAt: new Date(r.registeredAt).toISOString(), lastSeen: r.lastSeen ? new Date(r.lastSeen).toISOString() : null,
      updatedAt: new Date(r.updatedAt).toISOString(), reconnects: r.reconnects,
      description: r.description, label: r.label, owner: r.owner, kind: r.kind, session: r.session ?? {}, sessionUrl: r.sessionUrl,
      // offline: how long, and when the name is dropped if no provider comes back
      offlineSeconds: r.connected ? 0 : Math.max(0, Math.round((t - RegistryCore.lastActive(r)) / 1000)),
      expiresAt: r.connected || this.graceMs <= 0 ? null : new Date(RegistryCore.lastActive(r) + this.graceMs).toISOString(),
      bus: "pipe", ...hostFor(r.name),
    }));
  }
}
