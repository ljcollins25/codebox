# Poe Script Bot Creation/Update via Playwright

## Overview

A Playwright script (`publish-bots.mjs`) connects to a running Chromium instance via CDP and creates or updates Poe **script bots** by driving the poe.com web UI. Bot definitions live in a folder as paired files: a **YAML metadata file** (`.yml`) and a **Python script file** (`.py`) with matching base names.

## Prerequisites

- Chromium running with `--remote-debugging-port=9222`
- User logged into poe.com in that browser
- Node.js with `playwright` and `js-yaml` packages installed

## File Layout

```
bots/
  my-bot.yml          # metadata
  my-bot.py           # script code
  another-bot.yml
  another-bot.py
```

The `.yml` and `.py` files MUST share the same base name (case-sensitive).

## YAML Metadata Schema

```yaml
# Required
name: "My-Bot"               # Bot handle/name (unique on Poe, alphanumeric + hyphens)

# Optional
description: ""               # Up to 4000 chars, shown on bot profile
access: "private"             # "everyone" | "link" | "private" (default: "private")
allow_remix: false            # Allow others to copy/edit (default: false)
daily_message_limit: null     # Number or null; daily limit for non-subscribers (enables paywall)
message_price: null           # Number or null; USD per 1000 messages (0.00–10000.00)
```

### Field Details

| Field | Type | Default | Poe UI Element |
|-------|------|---------|----------------|
| `name` | string | **required** | `input[name="handle"]` — the bot's unique handle |
| `description` | string | `""` | `textarea[name="description"]` — max 4000 chars |
| `access` | enum | `"private"` | `select[name="botIsPublicOption"]` — maps to: `"everyone"` → value `"public"`, `"link"` → value `"unlisted"`, `"private"` → value `"private"` |
| `allow_remix` | bool | `false` | `input[name="promptIsPublic"]` checkbox |
| `daily_message_limit` | int \| null | `null` | `input[name="hasCustomMessageLimit"]` toggle + `input[name="customMessageLimit"]` number field (appears when toggle is on) |
| `message_price` | float \| null | `null` | `input[name="messagePrice"]` — number input (step 0.01, min 0, max 10000). USD per 1000 messages. GraphQL stores as `messagePriceCc` (cents). |

See [metadata-schema.yml](metadata-schema.yml) for the full annotated schema with comments.

### Fields NOT exposed (intentionally omitted)

- **Bot type** — always set to `"script"` via the type dropdown
- **Image** — not supported (would require file upload)
- **Base bot** — auto-set by Poe when type is "script" (Interpreter, botId 4209681)

## Python Script File

The `.py` file contains the full Poe script bot code. This is pasted verbatim into the `textarea[name="prompt"]` field (the "Python code" editor).

Poe script bots use the `fastapi_poe` SDK and the `poe` global. Common patterns:

```python
# poe: name=My-Bot
from fastapi_poe.types import SettingsResponse

poe.update_settings(SettingsResponse(
    introduction_message="Hello!",
))

# Bot logic here...
```

The `# poe: name=...` comment is convention but the actual bot name comes from the YAML `name` field / the handle input.

## How the Playwright Script Works

### Bot Existence Check

1. Navigate to `https://poe.com/edit_bot?bot={name}`
2. If the page loads with title containing "Edit", the bot exists → **update flow**
3. If the page redirects or shows an error → **create flow**

### Create Flow

1. Navigate to `https://poe.com/create_bot`
2. Select bot type: set the type dropdown (`select.dropdown_select__fee24`) to `"script"`
3. Wait for the form to adjust (script bots show the Python code textarea)
4. Fill in the form fields from the YAML metadata
5. Paste the Python code into `textarea[name="prompt"]`
6. Click "Create" / "Publish" button (`button[type="submit"]` with text "Create bot" or similar)
7. Wait for navigation/confirmation

### Update Flow

1. Navigate to `https://poe.com/edit_bot?bot={name}`
2. Clear and fill form fields from the YAML metadata
3. Clear and paste the Python code into `textarea[name="prompt"]`
4. Click "Publish" button
5. Wait for confirmation

### Form Interaction Details

All form elements are standard HTML — no shadow DOM or web components:

- **Type dropdown**: `select` with class `dropdown_select__fee24`, options include "Script bot" (value `"script"`)
- **Handle input**: `input[name="handle"]` (text, class `textInput_input__9YpqY`)
- **Description textarea**: `textarea[name="description"]` (class `textArea_root__HPeK1`)
- **Python code textarea**: `textarea[name="prompt"]` (class includes `BotInfoForm_darkTextArea__k3uMc`)
- **Access dropdown**: `select[name="botIsPublicOption"]` (values: `"public"`, `"unlisted"`, `"private"`)
- **Allow remix checkbox**: `input[name="promptIsPublic"]` (checkbox, class `switch_input__8I5Oq`)
- **Daily message limit toggle**: `input[name="hasCustomMessageLimit"]` (checkbox, same custom switch). When on, reveals a number input for the limit value.
- **Daily message limit value**: `input[name="customMessageLimit"]` (number input, only visible when toggle is on). May need to wait for the input to appear after toggling.
- **Message price**: `input[name="messagePrice"]` (number, `step="0.01"`, `min="0"`, `max="10000"`, `placeholder="0.00"`). Price in USD per thousand messages.
- **Publish button**: `button[type="submit"]` with text "Publish" (class includes `button_primary__Vo3KL`)

### GraphQL Calls (background, triggered by Poe)

When the edit page loads, Poe fires several GraphQL queries to `POST /api/gql_POST`:

- `AnnotateWithIdsProviderQuery` — viewer permissions
- `QuickSwitcherWrapperGateQuery` — UI feature gate
- `editBotBaseBotInfoQuery` — base bot info (baseBotId → numTokensFromPrompt)
- `BotInfoForm_NumTokensFromPromptQuery` — token count for the prompt content
- `BotInfoForm_NumTokensFromIntroductionQuery` — token count for introduction message

These are **read-only queries triggered by the page** — we don't need to call them ourselves. The form submission (Publish) triggers a mutation that we let the browser handle naturally.

### Required Headers for GraphQL (for reference)

```
poe-formkey: <dynamic, from cookies/page>
poe-tchannel: <dynamic>
poe-revision: <git commit hash>
poe-queryname: <query name>
poe-tag-id: <hash>
poegraphql: "1"
content-type: application/json
```

The formkey and tchannel are session-specific — another reason to drive the UI rather than call GraphQL directly.

## Usage

```bash
# Publish all bots in a folder
node publish-bots.mjs ./bots

# Publish a single bot (by specifying a folder with just that pair)
node publish-bots.mjs ./bots/my-bot.yml
```

## Implementation Notes

- **CDP connection**: `chromium.connectOverCDP('http://localhost:9222')` — reuses existing browser session
- **Timeouts**: 20s per operation, 30s for page navigation
- **Process timeout**: The calling shell wraps with a 60s kill timer per bot
- **Selectors**: Use `name` attributes and `type=submit` as stable selectors; avoid class-based selectors where possible since those are generated/minified
- **Textarea clearing**: Use triple-click select-all + type to replace content, or `fill()` which clears first
- **Error detection**: After clicking Publish, watch for error banners or toasts. If the URL changes to the bot's page, it succeeded.
- **Class names**: Poe uses CSS modules with hashed suffixes (e.g., `dropdown_select__fee24`). The base part (`dropdown_select`) is stable; the hash may change across deployments. Use `[class*="dropdown_select"]` or `name` attributes when possible.
- **Checkbox controls**: The "Allow remix" checkbox (`input[name="promptIsPublic"]`) uses a custom styled switch (`switch_input__8I5Oq`). Playwright's `setChecked()` hangs because the actual `<input>` is hidden. Instead, find the parent `<label>` and `.click()` it to toggle, or use `page.evaluate` to call `.click()` on the input directly.
- **Update flow stays on page**: When updating an existing bot, clicking Publish does NOT redirect — the page stays on `edit_bot`. This is normal; the form submits via GraphQL mutation in the background.
