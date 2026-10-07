import { test } from "node:test";
import assert from "node:assert/strict";
import { handle, isUiRequest } from "../src/gate.ts";
import { UI_HTML } from "../src/ui.ts";

const env = { ACCESS_REQUIRED: "true", ACCESS_TEAM_DOMAIN: "t.cloudflareaccess.com", ACCESS_AUD: "aud", BUS_CONTROL_HOST: "ctl.ref12.dev" };
const ok = (claims: Record<string, unknown>) => async () => ({ ok: true as const, claims });
const bad = async () => ({ ok: false as const, reason: "bad signature" });

function recorder() {
  const seen: Request[] = [];
  return { seen, forward: async (r: Request) => (seen.push(r), new Response("from router")) };
}

test("the dashboard is served at / on the control host and at /_ui anywhere, with a locked-down CSP", async () => {
  for (const u of ["https://ctl.ref12.dev/", "https://tunnel-bus.x.workers.dev/_ui"]) {
    const rec = recorder();
    const res = await handle(new Request(u, { headers: { "Cf-Access-Jwt-Assertion": "j" } }), env, { forward: rec.forward, verify: ok({ email: "Me@Example.com" }) });
    assert.equal(res.status, 200, u);
    assert.match(res.headers.get("content-type")!, /text\/html/);
    assert.match(res.headers.get("content-security-policy")!, /default-src 'none'/);
    assert.match(res.headers.get("content-security-policy")!, /frame-ancestors 'none'/);
    assert.equal(await res.text(), UI_HTML);
    assert.equal(rec.seen.length, 0, "page must not reach the router");
  }
});

test("the page is refused without a valid Access JWT", async () => {
  for (const verify of [bad, async () => ({ ok: false as const, reason: "missing Cf-Access-Jwt-Assertion" })]) {
    const rec = recorder();
    const res = await handle(new Request("https://ctl.ref12.dev/"), env, { forward: rec.forward, verify });
    assert.equal(res.status, 403);
    assert.ok(!(await res.text()).includes("<html"));
    assert.equal(rec.seen.length, 0);
  }
});

test("/ on other hosts is not the page (path routing and provider hosts keep working)", () => {
  assert.equal(isUiRequest(new URL("https://hexad.ref12.dev/"), env), false);
  assert.equal(isUiRequest(new URL("https://ctl.ref12.dev/other"), env), false);
  assert.equal(isUiRequest(new URL("https://tunnel-bus.x.workers.dev/"), env), false);
  assert.equal(isUiRequest(new URL("https://ctl.ref12.dev/_ui"), env), true);
});

test("the verified email reaches the router as X-Bus-User; a client-sent value is overwritten or dropped", async () => {
  let rec = recorder();
  await handle(new Request("https://ctl.ref12.dev/_api/providers", { headers: { "X-Bus-User": "evil@x.com", "Cf-Access-Jwt-Assertion": "j" } }), env, { forward: rec.forward, verify: ok({ email: "Me@Example.com" }) });
  assert.equal(rec.seen[0].headers.get("X-Bus-User"), "me@example.com");
  assert.equal(rec.seen[0].headers.get("X-Bus-Host"), "ctl.ref12.dev");
  // service token: no email claim, so a forged header must not survive
  rec = recorder();
  await handle(new Request("https://ctl.ref12.dev/_api/providers", { headers: { "X-Bus-User": "me@example.com", "Cf-Access-Jwt-Assertion": "j" } }), env, { forward: rec.forward, verify: ok({ common_name: "svc" }) });
  assert.equal(rec.seen[0].headers.get("X-Bus-User"), null);
  // Access off: nobody is verified, the header is still stripped
  rec = recorder();
  await handle(new Request("https://x.workers.dev/_api/providers", { headers: { "X-Bus-User": "me@example.com" } }), { ...env, ACCESS_REQUIRED: "false" }, { forward: rec.forward });
  assert.equal(rec.seen[0].headers.get("X-Bus-User"), null);
});

test("the page contains no secrets and makes no external requests", () => {
  for (const m of UI_HTML.matchAll(/(?:src|href)=["'](https?:)?\/\//g)) assert.fail("external reference: " + m[0]);
  assert.ok(!/ADMIN_TOKEN|Bearer/.test(UI_HTML));
});

test("misconfigured Access fails closed", async () => {
  const res = await handle(new Request("https://ctl.ref12.dev/"), { ACCESS_REQUIRED: "true" }, { forward: recorder().forward });
  assert.equal(res.status, 500);
});

test("p- hosts go to the pipe service binding, not the container; other hosts are unaffected", async () => {
  const rec = recorder(); const piped: string[] = [];
  const e = { ...env, BUS_BASE_DOMAIN: "ref12.dev" };
  const deps = { forward: rec.forward, pipe: async (r: Request) => (piped.push(new URL(r.url).host), new Response("from pipe")), verify: ok({ email: "a@b.c" }) };
  const get = (u: string) => handle(new Request(u, { headers: { "Cf-Access-Jwt-Assertion": "j" } }), e, deps);
  assert.equal(await (await get("https://p-app.ref12.dev/x")).text(), "from pipe");
  assert.equal(await (await get("https://3000--p-app.ref12.dev/x")).text(), "from pipe");
  for (const u of ["https://app.ref12.dev/", "https://hexad-project.ref12.dev/", "https://3000--app.ref12.dev/", "https://pipe.ref12.dev/", "https://ctl.ref12.dev/_api/registry", "https://x.p-y.ref12.dev/"]) assert.equal(await (await get(u)).text(), "from router", u);
  assert.deepEqual(piped, ["p-app.ref12.dev", "3000--p-app.ref12.dev"]);
});

test("registering a name that starts with p- is refused before the container sees it", async () => {
  const rec = recorder();
  const post = (name: string) => handle(new Request("https://ctl.ref12.dev/_api/register", { method: "POST", body: JSON.stringify({ name }), headers: { "Cf-Access-Jwt-Assertion": "j" } }), env, { forward: rec.forward, verify: ok({ email: "a@b.c" }) });
  assert.equal((await post("p-x")).status, 400);
  assert.equal(rec.seen.length, 0);
  assert.equal((await post("px")).status, 200);
});

test("/_api/pipe/* reaches the pipe Worker as /_api/*", async () => {
  const seen: string[] = [];
  const res = await handle(new Request("https://ctl.ref12.dev/_api/pipe/registry", { headers: { "Cf-Access-Jwt-Assertion": "j" } }), env, { forward: recorder().forward, pipe: async (r) => (seen.push(new URL(r.url).pathname), new Response("[]")), verify: ok({ email: "a@b.c" }) });
  assert.equal(res.status, 200); assert.deepEqual(seen, ["/_api/registry"]);
});
