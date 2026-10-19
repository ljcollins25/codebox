// Chrome native-messaging framing: 4-byte little-endian length + UTF-8 JSON, on stdio.
export function encodeFrame(obj) {
  const body = Buffer.from(JSON.stringify(obj), 'utf8');
  const head = Buffer.alloc(4);
  head.writeUInt32LE(body.length, 0);
  return Buffer.concat([head, body]);
}

export class FrameDecoder {
  constructor(maxBytes = 64 * 1024 * 1024) { this.buf = Buffer.alloc(0); this.max = maxBytes; }
  /** Feed bytes; returns the array of complete JSON messages. */
  push(chunk) {
    this.buf = Buffer.concat([this.buf, chunk]);
    const out = [];
    while (this.buf.length >= 4) {
      const n = this.buf.readUInt32LE(0);
      if (n > this.max) throw new Error('native frame too large: ' + n);
      if (this.buf.length < 4 + n) break;
      out.push(JSON.parse(this.buf.subarray(4, 4 + n).toString('utf8')));
      this.buf = this.buf.subarray(4 + n);
    }
    return out;
  }
}
