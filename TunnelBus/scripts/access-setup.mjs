#!/usr/bin/env node
// Put Cloudflare Access in front of the bus: a service token for hexad, two reusable policies, and one
// self-hosted application covering every bus hostname. Do NOT run before the Zero Trust organization and a
// login method exist (README "Cloudflare Access").
//
//   node access-setup.mjs --zone example.com --email you@example.com --out /safe/place/hexad-token.json
//   [--suffix -bus] [--email other@x] [--github-org my-org] [--team-domain myteam.cloudflareaccess.com]
//   [--token-name tunnel-bus-hexad] [--dry-run] [--write-config]
//
// The service token's client id and secret are written to --out (mode 0600), never printed; Cloudflare
// shows the secret only at creation. Needs CLOUDFLARE_API_TOKEN with Access: Apps and Policies Edit,
// Access: Service Tokens Edit, and Access: Organizations, Identity Providers, and Groups Read.
// (hexad can then load the file into secret session variables with set_variable.)
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, makeClient, accountId, writeVars } from "./cf.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const a = parseArgs(process.argv.slice(2), {
  zone: {}, suffix: { default: "" }, email: { multi: true }, "github-org": {}, out: {}, "team-domain": {},
  "token-name": { default: "tunnel-bus-hexad" }, "app-name": { default: "tunnel-bus" },
  "dry-run": { flag: true }, "write-config": { flag: true },
});
if (!a.zone || !a.out || (!a.email?.length && !a["github-org"])) {
  console.error("usage: access-setup.mjs --zone example.com --email you@example.com [--github-org ORG] --out token.json [--suffix -bus]");
  process.exit(2);
}
const cf = makeClient({ token: process.env.CLOUDFLARE_API_TOKEN, dryRun: a["dry-run"] });
const acct = await accountId(cf);
const A = `/accounts/${acct}/access`;

// Team domain (the Zero Trust org must exist).
let team = a["team-domain"];
if (!team) {
  const org = await cf.get(`${A}/organizations`).catch((e) => { throw new Error("no Zero Trust organization yet? " + e.message); });
  team = org.result.auth_domain;
}

// 1. Service token (one per consumer; the secret is only returned at creation).
const tokens = (await cf.get(`${A}/service_tokens`)).result ?? [];
let tok = tokens.find((t) => t.name === a["token-name"]);
if (tok) console.error(`service token "${a["token-name"]}" already exists (id ${tok.id}); its secret cannot be re-read, so --out is not written. Delete it in the dashboard to create a new one.`);
else {
  tok = (await cf.post(`${A}/service_tokens`, { name: a["token-name"], duration: "8760h" })).result;
  if (!a["dry-run"]) {
    fs.writeFileSync(a.out, JSON.stringify({ id: tok.id, client_id: tok.client_id, client_secret: tok.client_secret }, null, 2), { mode: 0o600 });
    fs.chmodSync(a.out, 0o600);
    console.error(`service token created; id/secret saved to ${a.out}`);
  }
}

// 2. Reusable policies.
const existingPolicies = (await cf.get(`${A}/policies`)).result ?? [];
async function policy(name, body) {
  const hit = existingPolicies.find((p) => p.name === name);
  if (hit) return hit.id;
  return (await cf.post(`${A}/policies`, { name, ...body })).result.id;
}
const include = [
  ...(a.email ?? []).map((email) => ({ email: { email } })),
  ...(a["github-org"] ? [{ "github-organization": { name: a["github-org"] } }] : []),
];
const humans = await policy("tunnel-bus: allow me", { decision: "allow", include });
const machines = await policy("tunnel-bus: service tokens", { decision: "non_identity", include: [{ service_token: { token_id: tok.id } }] });

// 3. The application. One wildcard label covers <name>.<zone> and <prefix>--<name>.<zone>
//    (or the -bus suffixed form), including the control host.
const host = a.suffix ? `*${a.suffix}.${a.zone}` : `*.${a.zone}`;
const apps = (await cf.get(`${A}/apps`)).result ?? [];
let app = apps.find((x) => x.name === a["app-name"]);
if (app) console.error(`application "${a["app-name"]}" exists; not modified`);
else {
  app = (await cf.post(`${A}/apps`, {
    name: a["app-name"], type: "self_hosted", domain: host,
    destinations: [{ type: "public", uri: `${host}/*` }],
    session_duration: "24h",
    service_auth_401_redirect: true, // programs get 401, not a login redirect
    policies: [{ id: humans, precedence: 1 }, { id: machines, precedence: 2 }],
  })).result;
}
console.log(JSON.stringify({ team, aud: app.aud, application: host, serviceTokenId: tok.id }, null, 2));
if (a["write-config"]) {
  writeVars(path.join(here, "..", "worker", "wrangler.jsonc"), { ACCESS_TEAM_DOMAIN: team, ACCESS_AUD: app.aud });
  console.error("wrangler.jsonc updated (ACCESS_REQUIRED stays false until you flip it and deploy)");
}
