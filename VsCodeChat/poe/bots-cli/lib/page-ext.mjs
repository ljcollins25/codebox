// GraphQL access through the extension. Cookies, formkey and tchannel stay inside the browser.
import { request } from './channel.mjs';
import { OPS } from './ops.mjs';

const HINT = {
  NO_EXTENSION: 'the poe-bots extension is not connected: in Chrome open chrome://extensions, check "poe-bots bridge" is enabled (see README, Install), then run "poe-bots status"',
  NOT_SIGNED_IN: 'poe.com is signed out in this Chrome profile: sign in to poe.com in Chrome, then retry',
};

export async function call(req, opts) {
  const r = await request(req, opts);
  if (!r.ok) throw Object.assign(new Error((HINT[r.code] ? HINT[r.code] + ' (' + r.code + ')' : r.code + ': ' + r.error)), { code: r.code });
  return r.data;
}

export async function connect(opts = {}) {
  const dir = opts.dir;
  const gql = async (queryName, variables = {}) => {
    const hash = OPS[queryName];
    if (!hash) throw new Error('unknown operation ' + queryName);
    const res = await call({ op: 'gql', queryName, variables, hash }, { dir });
    if (res.status !== 200 || !res.json) throw new Error(queryName + ': HTTP ' + res.status + ' ' + (res.text || ''));
    if (res.json.errors) throw new Error(queryName + ': ' + JSON.stringify(res.json.errors).slice(0, 400));
    return res.json.data;
  };
  const viewer = async () => (await call({ op: 'viewer' }, { dir })).handle;
  return { gql, viewer, close: async () => {} };
}
