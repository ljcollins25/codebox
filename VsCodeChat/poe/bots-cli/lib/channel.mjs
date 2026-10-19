// Local channel between the CLI and the native host process Chrome starts.
// Loopback TCP, newline-delimited JSON, every request carries a per-install secret read from a file in the user profile.
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { encodeFrame, FrameDecoder } from './native.mjs';

export function dataDir() {
  if (process.env.POE_BOTS_HOME) return path.resolve(process.env.POE_BOTS_HOME);
  if (process.platform === 'win32') return path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'poe-bots');
  if (process.platform === 'darwin') return path.join(os.homedir(), 'Library', 'Application Support', 'poe-bots');
  return path.join(process.env.XDG_DATA_HOME || path.join(os.homedir(), '.local', 'share'), 'poe-bots');
}

export const secretFile = (dir = dataDir()) => path.join(dir, 'secret');
export const hostFile = (dir = dataDir()) => path.join(dir, 'host.json');

export function ensureSecret(dir = dataDir()) {
  fs.mkdirSync(dir, { recursive: true });
  const f = secretFile(dir);
  if (!fs.existsSync(f)) fs.writeFileSync(f, crypto.randomBytes(32).toString('hex'), { mode: 0o600 });
  return fs.readFileSync(f, 'utf8').trim();
}

/** Route one CLI request. Pure function of its inputs so it can be unit tested. */
export function createRouter({ secret, sendToExtension, status }) {
  return async function route(req) {
    if (!req || typeof req !== 'object') return { ok: false, code: 'BAD_REQUEST', error: 'bad request' };
    const given = Buffer.from(String(req.secret || ''));
    const want = Buffer.from(secret);
    if (given.length !== want.length || !crypto.timingSafeEqual(given, want)) return { ok: false, code: 'AUTH', error: 'bad secret' };
    if (req.op === 'status') return { ok: true, data: status() };
    if (['ping', 'gql', 'viewer'].includes(req.op)) {
      if (!status().extensionConnected) return { ok: false, code: 'NO_EXTENSION', error: 'extension not connected' };
      return sendToExtension({ op: req.op, queryName: req.queryName, variables: req.variables, hash: req.hash });
    }
    return { ok: false, code: 'BAD_REQUEST', error: 'unknown op ' + req.op };
  };
}

/** The native host: stdin/stdout speak Chrome framing to the extension; a loopback server serves the CLI. */
export function runHost({ stdin = process.stdin, stdout = process.stdout, dir = dataDir(), log = () => {}, timeoutMs = 90000 } = {}) {
  const secret = ensureSecret(dir);
  const dec = new FrameDecoder();
  const pending = new Map();
  let seq = 0;
  let connected = true; // Chrome only starts the host for a connecting extension
  let lastPing = null;

  const sendToExtension = (msg) => new Promise((resolve) => {
    const id = ++seq;
    const t = setTimeout(() => { pending.delete(id); resolve({ ok: false, code: 'TIMEOUT', error: 'extension did not answer in time' }); }, timeoutMs);
    pending.set(id, (r) => { clearTimeout(t); resolve(r); });
    stdout.write(encodeFrame({ id, ...msg }));
  });
  const route = createRouter({ secret, sendToExtension, status: () => ({ extensionConnected: connected, extensionVersion: lastPing, pid: process.pid }) });

  stdin.on('data', (chunk) => {
    for (const m of dec.push(chunk)) {
      const cb = pending.get(m.id);
      if (cb) { pending.delete(m.id); cb(m.ok ? { ok: true, data: m.data } : { ok: false, code: m.code, error: m.error }); }
    }
  });
  const shutdown = () => { connected = false; try { fs.rmSync(hostFile(dir), { force: true }); } catch (e) {} server.close(); process.exit(0); };
  stdin.on('end', shutdown);

  const server = net.createServer((sock) => {
    let buf = '';
    sock.on('data', async (d) => {
      buf += d;
      const nl = buf.indexOf('\n');
      if (nl < 0) return;
      let req; try { req = JSON.parse(buf.slice(0, nl)); } catch (e) { req = null; }
      buf = '';
      const res = await route(req);
      sock.end(JSON.stringify(res) + '\n');
    });
    sock.on('error', () => {});
  });
  server.listen(0, '127.0.0.1', () => {
    fs.writeFileSync(hostFile(dir), JSON.stringify({ port: server.address().port, pid: process.pid }));
    log('host listening ' + server.address().port);
    sendToExtension({ op: 'ping' }).then((r) => { if (r.ok) lastPing = r.data.version; });
  });
  return { server, route, pending };
}

/** CLI side: one request/response over the loopback channel. */
export function request(req, { dir = dataDir(), timeoutMs = 120000 } = {}) {
  return new Promise((resolve, reject) => {
    let info;
    try { info = JSON.parse(fs.readFileSync(hostFile(dir), 'utf8')); } catch (e) {
      return reject(Object.assign(new Error('extension is not running: the native host is not up. Is Chrome open with the poe-bots extension loaded? (poe-bots status)'), { code: 'NO_HOST' }));
    }
    const secret = fs.existsSync(secretFile(dir)) ? fs.readFileSync(secretFile(dir), 'utf8').trim() : '';
    const sock = net.connect(info.port, '127.0.0.1');
    let buf = '';
    const t = setTimeout(() => { sock.destroy(); reject(Object.assign(new Error('timed out waiting for the extension'), { code: 'TIMEOUT' })); }, timeoutMs);
    sock.on('connect', () => sock.write(JSON.stringify({ ...req, secret }) + '\n'));
    sock.on('data', (d) => { buf += d; });
    sock.on('end', () => { clearTimeout(t); try { resolve(JSON.parse(buf)); } catch (e) { reject(new Error('bad reply from host')); } });
    sock.on('error', (e) => { clearTimeout(t); reject(Object.assign(new Error('cannot reach the native host (' + e.code + '): Chrome may have closed it; reopen Chrome'), { code: 'NO_HOST' })); });
  });
}
