# Copilot Proxy Cloudflare Worker Specification

## Overview

A Cloudflare Worker that:
1. Implements the **Poe Server Bot API** (receives requests from Poe)
2. Translates to **OpenAI-compatible API** format (sends to Copilot/OpenAI)
3. Manages authentication tokens with caching
4. Transforms streaming responses back to Poe format

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                       Cloudflare Worker                              │
│                                                                      │
│  ┌────────────────┐                        ┌────────────────────┐   │
│  │  Poe Server    │                        │  Token Manager     │   │
│  │  Bot Endpoint  │                        │  (KV Storage)      │   │
│  │  POST /        │                        │                    │   │
│  └───────┬────────┘                        └─────────┬──────────┘   │
│          │                                           │              │
│          ▼                                           │              │
│  ┌────────────────────────────────────────┐         │              │
│  │         REQUEST TRANSLATOR             │         │              │
│  │  Poe Format → OpenAI Format            │◄────────┘              │
│  └───────┬────────────────────────────────┘                        │
│          │                                                          │
│          ▼                                                          │
│  ┌────────────────────────────────────────┐                        │
│  │         Copilot/OpenAI API             │                        │
│  │  POST /chat/completions                │                        │
│  └───────┬────────────────────────────────┘                        │
│          │                                                          │
│          ▼                                                          │
│  ┌────────────────────────────────────────┐                        │
│  │         RESPONSE TRANSLATOR            │                        │
│  │  OpenAI SSE → Poe SSE                  │                        │
│  └───────┬────────────────────────────────┘                        │
│          │                                                          │
│          ▼                                                          │
│     Response to Poe                                                 │
└─────────────────────────────────────────────────────────────────────┘
```

---

## API Endpoints

### Web UI and Device Flow

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | Web UI for testing device flow and chat |
| POST | `/start-auth` | Start GitHub device flow authentication |
| POST | `/poll-auth` | Poll device flow status |
| POST | `/chat` | Web UI chat endpoint (uses session) |

### Direct Proxy (requires Copilot token)

| Method | Path | Description |
|--------|------|-------------|
| POST | `/chat/completions` | OpenAI-compatible chat completions |
| POST | `/completions` | Code completions |
| POST | `/v1/messages` | Anthropic-style messages endpoint |
| GET | `/models` | List available models |
| POST | `/token` | Exchange GitHub token for Copilot token |
| GET | `/health` | Health check |

---

## GitHub Device Flow Authentication

The worker implements GitHub's Device Flow (OAuth 2.0 Device Authorization Grant) to authenticate users without requiring them to share credentials. This is the same flow used by VS Code Copilot.

### Device Flow Overview

```
┌─────────┐                              ┌──────────┐                    ┌────────┐
│  User   │                              │  Worker  │                    │ GitHub │
└────┬────┘                              └────┬─────┘                    └───┬────┘
     │                                        │                              │
     │ 1. Request auth (POST /start-auth)     │                              │
     │───────────────────────────────────────>│                              │
     │                                        │                              │
     │                                        │ 2. POST /login/device/code   │
     │                                        │─────────────────────────────>│
     │                                        │                              │
     │                                        │ 3. device_code, user_code    │
     │                                        │<─────────────────────────────│
     │                                        │                              │
     │ 4. Return user_code + verification URL │                              │
     │<───────────────────────────────────────│                              │
     │                                        │                              │
     │ 5. User visits github.com/login/device │                              │
     │        and enters user_code            │                              │
     │───────────────────────────────────────────────────────────────────────>│
     │                                        │                              │
     │ 6. Poll auth (POST /poll-auth)         │                              │
     │───────────────────────────────────────>│                              │
     │                                        │                              │
     │                                        │ 7. POST /login/oauth/access_token
     │                                        │─────────────────────────────>│
     │                                        │                              │
     │                                        │ 8. access_token              │
     │                                        │<─────────────────────────────│
     │                                        │                              │
     │                                        │ 9. GET /copilot_internal/v2/token
     │                                        │─────────────────────────────>│
     │                                        │                              │
     │                                        │ 10. copilot_token            │
     │                                        │<─────────────────────────────│
     │                                        │                              │
     │ 11. Auth complete + session_id         │                              │
     │<───────────────────────────────────────│                              │
     │                                        │                              │
```

### Device Flow Constants

```javascript
const GITHUB_CLIENT_ID = "01ab8ac9400c4e429b23";  // VS Code's official client ID
const GITHUB_SCOPES = "read:user";
const GITHUB_TOKEN_LIFETIME_DAYS = 30;
const COPILOT_TOKEN_LIFETIME_MINUTES = 25;
```

### Start Auth Endpoint

**Request:** `POST /start-auth`

```json
{}
```

**Response:**

```json
{
  "session_id": "550e8400-e29b-41d4-a716-446655440000",
  "user_code": "ABCD-1234",
  "verification_uri": "https://github.com/login/device",
  "expires_in": 900,
  "interval": 5
}
```

### Poll Auth Endpoint

**Request:** `POST /poll-auth`

```json
{
  "session_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

**Response (pending):**

```json
{
  "status": "pending"
}
```

**Response (complete):**

```json
{
  "status": "complete",
  "session_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

**Response (expired/error):**

```json
{
  "status": "expired"
}
// or
{
  "status": "error",
  "error": "Your GitHub account does not have Copilot access."
}
```

### Web UI Chat Endpoint

**Request:** `POST /chat`

```json
{
  "session_id": "550e8400-e29b-41d4-a716-446655440000",
  "model": "gpt-4o",
  "messages": [
    { "role": "user", "content": "Hello!" }
  ]
}
```

**Response:** SSE stream (OpenAI format)

---

## CORS Configuration

All endpoints return CORS headers to allow any origin:

```javascript
{
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Request-Id',
  'Access-Control-Max-Age': '86400'
}
```

---

## Token Caching Strategy

### KV Storage Keys

```
webui_pending_v1_{session_id}  → Device flow state (expires with flow)
webui_session_v1_{session_id}  → GitHub + Copilot tokens (30 day TTL)
```

### Session Data Structure

```typescript
interface Session {
  github_token: string;        // GitHub OAuth token
  github_expires_at: string;   // 30 days from auth
  copilot_token?: string;      // Copilot API token
  copilot_expires_at?: string; // ~25 minutes from refresh
}
```

### Token Refresh Logic

1. Check if `copilot_token` exists and is not expired
2. If expired but `github_token` is valid, refresh Copilot token
3. If `github_token` is expired, require re-authentication

---

## Authentication Flow (Legacy)

### Token Caching Strategy

```
Access Key Format: <base_key>:<session_token>

base_key       = Static secret for accessing the worker
session_token  = Generated after GitHub auth, maps to cached Copilot token
```

### Flow

1. **First-time user**: Sends `AUTH AccessKey` message
2. **Worker**: Exchanges GitHub token for Copilot token
3. **Worker**: Caches Copilot token in KV, keyed by Poe `user_id`
4. **Subsequent requests**: Worker retrieves cached token using `user_id`

---

## Request Translation: Poe → OpenAI

### Poe Server Bot Request (Incoming)

```json
{
  "version": "1.0",
  "type": "query",
  "query": [
    {
      "role": "system",
      "content": "You are a helpful assistant.",
      "content_type": "text/markdown",
      "attachments": []
    },
    {
      "role": "user",
      "content": "Hello, how are you?",
      "content_type": "text/markdown",
      "attachments": []
    },
    {
      "role": "bot",
      "content": "I'm doing well, thank you!",
      "content_type": "text/markdown",
      "attachments": []
    },
    {
      "role": "user",
      "content": "Can you help me with code?",
      "content_type": "text/markdown",
      "attachments": []
    }
  ],
  "user_id": "u-abc123",
  "conversation_id": "c-xyz789",
  "message_id": "m-456",
  "metadata": "",
  "api_key": "<bot_api_key>",
  "access_key": "<bot_access_key>",
  "temperature": 0.7,
  "skip_system_prompt": false,
  "logit_bias": {},
  "stop_sequences": [],
  "language_code": "en",
  "tools": [...],           // Optional: Tool definitions
  "tool_calls": [...],      // Optional: Pending tool calls
  "tool_results": [...]     // Optional: Results from tool execution
}
```

### OpenAI-Compatible Request (Outgoing)

```json
{
  "model": "gpt-4",
  "messages": [
    {
      "role": "system",
      "content": "You are a helpful assistant."
    },
    {
      "role": "user",
      "content": "Hello, how are you?"
    },
    {
      "role": "assistant",
      "content": "I'm doing well, thank you!"
    },
    {
      "role": "user",
      "content": "Can you help me with code?"
    }
  ],
  "temperature": 0.7,
  "stream": true,
  "tools": [...],           // Translated tool definitions
  "tool_choice": "auto"     // If tools are provided
}
```

### Message Translation Rules

| Poe `role` | OpenAI `role` | Notes |
|------------|---------------|-------|
| `system` | `system` | Direct mapping |
| `user` | `user` | Direct mapping |
| `bot` | `assistant` | **Role name change** |
| `tool` | `tool` | Tool result messages |

### JavaScript Translation Function

```javascript
function translatePoeToOpenAI(poeRequest) {
  const messages = poeRequest.query.map(msg => {
    const translated = {
      role: msg.role === "bot" ? "assistant" : msg.role,
      content: msg.content
    };

    // Handle tool calls in assistant messages
    if (msg.role === "bot" && msg.tool_calls) {
      translated.tool_calls = msg.tool_calls.map(tc => ({
        id: tc.id,
        type: "function",
        function: {
          name: tc.function.name,
          arguments: tc.function.arguments
        }
      }));
    }

    // Handle tool results
    if (msg.role === "tool") {
      translated.tool_call_id = msg.tool_call_id;
    }

    return translated;
  });

  const openaiRequest = {
    model: "gpt-4",  // Or configurable
    messages: messages,
    stream: true
  };

  // Pass through temperature if provided
  if (poeRequest.temperature !== undefined && poeRequest.temperature !== null) {
    openaiRequest.temperature = poeRequest.temperature;
  }

  // Pass through stop sequences
  if (poeRequest.stop_sequences && poeRequest.stop_sequences.length > 0) {
    openaiRequest.stop = poeRequest.stop_sequences;
  }

  // Translate tools if provided
  if (poeRequest.tools && poeRequest.tools.length > 0) {
    openaiRequest.tools = translateTools(poeRequest.tools);
    openaiRequest.tool_choice = "auto";
  }

  return openaiRequest;
}
```

---

## Tool/Function Calling Translation

### Poe Tool Definition (Incoming)

```json
{
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "get_weather",
        "description": "Get the current weather for a location",
        "parameters": {
          "type": "object",
          "properties": {
            "location": {
              "type": "string",
              "description": "City name or coordinates"
            },
            "unit": {
              "type": "string",
              "enum": ["celsius", "fahrenheit"],
              "description": "Temperature unit"
            }
          },
          "required": ["location"]
        }
      }
    }
  ]
}
```

### OpenAI Tool Definition (Outgoing)

```json
{
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "get_weather",
        "description": "Get the current weather for a location",
        "parameters": {
          "type": "object",
          "properties": {
            "location": {
              "type": "string",
              "description": "City name or coordinates"
            },
            "unit": {
              "type": "string",
              "enum": ["celsius", "fahrenheit"],
              "description": "Temperature unit"
            }
          },
          "required": ["location"]
        }
      }
    }
  ]
}
```

**Note**: Poe and OpenAI tool definitions are nearly identical. Direct passthrough usually works.

### Tool Translation Function

```javascript
function translateTools(poeTools) {
  // Poe tool format is compatible with OpenAI
  // Just ensure the structure is correct
  return poeTools.map(tool => ({
    type: "function",
    function: {
      name: tool.function.name,
      description: tool.function.description || "",
      parameters: tool.function.parameters || { type: "object", properties: {} }
    }
  }));
}
```

### Tool Call Response Translation

When OpenAI returns a tool call request, translate back to Poe format:

```javascript
// OpenAI tool call in response
{
  "id": "call_abc123",
  "type": "function",
  "function": {
    "name": "get_weather",
    "arguments": "{\"location\": \"Paris\"}"
  }
}

// Send to Poe as:
await sendEvent("tool_call", {
  "id": "call_abc123",
  "function": {
    "name": "get_weather",
    "arguments": "{\"location\": \"Paris\"}"
  }
});
```

---

## Response Translation: OpenAI → Poe

### OpenAI SSE Response (Incoming from Copilot)

```
data: {"id":"chatcmpl-123","object":"chat.completion.chunk","created":1234567890,"model":"gpt-4","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

data: {"id":"chatcmpl-123","object":"chat.completion.chunk","created":1234567890,"model":"gpt-4","choices":[{"index":0,"delta":{"content":" there"},"finish_reason":null}]}

data: {"id":"chatcmpl-123","object":"chat.completion.chunk","created":1234567890,"model":"gpt-4","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

### Poe SSE Response (Outgoing to Poe)

```
event: text
data: {"text": "Hello"}

event: text
data: {"text": " there"}

event: done
data: {}
```

### Poe Event Types

| Event | Data | When to Use |
|-------|------|-------------|
| `text` | `{"text": "..."}` | Regular text content |
| `replace_response` | `{"text": "..."}` | Replace all previous text |
| `done` | `{}` | Stream complete |
| `error` | `{"text": "...", "allow_retry": bool}` | Error occurred |
| `suggested_reply` | `{"text": "..."}` | Add suggested reply button |
| `meta` | `{"suggested_replies": [...]}` | Multiple suggested replies |
| `tool_call` | `{"id": "...", "function": {...}}` | Request tool execution |

### Response Translation Function

```javascript
async function translateOpenAIStreamToPoe(openaiStream, writer, encoder) {
  const reader = openaiStream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  const sendEvent = async (event, data) => {
    await writer.write(encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`));
  };

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() || "";  // Keep incomplete line in buffer

      for (const line of lines) {
        if (!line.startsWith("data: ")) continue;
        if (line === "data: [DONE]") continue;

        try {
          const json = JSON.parse(line.slice(6));
          const choice = json.choices?.[0];

          if (!choice) continue;

          // Handle text content
          const content = choice.delta?.content;
          if (content) {
            await sendEvent("text", { text: content });
          }

          // Handle tool calls
          const toolCalls = choice.delta?.tool_calls;
          if (toolCalls) {
            for (const tc of toolCalls) {
              // Tool calls may come in chunks, accumulate them
              await sendEvent("tool_call", {
                id: tc.id,
                function: {
                  name: tc.function?.name,
                  arguments: tc.function?.arguments
                }
              });
            }
          }

          // Handle finish reason
          if (choice.finish_reason === "tool_calls") {
            // Don't send done yet - Poe will execute tools
          } else if (choice.finish_reason === "stop") {
            // Normal completion
          }

        } catch (parseError) {
          console.error("Parse error:", parseError);
        }
      }
    }

    await sendEvent("done", {});

  } catch (error) {
    await sendEvent("error", {
      text: error.message,
      allow_retry: true
    });
  }
}
```

---

## Complete Worker Implementation

```javascript
// worker.js

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // Poe Server Bot endpoint
    if (request.method === "POST" && (url.pathname === "/" || url.pathname === "/poe")) {
      return handlePoeRequest(request, env);
    }

    // Settings endpoint (optional - Poe calls this)
    if (request.method === "POST" && url.pathname === "/settings") {
      return Response.json({
        server_bot_dependencies: {},
        allow_attachments: false,
        introduction_message: "Hello! I'm a Copilot proxy bot."
      });
    }

    return new Response("Copilot Proxy Bot", { status: 200 });
  }
};

async function handlePoeRequest(request, env) {
  const poeRequest = await request.json();
  const { query, user_id, tools } = poeRequest;

  // Set up SSE response
  const { readable, writable } = new TransformStream();
  const writer = writable.getWriter();
  const encoder = new TextEncoder();

  const sendEvent = async (event, data) => {
    await writer.write(encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`));
  };

  // Process in background
  (async () => {
    try {
      // Get cached Copilot token
      const cachedToken = await env.TOKEN_CACHE.get(`user:${user_id}`);

      if (!cachedToken) {
        // Check if this is an AUTH message
        const lastMessage = query[query.length - 1]?.content || "";
        if (lastMessage.startsWith("AUTH ")) {
          await handleAuth(lastMessage, user_id, env, sendEvent);
          await sendEvent("done", {});
          await writer.close();
          return;
        }

        // No token, prompt for auth
        await sendEvent("text", {
          text: "Please authenticate first. Send: AUTH <your_github_token>"
        });
        await sendEvent("done", {});
        await writer.close();
        return;
      }

      const copilotToken = JSON.parse(cachedToken).token;

      // Translate Poe request to OpenAI format
      const openaiRequest = translatePoeToOpenAI(poeRequest);

      // Call Copilot API
      const copilotResponse = await fetch("https://api.githubcopilot.com/chat/completions", {
        method: "POST",
        headers: {
          "Authorization": `Bearer ${copilotToken}`,
          "Content-Type": "application/json",
          "Accept": "text/event-stream"
        },
        body: JSON.stringify(openaiRequest)
      });

      if (!copilotResponse.ok) {
        const error = await copilotResponse.text();
        await sendEvent("error", { text: `Copilot API error: ${error}`, allow_retry: true });
        await sendEvent("done", {});
        await writer.close();
        return;
      }

      // Stream and translate response
      await translateOpenAIStreamToPoe(copilotResponse.body, writer, encoder);

    } catch (error) {
      await sendEvent("error", { text: error.message, allow_retry: true });
      await sendEvent("done", {});
    } finally {
      await writer.close();
    }
  })();

  return new Response(readable, {
    headers: {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache"
    }
  });
}

async function handleAuth(message, userId, env, sendEvent) {
  const githubToken = message.replace("AUTH ", "").trim();

  try {
    // Exchange GitHub token for Copilot token
    const tokenResponse = await fetch("https://api.github.com/copilot_internal/v2/token", {
      headers: {
        "Authorization": `Bearer ${githubToken}`,
        "Accept": "application/json"
      }
    });

    if (!tokenResponse.ok) {
      await sendEvent("text", { text: "❌ Authentication failed. Check your GitHub token." });
      return;
    }

    const tokenData = await tokenResponse.json();

    // Cache the token
    await env.TOKEN_CACHE.put(
      `user:${userId}`,
      JSON.stringify({ token: tokenData.token }),
      { expirationTtl: 8 * 60 * 60 }  // 8 hours
    );

    await sendEvent("text", { text: "✅ Authenticated successfully! You can now chat." });

  } catch (error) {
    await sendEvent("text", { text: `❌ Auth error: ${error.message}` });
  }
}

function translatePoeToOpenAI(poeRequest) {
  const messages = poeRequest.query.map(msg => ({
    role: msg.role === "bot" ? "assistant" : msg.role,
    content: msg.content
  }));

  const openaiRequest = {
    model: "gpt-4",
    messages: messages,
    stream: true
  };

  if (poeRequest.temperature != null) {
    openaiRequest.temperature = poeRequest.temperature;
  }

  if (poeRequest.stop_sequences?.length > 0) {
    openaiRequest.stop = poeRequest.stop_sequences;
  }

  if (poeRequest.tools?.length > 0) {
    openaiRequest.tools = poeRequest.tools;
    openaiRequest.tool_choice = "auto";
  }

  return openaiRequest;
}

async function translateOpenAIStreamToPoe(stream, writer, encoder) {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  const sendEvent = async (event, data) => {
    await writer.write(encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`));
  };

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() || "";

    for (const line of lines) {
      if (!line.startsWith("data: ") || line === "data: [DONE]") continue;

      try {
        const json = JSON.parse(line.slice(6));
        const content = json.choices?.[0]?.delta?.content;
        if (content) {
          await sendEvent("text", { text: content });
        }
      } catch (e) {}
    }
  }

  await sendEvent("done", {});
}
```

---

## wrangler.toml

```toml
name = "copilot-proxy-bot"
main = "worker.js"
compatibility_date = "2024-01-01"

# Use bundled (default) for CPU-time billing
# Streaming waits are NOT billed
usage_model = "bundled"

# KV namespace for token caching
[[kv_namespaces]]
binding = "TOKEN_CACHE"
id = "your-kv-namespace-id"
```

---

## Summary Tables

### Request Field Mapping

| Poe Field | OpenAI Field | Transformation |
|-----------|--------------|----------------|
| `query` | `messages` | Array mapping, role translation |
| `query[].role` | `messages[].role` | `bot` → `assistant` |
| `query[].content` | `messages[].content` | Direct |
| `temperature` | `temperature` | Direct |
| `stop_sequences` | `stop` | Direct |
| `tools` | `tools` | Direct (compatible format) |
| `tool_results` | (in messages) | As `tool` role messages |

### Response Event Mapping

| OpenAI SSE | Poe SSE |
|------------|---------|
| `data: {"choices":[{"delta":{"content":"..."}}]}` | `event: text\ndata: {"text": "..."}` |
| `data: {"choices":[{"delta":{"tool_calls":[...]}}]}` | `event: tool_call\ndata: {...}` |
| `data: [DONE]` | `event: done\ndata: {}` |
| (HTTP error) | `event: error\ndata: {"text": "...", "allow_retry": true}` |
