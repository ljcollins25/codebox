# Poe web GraphQL, as captured 2026-10-19

Captured from poe.com in a logged-in browser by hooking `window.fetch` and (a) opening the Create > Script bot form, filling it for
`ref12-cli-test` and pressing Publish, then (b) editing and republishing that same bot. Only the test bot was written.

## Transport
`POST /api/gql_POST`, JSON body `{ "queryName", "variables", "extensions": { "hash": "<sha256 persisted query id>" } }`.
Headers the web app sends (all added by the app, never read by us): `poe-formkey`, `poe-queryname` (= queryName), `poegraphql: 1`,
`poe-tchannel`, `poe-revision`, `poe-tag-id`, `content-type: application/json`. Cookies (p-b etc.) via same-origin credentials.
The bridge (extension/bridge.js) wraps `fetch`, keeps the app's own header set in a closure, and re-issues calls with only
`poe-queryname` changed, so a call "looks like" the app's. A wrong hash gives HTTP 400 with an empty body. Hashes are in lib/ops.mjs and
change when Poe deploys: re-capture by repeating the form steps with a fetch hook.

## Operations
| queryName | purpose | variables |
|---|---|---|
| HandleProfilePageQuery | first page (10) of a user's bots; `data.user.id`, `createdBotsConnection`, `createdBotCount` | `{handle}` |
| CreatedBotsListPanelPaginationQuery | later pages | `{count:10, cursor:<endCursor>, id:<user.id>}` -> `data.node.createdBotsConnection` |
| editBotIndexPageQuery | one bot's full editable record (`data.bot`) | `{botName: handle}` |
| CreateBotMain_poeBotCreate_Mutation | create | see below, plus `model:"interpreter"` |
| EditBotMain_poeBotEdit_Mutation | update | see below, plus `botId`, `baseBot:"interpreter"` |
| BotProfileSectionEnableAccessLinkMutation / ...DisableAccessLinkMutation | "link" access | `{botId}` |
| botsIndexPageQuery | (seen) the app's own bot list, not used |  |

### Edit/Create variables
handle, displayName(null), description, introduction, isPromptPublic (= allow_remix), profilePictureUrl, prompt (the script),
promptParameters, apiUrl(null), apiKey(""), hasMarkdownRendering, isPrivateBot, temperature(null), baseBotId (4209681 = script bot),
customMessageLimit (-1 = none), messagePriceCc (USD per message x 10000... see below), allowRelatedBotRecommendations, botCategory (7),
optimizePromptForPreviews(false), canvasContent(null). Create also sends inviteCodeReqHash(null), publishSourceChatId(null).
Edit response: `data.poeBotEdit { status, statusMessage }` ("Changes published.").

### Field mapping (bot.yml <-> live `bot`)
- access: everyone = `!isPrivateBot`; private = `isPrivateBot && !inviteLink`; link = `isPrivateBot && inviteLink` (the UI's 3-way select: public/accessLink/private;
  the link itself is toggled by the Enable/Disable AccessLink mutations).
- allow_remix = `isPromptPublic` (checkbox "promptIsPublic").
- daily_message_limit = `customMessageLimit` (-1 none). message_price: setting 1.5 in the form sent `messagePriceCc: 15000`
  (so cc = price x 10000); VideoFFMpeg's live 50 is read back with the same factor.
- privacy_shield has no GraphQL field: it lives only in the script's `# poe: privacy_shield=` header, which is the source of truth.

## UI fallback
None needed: every operation above is reproduced with fetch(). The old UI driver (../bot-creation/publish-bots.mjs) still works and was left as is.
Not captured: profile picture upload, deleting a bot, bot secrets/settings pages.
