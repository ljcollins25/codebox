import { Env, err } from "../common";
import { handleHttpFront } from "../provider";


/** M1 is a fixed default mount, not a caller-selected provider namespace.
 * Future named mounts need a distinct, validated routing/auth contract. */
export async function mediusMount(req: Request, env: Env, url: URL): Promise<Response | null> {
  if (url.hostname !== "medius.ref12.dev" || !url.pathname.startsWith("/pd/")) return null;
  // Reject ambiguous separators, dot segments and double-encoding before dispatch.
  // URL parsing normalizes literal dot segments; encoded separators must never
  // become a new route or target path at the local server.
  const rawPath = req.url.split("?")[0].slice(req.url.indexOf("://") + 3).replace(/^[^/]+/, "");
  if (/%(?:2f|5c|2e|25)/i.test(rawPath) || /\\|\/\/|(?:^|\/)\.\.?(?:\/|$)/.test(rawPath))
    return err(400, "ambiguous mounted path");
  const { BUSREG, BUSPROV } = env;
  if (!BUSREG || !BUSPROV) return err(503, "pipe bus unavailable");
  const registered = await BUSREG.get(BUSREG.idFromName("registry")).fetch(

    new Request("https://do/exists", { method: "POST", body: JSON.stringify({ name: "playerd" }) }));
  if (!registered.ok) return err(503, "registry unavailable");
  const exists = await registered.json() as { ok?: boolean };
  if (!exists.ok) return err(404, "provider not registered");
  return handleHttpFront(req, env, url, async () => BUSPROV, { name: "playerd", mountPrefix: "/pd" });
}
