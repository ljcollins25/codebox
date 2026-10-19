#!/usr/bin/env node
// poe-bots: local dev workflow for Poe script bots. See README.md.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { login, profileDir, isSignedIn, launch } from './lib/page.mjs';
import { runHost, request } from './lib/channel.mjs';
import { listBots, getBot, liveToFiles, pushBot } from './lib/api.mjs';
import { botDir, listLocal, readLocal, writeLocal, headerProblems, applyHeader } from './lib/bots.mjs';
import { lineDiff } from './lib/diff.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = process.env.POE_BOTS_ROOT || here;
const TEST_BOT = 'ref12-cli-test';

function parseArgs(argv) {
  const pos = [], flags = {}, multi = { attach: [], param: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const k = a.slice(2);
      if (['yes', 'all', 'json', 'allow-real', 'headless', 'from-main'].includes(k)) flags[k] = true;
      else if (k in multi) multi[k].push(argv[++i]);
      else flags[k] = argv[++i];
    } else pos.push(a);
  }
  return { pos, flags, multi };
}

const USAGE = `poe-bots <command>
  login [--profile dir]               open the poe-bots browser profile on poe.com/login; sign in once, it waits
  login --from-main [--via mcp|cdp]   copy poe.com cookies+localStorage from your main Chrome: Playwright Extension (token) first, else Chrome remote debugging
  list [--json]                       your bots on poe.com
  pull <handle>|--all                 live script + metadata -> bots/<handle>/{bot.py,bot.yml}
  diff <handle>                       local vs live (script and metadata)
  push <handle> [--yes] [--allow-real] create or update; shows the diff first, applies only with --yes
  run <handle> --text "..." [--attach f] [--param k=v] [--out dir]   run locally (no browser needed)
  check [handle]                      "# poe:" header vs bot.yml consistency
Global flags: --user <poe handle>  --headless  --profile <dir>
Env: POE_BOTS_PROFILE, POE_BROWSER (chrome/edge exe), POE_BOTS_ROOT, POE_API_KEY (real poe.call in run)
Fallback bridge (Chrome extension + native host): ext-install, ext-status, with POE_BRIDGE=extension for other commands`;

function localFiles(handle) {
  const dir = botDir(root, handle);
  if (!fs.existsSync(path.join(dir, 'bot.yml'))) throw new Error('no local bot at ' + dir);
  return { dir, ...readLocal(dir) };
}

function metaText(meta) {
  return Object.entries(meta).filter(([, v]) => v !== null && v !== '').map(([k, v]) => k + ': ' + JSON.stringify(v)).join('\n');
}

function fullDiff(live, local) {
  const parts = [];
  const md = lineDiff(metaText(live.meta), metaText(local.meta), 'live metadata', 'local bot.yml');
  if (md) parts.push(md);
  const pd = lineDiff(live.py.replace(/\n+$/, ''), local.py.replace(/\n+$/, ''), 'live bot.py', 'local bot.py');
  if (pd) parts.push(pd);
  return parts.join('\n\n');
}

async function main() {
  if (process.argv.includes('--native-host')) { runHost({ log: (m) => process.stderr.write(m + '\\n') }); return new Promise(() => {}); }
  const { pos, flags, multi } = parseArgs(process.argv.slice(2));
  const cmd = pos[0];
  if (!cmd || cmd === 'help' || flags.help) { console.log(USAGE); return 0; }

  if (cmd === 'run') {
    const handle = pos[1];
    if (!handle) throw new Error('usage: run <handle> --text ...');
    const { dir } = localFiles(handle);
    const args = [path.join(here, 'runner', 'run_bot.py'), path.join(dir, 'bot.py'), '--text', flags.text ?? '', '--out', flags.out || 'out'];
    for (const a of multi.attach) args.push('--attach', a);
    for (const p of multi.param) args.push('--param', p);
    const r = spawnSync(process.env.PYTHON || 'python', args, { stdio: 'inherit' });
    return r.status ?? 1;
  }

  if (cmd === 'check') {
    let bad = 0;
    for (const h of pos[1] ? [pos[1]] : listLocal(root)) {
      const { py, meta } = localFiles(h);
      const p = headerProblems(py, meta);
      console.log((p.length ? 'MISMATCH ' : 'ok       ') + h);
      p.forEach((x) => console.log('    ' + x));
      bad += p.length;
    }
    return bad ? 1 : 0;
  }

  if (cmd === 'login' && flags['from-main']) {
    const { loginFromMain } = await import('./lib/export.mjs');
    const origin = flags.site || 'https://poe.com/';
    const c = await loginFromMain({ origin, profile: flags.profile, cdp: flags.cdp, via: flags.via || 'auto' });
    console.log('route: ' + c.route + '; copied ' + c.cookies + ' cookies (' + c.httpOnly + ' HttpOnly), ' + c.localStorage + ' localStorage keys; profile now holds ' + c.storedCookies + ' cookies, ' + c.storedLocalStorage + ' keys');
    if (origin.startsWith('https://poe.com')) {
      const ctx = await launch(profileDir(flags.profile), { headless: flags.headless });
      try { const ok = await isSignedIn(await ctx.newPage()); console.log(ok ? 'signed in to poe.com' : 'copied, but poe.com still shows signed out'); return ok ? 0 : 1; } finally { await ctx.close(); }
    }
    return 0;
  }
  if (cmd === 'login') {
    const ok = await login({ profile: flags.profile });
    console.log(ok ? 'signed in; profile: ' + profileDir(flags.profile) : 'timed out waiting for sign-in');
    return ok ? 0 : 1;
  }
  if (cmd === 'ext-install') { const { install } = await import('./lib/install.mjs'); await install(); console.log('\nNext: chrome://extensions -> Developer mode -> Load unpacked -> ' + path.join(here, 'extension') + '\nThen: poe-bots ext-status'); return 0; }
  if (cmd === 'ext-status') {
    const { call } = await import('./lib/page-ext.mjs');
    let r;
    try { r = await request({ op: 'status' }); } catch (e) { console.log('NOT CONNECTED: ' + e.message); return 1; }
    if (!r.ok || !r.data.extensionConnected) { console.log('NOT CONNECTED'); return 1; }
    let v = '?'; try { v = (await call({ op: 'ping' })).version; } catch (e) { console.log('host up but extension silent: ' + e.message); return 1; }
    console.log('connected: host pid ' + r.data.pid + ', extension v' + v);
    return 0;
  }

  const { connect } = process.env.POE_BRIDGE === 'extension' ? await import('./lib/page-ext.mjs') : await import('./lib/page.mjs');
  const conn = await connect({ profile: flags.profile, headless: flags.headless });
  try {
    if (cmd === 'list') {
      const bots = await listBots(conn, flags.user);
      if (flags.json) console.log(JSON.stringify(bots, null, 2));
      else bots.forEach((b) => console.log(b.handle.padEnd(36) + String(b.tags || '').padEnd(10) + (b.description || '').slice(0, 60)));
      console.error(bots.length + ' bots');
      return 0;
    }
    if (cmd === 'pull') {
      const handles = flags.all ? (await listBots(conn, flags.user)).map((b) => b.handle) : [pos[1]];
      if (!handles[0]) throw new Error('usage: pull <handle>|--all');
      for (const h of handles) {
        const bot = await getBot(conn, h);
        if (!bot) { console.log('skip ' + h + ' (not found / not yours)'); continue; }
        if (bot.baseBotId !== 4209681) { console.log('skip ' + h + ' (not a script bot)'); continue; }
        const { py, meta } = liveToFiles(bot);
        const dir = botDir(root, bot.handle);
        writeLocal(dir, py, meta);
        console.log('pulled ' + bot.handle + ' -> ' + path.relative(process.cwd(), dir));
      }
      return 0;
    }
    if (cmd === 'diff' || cmd === 'push') {
      const handle = pos[1];
      if (!handle) throw new Error('usage: ' + cmd + ' <handle>');
      const local = localFiles(handle);
      const bot = await getBot(conn, local.meta.handle);
      const live = bot ? liveToFiles(bot) : null;
      const probs = headerProblems(local.py, local.meta);
      if (probs.length) console.log('WARNING header/yml mismatch:\n  ' + probs.join('\n  '));
      const d = live ? fullDiff(live, local) : '(bot does not exist live: would CREATE)\n' + metaText(local.meta);
      console.log(d || 'no differences');
      if (cmd === 'diff') return d ? 2 : 0;
      if (!d) return 0;
      if (probs.length) throw new Error('fix header/yml mismatch first (poe-bots check)');
      if (local.meta.handle !== TEST_BOT && !flags['allow-real']) {
        throw new Error('refusing to push ' + local.meta.handle + ': only ' + TEST_BOT + ' is pushable without --allow-real (which needs the user\'s OK)');
      }
      if (!flags.yes) { console.log('\n(dry run: pass --yes to apply)'); return 0; }
      const r = await pushBot(conn, local.py, local.meta, bot);
      console.log((bot ? 'updated ' : 'created ') + local.meta.handle + ': ' + r.status);
      return 0;
    }
    throw new Error('unknown command ' + cmd + '\n' + USAGE);
  } finally {
    await conn.close();
  }
}

main().then((c) => process.exit(c), (e) => { console.error('error: ' + e.message); process.exit(1); });
