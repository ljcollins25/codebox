import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeFrame, FrameDecoder } from '../lib/native.mjs';

test('frame: 4-byte LE length + json', () => {
  const f = encodeFrame({ a: 'é' });
  assert.equal(f.readUInt32LE(0), f.length - 4);
  assert.deepEqual(JSON.parse(f.subarray(4).toString('utf8')), { a: 'é' });
});

test('decoder handles split and coalesced chunks', () => {
  const d = new FrameDecoder();
  const f1 = encodeFrame({ n: 1 }), f2 = encodeFrame({ n: 2 });
  const all = Buffer.concat([f1, f2]);
  assert.deepEqual(d.push(all.subarray(0, 3)), []);
  assert.deepEqual(d.push(all.subarray(3, f1.length + 2)), [{ n: 1 }]);
  assert.deepEqual(d.push(all.subarray(f1.length + 2)), [{ n: 2 }]);
});

test('decoder rejects oversize frames', () => {
  const d = new FrameDecoder(10);
  const h = Buffer.alloc(4); h.writeUInt32LE(11, 0);
  assert.throws(() => d.push(h));
});
