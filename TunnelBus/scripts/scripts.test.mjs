// Runs the setup scripts against a fake Cloudflare API: `node --test scripts/scripts.test.mjs`
import { test } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFile } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

function fakeApi(handler) {
  const calls = [];
  const srv = http.createServer((req, res) => {
    let b = "";
    req.on("data", (c) => (b += c));
    req.on("end", () => {
      const body = b ? JSON.parse(b) : undefined;
      calls.push({ method: req.method, url: req.url, body, auth: req.headers.authorization });
      const r = handler(req.method, req.url, body) ?? [];
      res.setHeader("content-type", "application/json");
      res.end(JSON.stringify({ success: true, result: r }));
    });
  });
  return new Promise((ok) => srv.listen(0, "127.0.0.1", () => ok({ srv, calls, base: `http://127.0.0.1:${srv.address().port}` })));
}
const run = (script, args, env) => new Promise((ok) =>
  execFile(process.execPath, [path.join(here, script), ...args], { env: { ...process.env, CLOUDFLARE_API_TOKEN: "tok-xyz", ...env } }, (e, stdout, stderr) => ok({ code: e ? e.code : 0, stdout, stderr })));

test("domain-setup creates a proxied wildcard record and a *.zone route", async () => {
  const f = await fakeApi((m, u) => (u.startsWith("/zones?name") ? [{ id: "Z1", name: "example.com" }] : []));
  const r = await run("domain-setup.mjs", ["--zone", "example.com"], { CF_API_BASE: f.base });
  f.srv.close();
  assert.equal(r.code, 0, r.stderr);
  const posts = f.calls.filter((c) => c.method === "POST");
  assert.equal(posts[0].body.name, "*");
  assert.equal(posts[0].body.proxied, true);
  assert.deepEqual(posts[1].body, { pattern: "*.example.com/*", script: "tunnel-bus" });
  assert.ok(f.calls.every((c) => c.auth === "Bearer tok-xyz"));
  assert.equal(JSON.parse(r.stdout).controlHost, "ctl.example.com");
});

test("domain-setup --suffix uses *-bus.zone and refuses a route owned by another script", async () => {
  const f = await fakeApi((m, u) => u.startsWith("/zones?name") ? [{ id: "Z1", name: "example.com" }] : u.endsWith("/workers/routes") ? [{ pattern: "*-bus.example.com/*", script: "other" }] : []);
  const r = await run("domain-setup.mjs", ["--zone", "example.com", "--suffix", "-bus"], { CF_API_BASE: f.base });
  f.srv.close();
  assert.notEqual(r.code, 0);
  assert.match(r.stderr, /another script/);
  assert.equal(f.calls.filter((c) => c.method === "POST").length, 1); // only the DNS record
});

test("access-setup creates token, policies, app; the secret goes to the file, not the output", async () => {
  const f = await fakeApi((m, u) => {
    if (m === "GET" && u.endsWith("/organizations")) return { auth_domain: "myteam.cloudflareaccess.com" };
    if (m === "POST" && u.endsWith("/service_tokens")) return { id: "T1", client_id: "cid.access", client_secret: "SECRET-VALUE" };
    if (m === "POST" && u.endsWith("/policies")) return { id: "P" + Math.random().toString(36).slice(2, 6) };
    if (m === "POST" && u.endsWith("/apps")) return { id: "A1", aud: "AUD-TAG" };
    return [];
  });
  const out = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "tb-")), "tok.json");
  const r = await run("access-setup.mjs", ["--zone", "example.com", "--email", "me@example.com", "--out", out], { CF_API_BASE: f.base, CLOUDFLARE_ACCOUNT_ID: "ACC" });
  f.srv.close();
  assert.equal(r.code, 0, r.stderr);
  assert.ok(!r.stdout.includes("SECRET-VALUE") && !r.stderr.includes("SECRET-VALUE"));
  assert.deepEqual(JSON.parse(fs.readFileSync(out, "utf8")), { id: "T1", client_id: "cid.access", client_secret: "SECRET-VALUE" });
  const posts = f.calls.filter((c) => c.method === "POST");
  const pol = posts.filter((c) => c.url.endsWith("/policies"));
  assert.equal(pol[0].body.decision, "allow");
  assert.deepEqual(pol[0].body.include, [{ email: { email: "me@example.com" } }]);
  assert.equal(pol[1].body.decision, "non_identity");
  assert.deepEqual(pol[1].body.include, [{ service_token: { token_id: "T1" } }]);
  const app = posts.find((c) => c.url.endsWith("/apps")).body;
  assert.equal(app.domain, "*.example.com");
  assert.equal(app.type, "self_hosted");
  assert.equal(app.policies.length, 2);
  assert.deepEqual(JSON.parse(r.stdout), { team: "myteam.cloudflareaccess.com", aud: "AUD-TAG", application: "*.example.com", serviceTokenId: "T1" });
});
