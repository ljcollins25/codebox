import { Container, getContainer } from "@cloudflare/containers";
import { verifyAccessJwt } from "./access.ts";

interface Env {
  BUS: DurableObjectNamespace<TunnelBusContainer>;
  /** Bus admin token (Worker secret). Handed to the router, which checks it on /_api/*. */
  ADMIN_TOKEN: string;
  /** "true" = reject requests without a valid Cloudflare Access JWT. */
  ACCESS_REQUIRED?: string;
  ACCESS_TEAM_DOMAIN?: string; // myteam.cloudflareaccess.com
  ACCESS_AUD?: string; // the Access application's AUD tag
  /** Host routing: provider hosts are <name>.<BUS_BASE_DOMAIN> (see README). */
  BUS_BASE_DOMAIN?: string;
  BUS_CONTROL_HOST?: string;
  BUS_LABEL_SUFFIX?: string;
}

/**
 * The single Durable Object that owns the container. Every request, HTTP or
 * WebSocket, goes Worker -> this DO -> router (port 8080) in the container.
 * The router does all the routing; nothing is stored here (no SQL).
 */
export class TunnelBusContainer extends Container<Env> {
  defaultPort = 8080;
  // Registrations are live state in the container; a provider re-registers when the
  // container restarts. Keep it awake a good while after the last request.
  sleepAfter = "30m";
  enableInternet = false;

  constructor(ctx: DurableObjectState<{}>, env: Env) {
    super(ctx, env);
    this.envVars = {
      ADMIN_TOKEN: env.ADMIN_TOKEN,
      BUS_BASE_DOMAIN: env.BUS_BASE_DOMAIN ?? "",
      BUS_CONTROL_HOST: env.BUS_CONTROL_HOST ?? "",
      BUS_LABEL_SUFFIX: env.BUS_LABEL_SUFFIX ?? "",
    };
  }

  override onStart() {
    console.log("tunnel bus container started");
  }
  override onStop() {
    console.log("tunnel bus container stopped");
  }
  override onError(error: unknown) {
    console.error("tunnel bus container error", error);
  }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    // Cloudflare Access: with Access in front of the zone, Access has already authenticated the
    // caller (browser login or service token) and attached the JWT. We verify it again here so a
    // route that bypasses Access, or workers.dev, cannot reach the bus. Nothing is exempt, not
    // even /_health: monitors need a service token like everything else.
    if (env.ACCESS_REQUIRED === "true") {
      if (!env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD) {
        return new Response("tunnel bus: ACCESS_REQUIRED is set but ACCESS_TEAM_DOMAIN/ACCESS_AUD are not", { status: 500 });
      }
      const v = await verifyAccessJwt(request.headers.get("Cf-Access-Jwt-Assertion"), {
        teamDomain: env.ACCESS_TEAM_DOMAIN,
        aud: env.ACCESS_AUD,
      });
      if (!v.ok) return new Response(`tunnel bus: access denied (${v.reason})`, { status: 403 });
    }
    // The router needs the public host. Overwrite whatever the client sent.
    const fwd = new Request(request);
    fwd.headers.set("X-Bus-Host", new URL(request.url).host);
    return getContainer(env.BUS, "bus").fetch(fwd);
  },
} satisfies ExportedHandler<Env>;
