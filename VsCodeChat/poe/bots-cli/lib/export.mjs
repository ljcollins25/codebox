// poe-bots login --from-main: copy a site's cookies (HttpOnly included) and localStorage from a browser that is
// already signed in into the CLI's own profile. Values go straight from one browser to the other inside this process:
// they are never printed, logged or written anywhere but the profile. Only counts are reported.
//
// Routes (tried in this order unless --via forces one):
//   mcp  Playwright Extension (Chrome Web Store id mmlmfjhmonkocbjadbfplnigmagldckm) in your main Chrome, driven through
//        `npx @playwright/mcp --extension` with PLAYWRIGHT_MCP_EXTENSION_TOKEN (needs Node 20+ for the MCP server).
//   cdp  your main Chrome's own remote debugging (DevToolsActivePort); Chrome shows its consent prompt. --cdp <url> targets any CDP endpoint.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { launch, profileDir } from './page.mjs';
import { findToken } from './token.mjs';

const EXPORT_CODE = (origin) => `async (page) => {
  const cookies = await page.context().cookies([${JSON.stringify(origin)}]);
  const ls = await page.evaluate(() => { const o = {}; for (let i = 0; i < localStorage.length; i++) { const k = localStorage.key(i); o[k] = localStorage.getItem(k); } return o; });
  return JSON.stringify({ cookies, ls });
}`;

/** Pull the JSON payload out of a Playwright MCP tool result ("### Result\n<json>\n### Ran Playwright code..."). */
export function parseMcpResult(text) {
  const m = String(text).match(/### Result\r?\n([\s\S]*?)(?:\r?\n### |$)/);
  if (!m) throw new Error('unexpected Playwright MCP result');
  let v = JSON.parse(m[1]);
  if (typeof v === 'string') v = JSON.parse(v);
  return v;
}

const withTimeout = (p, ms, what) => new Promise((res, rej) => { const t = setTimeout(() => rej(Object.assign(new Error(what + ' did not respond within ' + ms / 1000 + ' s'), { code: 'TIMEOUT' })), ms); p.then((v) => { clearTimeout(t); res(v); }, (x) => { clearTimeout(t); rej(x); }); });

export async function exportViaMcp(origin, { token = findToken()?.value, nodeDir = process.env.POE_MCP_NODE_DIR, extraArgs = (process.env.POE_MCP_ARGS || '').split(' ').filter(Boolean), connectMs = 8000 } = {}) {
  if (!token) throw new Error("no Playwright Extension token: run 'poe-bots token set' (see README)");
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  const sep = process.platform === 'win32' ? ';' : ':';
  const env = { ...process.env, PLAYWRIGHT_MCP_EXTENSION_TOKEN: token };
  if (nodeDir) env.PATH = nodeDir + sep + env.PATH;
  const npx = process.platform === 'win32' ? 'npx.cmd' : 'npx';
  const command = nodeDir ? nodeDir + (process.platform === 'win32' ? '\\' : '/') + npx : npx;
  const client = new Client({ name: 'poe-bots', version: '0.1.0' });
  const transport = new StdioClientTransport({ command, args: ['-y', '@playwright/mcp@latest', '--extension', ...extraArgs], env, stderr: 'ignore' });
  try {
    // the first navigate is what makes the extension attach to a tab; if the extension is not installed/reachable this stalls
    await withTimeout(client.connect(transport), connectMs + 15000, 'Playwright MCP server');
    const call = async (name, args) => {
      const r = await client.callTool({ name, arguments: args });
      const text = (r.content || []).map((c) => c.text || '').join('\n');
      if (r.isError) throw new Error(name + ' failed: ' + text.split(token).join('<token>').slice(0, 300));
      return text;
    };
    await withTimeout(call('browser_navigate', { url: origin }), connectMs, 'Playwright Extension');
    return parseMcpResult(await call('browser_run_code_unsafe', { code: EXPORT_CODE(origin) }));
  } finally {
    await Promise.race([client.close().catch(() => {}), new Promise((r) => setTimeout(r, 3000))]);
    await transport.close().catch(() => {});
  }
}

/** Where Chrome/Edge record their remote-debugging endpoint (enabled at chrome://inspect/#remote-debugging). */
export function mainDevToolsFiles(env = process.env, platform = process.platform, home = os.homedir()) {
  const rel = platform === 'win32' ? [['Google', 'Chrome', 'User Data'], ['Microsoft', 'Edge', 'User Data']]
    : platform === 'darwin' ? [['Google', 'Chrome'], ['Microsoft Edge']] : [['google-chrome'], ['chromium'], ['microsoft-edge']];
  const base = platform === 'win32' ? env.LOCALAPPDATA || path.join(home, 'AppData', 'Local')
    : platform === 'darwin' ? path.join(home, 'Library', 'Application Support') : env.XDG_CONFIG_HOME || path.join(home, '.config');
  return rel.map((r) => path.join(base, ...r, 'DevToolsActivePort'));
}

export function mainDevToolsEndpoint(files = mainDevToolsFiles()) {
  for (const f of files) {
    if (!fs.existsSync(f)) continue;
    const [port, wsPath] = fs.readFileSync(f, 'utf8').split(/\r?\n/);
    if (/^\d+$/.test(port) && wsPath) return 'ws://127.0.0.1:' + port + wsPath;
  }
  return null;
}

export async function exportViaCdp(origin, cdpUrl) {
  const { chromium } = await import('playwright-core');
  const browser = await chromium.connectOverCDP(cdpUrl);
  try {
    const ctx = browser.contexts()[0];
    const page = ctx.pages().find((p) => p.url().startsWith(origin)) || (await ctx.newPage());
    if (!page.url().startsWith(origin)) await page.goto(origin, { waitUntil: 'domcontentloaded' });
    const cookies = await ctx.cookies([origin]);
    const ls = await page.evaluate(() => { const o = {}; for (let i = 0; i < localStorage.length; i++) { const k = localStorage.key(i); o[k] = localStorage.getItem(k); } return o; });
    return { cookies, ls };
  } finally {
    await browser.close().catch(() => {});
  }
}

/** Write exported data into the CLI profile. Returns counts only. */
export async function importIntoProfile(data, origin, profile) {
  const ctx = await launch(profileDir(profile), { visible: false });
  try {
    const cookies = (data.cookies || []).map((c) => ({ name: c.name, value: c.value, domain: c.domain, path: c.path, expires: c.expires, httpOnly: c.httpOnly, secure: c.secure, sameSite: c.sameSite }));
    await ctx.addCookies(cookies);
    const page = ctx.pages()[0] || (await ctx.newPage());
    await page.goto(origin, { waitUntil: 'domcontentloaded' });
    await page.evaluate((ls) => { for (const [k, v] of Object.entries(ls)) localStorage.setItem(k, v); }, data.ls || {});
    const back = await ctx.cookies([origin]);
    const lsN = await page.evaluate(() => localStorage.length);
    return { cookies: cookies.length, httpOnly: cookies.filter((c) => c.httpOnly).length, storedCookies: back.length, localStorage: Object.keys(data.ls || {}).length, storedLocalStorage: lsN };
  } finally {
    await ctx.close().catch(() => {});
  }
}

const MCP_HINT = 'Playwright Extension: install it in your main Chrome and run poe-bots token set';
const CDP_HINT = 'remote debugging: in your main Chrome open chrome://inspect/#remote-debugging and turn it on (Chrome then asks you to allow the connection)';

/**
 * Choose the export route. via: 'auto' | 'mcp' | 'cdp'. `io` is injectable for tests.
 * Returns { route, data }. 'auto' tries mcp first (only if a token is set), then cdp.
 */
export async function exportFromMain(origin, { via = 'auto', cdp, io = {}, log = (m) => console.error(m) } = {}) {
  const viaMcp = io.mcp || exportViaMcp, viaCdp = io.cdp || exportViaCdp, endpoint = io.endpoint || mainDevToolsEndpoint;
  const hasToken = io.hasToken !== undefined ? io.hasToken : !!findToken();
  const tryCdp = async () => {
    const url = cdp || endpoint();
    if (!url) throw new Error('no main-Chrome remote debugging endpoint found (' + CDP_HINT + ')');
    log('using route: cdp (main Chrome remote debugging; allow the prompt in Chrome if it appears)');
    return { route: 'cdp', data: await viaCdp(origin, url) };
  };
  if (via === 'cdp' || (via === 'auto' && cdp)) return tryCdp();
  if (via === 'mcp') {
    if (!hasToken) throw new Error('--via mcp needs the Playwright Extension token (poe-bots token set, or env PLAYWRIGHT_MCP_EXTENSION_TOKEN)');
    log('using route: mcp (Playwright Extension)');
    return { route: 'mcp', data: await viaMcp(origin) };
  }
  if (via !== 'auto') throw new Error('--via must be mcp or cdp');
  let why;
  if (!hasToken) why = 'no PLAYWRIGHT_MCP_EXTENSION_TOKEN';
  else {
    try {
      log('trying route: mcp (Playwright Extension)');
      return { route: 'mcp', data: await viaMcp(origin) };
    } catch (err) { why = 'mcp failed: ' + String(err.message).split('\n')[0].slice(0, 160); }
  }
  log('mcp not used (' + why + '); falling back to cdp');
  try { return await tryCdp(); } catch (err) {
    throw new Error('could not export from the main Chrome. ' + err.message + '. Or use ' + MCP_HINT + '. (' + why + ')');
  }
}

export async function loginFromMain({ origin = 'https://poe.com/', profile, cdp, via = 'auto', io, log } = {}) {
  const { route, data } = await exportFromMain(origin, { via, cdp, io, log });
  if (!data.cookies || !data.cookies.length) throw new Error('the main Chrome has no cookies for ' + origin + ': is it signed in there? (route: ' + route + ')');
  const counts = await (io && io.import ? io.import(data, origin, profile) : importIntoProfile(data, origin, profile));
  return { route, ...counts };
}
