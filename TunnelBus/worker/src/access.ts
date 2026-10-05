// Cloudflare Access JWT verification (Cf-Access-Jwt-Assertion), RS256 via WebCrypto.
// Pure functions: only crypto.subtle / fetch / atob, so Node can test them.

export interface AccessConfig {
  /** "myteam.cloudflareaccess.com" (a bare "myteam" is accepted too). */
  teamDomain: string;
  /** The Access application's AUD tag. */
  aud: string;
}

export type Verdict = { ok: true; claims: Record<string, unknown> } | { ok: false; reason: string };

interface Jwk extends JsonWebKey {
  kid?: string;
}
type FetchLike = (url: string) => Promise<{ ok: boolean; status: number; json(): Promise<any> }>;

const keyCache = new Map<string, { at: number; keys: Jwk[] }>();
const KEY_TTL_MS = 10 * 60 * 1000;
const LEEWAY_S = 30;

export function normalizeTeam(t: string): string {
  t = t.trim().replace(/^https?:\/\//, "").replace(/\/+$/, "");
  return t.includes(".") ? t : `${t}.cloudflareaccess.com`;
}

function b64urlToBytes(s: string): Uint8Array<ArrayBuffer> {
  const b = atob(s.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(s.length / 4) * 4, "="));
  const out = new Uint8Array(new ArrayBuffer(b.length));
  for (let i = 0; i < b.length; i++) out[i] = b.charCodeAt(i);
  return out;
}
const dec = (s: string) => JSON.parse(new TextDecoder().decode(b64urlToBytes(s)));

async function getKeys(team: string, force: boolean, f: FetchLike): Promise<Jwk[]> {
  const hit = keyCache.get(team);
  if (hit && !force && Date.now() - hit.at < KEY_TTL_MS) return hit.keys;
  const res = await f(`https://${team}/cdn-cgi/access/certs`);
  if (!res.ok) throw new Error(`certs endpoint returned ${res.status}`);
  const keys = ((await res.json()).keys ?? []) as Jwk[];
  keyCache.set(team, { at: Date.now(), keys });
  return keys;
}

export function clearKeyCache() {
  keyCache.clear();
}

export async function verifyAccessJwt(
  token: string | null | undefined,
  cfg: AccessConfig,
  opts: { fetch?: FetchLike; nowSeconds?: number } = {},
): Promise<Verdict> {
  if (!token) return { ok: false, reason: "missing Cf-Access-Jwt-Assertion" };
  const parts = token.split(".");
  if (parts.length !== 3) return { ok: false, reason: "malformed token" };
  const team = normalizeTeam(cfg.teamDomain);
  const f: FetchLike = opts.fetch ?? ((u) => fetch(u) as any);
  let header: any, claims: any;
  try {
    header = dec(parts[0]);
    claims = dec(parts[1]);
  } catch {
    return { ok: false, reason: "malformed token" };
  }
  if (header.alg !== "RS256") return { ok: false, reason: `unsupported alg ${header.alg}` };
  try {
    let keys = await getKeys(team, false, f);
    let jwk = keys.find((k) => k.kid === header.kid);
    if (!jwk) {
      keys = await getKeys(team, true, f); // key rotation
      jwk = keys.find((k) => k.kid === header.kid);
    }
    if (!jwk) return { ok: false, reason: "unknown signing key" };
    const key = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
    const good = await crypto.subtle.verify(
      "RSASSA-PKCS1-v1_5", key, b64urlToBytes(parts[2]), new TextEncoder().encode(`${parts[0]}.${parts[1]}`));
    if (!good) return { ok: false, reason: "bad signature" };
  } catch (e) {
    return { ok: false, reason: `key error: ${(e as Error).message}` };
  }
  const now = opts.nowSeconds ?? Math.floor(Date.now() / 1000);
  if (claims.iss !== `https://${team}`) return { ok: false, reason: "wrong issuer" };
  const aud: string[] = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (!aud.includes(cfg.aud)) return { ok: false, reason: "wrong audience" };
  if (typeof claims.exp !== "number" || claims.exp + LEEWAY_S < now) return { ok: false, reason: "expired" };
  if (typeof claims.nbf === "number" && claims.nbf - LEEWAY_S > now) return { ok: false, reason: "not yet valid" };
  return { ok: true, claims };
}
