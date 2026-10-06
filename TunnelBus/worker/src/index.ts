import { Container, getContainer } from "@cloudflare/containers";
import { handle } from "./gate.ts";

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
  /** Comma-separated Access identities (emails) allowed to use the admin API from the dashboard. */
  BUS_ADMIN_EMAILS?: string;
  /** "clientid=label,...": how service tokens are named on the dashboard (passed at deploy, not committed). */
  BUS_SERVICE_NAMES?: string;
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
      BUS_ADMIN_EMAILS: env.BUS_ADMIN_EMAILS ?? "",
      ACCESS_REQUIRED: env.ACCESS_REQUIRED ?? "false",
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
    // Access verification, identity headers and the dashboard page live in gate.ts (unit-tested).
    return handle(request, env, { forward: (r) => getContainer(env.BUS, "bus").fetch(r) });
  },
} satisfies ExportedHandler<Env>;
