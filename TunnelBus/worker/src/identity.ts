// Who is signed in, for display: the GitHub login if Access's identity endpoint has one, else the display
// name, else the email (last resort). Service tokens show as "service: <name>", never the token id.
// Pure (fetch is injected), so Node can test it.
import { normalizeTeam } from "./access.ts";

type FetchLike = (url: string, init?: { headers?: Record<string, string> }) => Promise<{ ok: boolean; status: number; json(): Promise<any> }>;

const str = (v: unknown): string => (typeof v === "string" ? v.trim() : "");
const LOGIN_KEYS = ["login", "user_name", "username", "preferred_username", "nickname"];

/**
 * Pick the display name from a get-identity document.
 * Documented shape (Cloudflare docs, "extend SSO with Workers"): { id, name, email, idp: { id, type }, user_uuid, ...,
 * oidc_fields?, custom? }. The docs do not list the GitHub-specific claim names, so the login is looked for under the
 * usual keys at the top level, in oidc_fields / custom / idp_claims, in that order; then "name", then the email.
 * Returns the source too, so tests (and a log line) can say which one won.
 */
export function pickDisplay(id: any, jwtEmail = ""): { name: string; from: "login" | "name" | "email" | "none" } {
  if (id && typeof id === "object") {
    const bags = [id, id.oidc_fields, id.custom, id.idp_claims, id.github].filter((b) => b && typeof b === "object");
    for (const bag of bags) for (const k of LOGIN_KEYS) {
      const v = str(bag[k]);
      if (v && !v.includes("@") && !/\s/.test(v)) return { name: v, from: "login" };
    }
    const name = str(id.name);
    if (name && name.toLowerCase() !== str(id.email).toLowerCase()) return { name, from: "name" };
    const email = str(id.email) || jwtEmail;
    if (email) return { name: email, from: "email" };
  }
  return jwtEmail ? { name: jwtEmail, from: "email" } : { name: "", from: "none" };
}

/** BUS_SERVICE_NAMES: "clientid=label,clientid2=label2" (a deploy-time variable; client ids are not shown). */
export function parseServiceNames(s: string | undefined): Map<string, string> {
  const m = new Map<string, string>();
  for (const part of (s ?? "").split(",")) {
    const i = part.indexOf("=");
    if (i > 0) m.set(part.slice(0, i).trim().toLowerCase(), part.slice(i + 1).trim());
  }
  return m;
}

const cache = new Map<string, { at: number; value: string }>();
const TTL_MS = 10 * 60 * 1000;
export function clearIdentityCache() {
  cache.clear();
}

export interface IdentityOptions {
  teamDomain: string;
  serviceNames?: string;
  fetch?: FetchLike;
  now?: () => number;
}

/** claims: the verified JWT claims. jwt: the raw token (sent as the CF_Authorization cookie to get-identity). */
export async function displayName(jwt: string, claims: Record<string, any>, o: IdentityOptions): Promise<string> {
  const email = str(claims.email).toLowerCase();
  const common = str(claims.common_name);
  if (!email && (common || claims.type === "app")) {
    const label = parseServiceNames(o.serviceNames).get(common.toLowerCase());
    return label ? `service: ${label}` : "service token";
  }
  const now = (o.now ?? Date.now)();
  const key = `${str(claims.sub) || email}:${claims.iat ?? ""}`;
  const hit = cache.get(key);
  if (hit && now - hit.at < TTL_MS) return hit.value;
  let value = email;
  try {
    const f: FetchLike = o.fetch ?? ((u, i) => fetch(u, i) as any);
    const res = await f(`https://${normalizeTeam(o.teamDomain)}/cdn-cgi/access/get-identity`, { headers: { Cookie: `CF_Authorization=${jwt}` } });
    if (!res.ok) return email; // not cached: try again next time
    value = pickDisplay(await res.json(), email).name || email;
  } catch {
    // identity endpoint unavailable: fall back to the email, and do not cache the failure
    return email;
  }
  if (cache.size > 200) cache.clear();
  cache.set(key, { at: now, value });
  return value;
}
