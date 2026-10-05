// Tiny Cloudflare API helper shared by domain-setup.mjs and access-setup.mjs (Node 18+, no dependencies).
import fs from "node:fs";

export function parseArgs(argv, spec) {
  const out = { _: [] };
  for (const [k, v] of Object.entries(spec)) if (v.default !== undefined) out[k] = v.default;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) { out._.push(a); continue; }
    const key = a.slice(2);
    const s = spec[key];
    if (!s) throw new Error(`unknown option --${key}`);
    if (s.flag) { out[key] = true; continue; }
    const val = argv[++i];
    if (val === undefined) throw new Error(`--${key} needs a value`);
    if (s.multi) (out[key] ??= []).push(val); else out[key] = val;
  }
  return out;
}

export function makeClient({ token, base = process.env.CF_API_BASE || "https://api.cloudflare.com/client/v4", dryRun = false, log = console.error }) {
  if (!token) throw new Error("CLOUDFLARE_API_TOKEN is not set");
  async function call(method, path, body) {
    if (dryRun && method !== "GET") {
      log(`[dry-run] ${method} ${path} ${body ? JSON.stringify(body) : ""}`);
      return { result: { id: "dry-run-id", aud: "dry-run-aud", client_id: "dry-run", client_secret: "dry-run" } };
    }
    const res = await fetch(base + path, {
      method,
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let json;
    try { json = JSON.parse(text); } catch { json = { success: false, errors: [{ message: text.slice(0, 300) }] }; }
    if (!res.ok || json.success === false) {
      const msg = (json.errors || []).map((e) => `${e.code ?? ""} ${e.message}`).join("; ");
      throw new Error(`${method} ${path} -> ${res.status}: ${msg}`);
    }
    return json;
  }
  return { get: (p) => call("GET", p), post: (p, b) => call("POST", p, b), put: (p, b) => call("PUT", p, b) };
}

export async function findZone(cf, name) {
  const r = await cf.get(`/zones?name=${encodeURIComponent(name)}`);
  const z = r.result.find((z) => z.name === name);
  if (!z) throw new Error(`zone ${name} not found (is the token allowed to read it?)`);
  return z;
}

export async function accountId(cf) {
  if (process.env.CLOUDFLARE_ACCOUNT_ID) return process.env.CLOUDFLARE_ACCOUNT_ID;
  const r = await cf.get("/accounts");
  if (r.result.length !== 1) throw new Error("token sees " + r.result.length + " accounts; set CLOUDFLARE_ACCOUNT_ID");
  return r.result[0].id;
}

/** Set string vars in wrangler.jsonc's "vars" block: "KEY": "value". Keys must exist. */
export function writeVars(file, vars) {
  let s = fs.readFileSync(file, "utf8");
  for (const [k, v] of Object.entries(vars)) {
    const re = new RegExp(`("${k}"\\s*:\\s*)"[^"]*"`);
    if (!re.test(s)) throw new Error(`${k} not found in ${file}`);
    s = s.replace(re, (_, p) => `${p}${JSON.stringify(v)}`);
  }
  fs.writeFileSync(file, s);
}
