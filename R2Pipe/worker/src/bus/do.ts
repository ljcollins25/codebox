import { kvOf, json, err, Env } from "../common";
import { RegistryCore, RegistryError, DEFAULT_GRACE_MS } from "./registry";

/** BUS_STALE_HOURS (Worker var): hours a name may have no provider before it is removed. Default 24; 0 = keep forever. */
export function graceMs(env: Env): number { const h = Number(env.BUS_STALE_HOURS); return env.BUS_STALE_HOURS !== undefined && env.BUS_STALE_HOURS !== "" && Number.isFinite(h) && h >= 0 ? h * 3600 * 1000 : DEFAULT_GRACE_MS; }

/** The single registry DO: names, hashed tokens, metadata and connection state of every pipe-bus provider. */
export class BusRegistry implements DurableObject {
  private core: RegistryCore;
  constructor(ctx: DurableObjectState, private env: Env) { this.core = new RegistryCore(kvOf(ctx.storage), () => Date.now(), graceMs(env)); }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    try {
      const body = req.method === "GET" || req.method === "DELETE" ? {} : ((await req.json().catch(() => ({}))) as any);
      const m = url.pathname.match(/^\/register(?:\/([^/]+))?$/);
      if (m && req.method === "POST") { const r = await this.core.register(String(body.name ?? ""), body); return json({ name: r.record.name, token: r.token }); }
      if (m?.[1] && req.method === "PATCH") { const r = await this.core.update(decodeURIComponent(m[1]), body); return r ? json({ ok: true }) : err(404, "not registered"); }
      if (m?.[1] && req.method === "DELETE") return (await this.core.remove(decodeURIComponent(m[1]))) ? json({ ok: true }) : err(404, "not registered");
      if (url.pathname === "/verify") return json({ ok: await this.core.verify(String(body.name), String(body.token)) });
      if (url.pathname === "/exists") return json({ ok: (await this.core.get(String(body.name))) != null });
      if (url.pathname === "/info") { const i = await this.core.info(String(body.name)); return i ? json(i) : err(404, "not registered"); }
      if (url.pathname === "/connected") { await this.core.setConnected(String(body.name), !!body.connected); return json({ ok: true }); }
      if (url.pathname === "/list") {
        const sfx = this.env.HTTP_HOST_SUFFIX ?? "", dom = this.env.BUS_BASE_DOMAIN ?? "";
        // p-<name>.<domain> is the address; the older <name>--<suffix> keeps working until it is dropped
        return json(await this.core.list((n) => (dom ? { host: `p-${n}.${dom}`, prefixedHost: `<prefix>--p-${n}.${dom}`, path: `/p/${n}/` } : { host: sfx ? `${n}--${sfx}` : "", prefixedHost: sfx ? `<prefix>--${n}--${sfx}` : "", path: `/p/${n}/` })));
      }
      return err(404, "not found");
    } catch (e) {
      if (e instanceof RegistryError) return err(e.status, e.message);
      return err(500, (e as Error).message);
    }
  }
}
