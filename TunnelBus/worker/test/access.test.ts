import { test } from "node:test";
import assert from "node:assert/strict";
import { verifyAccessJwt, clearKeyCache } from "../src/access.ts";

const TEAM = "myteam.cloudflareaccess.com";
const AUD = "app-aud-123";
const NOW = 1_800_000_000;
const b64u = (b: ArrayBuffer | Uint8Array | string) =>
  Buffer.from(typeof b === "string" ? b : new Uint8Array(b as ArrayBuffer)).toString("base64url");

async function makeKey(kid: string) {
  const pair = await crypto.subtle.generateKey(
    { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" }, true, ["sign", "verify"]);
  const jwk = { ...(await crypto.subtle.exportKey("jwk", pair.publicKey)), kid, alg: "RS256", use: "sig" };
  return { priv: pair.privateKey, jwk, kid };
}
async function sign(k: { priv: CryptoKey; kid: string }, claims: object, header: object = {}) {
  const h = b64u(JSON.stringify({ alg: "RS256", kid: k.kid, typ: "JWT", ...header }));
  const p = b64u(JSON.stringify(claims));
  const sig = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", k.priv, new TextEncoder().encode(`${h}.${p}`));
  return `${h}.${p}.${b64u(sig)}`;
}
const good = (over: object = {}) => ({ iss: `https://${TEAM}`, aud: [AUD], exp: NOW + 300, iat: NOW, email: "me@example.com", ...over });

function certsFetch(keys: object[], counter = { n: 0 }) {
  return Object.assign(async (url: string) => {
    counter.n++;
    assert.equal(url, `https://${TEAM}/cdn-cgi/access/certs`);
    return { ok: true, status: 200, json: async () => ({ keys }) };
  }, { counter });
}
const cfg = { teamDomain: TEAM, aud: AUD };

test("valid token is accepted", async () => {
  clearKeyCache();
  const k = await makeKey("k1");
  const v = await verifyAccessJwt(await sign(k, good()), cfg, { fetch: certsFetch([k.jwk]), nowSeconds: NOW });
  assert.equal(v.ok, true);
  if (v.ok) assert.equal(v.claims.email, "me@example.com");
});

test("bare team name and string aud are accepted", async () => {
  clearKeyCache();
  const k = await makeKey("k1");
  const v = await verifyAccessJwt(await sign(k, good({ aud: AUD })), { teamDomain: "myteam", aud: AUD }, { fetch: certsFetch([k.jwk]), nowSeconds: NOW });
  assert.equal(v.ok, true);
});

test("missing and malformed tokens are rejected", async () => {
  clearKeyCache();
  const f = certsFetch([]);
  assert.deepEqual(await verifyAccessJwt(undefined, cfg, { fetch: f }), { ok: false, reason: "missing Cf-Access-Jwt-Assertion" });
  assert.equal((await verifyAccessJwt("a.b", cfg, { fetch: f })).ok, false);
  assert.equal((await verifyAccessJwt("a.b.c", cfg, { fetch: f })).ok, false);
});

test("expired token is rejected, small clock skew is tolerated", async () => {
  clearKeyCache();
  const k = await makeKey("k1");
  const f = certsFetch([k.jwk]);
  const v = await verifyAccessJwt(await sign(k, good({ exp: NOW - 100 })), cfg, { fetch: f, nowSeconds: NOW });
  assert.deepEqual(v, { ok: false, reason: "expired" });
  assert.equal((await verifyAccessJwt(await sign(k, good({ exp: NOW - 10 })), cfg, { fetch: f, nowSeconds: NOW })).ok, true);
});

test("wrong audience and wrong issuer are rejected", async () => {
  clearKeyCache();
  const k = await makeKey("k1");
  const f = certsFetch([k.jwk]);
  assert.deepEqual(await verifyAccessJwt(await sign(k, good({ aud: ["other"] })), cfg, { fetch: f, nowSeconds: NOW }), { ok: false, reason: "wrong audience" });
  assert.deepEqual(await verifyAccessJwt(await sign(k, good({ iss: "https://evil.cloudflareaccess.com" })), cfg, { fetch: f, nowSeconds: NOW }), { ok: false, reason: "wrong issuer" });
});

test("token signed by another key, or tampered, is rejected", async () => {
  clearKeyCache();
  const k = await makeKey("k1");
  const other = await makeKey("k1"); // same kid, different key
  const f = certsFetch([k.jwk]);
  assert.deepEqual(await verifyAccessJwt(await sign(other, good()), cfg, { fetch: f, nowSeconds: NOW }), { ok: false, reason: "bad signature" });
  const t = (await sign(k, good())).split(".");
  t[1] = b64u(JSON.stringify(good({ email: "attacker@example.com" })));
  assert.deepEqual(await verifyAccessJwt(t.join("."), cfg, { fetch: f, nowSeconds: NOW }), { ok: false, reason: "bad signature" });
});

test("alg none is rejected", async () => {
  clearKeyCache();
  const k = await makeKey("k1");
  const h = b64u(JSON.stringify({ alg: "none", kid: "k1" }));
  const v = await verifyAccessJwt(`${h}.${b64u(JSON.stringify(good()))}.`, cfg, { fetch: certsFetch([k.jwk]), nowSeconds: NOW });
  assert.equal(v.ok, false);
});

test("keys are cached, and re-fetched once when the kid is unknown (rotation)", async () => {
  clearKeyCache();
  const k1 = await makeKey("k1");
  const k2 = await makeKey("k2");
  const counter = { n: 0 };
  const f1 = certsFetch([k1.jwk], counter);
  await verifyAccessJwt(await sign(k1, good()), cfg, { fetch: f1, nowSeconds: NOW });
  await verifyAccessJwt(await sign(k1, good()), cfg, { fetch: f1, nowSeconds: NOW });
  assert.equal(counter.n, 1);
  const v = await verifyAccessJwt(await sign(k2, good()), cfg, { fetch: certsFetch([k1.jwk, k2.jwk], counter), nowSeconds: NOW });
  assert.equal(v.ok, true);
  assert.equal(counter.n, 2);
});
