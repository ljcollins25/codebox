import { afterEach, describe, it, expect, vi } from "vitest";
import { Registry, Transfer, transferUrlSeconds } from "../src/transfer";
import type { Meta } from "../src/state";
function fixture(){
 const map=new Map<string,any>(),deleted:string[]=[];
 const storage={
  get:async(k:string)=>map.get(k),put:async(k:string,v:any)=>{map.set(k,structuredClone(v));},
  delete:async(k:string|string[])=>{for(const key of Array.isArray(k)?k:[k]){map.delete(key);deleted.push(key);}},
  list:async(o:any)=>new Map([...map].filter(([k])=>k.startsWith(o.prefix)&&(!o.startAfter||k>o.startAfter)).sort(([a],[b])=>a.localeCompare(b)).slice(0,o.limit??10000))
 };
 const reg=new Registry({storage} as any);
 const write=async(b:any)=>{const r=await reg.fetch(new Request("https://do/upsert",{method:"POST",body:JSON.stringify(b)}));return {status:r.status,body:await r.json() as any};};
 return {map,deleted,reg,write};
}
const row=(id:string,version=1,remove=false)=>({id,version,remove,createdAt:1000,expiresAt:3601000,status:"open"});
afterEach(()=>vi.useRealTimers());
describe("transfer registry retention",()=>{
 it("retains version ordering and terminal tombstones until absolute deadline+grace",async()=>{
  vi.spyOn(Date,"now").mockReturnValue(2000);const f=fixture();
  await f.write(row("a",2,true));await f.write(row("a",1));
  expect(f.map.get("t:a").removed).toBe(true);
  expect(f.map.get("t:a").pruneAfter).toBe(4201000);
  vi.spyOn(Date,"now").mockReturnValue(3601000);
  await f.write({...row("fresh"),createdAt:3601000,expiresAt:7201000});
  expect(f.map.has("t:a")).toBe(true);
  vi.spyOn(Date,"now").mockReturnValue(4201000);
  await f.write({...row("new"),createdAt:4201000,expiresAt:7801000});
  expect(f.map.has("t:a")).toBe(false);
  expect((await f.write(row("a",99))).body.stale).toBe(true);
  expect(f.map.has("t:a")).toBe(false);
 });
 it("prunes old legacy tombstones and rows in bounded pages, preserving unrelated keys",async()=>{
  vi.spyOn(Date,"now").mockReturnValue(9000000);const f=fixture();
  for(let i=0;i<300;i++)f.map.set("t:"+String(i).padStart(3,"0"),{id:String(i),removed:i%2===0,version:1,expiresAt:1000});
  f.map.set("other","keep");
  await f.write({...row("fresh"),createdAt:9000000,expiresAt:12600000});
  expect(f.deleted.filter(k=>k.startsWith("t:")).length).toBe(128);
  await f.write({...row("fresh",2),createdAt:9000000,expiresAt:12600000});
  await f.write({...row("fresh",3),createdAt:9000000,expiresAt:12600000});
  expect(f.deleted.filter(k=>k.startsWith("t:")).length).toBe(300);
  expect(f.map.get("other")).toBe("keep");expect(f.map.has("t:fresh")).toBe(true);
 });
 it("serializes concurrent updates and rejects missing absolute timestamps",async()=>{
  vi.spyOn(Date,"now").mockReturnValue(2000);const f=fixture();
  await Promise.all([f.write(row("a",3,true)),f.write(row("a",2))]);
  expect(f.map.get("t:a").removed).toBe(true);
  expect((await f.write({id:"bad",version:1,expiresAt:5000})).status).toBe(400);
 });
});
describe("presigned transfer deadline",()=>{
 it("clamps URLs to remaining absolute lifetime and rejects expiry",()=>{
  const m={createdAt:1000,expiresAt:86401000} as Meta;
  expect(transferUrlSeconds(m,1000)).toBe(3600);
  expect(transferUrlSeconds(m,3591000)).toBe(10);
  expect(()=>transferUrlSeconds(m,3601000)).toThrow("transfer expired");
 });
});

describe("expired transport boundaries",()=>{
 afterEach(()=>{vi.restoreAllMocks();vi.unstubAllGlobals();});
 it("returns410 from long-poll and never forwards expired inline data",async()=>{
  vi.stubGlobal("WebSocketRequestResponsePair",class{});
  const recv={send:vi.fn()},sender={send:vi.fn()};
  const ctx={setWebSocketAutoResponse:()=>{},getTags:()=>["send"],getWebSockets:(role?:string)=>role==="recv"?[recv]:[],storage:{}} as any;
  const transfer=new Transfer(ctx,{BUCKET:{delete:async()=>{},head:async()=>null}} as any);
  const core=(transfer as any).core;
  core.snapshot=vi.fn().mockResolvedValueOnce({meta:{version:1,status:"open"},parts:[]}).mockRejectedValue(new (await import("../src/state")).HttpError(410,"transfer expired"));
  const r=await transfer.fetch(new Request("https://do/state?since=1&wait=0.001"));
  expect(r.status).toBe(410);
  core.inlineAppend=vi.fn().mockRejectedValue(new (await import("../src/state")).HttpError(410,"transfer expired"));
  const {packInline}=await import("../src/common");
  await transfer.webSocketMessage(sender as any,packInline(0,new Uint8Array([1]).buffer));
  expect(recv.send).not.toHaveBeenCalled();expect(sender.send).toHaveBeenCalledWith(expect.stringContaining("transfer expired"));
 });
});

describe("transfer heartbeat contract", () => {
 it("auto-responds to the same fixed strings and retains old handler fallback", async () => {
  vi.stubGlobal("WebSocketRequestResponsePair",class { constructor(public request:string,public response:string){} });
  const auto=vi.fn(), ws={send:vi.fn()};
  const transfer=new Transfer({setWebSocketAutoResponse:auto,storage:{},getTags:()=>[]} as any,{} as any);
  expect(auto).toHaveBeenCalledWith(expect.objectContaining({request:"ping",response:"pong"}));
  await transfer.webSocketMessage(ws as any,"ping");
  expect(ws.send).toHaveBeenCalledWith("pong");
  vi.unstubAllGlobals();
 });
});
