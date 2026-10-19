# poe-bots: local dev workflow for Poe script bots

Poe has no public API for script bots, so this tool uses the web app's own GraphQL (see [graphql-spec.md](graphql-spec.md)),
run with `fetch()` **inside a poe.com page** of a browser profile the tool owns. Cookies, formkey and tchannel stay in the
browser and are never read, printed or stored by the CLI.

Bots live in git: `bots/<handle>/bot.py` + `bot.yml` (handle, description, access = everyone|link|private, allow_remix,
privacy_shield, daily_message_limit, message_price (USD), introduction). The `# poe: privacy_shield=` header in the script is
checked against the yml (`poe-bots check`; `push` refuses on a mismatch). `# poe: name=` is a display name and is not compared.

Requirements: Node 18+ (Node 20+ only for `login --from-main`), Python 3, system Chrome or Edge, `npm install` in this folder.

## Commands
| command | what |
|---|---|
| `poe-bots login` | opens the tool's own profile visibly on poe.com/login; sign in once, it waits until signed in |
| `poe-bots login --from-main [--via mcp\|cdp]` | copy poe.com cookies (HttpOnly included) + localStorage from your main Chrome instead (below) |
| `poe-bots list [--json]` | your bots |
| `poe-bots pull <handle>\|--all` | script + metadata into `bots/` (script bots only) |
| `poe-bots diff <handle>` | local vs live (exit 2 if different) |
| `poe-bots push <handle> [--yes]` | create or update; prints the diff, applies only with `--yes`. Only `ref12-cli-test` is pushable without `--allow-real` |
| `poe-bots run <handle> --text "..." [--attach f] [--param k=v] [--out out]` | run locally, no browser or login needed |
| `poe-bots check [handle]` | header vs yml |

Run as `node poe-bots.mjs …` (or `poe-bots.cmd` on Windows). Global flags: `--headless`, `--profile <dir>`, `--user <handle>`.

## 1. Primary: the tool's own browser profile
The CLI launches system Chrome (or Edge; `POE_BROWSER` overrides) with its own user-data-dir
(`%LOCALAPPDATA%\poe-bots\chrome-profile`, `~/.local/share/poe-bots/chrome-profile`, or `POE_BOTS_PROFILE`), attached by Playwright
over `--remote-debugging-pipe`. By default the window is headed but minimized and off-screen. `--headless` also works: poe.com accepted it in
our test (list and pull). Poe's session cookie lasts about a year. If signed out, commands stop with "run poe-bots login".
Only one command can use the profile at a time. No hexad, no extension.

## 2. Optional: `login --from-main`
Copies poe.com's session from your signed-in main Chrome into the tool's profile, so you don't sign in again. Counts are printed, never values,
and it prints which route was used. Routes, tried in this order (`--via mcp` or `--via cdp` forces one and disables the fallback):
1. **mcp: Playwright Extension** (Chrome Web Store name "Playwright Extension", id `mmlmfjhmonkocbjadbfplnigmagldckm`). Install it in your main Chrome,
   copy the token from its page, and set `PLAYWRIGHT_MCP_EXTENSION_TOKEN=<token>`. Needs Node 20+ for `npx @playwright/mcp@latest --extension`
   (`POE_MCP_NODE_DIR` points at another Node). It reads `page.context().cookies()` for https://poe.com/ plus localStorage. Skipped when no token is set;
   if it does not connect within about 8 s the command falls back.
2. **cdp: the main Chrome's remote debugging.** Turn it on at `chrome://inspect/#remote-debugging`; the tool finds Chrome's (or Edge's)
   `DevToolsActivePort` and Chrome shows its own consent prompt, which you allow. `--cdp <ws/http url>` targets any CDP endpoint instead.
Status: the copy into the profile was verified end to end over CDP (10 cookies, 3 HttpOnly, 15 localStorage keys, then signed in). Reading HttpOnly cookies
through Playwright Extension was verified in a throwaway profile; the real main-Chrome mcp route has not been run end to end yet. Route selection and the fallback are unit tested.

## 3. Fallback: custom extension + native host
If Playwright MCP can't be used: `poe-bots ext-install`, then in chrome://extensions turn on Developer mode, Load unpacked, choose `extension/`;
`poe-bots ext-status`; run other commands with `POE_BRIDGE=extension`. The extension talks only to the native host (no external messaging). Calls run in
a background poe.com tab in your own Chrome profile.

## Local runner
`runner/poe_stub.py` stands in for the `poe` global per the [Python reference](https://creator.poe.com/docs/script-bots/poe-python-reference):
query (text, attachments, parameters with control defaults), start_message (write, overwrite, attach_file, add_attachment, add_inline_ref), Attachment,
BotError, update_settings, Chat, call/stream (stub; real call via the OpenAI-compatible API when `POE_API_KEY` is set), parallel, repeat. `print()` posts a message.
Output attachments are saved to `out/`; input attachments are served from a loopback URL.
**Gaps:** `call_tools`/`update_tool_calls` unimplemented; `poe.call` real mode is text-only (no bot parameters or attachments) and untested; no `fastapi_poe` validation
(a permissive shim is used if the package is missing); no real hosting, time limits, rate limits or points.

## Tests
`python -m unittest discover -s tests` and `node --test tests` (or `npm test`), Windows-safe.

## Not covered
Profile picture upload, deleting bots, bot secrets. Persisted-query hashes in `lib/ops.mjs` may change when Poe deploys; recapture as described in the spec.
