// poe-bots install: writes the native host manifest and registers it for the current user.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dataDir, ensureSecret } from './channel.mjs';

export const HOST_NAME = 'com.ref12.poebots';
const here = path.dirname(fileURLToPath(import.meta.url));
export const CLI = path.resolve(here, '..', 'poe-bots.mjs');

export function extensionId() {
  const m = JSON.parse(fs.readFileSync(path.resolve(here, '..', 'extension', 'manifest.json'), 'utf8'));
  // id = first 16 bytes of sha256(DER public key), mapped to a-p
  return import('node:crypto').then(({ createHash }) => {
    const h = createHash('sha256').update(Buffer.from(m.key, 'base64')).digest().subarray(0, 16);
    return [...h].map((x) => String.fromCharCode(97 + (x >> 4)) + String.fromCharCode(97 + (x & 15))).join('');
  });
}

export function hostManifest({ id, launcher }) {
  return {
    name: HOST_NAME,
    description: 'poe-bots native host (bridges the poe-bots CLI to the poe-bots Chrome extension)',
    path: launcher,
    type: 'stdio',
    allowed_origins: ['chrome-extension://' + id + '/'],
  };
}

export function writeLauncher(dir, node = process.execPath, cli = CLI) {
  fs.mkdirSync(dir, { recursive: true });
  if (process.platform === 'win32') {
    const f = path.join(dir, 'poe-bots-host.cmd');
    fs.writeFileSync(f, '@echo off\r\n"' + node + '" "' + cli + '" --native-host %*\r\n');
    return f;
  }
  const f = path.join(dir, 'poe-bots-host.sh');
  fs.writeFileSync(f, '#!/bin/sh\nexec "' + node + '" "' + cli + '" --native-host "$@"\n', { mode: 0o755 });
  return f;
}

function manifestDirs() {
  const h = os.homedir();
  if (process.platform === 'linux') return ['google-chrome', 'chromium', 'microsoft-edge'].map((n) => path.join(h, '.config', n, 'NativeMessagingHosts'));
  if (process.platform === 'darwin') return ['Google/Chrome', 'Chromium', 'Microsoft Edge'].map((n) => path.join(h, 'Library', 'Application Support', n, 'NativeMessagingHosts'));
  return [];
}

export async function install({ dir = dataDir(), log = console.log } = {}) {
  ensureSecret(dir);
  const id = await extensionId();
  const launcher = writeLauncher(dir);
  const manifestPath = path.join(dir, HOST_NAME + '.json');
  fs.writeFileSync(manifestPath, JSON.stringify(hostManifest({ id, launcher }), null, 2));
  log('wrote ' + manifestPath);
  if (process.platform === 'win32') {
    for (const key of ['Software\\Google\\Chrome\\NativeMessagingHosts\\' + HOST_NAME, 'Software\\Microsoft\\Edge\\NativeMessagingHosts\\' + HOST_NAME]) {
      const r = spawnSync('reg', ['add', 'HKCU\\' + key, '/ve', '/t', 'REG_SZ', '/d', manifestPath, '/f'], { encoding: 'utf8' });
      log((r.status === 0 ? 'registered HKCU\\' : 'FAILED to register HKCU\\') + key);
    }
  } else {
    for (const d of manifestDirs()) {
      fs.mkdirSync(d, { recursive: true });
      fs.copyFileSync(manifestPath, path.join(d, HOST_NAME + '.json'));
      log('registered ' + path.join(d, HOST_NAME + '.json'));
    }
  }
  log('extension id: ' + id);
  return { id, manifestPath, launcher };
}
