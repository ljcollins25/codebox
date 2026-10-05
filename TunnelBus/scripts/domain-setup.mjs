#!/usr/bin/env node
// Point a zone at the tunnel bus: proxied wildcard DNS record + Worker route + wrangler vars.
//
//   node domain-setup.mjs --zone example.com                    # dedicated zone: <name>.example.com
//   node domain-setup.mjs --zone example.com --suffix -bus      # shared zone:    <name>-bus.example.com
//   add --dry-run to print the changes, --write-config to set BUS_* vars in worker/wrangler.jsonc
//
// Needs CLOUDFLARE_API_TOKEN with Zone:Read, DNS:Edit, Workers Routes:Edit on the zone.
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, makeClient, findZone, writeVars } from "./cf.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const a = parseArgs(process.argv.slice(2), {
  zone: {}, suffix: { default: "" }, worker: { default: "tunnel-bus" }, control: { default: "ctl" },
  "dry-run": { flag: true }, "write-config": { flag: true },
});
if (!a.zone) { console.error("usage: domain-setup.mjs --zone example.com [--suffix -bus] [--control ctl] [--dry-run] [--write-config]"); process.exit(2); }
const cf = makeClient({ token: process.env.CLOUDFLARE_API_TOKEN, dryRun: a["dry-run"] });
const zone = await findZone(cf, a.zone);

// 1. Proxied wildcard record. Workers routes only run for hostnames that resolve to Cloudflare; the
//    address is a placeholder (100:: is the IPv6 discard prefix) because the Worker answers every request.
//    DNS wildcards only work as a whole first label ("*"), so a suffix like "-bus" still needs "*".
//    Specific records in the zone take precedence over the wildcard, so existing sites keep working.
const existing = (await cf.get(`/zones/${zone.id}/dns_records?name=${encodeURIComponent("*." + a.zone)}`)).result;
if (existing.length) console.error(`wildcard DNS record already exists (${existing[0].type} ${existing[0].content}); leaving it`);
else await cf.post(`/zones/${zone.id}/dns_records`, { type: "AAAA", name: "*", content: "100::", proxied: true, comment: "tunnel-bus wildcard (placeholder origin; the Worker answers)" });

// 2. Worker route. A route hostname may begin with "*" but not contain a wildcard elsewhere, so:
//    dedicated zone -> *.example.com/*      shared zone -> *-bus.example.com/*
const pattern = a.suffix ? `*${a.suffix}.${a.zone}/*` : `*.${a.zone}/*`;
const routes = (await cf.get(`/zones/${zone.id}/workers/routes`)).result;
if (routes.some((r) => r.pattern === pattern && r.script === a.worker)) console.error(`route ${pattern} already points at ${a.worker}`);
else if (routes.some((r) => r.pattern === pattern)) throw new Error(`route ${pattern} exists for another script; not touching it`);
else await cf.post(`/zones/${zone.id}/workers/routes`, { pattern, script: a.worker });

// Control host (register API, chisel endpoint, /_health): "<control><suffix>.<zone>", covered by the same route.
const control = `${a.control}${a.suffix}.${a.zone}`;
const vars = { BUS_BASE_DOMAIN: a.zone, BUS_CONTROL_HOST: control, BUS_LABEL_SUFFIX: a.suffix };
console.log(JSON.stringify({ zone: a.zone, routePattern: pattern, controlHost: control, vars }, null, 2));
if (a["write-config"]) { writeVars(path.join(here, "..", "worker", "wrangler.jsonc"), vars); console.error("wrangler.jsonc updated; now run: cd TunnelBus/worker && npx wrangler deploy"); }
else console.error("set these in worker/wrangler.jsonc vars (or re-run with --write-config), then deploy");
