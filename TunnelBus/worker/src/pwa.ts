// The installable launcher: web app manifest, service worker and icons, served under /app/ on the control host only.
// Everything lives under /app/ so the PWA's scope is /app/: links to the dashboard at / and to every app host
// (<name>.<domain>) are out of scope and are not captured by the installed app.
// Pure (no cloudflare:* imports), so Node can test it.
import { ICONS } from "./icons.ts";

export const PWA_BASE = "/app/";
/** Bump to drop old caches: the service worker deletes every cache that is not CACHE. */
export const SW_VERSION = "1";

export const MANIFEST = {
  id: PWA_BASE,
  name: "Tunnel bus",
  short_name: "Tunnel bus",
  description: "What is available on the tunnel bus, one tap away.",
  start_url: PWA_BASE,
  scope: PWA_BASE,
  display: "standalone",
  background_color: "#0e1116",
  theme_color: "#0e1116",
  // Do not claim links to this app: they should open in the browser (Android may still register the scope for a WebAPK).
  handle_links: "not-preferred",
  launch_handler: { client_mode: "focus-existing" },
  icons: [
    { src: PWA_BASE + "icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
    { src: PWA_BASE + "icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
    { src: PWA_BASE + "icons/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
  ],
};

/** Tags added to the page when it is served under /app/ (the manifest link sends the Access cookie). */
export const PWA_HEAD = `<link rel="manifest" href="${PWA_BASE}manifest.webmanifest" crossorigin="use-credentials">
<meta name="theme-color" content="#0e1116">
<meta name="mobile-web-app-capable" content="yes"><meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="Tunnel bus"><meta name="apple-mobile-web-app-status-bar-style" content="black">
<link rel="icon" type="image/png" sizes="192x192" href="${PWA_BASE}icons/icon-192.png">
<link rel="apple-touch-icon" href="${PWA_BASE}icons/apple-touch-icon.png">
<script>if("serviceWorker" in navigator)addEventListener("load",function(){navigator.serviceWorker.register("${PWA_BASE}sw.js",{scope:"${PWA_BASE}"}).catch(function(){})})</script>`;

export const SW_JS = `// Tunnel bus service worker (v${SW_VERSION}). Network-first for the page and the API; only static assets are cached.
// Nothing that could be a Cloudflare Access redirect or login page is ever cached or answered from the cache.
const CACHE = "bus-static-v${SW_VERSION}";
const BASE = "${PWA_BASE}";
const STATIC = [BASE + "icons/icon-192.png", BASE + "icons/icon-512.png", BASE + "icons/icon-maskable-512.png", BASE + "icons/apple-touch-icon.png"];
const OFFLINE = '<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark"><title>Tunnel bus: offline</title>' +
  '<body style="margin:0;background:#0e1116;color:#e6edf3;font:16px system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;text-align:center"><div style="padding:1.5rem;max-width:24rem">' +
  '<h1 style="font-size:1.3rem">The bus is unreachable</h1><p style="color:#8b949e">This device is offline or the tunnel bus is not answering. Nothing is shown from memory, because the list changes all the time.</p>' +
  '<button onclick="location.reload()" style="font:inherit;color:#e6edf3;background:#1f6feb;border:0;border-radius:6px;padding:.6rem 1.2rem">Try again</button></div>';

// A response must never be stored or reused when it could be part of an Access login.
function isAccessResponse(res) {
  if (!res) return true;
  if (res.type === "opaque" || res.type === "opaqueredirect" || res.type === "error") return true;
  if (res.redirected || (res.status >= 300 && res.status < 400)) return true;
  try { const u = new URL(res.url || self.location.href); if (u.hostname.endsWith(".cloudflareaccess.com") || u.pathname.indexOf("/cdn-cgi/access/") === 0) return true; } catch (e) { return true; }
  const ct = res.headers.get("content-type") || "";
  return res.status !== 200 || ct.indexOf("text/html") === 0; // HTML where an icon should be = a login page
}

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => Promise.all(STATIC.map((u) => fetch(u, { credentials: "include", redirect: "manual" }).then((r) => (isAccessResponse(r) ? null : c.put(u, r))).catch(() => null)))).then(() => self.skipWaiting()));
});
self.addEventListener("activate", (e) => {
  e.waitUntil(caches.keys().then((ks) => Promise.all(ks.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener("fetch", (e) => {
  const req = e.request, url = new URL(req.url);
  if (req.method !== "GET" || url.origin !== self.location.origin) return; // Access login pages are another origin: never touched
  if (req.mode === "navigate") {
    // network-first; an Access redirect comes back as an opaque redirect and is handed to the browser untouched
    e.respondWith(fetch(req).catch(() => new Response(OFFLINE, { status: 503, headers: { "Content-Type": "text/html; charset=utf-8" } })));
    return;
  }
  if (url.pathname.indexOf("/_api/") === 0) {
    e.respondWith(fetch(req).catch(() => new Response(JSON.stringify({ error: "the bus is unreachable" }), { status: 503, headers: { "Content-Type": "application/json" } })));
    return;
  }
  if (url.pathname.indexOf(BASE + "icons/") === 0) {
    e.respondWith(caches.open(CACHE).then((c) => c.match(req).then((hit) => hit || fetch(req).then((r) => { if (!isAccessResponse(r)) c.put(req, r.clone()); return r; }))));
  }
});
`;

const b64 = (s: string) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
const BASE_HEADERS = { "Cache-Control": "no-cache", "X-Content-Type-Options": "nosniff" };

/** The /app/ assets (not the page itself). null = not a PWA asset. */
export function pwaAsset(pathname: string, method: string): Response | null {
  if (!pathname.startsWith(PWA_BASE) || pathname === PWA_BASE) return null;
  const head = method === "HEAD";
  const sub = pathname.slice(PWA_BASE.length);
  if (sub === "manifest.webmanifest") {
    return new Response(head ? null : JSON.stringify(MANIFEST), { headers: { ...BASE_HEADERS, "Content-Type": "application/manifest+json" } });
  }
  if (sub === "sw.js") {
    return new Response(head ? null : SW_JS, { headers: { ...BASE_HEADERS, "Content-Type": "text/javascript; charset=utf-8", "Service-Worker-Allowed": PWA_BASE, "Content-Security-Policy": "default-src 'none'; connect-src 'self'" } });
  }
  const m = /^icons\/([a-z0-9-]+\.png)$/.exec(sub);
  if (m && ICONS[m[1]]) {
    return new Response(head ? null : b64(ICONS[m[1]]), { headers: { "Content-Type": "image/png", "Cache-Control": "public, max-age=86400", "X-Content-Type-Options": "nosniff" } });
  }
  return new Response("not found", { status: 404 });
}
