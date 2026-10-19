import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { wire, createWorker } from '../extension/core.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const ext = (f) => fs.readFileSync(path.join(here, '..', 'extension', f), 'utf8');

test('manifest: minimal permissions, poe.com only, no externally_connectable', () => {
  const m = JSON.parse(ext('manifest.json'));
  assert.deepEqual([...m.permissions].sort(), ['nativeMessaging', 'scripting', 'tabs']);
  assert.deepEqual(m.host_permissions, ['https://poe.com/*']);
  assert.equal(m.externally_connectable, undefined);
  assert.ok(m.key);
});

test('extension source registers no page/external message listeners', () => {
  for (const f of ['background.js', 'core.js', 'bridge.js']) {
    const src = ext(f);
    assert.ok(!/onMessageExternal|onConnectExternal|runtime\.onMessage|runtime\.onConnect\b|window\.addEventListener\(\s*['"]message/.test(src), f);
  }
});

function fakeChrome({ url = 'https://poe.com/bots', ready = true, result = { status: 200, json: { data: 1 } } } = {}) {
  const log = { created: 0, removed: 0, exec: [], listeners: [] };
  const port = { handlers: [], posted: [], onMessage: { addListener: (f) => port.handlers.push(f) }, onDisconnect: { addListener() {} }, postMessage: (m) => port.posted.push(m) };
  const chrome = {
    runtime: { connectNative: (n) => { log.native = n; return port; }, onMessage: { addListener: () => log.listeners.push('onMessage') }, onMessageExternal: { addListener: () => log.listeners.push('ext') } },
    tabs: { create: async () => { log.created++; return { id: 7 }; }, get: async () => ({ id: 7, status: 'complete', url }), remove: async () => { log.removed++; } },
    scripting: { executeScript: async ({ func, args, world }) => { log.exec.push(world); const s = String(func); return [{ result: s.includes('__poeGqlReady') ? ready : result }]; } },
  };
  return { chrome, port, log };
}

test('wire connects to the native host only and answers on that port', async () => {
  const { chrome, port, log } = fakeChrome();
  wire(chrome);
  assert.equal(log.native, 'com.ref12.poebots');
  assert.deepEqual(log.listeners, []);
  await port.handlers[0]({ id: 1, op: 'ping' });
  assert.equal(port.posted[0].ok, true);
  await port.handlers[0]({ id: 2, op: 'eval', code: 'alert(1)' });
  assert.equal(port.posted[1].ok, false);
  assert.equal(port.posted[1].code, 'BAD_REQUEST');
  await port.handlers[0]({ id: 3, op: 'gql', queryName: 'bad name!', hash: 'x', variables: {} });
  assert.equal(port.posted[2].code, 'BAD_REQUEST');
});

test('gql runs in MAIN world in a background tab and returns only the response', async () => {
  const { chrome, log } = fakeChrome();
  const w = createWorker(chrome, { idleCloseMs: 10 });
  const r = await w.handle({ id: 1, op: 'gql', queryName: 'HandleProfilePageQuery', hash: 'a'.repeat(64), variables: {} });
  assert.deepEqual(r, { status: 200, json: { data: 1 } });
  assert.ok(log.exec.every((x) => x === 'MAIN'));
  assert.equal(log.created, 1);
  await new Promise((r) => setTimeout(r, 60));
  assert.equal(log.removed, 1);
});

test('signed-out redirect gives NOT_SIGNED_IN', async () => {
  const { chrome } = fakeChrome({ url: 'https://poe.com/login?x=1', ready: false });
  const w = createWorker(chrome, { idleCloseMs: 10 });
  await assert.rejects(() => w.handle({ id: 1, op: 'gql', queryName: 'Q', hash: 'a'.repeat(64), variables: {} }), (e) => e.code === 'NOT_SIGNED_IN');
});
