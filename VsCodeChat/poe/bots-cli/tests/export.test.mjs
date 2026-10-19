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
