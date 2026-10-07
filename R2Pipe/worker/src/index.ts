import { authenticate } from "./auth";
import { HttpError } from "./state";
import { Env, json, err, s3Config, newId } from "./common";
export { Transfer, Registry } from "./transfer";
export { Provider } from "./provider";
export { BusRegistry } from "./bus/do";
export { BusProvider } from "./bus/provider";
import { providerSocket, adminApi, viewer } from "./bus/route";
import { handleHttpFront, providerNameFromHost } from "./provider";

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === "/_health" && !providerNameFromHost(url.hostname, env)) return json({ ok: true, presigned: s3Config(env) != null });
    const ps = await providerSocket(req, env, url); // the provider's own token, not Access
    if (ps) return ps;
    const a = await authenticate(req, env);
    if (!a.ok) return err(403, `forbidden: ${a.reason}`);
    try {
      const bus = (await adminApi(req, env, url)) ?? (await viewer(req, env, url));
      if (bus) return bus;
      // an unregistered p-<name> host is a 404, never the r2pipe root or another name
      if (/^(?:[^.]*--)?p-[^.]*\.ref12\.dev$/i.test(url.hostname) || (env.BUS_BASE_DOMAIN && new RegExp(`^(?:[^.]*--)?p-[^.]*\\.${env.BUS_BASE_DOMAIN.replace(/\./g, "\\.")}$`, "i").test(url.hostname))) return err(404, "no such name on the pipe bus");
      const front = await handleHttpFront(req, env, url);
      if (front) return front;
      const m = url.pathname.match(/^\/t(?:\/([a-z2-7]{12}))?(\/.*)?$/);
      if (!m) return url.pathname === "/" ? json({ name: "r2pipe", presigned: s3Config(env) != null, api: "/t" }) : err(404, "not found");
      const id = m[1], rest = m[2] ?? "";
      if (!id) {
        if (req.method === "GET" && rest === "") return env.REGISTRY.get(env.REGISTRY.idFromName("registry")).fetch("https://do/list");
        if (req.method === "POST" && rest === "") {
          const body = (await req.json().catch(() => ({}))) as { name?: string; size?: number; partSize?: number; mode?: string };
          const want = body.mode === "binding" ? "binding" : body.mode === "presigned" ? "presigned" : undefined;
          if (want === "presigned" && !s3Config(env)) return err(400, "presigned mode is not configured on this Worker (no R2 S3 credentials)");
          const mode = want ?? (s3Config(env) ? "presigned" : "binding");
          const nid = newId();
          const stub = env.TRANSFER.get(env.TRANSFER.idFromName(nid));
          return stub.fetch("https://do/create", { method: "POST", body: JSON.stringify({ ...body, id: nid, mode, creator: a.who, base: url.origin }) });
        }
        return err(405, "method not allowed");
      }
      const stub = env.TRANSFER.get(env.TRANSFER.idFromName(id));
      const fwd = new Request(`https://do${rest || "/state"}${url.search}`, req);
      const headers = new Headers(req.headers); headers.set("x-r2pipe-base", url.origin);
      return await stub.fetch(new Request(fwd, { headers }));
    } catch (e) {
      if (e instanceof HttpError) return err(e.status, e.message);
      return err(500, (e as Error).message);
    }
  },
};
