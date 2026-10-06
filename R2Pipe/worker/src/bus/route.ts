// pipe-bus routing in front of the r2pipe Worker: provider sockets (own token), admin API, viewers.
import { authenticate } from "../auth";
import { Env, json, err } from "../common";
import { handleHttpFront, providerNameFromHost } from "../provider";
import { parseViewerHost, validName, timingEqual } from "./common";

const reg = (env: Env) => env.BUSREG!.get(env.BUSREG!.idFromName("registry"));
const call = async (env: Env, path: string, method = "POST", body?: unknown) => reg(env).fetch("https://do" + path, { method, body: body === undefined ? undefined : JSON.stringify(body) });
const exists = async (env: Env, name: string) => ((await (await call(env, "/exists", "POST", { name })).json()) as { ok: boolean }).ok;
const provider = (env: Env, name: string) => env.BUSPROV!.get(env.BUSPROV!.idFromName(name));

export function viewerOf(url: URL, env: Env): { name: string; prefix?: string } | null {
  return parseViewerHost(url.hostname, { suffix: env.HTTP_HOST_SUFFIX, baseDomain: env.BUS_BASE_DOMAIN, reserved: env.BUS_RESERVED?.split(",").map((s) => s.trim()).filter(Boolean) });
}

/** Provider side: GET /_bus/ws/<name> with "Authorization: Bearer <that name's token>". Outside Access (workers.dev). */
export async function providerSocket(req: Request, env: Env, url: URL): Promise<Response | null> {
  const m = url.pathname.match(/^\/_bus\/ws\/([^/]+)$/);
  if (!m) return null;
  if (!env.BUSREG) return err(404, "pipe-bus is not enabled");
  const name = m[1];
  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") return err(426, "websocket expected");
  const auth = req.headers.get("authorization") ?? "";
  const token = auth.startsWith("Bearer ") ? auth.slice(7) : "";
  if (!validName(name) || !token || !((await (await call(env, "/verify", "POST", { name, token })).json()) as { ok: boolean }).ok) return err(401, "unauthorized");
  return provider(env, name).fetch(new Request("https://do/ws", { headers: { upgrade: "websocket", "x-bus-name": name } }));
}

/** Admin side (Access or admin bearer): same paths and shapes as the old router's /_api. */
export async function adminApi(req: Request, env: Env, url: URL): Promise<Response | null> {
  if (!url.pathname.startsWith("/_api/") || !env.BUSREG) return null;
  const m = url.pathname.match(/^\/_api\/register(?:\/([^/]+))?$/);
  if (url.pathname === "/_api/registry" && req.method === "GET") return call(env, "/list", "GET");
  if (url.pathname === "/_api/status" && req.method === "GET") return json({ bus: "pipe", version: "pipe-bus 1" });
  if (!m) return err(404, "not found");
  const name = m[1] ? decodeURIComponent(m[1]) : undefined;
  const body = req.method === "DELETE" ? {} : await req.json().catch(() => ({}));
  if (req.method === "POST" && !name) {
    const r = await call(env, "/register", "POST", body);
    if (r.ok) { const j = (await r.json()) as { name: string; token: string }; await provider(env, j.name).fetch("https://do/revoke"); return json({ ...j, path: "/_bus/ws/" + j.name }); }
    return r;
  }
  if (name && req.method === "PATCH") return call(env, "/register/" + encodeURIComponent(name), "PATCH", body);
  if (name && req.method === "DELETE") {
    const r = await call(env, "/register/" + encodeURIComponent(name), "DELETE");
    if (r.ok) await provider(env, name).fetch("https://do/revoke");
    return r;
  }
  return err(405, "method not allowed");
}

/** Viewer side: a request whose host (or /p/<name> path) names a registered pipe-bus provider. Null = not ours. */
export async function viewer(req: Request, env: Env, url: URL): Promise<Response | null> {
  if (!env.BUSREG) return null;
  const v = viewerOf(url, env);
  const pathName = v ? null : url.pathname.match(/^\/p\/([a-z0-9-]{1,40})(\/|$)/)?.[1] ?? null;
  const name = v?.name ?? pathName;
  if (!name || !(await exists(env, name))) return null;
  return handleHttpFront(req, env, url, async () => env.BUSPROV!, v ?? undefined);
}
