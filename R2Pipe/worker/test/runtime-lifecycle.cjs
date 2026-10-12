const {createRequire}=require("node:module"),path=require("node:path"),assert=require("node:assert/strict");
const req=createRequire(path.join(__dirname,"../package.json"));const WebSocket=req("ws");
(async()=>{
 const origin=process.env.R2PIPE_TEST_ORIGIN || "http://127.0.0.1:8799",headers={authorization:"Bearer runtime-test"};
 const r=await fetch(origin+"/_api/register",{method:"POST",headers,body:JSON.stringify({name:"runtime-probe"})});
 assert.equal(r.status,200);const {token}=await r.json();
 const connect=()=>new Promise((resolve,reject)=>{const ws=new WebSocket("ws://127.0.0.1:8799/_bus/ws/runtime-probe",{headers:{authorization:"Bearer "+token}});
 ws.on("error",reject);ws.on("upgrade",r=>{assert.ok(r.headers["x-r2pipe-provider-version"]);assert.ok(r.headers["x-r2pipe-provider-boot"]);});
 ws.on("open",()=>resolve(ws));});
 const serve=async ws=>{ws.on("message",data=>{const m=JSON.parse(data.toString());if(m.t==="req")ws.send(JSON.stringify({t:"res",rid:m.rid,status:204,headers:[]}));});
 const r=await fetch(origin+"/p/runtime-probe/",{headers});assert.equal(r.status,204);
 assert.ok(r.headers.get("x-r2pipe-provider-version"));assert.ok(r.headers.get("x-r2pipe-worker-version"));return r.headers.get("x-r2pipe-provider-boot");};
 const a=await connect();const boot=await serve(a);const closed=new Promise(resolve=>a.on("close",resolve));const b=await connect();await closed;
 assert.equal(await serve(b),boot);b.close();await new Promise(resolve=>b.on("close",resolve));
 const offline=await fetch(origin+"/p/runtime-probe/",{headers});assert.equal(offline.status,503);
 await fetch(origin+"/_api/register/runtime-probe",{method:"DELETE",headers});
 console.log("NATIVE WORKERD PASS: versioned upgrade, serve204, takeover oldclose, stableboot, offline503, cleanup");
})().catch(e=>{console.error(e);process.exitCode=1});
