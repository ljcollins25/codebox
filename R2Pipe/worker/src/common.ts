import type { Kv } from "./state";
import type { S3Config } from "./presign";

export interface Env {
  BUCKET: R2Bucket;
  TRANSFER: DurableObjectNamespace;
  REGISTRY: DurableObjectNamespace;
  PROVIDER: DurableObjectNamespace;
  /** pipe-bus: registry DO and per-name provider DOs. */
  BUSREG?: DurableObjectNamespace;
  BUSPROV?: DurableObjectNamespace;
  BUS_RATE?: string; BUS_BURST?: string; BUS_MAX_STREAMS?: string; BUS_MAX_WS?: string;
  /** Cutover: also serve "<name>.<domain>" and "<prefix>--<name>.<domain>". */
  BUS_BASE_DOMAIN?: string; BUS_CUTOVER?: string; BUS_RESERVED?: string;
  ACCESS_TEAM_DOMAIN?: string;
  ACCESS_AUD?: string;
  ADMIN_TOKEN?: string;
  R2_ACCOUNT_ID?: string;
  R2_ACCESS_KEY_ID?: string;
  R2_SECRET_ACCESS_KEY?: string;
  R2_BUCKET_NAME?: string;
  TRANSFER_TTL_SECONDS?: string;
  /** e.g. "pipe.ref12.dev": hosts "<name>--pipe.ref12.dev" reach provider <name>. */
  HTTP_HOST_SUFFIX?: string;
  /** HTTP front: parts read from R2 ahead of the one being streamed (count and bytes). */
  HTTP_PREFETCH?: string;
  HTTP_PREFETCH_BYTES?: string;
}

export const json = (v: unknown, status = 200) => new Response(JSON.stringify(v), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });
export const err = (status: number, message: string) => json({ error: message }, status);

export function s3Config(env: Env): S3Config | null {
  if (!env.R2_ACCOUNT_ID || !env.R2_ACCESS_KEY_ID || !env.R2_SECRET_ACCESS_KEY) return null;
  return { accountId: env.R2_ACCOUNT_ID, accessKeyId: env.R2_ACCESS_KEY_ID, secretAccessKey: env.R2_SECRET_ACCESS_KEY, bucket: env.R2_BUCKET_NAME ?? "r2pipe" };
}

export function newId(): string {
  const b = crypto.getRandomValues(new Uint8Array(12));
  return [...b].map((x) => "abcdefghijklmnopqrstuvwxyz234567"[x & 31]).join("");
}

export function kvOf(storage: DurableObjectStorage): Kv {
  return {
    get: (k) => storage.get(k) as any,
    put: (k, v) => storage.put(k, v as any),
    delete: async (k) => { await storage.delete(k); },
    list: async (p) => (await storage.list({ prefix: p })) as any,
    deleteAll: () => storage.deleteAll(),
  };
}

/** Inline frame: 8 bytes big-endian offset, then the bytes. */
export function packInline(offset: number, data: ArrayBuffer | Uint8Array): ArrayBuffer {
  const d = data instanceof Uint8Array ? data : new Uint8Array(data);
  const out = new Uint8Array(8 + d.byteLength);
  const v = new DataView(out.buffer);
  v.setUint32(0, Math.floor(offset / 2 ** 32)); v.setUint32(4, offset >>> 0);
  out.set(d, 8);
  return out.buffer;
}
export function unpackInline(buf: ArrayBuffer): { offset: number; data: ArrayBuffer } {
  const v = new DataView(buf);
  return { offset: v.getUint32(0) * 2 ** 32 + v.getUint32(4), data: buf.slice(8) };
}
