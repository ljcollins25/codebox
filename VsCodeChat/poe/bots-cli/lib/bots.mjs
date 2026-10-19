// Bot files: bots/<handle>/bot.py + bot.yml, the "# poe:" header, and the mapping to/from Poe's GraphQL fields.
import fs from 'node:fs';
import path from 'node:path';
import yaml from 'js-yaml';

export const ACCESS = ['everyone', 'link', 'private'];
export const YML_KEYS = ['handle', 'description', 'access', 'allow_remix', 'privacy_shield', 'daily_message_limit', 'message_price', 'introduction'];
// `name=` is a display name in existing scripts (e.g. Video-FFmpeg for handle VideoFFMpeg), so only privacy_shield is cross-checked.
export const HEADER_TO_YML = { privacy_shield: 'privacy_shield' };

export function botDir(root, handle) {
  const base = path.join(root, 'bots');
  if (fs.existsSync(base)) {
    const hit = fs.readdirSync(base).find((d) => d.toLowerCase() === handle.toLowerCase());
    if (hit) return path.join(base, hit);
  }
  return path.join(base, handle);
}

export function listLocal(root) {
  const base = path.join(root, 'bots');
  if (!fs.existsSync(base)) return [];
  return fs.readdirSync(base).filter((d) => fs.existsSync(path.join(base, d, 'bot.yml')));
}

export function parseHeader(py) {
  const h = {};
  for (const line of py.split(/\r?\n/)) {
    if (!line.trim()) continue;
    const m = line.match(/^#\s*poe:\s*([A-Za-z_]+)\s*=\s*(.*?)\s*$/);
    if (m) { h[m[1]] = m[2]; continue; }
    if (line.startsWith('#')) continue;
    break;
  }
  return h;
}

/** Rewrite/insert the "# poe:" header lines so they agree with the yml. */
export function applyHeader(py, meta) {
  const want = {};
  if (meta.privacy_shield) want.privacy_shield = meta.privacy_shield;
  const lines = py.split(/\r?\n/);
  const existing = new Set();
  const out = lines.map((l) => {
    const m = l.match(/^#\s*poe:\s*([A-Za-z_]+)\s*=/);
    if (m && m[1] in want) { existing.add(m[1]); return '# poe: ' + m[1] + '=' + want[m[1]]; }
    return l;
  });
  const add = Object.keys(want).filter((k) => !existing.has(k)).map((k) => '# poe: ' + k + '=' + want[k]);
  return [...add, ...out].join('\n');
}

/** Differences between header comments and yml. Empty array when consistent. */
export function headerProblems(py, meta) {
  const h = parseHeader(py), p = [];
  for (const [hk, v] of Object.entries(h)) {
    const yk = HEADER_TO_YML[hk];
    if (!yk) continue;
    const y = meta[yk];
    if (y === undefined || y === null || String(y).toLowerCase() !== String(v).toLowerCase())
      p.push('# poe: ' + hk + '=' + v + '  but bot.yml ' + yk + ' = ' + JSON.stringify(y ?? null));
  }
  if (meta.privacy_shield && h.privacy_shield === undefined)
    p.push('bot.yml privacy_shield = ' + meta.privacy_shield + ' but no "# poe: privacy_shield=" header');
  return p;
}

export function normalizeMeta(raw) {
  const m = { ...raw };
  if (m.handle === undefined && m.name !== undefined) m.handle = m.name; // legacy publish-bots.mjs schema
  delete m.name;
  m.description = m.description ?? '';
  m.access = m.access ?? 'private';
  if (!ACCESS.includes(m.access)) throw new Error('access must be one of ' + ACCESS.join(', ') + ', got ' + m.access);
  m.allow_remix = !!m.allow_remix;
  m.daily_message_limit = m.daily_message_limit ?? null;
  m.message_price = m.message_price ?? null;
  m.privacy_shield = m.privacy_shield ?? null;
  m.introduction = m.introduction ?? '';
  if (!m.handle) throw new Error('bot.yml needs a handle');
  return m;
}

export function readLocal(dir) {
  const py = fs.readFileSync(path.join(dir, 'bot.py'), 'utf8').replace(/\r\n/g, '\n');
  const meta = normalizeMeta(yaml.load(fs.readFileSync(path.join(dir, 'bot.yml'), 'utf8')) || {});
  return { py, meta };
}

export function writeLocal(dir, py, meta) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'bot.py'), py.endsWith('\n') ? py : py + '\n');
  const o = {};
  for (const k of YML_KEYS) {
    if (k === 'introduction' && !meta.introduction) continue;
    if (k === 'privacy_shield' && !meta.privacy_shield) continue;
    o[k] = meta[k] ?? null;
  }
  const body = yaml.dump(o, { lineWidth: 120, noRefs: true, quotingType: '"' });
  fs.writeFileSync(path.join(dir, 'bot.yml'), '# Poe script bot metadata. Keep "# poe:" header comments in bot.py consistent with this file.\n' + body);
}

// ---- GraphQL <-> yml mapping ----
export const priceToCc = (usd) => (usd == null ? 0 : Math.round(usd * 10000)); // 1.5 USD/1000 msgs -> 15000 (verified by capture)
export const ccToPrice = (cc) => (cc == null || cc === 0 ? null : cc / 10000);

export function metaFromLive(bot) {
  const header = parseHeader(bot.promptPlaintext || '');
  return normalizeMeta({
    handle: bot.handle,
    description: bot.description || '',
    access: !bot.isPrivateBot ? 'everyone' : bot.inviteLink ? 'link' : 'private',
    allow_remix: bot.isPromptPublic,
    privacy_shield: header.privacy_shield ?? null,
    daily_message_limit: bot.customMessageLimit == null || bot.customMessageLimit < 0 ? null : bot.customMessageLimit,
    message_price: ccToPrice(bot.messagePriceCc),
    introduction: bot.introduction || '',
  });
}

/** Variables for EditBotMain_poeBotEdit_Mutation / CreateBotMain_poeBotCreate_Mutation. */
export function mutationVars(py, meta, live) {
  const common = {
    handle: meta.handle,
    displayName: null,
    description: meta.description,
    introduction: meta.introduction || '',
    isPromptPublic: meta.allow_remix,
    profilePictureUrl: live ? (live.profilePicture ?? '') : null,
    prompt: py,
    promptParameters: live ? live.promptParameters ?? null : null,
    apiUrl: null,
    apiKey: '',
    hasMarkdownRendering: live ? live.hasMarkdownRendering : true,
    isPrivateBot: meta.access !== 'everyone',
    temperature: null,
    baseBotId: live ? live.baseBotId : 4209681,
    customMessageLimit: meta.daily_message_limit == null ? (live ? -1 : null) : meta.daily_message_limit,
    messagePriceCc: meta.message_price == null ? (live ? 0 : null) : priceToCc(meta.message_price),
    allowRelatedBotRecommendations: live ? live.allowRelatedBotRecommendations : true,
    botCategory: live ? live.botCategory : 7,
    optimizePromptForPreviews: false,
    canvasContent: null,
  };
  if (live) return { botId: live.botId, baseBot: 'interpreter', ...common };
  return { model: 'interpreter', ...common, inviteCodeReqHash: null, publishSourceChatId: null };
}
