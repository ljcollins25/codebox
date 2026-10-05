// Asks headless Chrome whether a page is installable (Chrome's own installability check, via the DevTools protocol:
// Page.getInstallabilityErrors + Page.getAppManifest), and that the service worker takes control.
//   node scripts/check-installable.mjs http://localhost:8799/app/
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
const url = process.argv[2] ?? "http://localhost:8799/app/";
const chrome = process.env.CHROME ?? "google-chrome";
const dir = fs.mkdtempSync(path.join(os.tmpdir(), "inst-"));
const proc = spawn(chrome, ["--headless=new", "--no-sandbox", "--disable-gpu", "--remote-debugging-port=9333", "--user-data-dir=" + dir, "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ws, id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });
try {
  let tabs;
  for (let n = 0; n < 50 && !tabs; n++) { try { tabs = await (await fetch("http://127.0.0.1:9333/json/list")).json(); } catch { await sleep(200); } }
  ws = new WebSocket(tabs.find((t) => t.type === "page").webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));
  ws.onmessage = (e) => { const m = JSON.parse(e.data); const p = pending.get(m.id); if (p) { pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); } };
  await send("Page.enable");
  await send("Page.navigate", { url });
  await sleep(2500);
  const sw = await send("Runtime.evaluate", { awaitPromise: true, returnByValue: true, expression: "navigator.serviceWorker.ready.then(r => ({scope: r.scope, active: !!r.active}))" });
  await sleep(500);
  const man = await send("Page.getAppManifest");
  const errs = await send("Page.getInstallabilityErrors");
  const out = { url, manifestUrl: man.url, manifestErrors: man.errors, installabilityErrors: errs.installabilityErrors, serviceWorker: sw.result.value };
  console.log(JSON.stringify(out, null, 1));
  process.exitCode = errs.installabilityErrors.length || man.errors.length ? 1 : 0;
} finally { proc.kill(); await sleep(500); try { fs.rmSync(dir, { recursive: true, force: true, maxRetries: 5 }); } catch {} }
