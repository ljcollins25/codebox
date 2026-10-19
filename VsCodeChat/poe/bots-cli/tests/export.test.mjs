import test from 'node:test';
import assert from 'node:assert/strict';
import { parseMcpResult } from '../lib/export.mjs';

test('parseMcpResult unwraps the double-encoded run-code result', () => {
  const payload = { cookies: [{ name: 'a', httpOnly: true }], ls: { k: 'v' } };
  const text = '### Result\n' + JSON.stringify(JSON.stringify(payload)) + '\n### Ran Playwright code\n```js\nawait x;\n```';
  assert.deepEqual(parseMcpResult(text), payload);
});

test('parseMcpResult rejects unexpected text', () => {
  assert.throws(() => parseMcpResult('boom'));
});

import { exportFromMain, mainDevToolsFiles, mainDevToolsEndpoint } from '../lib/export.mjs';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const quiet = () => {};
const D = { cookies: [{ name: 'p-b' }], ls: {} };

test('auto: token set and mcp works -> mcp, cdp untouched', async () => {
  const calls = [];
  const r = await exportFromMain('https://poe.com/', { io: { hasToken: true, mcp: async () => { calls.push('mcp'); return D; }, cdp: async () => { calls.push('cdp'); return D; }, endpoint: () => 'ws://x' }, log: quiet });
  assert.equal(r.route, 'mcp'); assert.deepEqual(calls, ['mcp']);
});

test('auto: no token -> falls back to cdp without trying mcp', async () => {
  const calls = [];
  const r = await exportFromMain('https://poe.com/', { io: { hasToken: false, mcp: async () => { calls.push('mcp'); return D; }, cdp: async () => { calls.push('cdp'); return D; }, endpoint: () => 'ws://x' }, log: quiet });
  assert.equal(r.route, 'cdp'); assert.deepEqual(calls, ['cdp']);
});

test('auto: mcp fails or times out -> falls back to cdp and says so', async () => {
  const logs = [];
  const r = await exportFromMain('https://poe.com/', { io: { hasToken: true, mcp: async () => { throw new Error('Playwright Extension did not respond within 8 s'); }, cdp: async () => D, endpoint: () => 'ws://x' }, log: (m) => logs.push(m) });
  assert.equal(r.route, 'cdp');
  assert.ok(logs.some((l) => /falling back to cdp/.test(l)) && logs.some((l) => /route: cdp/.test(l)));
});

test('--via forces one route and does not fall back', async () => {
  const io = { hasToken: true, mcp: async () => { throw new Error('boom'); }, cdp: async () => D, endpoint: () => 'ws://x' };
  await assert.rejects(() => exportFromMain('https://poe.com/', { via: 'mcp', io, log: quiet }), /boom/);
  assert.equal((await exportFromMain('https://poe.com/', { via: 'cdp', io, log: quiet })).route, 'cdp');
  await assert.rejects(() => exportFromMain('https://poe.com/', { via: 'mcp', io: { ...io, hasToken: false }, log: quiet }), /TOKEN/);
  await assert.rejects(() => exportFromMain('https://poe.com/', { via: 'x', io, log: quiet }), /--via/);
});

test('both routes unavailable -> one actionable error', async () => {
  await assert.rejects(() => exportFromMain('https://poe.com/', { io: { hasToken: false, endpoint: () => null }, log: quiet }), /chrome:\/\/inspect.*Playwright Extension/s);
});

test('DevToolsActivePort discovery per platform and parsing', () => {
  assert.match(mainDevToolsFiles({ LOCALAPPDATA: 'C:\\L' }, 'win32', 'C:\\h')[0], /Google.Chrome.User Data.DevToolsActivePort$/);
  assert.match(mainDevToolsFiles({}, 'linux', '/h')[0], /\.config.google-chrome.DevToolsActivePort$/);
  const d = fs.mkdtempSync(path.join(os.tmpdir(), 'dtap-'));
  const f = path.join(d, 'DevToolsActivePort');
  fs.writeFileSync(f, '4242\n/devtools/browser/abc\n');
  assert.equal(mainDevToolsEndpoint([path.join(d, 'nope'), f]), 'ws://127.0.0.1:4242/devtools/browser/abc');
  assert.equal(mainDevToolsEndpoint([path.join(d, 'nope')]), null);
});
