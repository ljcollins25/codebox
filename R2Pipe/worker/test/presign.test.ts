import { describe, it, expect } from "vitest";
import { presign, s3Endpoint, amzDate } from "../src/presign";

const cfg = { accountId: "acc123", accessKeyId: "AKID", secretAccessKey: "not-a-real-secret", bucket: "r2pipe" };
const when = new Date("2026-10-05T06:30:00Z");

describe("presign", () => {
  it("builds an R2 S3 URL with query signing, expiry and no secret", async () => {
    const u = new URL(await presign(cfg, "PUT", "t/abc/000001", 900, when));
    expect(u.origin).toBe(s3Endpoint("acc123"));
    expect(u.pathname).toBe("/r2pipe/t/abc/000001");
    expect(u.searchParams.get("X-Amz-Algorithm")).toBe("AWS4-HMAC-SHA256");
    expect(u.searchParams.get("X-Amz-Expires")).toBe("900");
    expect(u.searchParams.get("X-Amz-Date")).toBe("20261005T063000Z");
    expect(u.searchParams.get("X-Amz-Credential")).toBe("AKID/20261005/auto/s3/aws4_request");
    expect(u.searchParams.get("X-Amz-SignedHeaders")).toBe("host");
    expect(u.searchParams.get("X-Amz-Signature")).toMatch(/^[0-9a-f]{64}$/);
    expect(u.toString()).not.toContain("not-a-real-secret");
  });
  it("is deterministic and depends on method, key and secret", async () => {
    const a = await presign(cfg, "GET", "t/x/000001", 60, when);
    expect(await presign(cfg, "GET", "t/x/000001", 60, when)).toBe(a);
    expect(await presign(cfg, "PUT", "t/x/000001", 60, when)).not.toBe(a);
    expect(await presign(cfg, "GET", "t/x/000002", 60, when)).not.toBe(a);
    expect(await presign({ ...cfg, secretAccessKey: "other" }, "GET", "t/x/000001", 60, when)).not.toBe(a);
  });
  it("amzDate format", () => expect(amzDate(when)).toBe("20261005T063000Z"));
  it("matches an independent SigV4 computation", async () => {
    const u = new URL(await presign(cfg, "GET", "t/x/000001", 60, when));
    const sig = u.searchParams.get("X-Amz-Signature")!;
    const q = new URLSearchParams(u.search); q.delete("X-Amz-Signature");
    const canonQuery = [...q.entries()].map(([k, v]) => [encodeURIComponent(k), encodeURIComponent(v)]).sort((a, b) => (a[0] < b[0] ? -1 : 1)).map(([k, v]) => k + "=" + v).join("&");
    const canon = ["GET", "/r2pipe/t/x/000001", canonQuery, "host:" + u.host + "\n", "host", "UNSIGNED-PAYLOAD"].join("\n");
    const enc = new TextEncoder();
    const hex = (b: ArrayBuffer) => [...new Uint8Array(b)].map((x) => x.toString(16).padStart(2, "0")).join("");
    const hmac = async (k: ArrayBuffer | Uint8Array, d: string) => crypto.subtle.sign("HMAC", await crypto.subtle.importKey("raw", k, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]), enc.encode(d));
    const sts = ["AWS4-HMAC-SHA256", "20261005T063000Z", "20261005/auto/s3/aws4_request", hex(await crypto.subtle.digest("SHA-256", enc.encode(canon)))].join("\n");
    let k = await hmac(enc.encode("AWS4" + cfg.secretAccessKey), "20261005");
    for (const s of ["auto", "s3", "aws4_request"]) k = await hmac(k, s);
    expect(hex(await hmac(k, sts))).toBe(sig);
  });
});
