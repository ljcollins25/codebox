// Serves the dashboard page with made-up data, for screenshots and local UI work:
//   node scripts/mock-dashboard.mjs [port]      then open http://localhost:<port>/
import http from "node:http";
import { handle } from "../src/gate.ts";
const port = Number(process.argv[2] ?? 8799);
const ago = (min) => new Date(Date.now() - min * 60000).toISOString();
const base = (o) => ({ port: 20000, up: false, lastSeen: null, connectedSince: null, reconnects: 0, description: "", label: "", owner: "", kind: "", session: {}, sessionUrl: "", metaUpdatedAt: null, path: "/" + o.name + "/", host: o.name + ".example.test", ...o });
const providers = [
  base({ name: "hexad-project", label: "hexad", description: "The hexad control plane for this project: sessions, browser and API.", owner: "hexad project", kind: "hexad", session: { name: "<b>main</b>", hexad: "hexad project" }, up: true, connectedSince: ago(190), registeredAt: ago(2900), lastSeen: ago(0), reconnects: 2 }),
  base({ name: "csharp-wasm-2", label: "C# WASM demo", description: "Blazor WebAssembly sample served from the session sandbox, port 5000.", owner: "session csharp-wasm-2 (hexad project)", kind: "app", session: { name: "csharp-wasm-2", id: "s-20261005-083139-c037", hexad: "hexad project" }, sessionUrl: "https://hexad.example.test/s/s-20261005-083139-c037", up: true, connectedSince: ago(14), registeredAt: ago(15), lastSeen: ago(0) }),
  base({ name: "bus-dashboard", label: "Bus dashboard (dev server)", description: "Local dev server of the tunnel bus dashboard.", owner: "session bus-dashboard (hexad project)", kind: "app", session: { name: "bus-dashboard", id: "s-20261005-150053-55de", hexad: "hexad project" }, up: true, connectedSince: ago(3), registeredAt: ago(60), lastSeen: ago(0), reconnects: 1 }),
  base({ name: "code-laptop", label: "VS Code on the laptop", description: "code serve-web on the Windows laptop. <img src=x onerror=alert(1)> stays text.", owner: "laptop", kind: "vscode", up: false, registeredAt: ago(4000), lastSeen: ago(95) }),
  base({ name: "legacy-api", registeredAt: ago(9000), lastSeen: ago(7000) }),
];
const status = { version: "0.4.0", uptimeSeconds: 4 * 3600 + 120, providers: providers.length, connections: providers.filter((p) => p.up).length, accessRequired: true, controlHost: "ctl.example.test", you: "ljcollins25" };
// The real gate serves the page, /app/ manifest, service worker and icons; only the router is faked.
const env = { BUS_CONTROL_HOST: "localhost" };
const forward = async (req) => {
  const p = new URL(req.url).pathname;
  if (p === "/_api/status") return Response.json(status);
  if (p === "/_api/providers") return Response.json(providers);
  return new Response("not found", { status: 404 });
};
http.createServer(async (req, res) => {
  const r = await handle(new Request("http://" + req.headers.host + req.url, { method: req.method, headers: { host: req.headers.host } }), env, { forward });
  res.writeHead(r.status, Object.fromEntries(r.headers));
  res.end(Buffer.from(await r.arrayBuffer()));
}).listen(port, () => console.log("mock dashboard on http://localhost:" + port + "/  (installable launcher: /app/)"));
