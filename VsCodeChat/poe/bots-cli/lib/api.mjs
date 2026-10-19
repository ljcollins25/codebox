// The "personal API": list / get / create / update, all through the page's own GraphQL.
import { metaFromLive, mutationVars } from './bots.mjs';

export async function listBots(conn, userHandle) {
  const handle = userHandle || (await conn.viewer());
  if (!handle) throw new Error('could not detect your poe handle; pass --user <handle>');
  const first = await conn.gql('HandleProfilePageQuery', { handle });
  const uid = first.user.id;
  let conn_ = first.user.createdBotsConnection;
  const bots = [];
  const take = (c) => c.edges.forEach((e) => bots.push(e.node));
  take(conn_);
  while (conn_.pageInfo.hasNextPage) {
    const d = await conn.gql('CreatedBotsListPanelPaginationQuery', { count: 10, cursor: conn_.pageInfo.endCursor, id: uid });
    conn_ = d.node.createdBotsConnection;
    take(conn_);
  }
  return bots.map((b) => ({ handle: b.handle, botId: b.botId, description: b.description, tags: b.translatedBotTags, deleted: b.deletionState !== 'not_deleted' }))
    .filter((b) => !b.deleted);
}

export async function getBot(conn, handle) {
  const d = await conn.gql('editBotIndexPageQuery', { botName: handle });
  if (!d.bot) return null;
  return d.bot;
}

export function liveToFiles(bot) {
  return { py: bot.promptPlaintext || '', meta: metaFromLive(bot) };
}

async function applyAccessLink(conn, botId, wantLink, isLink) {
  if (wantLink && !isLink) await conn.gql('BotProfileSectionEnableAccessLinkMutation', { botId });
  if (!wantLink && isLink) await conn.gql('BotProfileSectionDisableAccessLinkMutation', { botId });
}

export async function pushBot(conn, py, meta, live) {
  const vars = mutationVars(py, meta, live);
  const name = live ? 'EditBotMain_poeBotEdit_Mutation' : 'CreateBotMain_poeBotCreate_Mutation';
  const d = await conn.gql(name, vars);
  const r = live ? d.poeBotEdit : d.poeBotCreate;
  if (r.status !== 'success') throw new Error(name + ' failed: ' + r.status + ' ' + (r.statusMessage || ''));
  const botId = live ? live.botId : r.bot.id && (await getBot(conn, meta.handle)).botId;
  const isLink = live ? !!live.inviteLink : false;
  if (meta.access === 'link' || isLink) await applyAccessLink(conn, botId, meta.access === 'link', isLink);
  return { status: r.statusMessage, botId };
}
