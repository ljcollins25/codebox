import { Container, getContainer } from "@cloudflare/containers";

interface Env {
  BUS: DurableObjectNamespace<TunnelBusContainer>;
  /** Bus admin token (Worker secret). Handed to the router, which checks it on /_api/*. */
  ADMIN_TOKEN: string;
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
    this.envVars = { ADMIN_TOKEN: env.ADMIN_TOKEN };
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
    // Cloudflare Access (later) plugs in here: validate the Cf-Access-Jwt-Assertion
    // header (or put Access in front of the custom domain) before forwarding.
    // Subdomain routing (later): the router resolves <name>.bus.<domain> from Host.
    return getContainer(env.BUS, "bus").fetch(request);
  },
} satisfies ExportedHandler<Env>;
