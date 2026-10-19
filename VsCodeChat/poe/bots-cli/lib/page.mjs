// PRIMARY bridge: the CLI owns a dedicated Chrome/Edge profile (its own user-data-dir) and runs the web app's own
// GraphQL calls with fetch() INSIDE a poe.com page of that profile. Cookies, formkey and tchannel never leave the
// browser: this module never reads them. (Fallback: lib/page-ext.mjs, via the Chrome extension + native host.)
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { OPS } from './ops.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const BRIDGE_JS = fs.readFileSync(path.join(here, '..', 'extension', 'bridge.js'), 'utf8');

export class NotSignedIn extends Error {
  constructor() { super('not signed in to poe.com in the poe-bots browser profile: run "poe-bots login"'); this.code = 'NOT_SIGNED_IN'; }
}

export function profileDir(opt) {
  if (opt) return path.resolve(opt);
  if (process.env.POE_BOTS_PROFILE) return path.resolve(process.env.POE_BOTS_PROFILE);
  if (process.platform === 'win32') return path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'poe-bots', 'chrome-profile');
  if (process.platform === 'darwin') return path.join(os.homedir(), 'Library', 'Application Support', 'poe-bots', 'chrome-profile');
  return path.join(process.env.XDG_DATA_HOME || path.join(os.homedir(), '.local', 'share'), 'poe-bots', 'chrome-profile');
}

export async function launch(profile, { headless, visible } = {}) {
  fs.mkdirSync(profile, { recursive: true });
  const args = ['--no-first-run', '--no-default-browser-check'];
  // Default: headed (headless Chrome is more likely to be bot-checked) but minimized and parked off-screen.
  if (!headless && !visible) args.push('--start-minimized', '--window-position=-32000,-32000', '--window-size=1100,800');
  const base = { headless: !!headless, args, viewport: null, ignoreDefaultArgs: ['--enable-automation'] };
  const exe = process.env.POE_BROWSER;
  const tries = exe ? [{ executablePath: exe }] : [{ channel: 'chrome' }, { channel: 'msedge' }];
  let last;
  for (const t of tries) {
    try { return await chromium.launchPersistentContext(profile, { ...base, ...t }); } // Playwright attaches over --remote-debugging-pipe
    catch (e) {
      last = e;
      if (/user data directory is already in use|ProcessSingleton/i.test(String(e))) throw new Error('the poe-bots browser profile is already open (another poe-bots command or login window): close it and retry');
    }
  }
  throw new Error('could not start system Chrome or Edge (set POE_BROWSER to an executable): ' + String(last).split('\n')[0]);
}

export async function isSignedIn(page) {
  await page.goto('https://poe.com/create_bot', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  return !/\/login/.test(page.url());
}

/** Open the profile visibly on poe.com/login and wait until the user has signed in (poe-bots login). */
export async function login(opts = {}) {
  const ctx = await launch(profileDir(opts.profile), { visible: true });
  try {
    const page = ctx.pages()[0] || (await ctx.newPage());
    await page.goto('https://poe.com/login', { waitUntil: 'domcontentloaded' });
    const probe = await ctx.newPage(); // separate tab, so the sign-in flow is never navigated away
    const mins = opts.timeoutMin || 15;
    const deadline = Date.now() + mins * 60000;
    console.error('Sign in to poe.com in the browser window that just opened (waiting up to ' + mins + ' min)...');
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 4000));
      if (await isSignedIn(probe).catch(() => false)) { await probe.close(); return true; }
    }
    return false;
  } finally {
    await ctx.close().catch(() => {});
  }
}

export async function connect(opts = {}) {
  const ctx = await launch(profileDir(opts.profile), { headless: opts.headless });
  const close = () => ctx.close().catch(() => {});
  try {
    await ctx.addInitScript(BRIDGE_JS); // installed before the app's scripts, so it sees the first request
    const page = await ctx.newPage();
    await page.goto('https://poe.com/bots', { waitUntil: 'domcontentloaded' });
    const ready = () => page.evaluate('window.__poeGqlReady && window.__poeGqlReady()').catch(() => false);
    for (let i = 0; i < 80 && !(await ready()); i++) {
      if (/\/login/.test(page.url())) throw new NotSignedIn();
      await page.waitForTimeout(250);
    }
    if (/\/login/.test(page.url())) throw new NotSignedIn();
    if (!(await ready())) throw new Error('poe.com did not issue a GraphQL request (page changed?)');
    const gql = async (queryName, variables = {}) => {
      const hash = OPS[queryName];
      if (!hash) throw new Error('unknown operation ' + queryName);
      const res = await page.evaluate(([q, v, h]) => window.__poeGql(q, v, h), [queryName, variables, hash]);
      if (res.status !== 200 || !res.json) throw new Error(queryName + ': HTTP ' + res.status + ' ' + (res.text || ''));
      if (res.json.errors) throw new Error(queryName + ': ' + JSON.stringify(res.json.errors).slice(0, 400));
      return res.json.data;
    };
    const viewer = async () => {
      for (let i = 0; i < 20; i++) {
        const h = await page.evaluate(() => {
          const els = [...document.querySelectorAll('a,div,span,p')].filter((e) => e.children.length === 0 && /^@[\w.-]+$/.test((e.textContent || '').trim()));
          return els.length ? els[els.length - 1].textContent.trim().slice(1) : null;
        });
        if (h) return h;
        await page.waitForTimeout(500);
      }
      return null;
    };
    return { page, gql, viewer, close };
  } catch (e) {
    await close();
    throw e;
  }
}
