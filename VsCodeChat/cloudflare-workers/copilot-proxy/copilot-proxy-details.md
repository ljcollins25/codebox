# Copilot Proxy - Implementation Details

This document provides implementation details for the Copilot Proxy worker.
For the authoritative specification, see [copilot-proxy-spec.md](copilot-proxy-spec.md).

## References

- [Poe Server Bots Quick Start](https://creator.poe.com/docs/server-bots/quick-start)
- [Poe Server Bots Functional Guides](https://creator.poe.com/docs/server-bots/server-bots-functional-guides)
- [Poe fastapi_poe Python Reference](https://creator.poe.com/docs/server-bots/fastapi_poe-python-reference)
- [Poe Parameter Controls](https://creator.poe.com/docs/server-bots/parameter-controls)
- [Poe Function Calling](https://creator.poe.com/docs/server-bots/function-calling)
- [Poe Examples](https://creator.poe.com/docs/server-bots/examples)

---

## Architecture Overview

```
Static UI (/)                Worker Endpoints
     |                              |
     | POST /login                  |
     +----------------------------->| Device flow initiation
     |                              |
     | (polls GitHub directly)      |
     |                              |
     | Authorization: <token>       |
     +----------------------------->| /copilot/v1/*
     |                              | GitHub token -> Copilot token
     |                              | Forward to Copilot API
     |                              |
     | Poe Server Bot request       |
     +----------------------------->| /poe/server
     |                              | Translate Poe -> OpenAI
     |                              | Forward to target
     |                              | Translate OpenAI -> Poe SSE
```

---

## GitHub Device Flow

### Constants

```typescript
const GITHUB_CLIENT_ID = "01ab8ac9400c4e429b23";  // VS Code client ID
const GITHUB_SCOPES = "read:user";
```

### /login Response

```json
{
  "device_code": "...",
  "user_code": "ABCD-1234",
  "verification_uri": "https://github.com/login/device",
  "verification_uri_complete": "https://github.com/login/device?user_code=ABCD-1234",
  "expires_in": 900,
  "expires_at": "2024-01-01T12:15:00.000Z",
  "interval": 5
}
```

### Client-Side Polling

The static UI polls GitHub directly (not our server):

```javascript
const res = await fetch("https://github.com/login/oauth/access_token", {
  method: "POST",
  headers: {
    "Accept": "application/json",
    "Content-Type": "application/x-www-form-urlencoded"
  },
  body: `client_id=${CLIENT_ID}&device_code=${device_code}&grant_type=urn:ietf:params:oauth:grant-type:device_code`
});
```

---

## Copilot Token Resolution

### Cache Key Generation

```typescript
// With SERVER_SECRET (recommended):
key = "copilot_v1:" + HMAC_SHA256(server_secret, github_token).slice(0, 32)

// Without SERVER_SECRET:
key = "copilot_v1:" + SHA256(github_token).slice(0, 32)
```

### Cache Value

```typescript
interface CopilotTokenCache {
  copilot_token: string;
  expires_at: string;  // ISO timestamp
}
```

### Token Lifetime

- Copilot tokens expire in ~30 minutes
- We cache for 23 minutes (COPILOT_TOKEN_LIFETIME_MINUTES - 2)

### VS Code Emulation Headers

```typescript
const VSCODE_HEADERS = {
  "User-Agent": "GitHubCopilotChat/1.0.0",
  "Editor-Version": "vscode/1.96.0",
  "Editor-Plugin-Version": "copilot-chat/0.26.0",
  "Openai-Organization": "github-copilot",
  "Copilot-Integration-Id": "vscode-chat",
  "Content-Type": "application/json",
};
```

---

## Poe Server Bot Translation

### Request Translation (Poe -> OpenAI)

| Poe Field | OpenAI Field | Transformation |
|-----------|--------------|----------------|
| `query` | `messages` | Array mapping |
| `query[].role = "bot"` | `messages[].role = "assistant"` | Role rename |
| `query[].role = "user"` | `messages[].role = "user"` | Direct |
| `query[].role = "system"` | `messages[].role = "system"` | Direct |
| `query[].role = "tool"` | `messages[].role = "tool"` | Direct |
| `temperature` | `temperature` | Direct |
| `stop_sequences` | `stop` | Direct |
| `tools` | `tools` | Compatible format |
| `tool_results` | `messages` (tool role) | Append as messages |

### Response Translation (OpenAI SSE -> Poe SSE)

| OpenAI SSE | Poe SSE |
|------------|---------|
| `data: {"choices":[{"delta":{"content":"..."}}]}` | `event: text\ndata: {"text": "..."}` |
| `data: {"choices":[{"delta":{"tool_calls":[...]}}]}` | `event: tool_call\ndata: {...}` (accumulated) |
| `data: [DONE]` | `event: done\ndata: {}` |
| (error) | `event: error\ndata: {"text": "...", "allow_retry": true}` |

### Poe Event Types

| Event | Data | Description |
|-------|------|-------------|
| `text` | `{"text": "..."}` | Regular text content |
| `replace_response` | `{"text": "..."}` | Replace all previous text |
| `done` | `{}` | Stream complete |
| `error` | `{"text": "...", "allow_retry": bool}` | Error occurred |
| `suggested_reply` | `{"text": "..."}` | Add suggested reply button |
| `meta` | `{"suggested_replies": [...]}` | Multiple suggested replies |
| `tool_call` | `{"id": "...", "function": {...}}` | Request tool execution |

### Tool Call Accumulation

Tool calls in OpenAI streaming come in chunks:

```javascript
// Chunk 1: id and name
{"tool_calls": [{"index": 0, "id": "call_abc", "function": {"name": "get_weather"}}]}

// Chunk 2+: arguments (streamed)
{"tool_calls": [{"index": 0, "function": {"arguments": "{\"loc"}}]}
{"tool_calls": [{"index": 0, "function": {"arguments": "ation\":"}}]}
{"tool_calls": [{"index": 0, "function": {"arguments": "\"Paris\"}"}}]}

// finish_reason: "tool_calls"
```

We accumulate by index and emit complete tool_call events on finish.

---

## Poe Parameter Controls (/poe/settings)

Response format:

```typescript
interface PoeSettingsResponse {
  server_bot_dependencies?: Record<string, number>;  // Bot dependencies
  allow_attachments?: boolean;                       // Allow file attachments
  expand_text_attachments?: boolean;                 // Include parsed text content
  enable_image_comprehension?: boolean;              // Process images
  introduction_message?: string;                     // Initial bot message
  enforce_author_role_alternation?: boolean;         // Strict user/bot alternation
  enable_multi_bot_chat_prompting?: boolean;         // Multi-bot support
}
```

---

## SSRF Protection

For `/poe/server?target=...`:

```typescript
function validateTargetUrl(target: string): boolean {
  const url = new URL(target);
  
  // Only HTTPS
  if (url.protocol !== "https:") return false;
  
  // No localhost/private IPs
  const hostname = url.hostname.toLowerCase();
  if (hostname === "localhost") return false;
  if (hostname === "127.0.0.1") return false;
  if (hostname.startsWith("192.168.")) return false;
  if (hostname.startsWith("10.")) return false;
  if (hostname.startsWith("172.")) return false;
  if (hostname.endsWith(".local")) return false;
  
  return true;
}
```

---

## Error Handling

### OpenAI-Compatible Errors (/copilot/v1/*)

```json
{
  "error": {
    "message": "Invalid GitHub token",
    "type": "invalid_token"
  }
}
```

### Poe SSE Errors (/poe/server)

```
event: error
data: {"text": "Backend error: ...", "allow_retry": true}

event: done
data: {}
```

---

## KV Storage Keys

| Pattern | Purpose | TTL |
|---------|---------|-----|
| `copilot_v1:{hash}` | Copilot token cache | 23 minutes |

Note: The static UI handles device flow state in localStorage, not server-side KV.

---

## Deployment

### wrangler.toml

```toml
name = "copilot-proxy"
main = "src/index.ts"
compatibility_date = "2024-01-29"

[assets]
directory = "./public"

[[kv_namespaces]]
binding = "TOKEN_CACHE"
id = "your-kv-namespace-id"

[vars]
# Optional: Set SERVER_SECRET for HMAC-based cache keys
# SERVER_SECRET = "your-secret"
```

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `TOKEN_CACHE` | Yes | KV namespace binding |
| `SERVER_SECRET` | No | HMAC secret for cache key generation |
