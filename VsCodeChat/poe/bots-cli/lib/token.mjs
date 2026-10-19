// Where the Playwright Extension token lives for the standalone CLI. Lookup order:
//   1. env PLAYWRIGHT_MCP_EXTENSION_TOKEN   2. OS credential store ("poe-bots/playwright-token")   3. .local/playwright-token
// The value is never printed; status reports only where it was found.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const TARGET = 'poe-bots/playwright-token';
const here = path.dirname(fileURLToPath(import.meta.url));
export const defaultFile = () => process.env.POE_BOTS_TOKEN_FILE || path.join(here, '..', '.local', 'playwright-token');

const PS_CRED = `
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class Cred {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct CREDENTIAL { public int Flags; public int Type; public string TargetName; public string Comment; public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist; public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName; }
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CredWrite(ref CREDENTIAL c, int f);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CredRead(string t, int type, int f, out IntPtr p);
  [DllImport("advapi32.dll", SetLastError=true)] static extern void CredFree(IntPtr p);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CredDelete(string t, int type, int f);
  public static bool Write(string t, string v) { var b = Encoding.Unicode.GetBytes(v); var c = new CREDENTIAL(); c.Type = 1; c.TargetName = t; c.UserName = "poe-bots"; c.CredentialBlobSize = b.Length; c.CredentialBlob = Marshal.AllocHGlobal(b.Length); Marshal.Copy(b, 0, c.CredentialBlob, b.Length); c.Persist = 2; return CredWrite(ref c, 0); }
  public static string Read(string t) { IntPtr p; if (!CredRead(t, 1, 0, out p)) return null; var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL)); var s = Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2); CredFree(p); return s; }
  public static bool Delete(string t) { return CredDelete(t, 1, 0); }
}
'@
`;

function winCred(action, value) {
  const body = PS_CRED + (action === 'write'
    ? "$v=[Console]::In.ReadToEnd().TrimEnd(); if([Cred]::Write('" + TARGET + "',$v)){'ok'}else{exit 1}"
    : action === 'read' ? "$v=[Cred]::Read('" + TARGET + "'); if($v -eq $null){exit 3}else{[Console]::Out.Write($v)}"
    : "if([Cred]::Delete('" + TARGET + "')){'ok'}else{exit 3}");
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(body, 'utf16le').toString('base64')], { input: value || '', encoding: 'utf8' });
  return { code: r.status, out: r.stdout };
}

function linuxCred(action, value) {
  const bin = spawnSync('secret-tool', ['--version'], { encoding: 'utf8' });
  if (bin.error) return { code: 127, out: '' };
  const attrs = ['service', 'poe-bots', 'account', 'playwright-token'];
  const r = action === 'write' ? spawnSync('secret-tool', ['store', '--label=poe-bots playwright token', ...attrs], { input: value, encoding: 'utf8' })
    : action === 'read' ? spawnSync('secret-tool', ['lookup', ...attrs], { encoding: 'utf8' }) : spawnSync('secret-tool', ['clear', ...attrs], { encoding: 'utf8' });
  return { code: r.status, out: r.stdout };
}

const store = (a, v) => (process.platform === 'win32' ? winCred(a, v) : process.platform === 'linux' ? linuxCred(a, v) : { code: 127, out: '' });
const clean = (s) => (s || '').trim();

/** Returns { value, source } or null. source: env | credential-store | file. */
export function findToken({ env = process.env, file = defaultFile(), credRead = () => { const r = store('read'); return r.code === 0 ? r.out : null; } } = {}) {
  if (clean(env.PLAYWRIGHT_MCP_EXTENSION_TOKEN)) return { value: clean(env.PLAYWRIGHT_MCP_EXTENSION_TOKEN), source: 'env' };
  const c = clean(credRead());
  if (c) return { value: c, source: 'credential-store' };
  if (fs.existsSync(file) && clean(fs.readFileSync(file, 'utf8'))) return { value: clean(fs.readFileSync(file, 'utf8')), source: 'file' };
  return null;
}

export function setToken(value, { useFile = false, file = defaultFile(), credWrite } = {}) {
  const v = clean(value);
  if (!v) throw new Error('empty token');
  if (useFile) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, v + '\n', { mode: 0o600 });
    return 'file ' + file;
  }
  const r = (credWrite || ((x) => store('write', x)))(v);
  if (r.code !== 0) throw new Error('the OS credential store is not available here (' + process.platform + '); use: poe-bots token set --file');
  return 'credential store (' + TARGET + ')';
}

export function clearToken({ file = defaultFile(), credDelete = () => store('delete') } = {}) {
  const cleared = [];
  if (credDelete().code === 0) cleared.push('credential store');
  if (fs.existsSync(file)) { fs.rmSync(file); cleared.push('file'); }
  return cleared;
}

export function tokenStatus(opts) {
  const t = findToken(opts);
  return t ? 'found in ' + t.source + ' (' + t.value.length + ' chars)' : 'not set';
}
