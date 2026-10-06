// Request gate: Access verification, identity headers, and the dashboard page. Pure (no cloudflare:* imports),
// so Node can test it. index.ts supplies the real container forwarder.
import { verifyAccessJwt, type AccessConfig } from "./access.ts";
import { UI_HTML } from "./ui.ts";
import { displayName } from "./identity.ts";
import { PWA_BASE, PWA_HEAD, pwaAsset } from "./pwa.ts";

export interface GateEnv {
  ACCESS_REQUIRED?: string;
  ACCESS_TEAM_DOMAIN?: string;
  ACCESS_AUD?: string;
  BUS_CONTROL_HOST?: string;
  /** "clientid=label,...": names for service tokens on the dashboard (deploy-time variable). */
  BUS_SERVICE_NAMES?: string;
}

export interface GateDeps {
  forward(request: Request): Promise<Response>;
  verify?: typeof verifyAccessJwt;
  fetchIdentity?: Parameters<typeof displayName>[2]["fetch"];
}

const UI_HEADERS = {
  "Content-Type": "text/html; charset=utf-8",
  "Cache-Control": "no-store",
  // The page is self-contained: no external scripts, styles, frames or requests.
  "Content-Security-Policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; manifest-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
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
  let display = "";
  if (env.ACCESS_REQUIRED === "true") {
    // Nothing is exempt: not the page, not /_health, not the API.
    if (!env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD) {
      return new Response("tunnel bus: ACCESS_REQUIRED is set but ACCESS_TEAM_DOMAIN/ACCESS_AUD are not", { status: 500 });
    }
    const cfg: AccessConfig = { teamDomain: env.ACCESS_TEAM_DOMAIN, aud: env.ACCESS_AUD };
    const v = await verify(request.headers.get("Cf-Access-Jwt-Assertion"), cfg);
    if (!v.ok) return new Response(`tunnel bus: access denied (${v.reason})`, { status: 403 });
    if (typeof v.claims.email === "string") email = v.claims.email.toLowerCase(); // service tokens have no email
    if (new URL(request.url).pathname === "/_api/status") {
      // Only the dashboard's status call needs a name; skip the identity lookup elsewhere.
      display = await displayName(request.headers.get("Cf-Access-Jwt-Assertion") ?? "", v.claims, { teamDomain: env.ACCESS_TEAM_DOMAIN, serviceNames: env.BUS_SERVICE_NAMES, fetch: deps.fetchIdentity });
    }
  }
  const url = new URL(request.url);
  const onControlHost = !!env.BUS_CONTROL_HOST && url.hostname.toLowerCase() === env.BUS_CONTROL_HOST.toLowerCase();
  if (onControlHost && (url.pathname === "/app" || url.pathname.startsWith(PWA_BASE))) {
    // The installable launcher (scope /app/) and its manifest, service worker and icons. Only the control host:
    // on provider hosts and workers.dev a provider may be called "app".
    if (request.method !== "GET" && request.method !== "HEAD") return new Response("method not allowed", { status: 405 });
    if (url.pathname === "/app") return Response.redirect(url.origin + PWA_BASE, 308);
    if (url.pathname === PWA_BASE) {
      return new Response(request.method === "HEAD" ? null : UI_HTML.replace("<!--PWA-->", PWA_HEAD), { headers: UI_HEADERS });
    }
    return pwaAsset(url.pathname, request.method)!;
  }
  if (isUiRequest(url, env)) {
    if (request.method !== "GET" && request.method !== "HEAD") return new Response("method not allowed", { status: 405 });
    return new Response(request.method === "HEAD" ? null : UI_HTML, { headers: UI_HEADERS });
  }
  // The router gets the public host and the verified identity. Whatever the client sent for these is overwritten.
  const fwd = new Request(request);
  fwd.headers.set("X-Bus-Host", url.host);
  fwd.headers.delete("X-Bus-User");
  fwd.headers.delete("X-Bus-Display");
  if (email) fwd.headers.set("X-Bus-User", email);
  // The name to show (GitHub login, else display name, else email), URI-encoded because it may not be ASCII.
  if (display) fwd.headers.set("X-Bus-Display", encodeURIComponent(display));
  return deps.forward(fwd);
}
