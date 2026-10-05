// Request gate: Access verification, identity headers, and the dashboard page. Pure (no cloudflare:* imports),
// so Node can test it. index.ts supplies the real container forwarder.
import { verifyAccessJwt, type AccessConfig } from "./access.ts";
import { UI_HTML } from "./ui.ts";

export interface GateEnv {
  ACCESS_REQUIRED?: string;
  ACCESS_TEAM_DOMAIN?: string;
  ACCESS_AUD?: string;
  BUS_CONTROL_HOST?: string;
}

export interface GateDeps {
  forward(request: Request): Promise<Response>;
  verify?: typeof verifyAccessJwt;
}

const UI_HEADERS = {
  "Content-Type": "text/html; charset=utf-8",
  "Cache-Control": "no-store",
  // The page is self-contained: no external scripts, styles, frames or requests.
  "Content-Security-Policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
};

/** The dashboard lives at /_ui everywhere and at / on the control host. */
export function isUiRequest(url: URL, env: GateEnv): boolean {
  if (url.pathname === "/_ui" || url.pathname === "/_ui/") return true;
  return url.pathname === "/" && !!env.BUS_CONTROL_HOST && url.hostname.toLowerCase() === env.BUS_CONTROL_HOST.toLowerCase();
}

export async function handle(request: Request, env: GateEnv, deps: GateDeps): Promise<Response> {
  const verify = deps.verify ?? verifyAccessJwt;
  let email = "";
  if (env.ACCESS_REQUIRED === "true") {
    // Nothing is exempt: not the page, not /_health, not the API.
    if (!env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD) {
      return new Response("tunnel bus: ACCESS_REQUIRED is set but ACCESS_TEAM_DOMAIN/ACCESS_AUD are not", { status: 500 });
    }
    const cfg: AccessConfig = { teamDomain: env.ACCESS_TEAM_DOMAIN, aud: env.ACCESS_AUD };
    const v = await verify(request.headers.get("Cf-Access-Jwt-Assertion"), cfg);
    if (!v.ok) return new Response(`tunnel bus: access denied (${v.reason})`, { status: 403 });
    if (typeof v.claims.email === "string") email = v.claims.email.toLowerCase(); // service tokens have no email
  }
  const url = new URL(request.url);
  if (isUiRequest(url, env)) {
    if (request.method !== "GET" && request.method !== "HEAD") return new Response("method not allowed", { status: 405 });
    return new Response(request.method === "HEAD" ? null : UI_HTML, { headers: UI_HEADERS });
  }
  // The router gets the public host and the verified identity. Whatever the client sent for these is overwritten.
  const fwd = new Request(request);
  fwd.headers.set("X-Bus-Host", url.host);
  fwd.headers.delete("X-Bus-User");
  if (email) fwd.headers.set("X-Bus-User", email);
  return deps.forward(fwd);
}
