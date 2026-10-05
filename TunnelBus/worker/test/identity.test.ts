import { test } from "node:test";
import assert from "node:assert/strict";
import { pickDisplay, displayName, parseServiceNames, clearIdentityCache } from "../src/identity.ts";
import { handle } from "../src/gate.ts";

// Fixtures: the SHAPE of a get-identity document (Cloudflare docs, "extend SSO with Workers"): id, name, email,
// idp { id, type }, geo, user_uuid ... All values here are made up. The GitHub-specific claim names are not
// documented, so the login is looked up under the usual keys; see identity.ts.
const base = { id: "id-1", name: "Pat Example", email: "pat@example.test", idp: { id: "idp-1", type: "github" }, geo: { country: "XX" }, user_uuid: "u-1" };

test("login wins, wherever the IdP puts it", () => {
  assert.deepEqual(pickDisplay({ ...base, login: "patex" }), { name: "patex", from: "login" });
  assert.deepEqual(pickDisplay({ ...base, user_name: "patex" }), { name: "patex", from: "login" });
  assert.deepEqual(pickDisplay({ ...base, oidc_fields: { preferred_username: "patex" } }), { name: "patex", from: "login" });
  assert.deepEqual(pickDisplay({ ...base, custom: { login: "patex" } }), { name: "patex", from: "login" });
});

test("fallbacks: display name, then email; an email or a name with spaces is never taken as a login", () => {
  assert.deepEqual(pickDisplay(base), { name: "Pat Example", from: "name" });
  assert.deepEqual(pickDisplay({ ...base, login: "pat@example.test" }), { name: "Pat Example", from: "name" });
  assert.deepEqual(pickDisplay({ ...base, name: "" }), { name: "pat@example.test", from: "email" });
  assert.deepEqual(pickDisplay({ ...base, name: "pat@example.test" }), { name: "pat@example.test", from: "email" });
  assert.deepEqual(pickDisplay({}, "jwt@example.test"), { name: "jwt@example.test", from: "email" });
  assert.deepEqual(pickDisplay(null), { name: "", from: "none" });
});

const claims = { email: "Pat@Example.test", sub: "sub-1", iat: 100 };
const idFetch = (doc: any, calls: string[] = []) => async (url: string, init?: any) => {
  calls.push(url + "|" + init?.headers?.Cookie);
  return { ok: true, status: 200, json: async () => doc };
};

test("the identity endpoint is called with the session JWT as the CF_Authorization cookie, and cached by sub+iat", async () => {
  clearIdentityCache();
  const calls: string[] = [];
  const o = { teamDomain: "team", fetch: idFetch({ ...base, login: "patex" }, calls) };
  assert.equal(await displayName("jwt-a", claims, o), "patex");
  assert.equal(await displayName("jwt-a", claims, o), "patex");
  assert.deepEqual(calls, ["https://team.cloudflareaccess.com/cdn-cgi/access/get-identity|CF_Authorization=jwt-a"]);
  assert.equal(await displayName("jwt-b", { ...claims, iat: 200 }, o), "patex"); // a new session fetches again
  assert.equal(calls.length, 2);
});

test("endpoint failures fall back to the email and are not cached", async () => {
  clearIdentityCache();
  let n = 0;
  const failing = async () => { n++; throw new Error("down"); };
  assert.equal(await displayName("j", claims, { teamDomain: "t", fetch: failing }), "pat@example.test");
  const notOk = async () => ({ ok: false, status: 403, json: async () => ({}) });
  assert.equal(await displayName("j", claims, { teamDomain: "t", fetch: notOk }), "pat@example.test");
  assert.equal(await displayName("j", claims, { teamDomain: "t", fetch: idFetch({ ...base, login: "patex" }) }), "patex");
  assert.equal(n, 1);
});

test("service tokens show as service: <name>, never the token id", async () => {
  clearIdentityCache();
  const o = { teamDomain: "t", serviceNames: "abc123.access=tunnel-bus-hexad", fetch: async () => { throw new Error("must not be called"); } };
  assert.equal(await displayName("j", { type: "app", common_name: "ABC123.access", sub: "" }, o), "service: tunnel-bus-hexad");
  const unknown = await displayName("j", { type: "app", common_name: "zzz999.access", sub: "" }, o);
  assert.equal(unknown, "service token");
  assert.ok(!unknown.includes("zzz999"));
  assert.equal(parseServiceNames("a=b, c = d ,bad").size, 2);
});

test("the gate sends the display name, not the email, to the router for the status call; other calls skip the lookup", async () => {
  clearIdentityCache();
  const env = { ACCESS_REQUIRED: "true", ACCESS_TEAM_DOMAIN: "t.cloudflareaccess.com", ACCESS_AUD: "aud" };
  const seen: Request[] = [];
  const forward = async (r: Request) => (seen.push(r), new Response("ok"));
  const calls: string[] = [];
  const verify = async () => ({ ok: true as const, claims });
  const hdr = { "Cf-Access-Jwt-Assertion": "jwt", "X-Bus-Display": "forged" };
  await handle(new Request("https://ctl.x/_api/status", { headers: hdr }), env, { forward, verify, fetchIdentity: idFetch({ ...base, login: "pat ex" }, calls) });
  assert.equal(decodeURIComponent(seen[0].headers.get("X-Bus-Display")!), "Pat Example");
  assert.equal(seen[0].headers.get("X-Bus-User"), "pat@example.test"); // authorisation still uses the email
  await handle(new Request("https://ctl.x/_api/providers", { headers: hdr }), env, { forward, verify, fetchIdentity: idFetch({}, calls) });
  assert.equal(seen[1].headers.get("X-Bus-Display"), null, "client-sent value is dropped");
});
