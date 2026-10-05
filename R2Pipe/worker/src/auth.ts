import { verifyAccessJwt } from "./access";

export interface AuthEnv {
  ACCESS_TEAM_DOMAIN?: string;
  ACCESS_AUD?: string;
  /** Optional shared secret for programs that cannot pass Access (workers.dev testing). */
  ADMIN_TOKEN?: string;
}

function timingEqual(a: string, b: string): boolean {
  const ea = new TextEncoder().encode(a), eb = new TextEncoder().encode(b);
  let d = ea.length ^ eb.length;
  for (let i = 0; i < Math.max(ea.length, eb.length); i++) d |= (ea[i] ?? 0) ^ (eb[i] ?? 0);
  return d === 0;
}

/** Access JWT (browser login or service token, verified here as well as at the edge) or the admin bearer token. */
export async function authenticate(req: Request, env: AuthEnv): Promise<{ ok: true; who: string } | { ok: false; reason: string }> {
  const auth = req.headers.get("authorization");
  if (env.ADMIN_TOKEN && auth?.startsWith("Bearer ") && timingEqual(auth.slice(7), env.ADMIN_TOKEN)) return { ok: true, who: "admin-token" };
  if (!env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD) return { ok: false, reason: "Access is not configured" };
  const v = await verifyAccessJwt(req.headers.get("cf-access-jwt-assertion"), { teamDomain: env.ACCESS_TEAM_DOMAIN, aud: env.ACCESS_AUD });
  if (!v.ok) return { ok: false, reason: v.reason };
  const c = v.claims as { email?: string; common_name?: string; sub?: string };
  return { ok: true, who: c.email ?? c.common_name ?? c.sub ?? "access" };
}
