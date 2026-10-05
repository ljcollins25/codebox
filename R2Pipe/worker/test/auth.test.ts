import { describe, it, expect } from "vitest";
import { authenticate } from "../src/auth";

describe("auth", () => {
  const env = { ADMIN_TOKEN: "fake-admin", ACCESS_TEAM_DOMAIN: "team.example.invalid", ACCESS_AUD: "aud" };
  it("accepts the admin bearer token", async () => {
    const r = await authenticate(new Request("https://x/t", { headers: { authorization: "Bearer fake-admin" } }), env);
    expect(r).toEqual({ ok: true, who: "admin-token" });
  });
  it("rejects a wrong token and a missing Access JWT", async () => {
    expect((await authenticate(new Request("https://x/t", { headers: { authorization: "Bearer nope" } }), env)).ok).toBe(false);
    expect((await authenticate(new Request("https://x/t"), env)).ok).toBe(false);
  });
  it("refuses when nothing is configured", async () => {
    expect((await authenticate(new Request("https://x/t"), {})).ok).toBe(false);
  });
});
