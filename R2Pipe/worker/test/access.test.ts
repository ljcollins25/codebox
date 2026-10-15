import { beforeEach, describe, expect, it } from "vitest";
import { clearKeyCache, verifyAccessJwt } from "../src/access";

const team = "test.cloudflareaccess.com";
const now = 1_800_000_000;
const pair = await crypto.subtle.generateKey(
  { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
  true, ["sign", "verify"],
) as CryptoKeyPair;
const jwk = { ...await crypto.subtle.exportKey("jwk", pair.publicKey), kid: "test-key" };
const b64 = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const json = (value: unknown) => b64(new TextEncoder().encode(JSON.stringify(value)));
async function token(aud: string | string[]) {
  const unsigned = `${json({ alg: "RS256", kid: jwk.kid })}.${json({ iss: `https://${team}`, aud, exp: now + 300 })}`;
  const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", pair.privateKey, new TextEncoder().encode(unsigned));
  return `${unsigned}.${b64(new Uint8Array(signature))}`;
}
async function verify(aud: string | string[], accepted = " wildcard, medius\t third\n") {
  return verifyAccessJwt(await token(aud), { teamDomain: team, aud: accepted }, {
    nowSeconds: now,
    fetch: async () => ({ ok: true, status: 200, json: async () => ({ keys: [jwk] }) }),
  });
}
describe("Access audience allowlist", () => {
  beforeEach(clearKeyCache);
  it.each(["wildcard", "medius", "third"])("accepts configured audience %s", async (aud) => {
    expect((await verify(aud)).ok).toBe(true);
    expect((await verify([aud])).ok).toBe(true);
  });
  it("accepts any matching token audience, not only the first", async () => {
    expect((await verify(["other", "medius"])).ok).toBe(true);
  });
  it("rejects unrelated audiences and substring matches", async () => {
    for (const aud of ["other", "medi", ["other", "unrelated"]]) {
      expect(await verify(aud)).toEqual({ ok: false, reason: "wrong audience" });
    }
  });
  it("retains single-audience compatibility and rejects an empty allowlist", async () => {
    expect((await verify("wildcard", "wildcard")).ok).toBe(true);
    expect(await verify("wildcard", " , \t")).toEqual({ ok: false, reason: "wrong audience" });
  });
});
