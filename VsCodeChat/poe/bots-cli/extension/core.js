// Extension logic, kept free of top-level chrome.* use so node tests can drive it with a fake `chrome`.
export const HOST = 'com.ref12.poebots';
export const VERSION = '0.1.0';
const QN = /^[A-Za-z0-9_]{1,120}$/;
const HASH = /^[0-9a-f]{64}$/;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export class CmdError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

export function createWorker(chrome, { idleCloseMs = 10000 } = {}) {
  let tabId = null;
  let closeTimer = null;
  const exec = async (func, args = []) => {
    const r = await chrome.scripting.executeScript({ target: { tabId }, world: 'MAIN', func, args });
    return r && r[0] ? r[0].result : undefined;
  };

  async function openTab() {
    const t = await chrome.tabs.create({ url: 'https://poe.com/bots', active: false });
    tabId = t.id;
    for (let i = 0; i < 150; i++) { // load complete (30 s max)
      const cur = await chrome.tabs.get(tabId);
      if (cur.status === 'complete') break;
      await sleep(200);
    }
  }

  async function ensureTab() {
    clearTimeout(closeTimer);
    if (tabId !== null) {
      try { await chrome.tabs.get(tabId); } catch (e) { tabId = null; }
    }
    if (tabId === null) await openTab();
    for (let i = 0; i < 75; i++) { // wait for the app's first GraphQL request so the bridge has headers
      const cur = await chrome.tabs.get(tabId);
      if (/\/login/.test(cur.url || '')) throw new CmdError('NOT_SIGNED_IN', 'poe.com redirected to /login: sign in to poe.com in this Chrome profile');
      if (await exec(() => !!(window.__poeGqlReady && window.__poeGqlReady())).catch(() => false)) return;
      await sleep(200);
    }
    throw new CmdError('NOT_READY', 'poe.com page did not issue a GraphQL request (page changed, or the content script did not load)');
  }

  function scheduleClose() {
    clearTimeout(closeTimer);
    closeTimer = setTimeout(async () => {
      const id = tabId;
      tabId = null;
      if (id !== null) await chrome.tabs.remove(id).catch(() => {});
    }, idleCloseMs);
  }

  async function handle(msg) {
    if (!msg || typeof msg !== 'object' || msg.id === undefined) throw new CmdError('BAD_REQUEST', 'message needs an id');
    switch (msg.op) {
      case 'ping':
        return { version: VERSION };
      case 'gql': {
        if (!QN.test(msg.queryName || '') || !HASH.test(msg.hash || '') || typeof msg.variables !== 'object')
          throw new CmdError('BAD_REQUEST', 'gql needs queryName, hash (sha256 hex) and variables');
        await ensureTab();
        try {
          const res = await exec((q, v, h) => window.__poeGql(q, v, h), [msg.queryName, msg.variables, msg.hash]);
          if (/\/login/.test((await chrome.tabs.get(tabId)).url || '')) throw new CmdError('NOT_SIGNED_IN', 'poe.com redirected to /login');
          return res; // { status, json, text } only: no headers, no cookies
        } finally { scheduleClose(); }
      }
      case 'viewer': {
        await ensureTab();
        try {
          for (let i = 0; i < 40; i++) {
            const h = await exec(() => {
              const els = [...document.querySelectorAll('a,div,span,p')].filter((e) => e.children.length === 0 && /^@[\w.-]+$/.test((e.textContent || '').trim()));
              return els.length ? els[els.length - 1].textContent.trim().slice(1) : null;
            });
            if (h) return { handle: h };
            await sleep(250);
          }
          throw new CmdError('NOT_READY', 'could not read your poe handle from the page');
        } finally { scheduleClose(); }
      }
      default:
        throw new CmdError('BAD_REQUEST', 'unknown op ' + msg.op);
    }
  }

  return { handle };
}

/** Connects to the native host and answers commands from that port only. No other message source is registered. */
export function wire(chrome, { reconnectMs = 5000, setTimer = setTimeout } = {}) {
  const worker = createWorker(chrome);
  let port = null;
  function connect() {
    try { port = chrome.runtime.connectNative(HOST); } catch (e) { setTimer(connect, reconnectMs); return; }
    port.onMessage.addListener(async (msg) => {
      const id = msg && msg.id;
      try { port.postMessage({ id, ok: true, data: await worker.handle(msg) }); }
      catch (e) { port.postMessage({ id, ok: false, code: e.code || 'ERROR', error: String(e.message || e) }); }
    });
    port.onDisconnect.addListener(() => { port = null; setTimer(connect, reconnectMs); });
  }
  connect();
  return { worker, connect, get port() { return port; } };
}
