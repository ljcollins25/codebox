// The answer for a registered name whose provider is not connected: 503 (never 200), HTML for browsers, JSON for everything else.
export interface OfflineInfo { name: string; lastSeen: string | null; registeredAt?: string }

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);

export function ago(fromIso: string | null, now: number): string {
  if (!fromIso) return "never connected";
  const s = Math.max(0, Math.round((now - Date.parse(fromIso)) / 1000));
  if (s < 60) return s + " s ago";
  if (s < 3600) return Math.floor(s / 60) + " min ago";
  if (s < 86400) return Math.floor(s / 3600) + " h ago";
  return Math.floor(s / 86400) + " d ago";
}

export function wantsHtml(req: Request): boolean {
  const a = req.headers.get("accept") ?? "";
  return a.includes("text/html") && (req.method === "GET" || req.method === "HEAD");
}

export function offlineResponse(req: Request, info: OfflineInfo, dashboard: string, now = Date.now()): Response {
  const headers = { "cache-control": "no-store", "retry-after": "30" };
  if (wantsHtml(req)) {
    const seen = info.lastSeen ? `${esc(new Date(info.lastSeen).toUTCString())} (${ago(info.lastSeen, now)})` : "never connected";
    const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark"><title>${esc(info.name)} is offline</title>
<style>body{margin:0;background:#0e1116;color:#e6edf3;font:16px/1.5 system-ui,sans-serif;display:grid;place-items:center;min-height:100vh}main{max-width:32rem;padding:1.5rem}h1{font-size:1.3rem;margin:0 0 .6rem}.d{color:#8b949e}a{color:#58a6ff}</style></head>
<body><main><h1>${esc(info.name)}</h1><p>Registered on the pipe bus, but its provider is offline.</p><p class="d">Last seen: ${seen}</p><p><a href="${esc(dashboard)}">Open the dashboard</a></p></main></body></html>`;
    return new Response(req.method === "HEAD" ? null : html, { status: 503, headers: { ...headers, "content-type": "text/html; charset=utf-8" } });
  }
  return new Response(JSON.stringify({ error: "no provider is connected for this name", name: info.name, bus: "pipe", lastSeen: info.lastSeen }), { status: 503, headers: { ...headers, "content-type": "application/json" } });
}
