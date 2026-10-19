import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { findToken, setToken, clearToken, tokenStatus } from '../lib/token.mjs';

const tmp = () => path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'tok-')), '.local', 'playwright-token');
const none = () => null;

test('lookup order: env, then credential store, then file', () => {
  const file = tmp();
  setToken('from-file', { useFile: true, file });
  assert.deepEqual(findToken({ env: {}, file, credRead: none }), { value: 'from-file', source: 'file' });
  assert.deepEqual(findToken({ env: {}, file, credRead: () => 'from-cred\n' }), { value: 'from-cred', source: 'credential-store' });
  assert.deepEqual(findToken({ env: { PLAYWRIGHT_MCP_EXTENSION_TOKEN: ' from-env ' }, file, credRead: () => 'from-cred' }), { value: 'from-env', source: 'env' });
  assert.equal(findToken({ env: {}, file: file + '.missing', credRead: none }), null);
});

test('status never contains the value', () => {
  const file = tmp();
  setToken('SUPERSECRETVALUE', { useFile: true, file });
  const s = tokenStatus({ env: {}, file, credRead: none });
  assert.match(s, /found in file/);
  assert.ok(!s.includes('SUPERSECRET'));
});

test('set goes to the credential store by default and errors clearly if unavailable', () => {
  let got;
  assert.match(setToken('abc', { credWrite: (v) => { got = v; return { code: 0 }; } }), /credential store/);
  assert.equal(got, 'abc');
  assert.throws(() => setToken('abc', { credWrite: () => ({ code: 127 }) }), /--file/);
  assert.throws(() => setToken('  ', { useFile: true, file: tmp() }), /empty/);
});

test('clear removes file and store entry', () => {
  const file = tmp();
  setToken('x', { useFile: true, file });
  assert.deepEqual(clearToken({ file, credDelete: () => ({ code: 0 }) }), ['credential store', 'file']);
  assert.equal(fs.existsSync(file), false);
  assert.deepEqual(clearToken({ file, credDelete: () => ({ code: 3 }) }), []);
});

test('.local/ is git-ignored', () => {
  assert.match(fs.readFileSync(new URL('../.gitignore', import.meta.url), 'utf8'), /^\.local\/$/m);
});
