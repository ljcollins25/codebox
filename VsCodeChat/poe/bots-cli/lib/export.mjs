// poe-bots login --from-main: copy a site's cookies (HttpOnly included) and localStorage from a browser that is
// already signed in into the CLI's own profile. Values go straight from one browser to the other inside this process:
// they are never printed, logged or written anywhere but the profile. Only counts are reported.
//
// Sources:
//   mcp  Playwright MCP Bridge (Chrome Web Store extension) in your main Chrome, driven through `npx @playwright/mcp --extension`
//        with PLAYWRIGHT_MCP_EXTENSION_TOKEN (needs Node 20+ for the MCP server).
//   cdp  any browser with a CDP endpoint (--cdp http://127.0.0.1:PORT), used for tests.
import { launch, profileDir } from './page.mjs';

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

export async function exportViaMcp(origin, { token = process.env.PLAYWRIGHT_MCP_EXTENSION_TOKEN, nodeDir = process.env.POE_MCP_NODE_DIR, extraArgs = (process.env.POE_MCP_ARGS || '').split(' ').filter(Boolean) } = {}) {
  if (!token) throw new Error('set PLAYWRIGHT_MCP_EXTENSION_TOKEN (shown on the Playwright MCP Bridge extension page); see README');
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  const sep = process.platform === 'win32' ? ';' : ':';
  const env = { ...process.env, PLAYWRIGHT_MCP_EXTENSION_TOKEN: token };
  if (nodeDir) env.PATH = nodeDir + sep + env.PATH;
  const npx = process.platform === 'win32' ? 'npx.cmd' : 'npx';
  const command = nodeDir ? nodeDir + (process.platform === 'win32' ? '\\' : '/') + npx : npx;
  const client = new Client({ name: 'poe-bots', version: '0.1.0' });
  await client.connect(new StdioClientTransport({ command, args: ['-y', '@playwright/mcp@latest', '--extension', ...extraArgs], env, stderr: 'ignore' }));
  try {
    const call = async (name, args) => {
      const r = await client.callTool({ name, arguments: args });
      const text = (r.content || []).map((c) => c.text || '').join('\n');
      if (r.isError) throw new Error(name + ' failed: ' + text.split(token).join('<token>').slice(0, 300));
      return text;
    };
    await call('browser_navigate', { url: origin });
    return parseMcpResult(await call('browser_run_code_unsafe', { code: EXPORT_CODE(origin) }));
  } finally {
    await client.close().catch(() => {});
  }
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

export async function loginFromMain({ origin = 'https://poe.com/', profile, cdp } = {}) {
  const data = cdp ? await exportViaCdp(origin, cdp) : await exportViaMcp(origin);
  if (!data.cookies || !data.cookies.length) throw new Error('the source browser has no cookies for ' + origin + ': is it signed in there?');
  return importIntoProfile(data, origin, profile);
}
