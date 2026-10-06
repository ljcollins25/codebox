import { test } from "node:test";
import assert from "node:assert/strict";
import { handle } from "../src/gate.ts";
import { MANIFEST, SW_JS, PWA_BASE } from "../src/pwa.ts";

const env = { ACCESS_REQUIRED: "true", ACCESS_TEAM_DOMAIN: "t.cloudflareaccess.com", ACCESS_AUD: "aud", BUS_CONTROL_HOST: "ctl.ref12.dev" };
const ok = async () => ({ ok: true as const, claims: { email: "me@example.test" } });
const bad = async () => ({ ok: false as const, reason: "missing Cf-Access-Jwt-Assertion" });
const deps = (verify: any = ok) => ({ forward: async () => new Response("router"), verify });
const get = (u: string, verify?: any, method = "GET") => handle(new Request(u, { method, headers: { "Cf-Access-Jwt-Assertion": "j" } }), env, deps(verify));

test("manifest: standalone, scope and start_url /app/, launch handler, no link capture, three icons", async () => {
  const res = await get("https://ctl.ref12.dev/app/manifest.webmanifest");
  assert.equal(res.status, 200);
  assert.match(res.headers.get("content-type")!, /^application\/manifest\+json/);
  const m = await res.json() as any;
  assert.deepEqual([m.display, m.scope, m.start_url, m.id], ["standalone", "/app/", "/app/", "/app/"]);
  assert.equal(m.handle_links, "not-preferred");
  assert.equal(m.capture_links, undefined);
  assert.deepEqual(m.launch_handler, { client_mode: "focus-existing" });
  assert.equal(m.name, "Tunnel bus");
  assert.deepEqual(m.icons.map((i: any) => [i.sizes, i.purpose]), [["192x192", "any"], ["512x512", "any"], ["512x512", "maskable"]]);
  for (const i of m.icons) assert.ok(i.src.startsWith(PWA_BASE), "icons are inside the scope");
});

test("icons are PNGs, the service worker is JavaScript with a scope header", async () => {
  for (const n of ["icon-192.png", "icon-512.png", "icon-maskable-512.png", "apple-touch-icon.png"]) {
    const res = await get("https://ctl.ref12.dev/app/icons/" + n);
    assert.equal(res.status, 200, n);
    assert.equal(res.headers.get("content-type"), "image/png");
    assert.deepEqual([...new Uint8Array(await res.arrayBuffer()).slice(0, 4)], [137, 80, 78, 71]);
  }
  const sw = await get("https://ctl.ref12.dev/app/sw.js");
  assert.match(sw.headers.get("content-type")!, /^text\/javascript/);
  assert.equal(sw.headers.get("service-worker-allowed"), "/app/");
  assert.equal(await sw.text(), SW_JS);
  assert.equal((await get("https://ctl.ref12.dev/app/icons/nope.png")).status, 404);
  assert.equal((await get("https://ctl.ref12.dev/app/sw.js", undefined, "HEAD")).status, 200);
});

test("everything under /app/ needs the Access JWT like the rest (so the manifest link must send the cookie)", async () => {
  for (const p of ["/app/", "/app/manifest.webmanifest", "/app/sw.js", "/app/icons/icon-192.png"]) {
    assert.equal((await get("https://ctl.ref12.dev" + p, bad)).status, 403, p);
  }
});

test("the page at /app/ has the manifest (credentialed), icons and the registration; / stays the plain dashboard", async () => {
  const app = await (await get("https://ctl.ref12.dev/app/")).text();
  assert.match(app, /<link rel="manifest" href="\/app\/manifest.webmanifest" crossorigin="use-credentials">/);
  assert.match(app, /rel="apple-touch-icon"/);
  assert.match(app, /serviceWorker.*register\("\/app\/sw.js"/);
  const res = await get("https://ctl.ref12.dev/app/");
  assert.match(res.headers.get("content-security-policy")!, /manifest-src 'self'; worker-src 'self'/);
  const root = await (await get("https://ctl.ref12.dev/")).text();
  assert.ok(!root.includes('rel="manifest"') && !root.includes("serviceWorker.register"));
  assert.equal((await get("https://ctl.ref12.dev/app")).status, 308);
});

test("/app/ is only the launcher on the control host: a provider called app elsewhere is untouched", async () => {
  const seen: string[] = [];
  const d = { forward: async (r: Request) => (seen.push(new URL(r.url).pathname), new Response("provider")), verify: ok };
  for (const u of ["https://tunnel-bus.x.workers.dev/app/", "https://app.ref12.dev/app/sw.js"]) {
    const res = await handle(new Request(u, { headers: { "Cf-Access-Jwt-Assertion": "j" } }), env, d);
    assert.equal(await res.text(), "provider", u);
  }
  assert.equal(seen.length, 2);
});

test("service worker: versioned cache, old caches deleted, network-first, never caches or answers Access redirects", () => {
  assert.match(SW_JS, /const CACHE = "bus-static-v\d+"/);
  assert.match(SW_JS, /ks\.filter\(\(k\) => k !== CACHE\)\.map\(\(k\) => caches\.delete\(k\)\)/);
  assert.match(SW_JS, /cloudflareaccess\.com/);
  assert.match(SW_JS, /opaqueredirect/);
  assert.match(SW_JS, /res\.redirected/);
  assert.match(SW_JS, /url\.origin !== self\.location\.origin\) return/);
  assert.match(SW_JS, /unreachable/);
});

// Run the worker's fetch logic against fakes: navigations and the API are never cached; an Access redirect goes through.
test("service worker behaviour with fakes", async () => {
  const listeners: Record<string, Function> = {};
  const store = new Map<string, Response>();
  const cache = { match: async (r: Request) => store.get(r.url), put: async (r: any, res: Response) => void store.set(typeof r === "string" ? r : r.url, res) };
  let net: (r: Request) => Promise<Response> = async () => new Response("x");
  const ctx: any = { self: { location: { href: "https://ctl.ref12.dev/app/sw.js", origin: "https://ctl.ref12.dev" }, addEventListener: (n: string, f: Function) => (listeners[n] = f), skipWaiting() {}, clients: { claim() {} } },
    caches: { open: async () => cache, keys: async () => [], delete: async () => true }, fetch: (r: any) => net(typeof r === "string" ? new Request("https://ctl.ref12.dev" + r) : r), Response, URL, Request };
  new Function(...Object.keys(ctx), SW_JS)(...Object.values(ctx));
  const run = async (req: Request) => { let out: any; listeners.fetch({ request: req, respondWith: (p: any) => (out = p) }); return out && (await out); };
  const nav = (u: string) => { const r = new Request(u); Object.defineProperty(r, "mode", { value: "navigate" }); return r; };

  net = async () => { throw new TypeError("offline"); };
  const off = await run(nav("https://ctl.ref12.dev/app/"));
  assert.equal(off.status, 503); assert.match(await off.text(), /unreachable/);
  const api = await run(new Request("https://ctl.ref12.dev/_api/providers"));
  assert.equal(api.status, 503);

  const redirect = new Response(null, { status: 302, headers: { location: "https://t.cloudflareaccess.com/login" } });
  net = async () => redirect;
  assert.equal(await run(nav("https://ctl.ref12.dev/app/")), redirect, "an Access redirect is handed to the browser as is");
  assert.equal(store.size, 0, "nothing cached");

  assert.equal(await run(new Request("https://t.cloudflareaccess.com/cdn-cgi/access/login")), undefined, "other origins are not touched");
  assert.equal(await run(new Request("https://ctl.ref12.dev/app/x", { method: "POST" })), undefined);

  net = async () => new Response("<html>Sign in with GitHub</html>", { headers: { "content-type": "text/html" } });
  await run(new Request("https://ctl.ref12.dev/app/icons/icon-192.png"));
  assert.equal(store.size, 0, "a login page in place of an icon is never stored");
  net = async () => new Response(new Uint8Array([1, 2]), { headers: { "content-type": "image/png" } });
  await run(new Request("https://ctl.ref12.dev/app/icons/icon-512.png"));
  assert.equal(store.size, 1, "a real icon is stored");
});
