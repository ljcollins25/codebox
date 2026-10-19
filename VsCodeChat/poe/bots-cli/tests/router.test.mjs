import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import { createRouter, runHost, request, hostFile } from '../lib/channel.mjs';
import { encodeFrame, FrameDecoder } from '../lib/native.mjs';
import { hostManifest } from '../lib/install.mjs';

const mk = (connected = true) => {
  const sent = [];
  const r = createRouter({ secret: 's3', sendToExtension: async (m) => { sent.push(m); return { ok: true, data: { echoed: m.op } }; }, status: () => ({ extensionConnected: connected }) });
  return { r, sent };
};

test('router rejects wrong or missing secret', async () => {
  const { r, sent } = mk();
  assert.equal((await r({ op: 'gql', secret: 'nope' })).code, 'AUTH');
  assert.equal((await r({ op: 'gql' })).code, 'AUTH');
  assert.equal(sent.length, 0);
});

test('router routes gql/ping/viewer to the extension and strips unknown fields', async () => {
  const { r, sent } = mk();
  const res = await r({ secret: 's3', op: 'gql', queryName: 'Q', variables: {}, hash: 'h', evil: 'x' });
  assert.deepEqual(res, { ok: true, data: { echoed: 'gql' } });
  assert.deepEqual(Object.keys(sent[0]).sort(), ['hash', 'op', 'queryName', 'variables']);
});

test('router: status answered locally, unknown op rejected, no extension reported', async () => {
  assert.equal((await mk().r({ secret: 's3', op: 'status' })).data.extensionConnected, true);
  assert.equal((await mk().r({ secret: 's3', op: 'rm -rf' })).code, 'BAD_REQUEST');
  assert.equal((await mk(false).r({ secret: 's3', op: 'gql' })).code, 'NO_EXTENSION');
});

test('host end to end over loopback: CLI request -> framed message on stdout -> reply on stdin', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'poebots-'));
  const stdin = new PassThrough(), stdout = new PassThrough();
  const dec = new FrameDecoder();
  stdout.on('data', (c) => { for (const m of dec.push(c)) { if (m.op === 'ping') stdin.write(encodeFrame({ id: m.id, ok: true, data: { version: 'T' } })); if (m.op === 'gql') stdin.write(encodeFrame({ id: m.id, ok: true, data: { status: 200, json: { data: { x: 1 } } } })); } });
  const h = runHost({ stdin, stdout, dir, timeoutMs: 3000 });
  for (let i = 0; i < 50 && !fs.existsSync(hostFile(dir)); i++) await new Promise((r) => setTimeout(r, 50));
  const res = await request({ op: 'gql', queryName: 'Q', variables: {}, hash: 'a'.repeat(64) }, { dir });
  assert.equal(res.data.json.data.x, 1);
  const st = await request({ op: 'status' }, { dir });
  assert.equal(st.ok, true);
  // a client without the secret is refused
  fs.writeFileSync(path.join(dir, 'secret'), 'wrong');
  assert.equal((await request({ op: 'status' }, { dir })).code, 'AUTH');
  h.server.close();
});

test('host manifest only allows the extension origin', () => {
  const m = hostManifest({ id: 'abc', launcher: 'C:\\x.cmd' });
  assert.deepEqual(m.allowed_origins, ['chrome-extension://abc/']);
  assert.equal(m.type, 'stdio');
});
