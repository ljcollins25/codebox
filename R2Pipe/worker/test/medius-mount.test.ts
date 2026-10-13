import { afterEach, describe, expect, it, vi } from "vitest";
import { mediusMount } from "../src/bus/mount";
import type { Env } from "../src/common";
import worker from "../src/index";

vi.mock("../src/auth", () => ({ authenticate: vi.fn(async (r: Request) =>
  r.headers.get("cf-access-jwt-assertion") === "valid-test-jwt" ? { ok: true, who: "owner" } : { ok: false, reason: "denied JWT" }) }));
function setup(row: any = { kind: "pipe" }, reply = new Response("range-data", { status: 206,
  headers: { "Content-Range": "bytes 1-10/100", "Content-Length": "10", "Accept-Ranges": "bytes", "ETag": "\"test\"" } })) {
  let forwarded: Request | undefined;
  const get = vi.fn(async () => new Response(JSON.stringify({ ok: row != null })));
  const ns = {
    idFromName: vi.fn((n: string) => n),
    get: vi.fn(() => ({ fetch: vi.fn(async (r: Request) => { forwarded = r; return reply; }) })),
  };
  const env = { VERSION_METADATA: { id: "mount-version" }, ADMIN_TOKEN: "test-admin",
    BUS_BASE_DOMAIN: "ref12.dev", BUS_RESERVED: "ctl,pipe", HTTP_HOST_SUFFIX: "pipe.ref12.dev",
    BUSREG: { idFromName: (n: string) => n, get: () => ({ fetch: get }) }, BUSPROV: ns } as unknown as Env;
  return { env, ns, get, forwarded: () => forwarded };
}
afterEach(() => vi.clearAllMocks());
describe("Medius fixed playerd mount", () => {
  it("strips exactly /pd, preserves raw query and sends prefix/public base", async () => {
    const f = setup();
    const req = new Request("https://medius.ref12.dev/pd/v1/status?x=1&raw=%2F+%20");
    expect((await mediusMount(req, f.env, new URL(req.url)))?.status).toBe(206);
    expect(f.ns.idFromName).toHaveBeenCalledWith("playerd");
    const h = f.forwarded()!.headers;
    expect(h.get("x-r2pipe-path")).toBe("/v1/status?x=1&raw=%2F+%20");
    expect(h.get("x-forwarded-prefix")).toBe("/pd");
    expect(h.get("x-r2pipe-base")).toBe("https://medius.ref12.dev/pd");
    expect(h.get("x-forwarded-host")).toBe("medius.ref12.dev");
  });
  it("preserves POST streaming body, Range, If-Range, bearer, Origin and 206 headers", async () => {
    const f = setup();
    const req = new Request("https://medius.ref12.dev/pd/v1/files?chunk=1", { method: "POST", body: "stream-body",
      headers: { "content-type": "application/octet-stream", range: "bytes=1-10", "if-range": "\"test\"",
        authorization: "Bearer paired-daemon-token", origin: "https://medius.ref12.dev",
        "cf-access-client-id": "private-id", "cf-access-client-secret": "private-secret", "cf-access-jwt-assertion": "private-jwt" } });
    const res = (await mediusMount(req, f.env, new URL(req.url)))!;
    const forwarded = f.forwarded()!;
    expect(forwarded.method).toBe("POST"); expect(await forwarded.text()).toBe("stream-body");
    for (const k of ["range", "if-range", "authorization", "origin", "content-type"])
      expect(forwarded.headers.get(k)).toBe(req.headers.get(k));
    for (const k of ["cf-access-client-id", "cf-access-client-secret", "cf-access-jwt-assertion"])
      expect(forwarded.headers.has(k)).toBe(false);
    expect(res.status).toBe(206); expect(res.headers.get("Content-Range")).toBe("bytes 1-10/100");
    expect(res.headers.get("Content-Length")).toBe("10"); expect(await res.text()).toBe("range-data");
  });
  it.each(["https://other.ref12.dev/pd/v1/status","https://medius.ref12.dev/v1/status",
    "https://medius.ref12.dev/pd","https://medius.ref12.dev/pdx/v1/status"])("leaves other routing unchanged: %s", async u => {
    const f = setup();const req = new Request(u);
    expect(await mediusMount(req,f.env,new URL(u))).toBeNull();expect(f.get).not.toHaveBeenCalled();
  });
  it.each(["/pd/a%2fb","/pd/a%5cb","/pd/a%252fb","/pd/a%2eb","/pd//v1/status"])
    ("rejects ambiguous encoded path %s", async path => {
      const f=setup();const req=new Request("https://medius.ref12.dev"+path);
      expect((await mediusMount(req,f.env,new URL(req.url)))?.status).toBe(400);
      expect(f.get).not.toHaveBeenCalled();expect(f.ns.get).not.toHaveBeenCalled();
    });
  it("does not interpret v1 or a caller header as a provider name",async()=>{
    const f=setup();const req=new Request("https://medius.ref12.dev/pd/v1/status?provider=other",{headers:{"x-r2pipe-provider":"other","x-bus-host-prefix":"other","x-r2pipe-path":"/evil","x-forwarded-prefix":"/evil"}});
    await mediusMount(req,f.env,new URL(req.url));
    expect(f.ns.idFromName).toHaveBeenCalledWith("playerd");
    expect(f.forwarded()!.headers.get("x-r2pipe-path")).toBe("/v1/status?provider=other");
    expect(f.forwarded()!.headers.get("x-bus-host-prefix")).toBeNull();
    expect(f.forwarded()!.headers.get("x-forwarded-prefix")).toBe("/pd");
  });
  it("fails closed for missing registration",async()=>{
    for(const [row,status] of [[null,404]] as const){
      const f=setup(row);const req=new Request("https://medius.ref12.dev/pd/v1/status");
      expect((await mediusMount(req,f.env,new URL(req.url)))?.status).toBe(status);
      expect(f.ns.get).not.toHaveBeenCalled();
    }
  });
  it("preserves provider offline 503 and version header",async()=>{
    const f=setup({kind:"pipe"},new Response('{"error":"provider offline"}',{status:503,headers:{"x-r2pipe-provider-version":"do-version"}}));
    const req=new Request("https://medius.ref12.dev/pd/v1/status");
    const res=(await mediusMount(req,f.env,new URL(req.url)))!;
    expect(res.status).toBe(503);expect(res.headers.get("x-r2pipe-provider-version")).toBe("do-version");
  });
  it("returns streaming responses without buffering and preserves cancellation",async()=>{
    let cancel=false;
    const body=new ReadableStream({start(c){c.enqueue(new Uint8Array([1,2]));},cancel(){cancel=true;}});
    const f=setup({kind:"pipe"},new Response(body));
    const req=new Request("https://medius.ref12.dev/pd/v1/files");
    const res=(await mediusMount(req,f.env,new URL(req.url)))!;
    const reader=res.body!.getReader();expect((await reader.read()).value).toEqual(new Uint8Array([1,2]));
    await reader.cancel();expect(cancel).toBe(true);
  });
  it("denies JWT before registry lookup and stamps the Worker version",async()=>{
    const f=setup();const res=await worker.fetch(new Request("https://medius.ref12.dev/pd/v1/status"),f.env);
    expect(res.status).toBe(403);expect(f.get).not.toHaveBeenCalled();
    expect(res.headers.get("x-r2pipe-worker-version")).toBe("mount-version");
  });
  it("dispatches only after existing auth and preserves both versions",async()=>{
    const f=setup({kind:"pipe"},new Response("ok",{headers:{"x-r2pipe-provider-version":"do-version"}}));
    const res=await worker.fetch(new Request("https://medius.ref12.dev/pd/v1/status",{headers:{"cf-access-jwt-assertion":"valid-test-jwt"}}),f.env);
    expect(res.status).toBe(200);expect(f.ns.idFromName).toHaveBeenCalledWith("playerd");
    expect(res.headers.get("x-r2pipe-worker-version")).toBe("mount-version");
    expect(res.headers.get("x-r2pipe-provider-version")).toBe("do-version");
  });
});
