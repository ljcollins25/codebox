# Copilot Proxy Spec

## References

- [Poe Server Bots Quick Start](https://creator.poe.com/docs/server-bots/quick-start)
- [Poe Server Bots Functional Guides](https://creator.poe.com/docs/server-bots/server-bots-functional-guides)
- [Poe fastapi_poe Python Reference](https://creator.poe.com/docs/server-bots/fastapi_poe-python-reference)
- [Poe Parameter Controls](https://creator.poe.com/docs/server-bots/parameter-controls)
- [Poe Function Calling](https://creator.poe.com/docs/server-bots/function-calling)
- [Poe Examples](https://creator.poe.com/docs/server-bots/examples)
- [GitHub Device Flow](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow)

See also: [copilot-proxy-details.md](copilot-proxy-details.md) for implementation details.

---

## Summary
A single Cloudflare Worker + static site deployment that:

- Serves a static login UI at `/` to help users obtain a GitHub OAuth token via device flow.
- Exposes an OpenAI-compatible API surface at `/copilot/v1/*` that **assumes the caller provides a GitHub OAuth token** in the `Authorization` header and internally resolves it to a short-lived Copilot token.
- Exposes a generic Poe Server Bot translation endpoint at `/poe/server` that converts Poe Server Bot requests into OpenAI-compatible requests and forwards them to a configurable target URL; by default it forwards to this same deployment’s `/copilot/v1/*` endpoints.

Design goals:

- Keep the Worker stateless where possible.
- Treat GitHub tokens as opaque bearer credentials supplied by the user.
- Keep Copilot-specific logic confined to the `/copilot/v1/*` layer.
- Keep Poe server translation generic and backend-agnostic.

---

## Resulting Layer Stack

```
/            (static site)
  - serves HTML login UI
  - UI calls /login
  - stores device-flow metadata in localStorage
  - polls GitHub token endpoint directly
  - displays GitHub OAuth token on success
  - provides reset / new-token affordance

/login
  - initiates GitHub OAuth device flow
  - returns verification_uri_complete + device_code + interval (+ expires_at)
  - stateless
  - no polling
  - no token storage
  - no Copilot logic

/copilot/v1/*
  - Authorization: <GitHub OAuth token>
    or Authorization: Bearer <GitHub OAuth token>
  - normalizes header (Bearer optional)
  - treats GitHub token as access key
  - resolves GitHub token → Copilot token (cached)
  - injects headers to emulate VS Code
  - OpenAI-compatible request/response surface
  - forwards to Copilot backend

/poe/server
  - Poe Server Bot ⇄ OpenAI translation
  - target routing via query param
  - if target missing → default relative path (/copilot/v1/...)
  - relative default resolved against request.url
  - absolute targets allowed (validated)
  - always forwards via fetch()
```

---

## Static Site at `/`

### Purpose
Provide a simple UI that:

- Starts GitHub device flow by calling `/login`.
- Shows a verification link that opens GitHub in a new tab by default.
- Polls GitHub’s token endpoint until authorization completes.
- Displays the resulting GitHub OAuth token to the user (copy/paste).
- Offers a **Reset / Generate New Token** action that clears in-progress state and starts over.

### Storage
Use `localStorage` to persist an in-progress device flow across navigation/reload.

Stored fields (example):

- `verification_uri_complete`
- `device_code`
- `interval`
- `expires_at` (client-computed from `expires_in` or returned explicitly)

Rules:

- On page load: resume if state exists and not expired.
- On reset: clear state and start a new device flow.
- On success: stop polling, display token, clear in-progress device-flow state.

### GitHub verification link behavior
Render link using:

- `target="_blank"`
- `rel="noopener noreferrer"`

---

## `/login` (Device Flow Initiation)

### Purpose
Start the GitHub OAuth Device Authorization Grant flow and return the initiation payload to the browser.

### Behavior
- Accepts `POST /login` (or `GET /login` if preferred; keep it simple).
- Calls GitHub device code endpoint.
- Returns JSON containing:
  - `verification_uri_complete`
  - `device_code`
  - `interval`
  - `expires_in` and/or `expires_at`
  - (optional) `user_code` and `verification_uri` for fallback display

### Non-goals
- Does not poll.
- Does not store tokens.
- Does not establish a server-side session.

---

## `/copilot/v1/*` (OpenAI-Compatible Copilot Proxy)

### Purpose
Expose an OpenAI-compatible surface area for clients while using GitHub Copilot as the backend.

### Auth
Accept `Authorization` header in either form:

- `Authorization: Bearer <GitHub OAuth token>`
- `Authorization: <GitHub OAuth token>`

Normalization:

- If starts with `Bearer ` → strip it.
- Otherwise treat entire header value as the token.
- Missing/empty → reject (401).

The GitHub OAuth token is the user’s access key.

### Copilot token resolution
Input: GitHub OAuth token.

Output: Copilot token + expiry.

Cache key:

- `key = "v1:" + HMAC_SHA256(server_secret, github_token)`

Stored value:

- `copilot_token`
- `expires_at`
- optional metadata (scope, token_type, etc.)

Rules:

- If cached token exists and not expired → reuse.
- Else mint a new Copilot token using GitHub token and store.

### VS Code emulation
When forwarding to Copilot backend, inject the headers/identifiers required for Copilot to treat the caller like VS Code.

All Copilot/VS Code-specific behavior is confined to this layer.

### API surface
- OpenAI-compatible paths under `/copilot/v1/*`.
- Requests are forwarded (pass-through) except for:
  - Authorization translation GitHub→Copilot
  - VS Code emulation headers

Streaming:

- If the incoming OpenAI-compatible request is streaming, relay stream as-is.

---

## `/poe/server` (Generic Poe Server Bot Translation)

### Purpose
Translate Poe Server Bot API requests/responses to/from OpenAI-compatible requests/responses.

### Target routing
- Query parameter: `target`

Rules:

- If `target` **missing**:
  - Use a **relative** default target path (e.g. `/copilot/v1/chat/completions` or appropriate OpenAI-compatible endpoint).
  - Resolve relative against `request.url` base.
- If `target` **present**:
  - Must be an **absolute** `https://` URL.
  - Validate scheme/host per SSRF guardrails.

### Forwarding
- Always forward via `fetch()`.
- Never call internal handlers directly.

### Copilot awareness
- None. This layer treats the backend as a generic OpenAI-compatible API.

### Auth passthrough
- Forward the `Authorization` header through to the target.
- Do not interpret GitHub vs other tokens.

### Request Translation (Poe → OpenAI)

| Poe Field | OpenAI Field | Transformation |
|-----------|--------------|----------------|
| `query` | `messages` | Array mapping |
| `query[].role = "bot"` | `messages[].role = "assistant"` | Role rename |
| `query[].role = "user/system/tool"` | Same | Direct mapping |
| `temperature` | `temperature` | Direct |
| `stop_sequences` | `stop` | Direct |
| `tools` | `tools` | Compatible format (see below) |
| `tool_results` | Appended to `messages` as `role: "tool"` | |

### Response Translation (OpenAI SSE → Poe SSE)

| OpenAI Event | Poe Event |
|--------------|-----------|
| `{"choices":[{"delta":{"content":"..."}}]}` | `event: text` / `{"text": "..."}` |
| `{"choices":[{"delta":{"tool_calls":[...]}}]}` | Accumulate, then `event: tool_call` on finish |
| `[DONE]` | `event: done` / `{}` |
| Error | `event: error` / `{"text": "...", "allow_retry": true}` |

### Tool/Function Calling

Poe and OpenAI tool formats are nearly identical. The translation:

1. **Request**: Pass `tools` array through with minimal transformation
2. **Response**: Accumulate streamed tool call chunks by index, emit complete `tool_call` events when `finish_reason === "tool_calls"`

---

## `/poe/settings` (Parameter Controls)

### Purpose
Return bot configuration settings per Poe's parameter controls specification.

### Endpoint
`POST /poe/settings`

### Response
```json
{
  "server_bot_dependencies": {},
  "allow_attachments": true,
  "expand_text_attachments": true,
  "enable_image_comprehension": false,
  "introduction_message": "Hello! I'm a GitHub Copilot proxy bot.",
  "enforce_author_role_alternation": false,
  "enable_multi_bot_chat_prompting": false
}
```

See [Poe Parameter Controls](https://creator.poe.com/docs/server-bots/parameter-controls) for field definitions.

---

## SSRF / Target Validation (for `/poe/server`)

Minimum guardrails for absolute targets:

- Allow only `https:` scheme.
- Optional allowlist of hostnames.
- Deny private IP ranges / link-local / localhost.

---

## Error Handling

- `/login` should surface GitHub device-flow errors clearly to the UI.
- `/copilot/v1/*` should map backend failures into OpenAI-compatible error shapes when feasible.
- `/poe/server` should map backend failures into Poe Server Bot error shapes.

---

## Non-Goals / Explicit Exclusions

- No server-side storage of GitHub OAuth tokens from the login flow.
- No callback/webhook completion for device flow.
- No cross-tab coordination beyond localStorage resume.
- No attempt to make `/copilot/v1` a generic OpenAI proxy; it is Copilot-specific by design.

