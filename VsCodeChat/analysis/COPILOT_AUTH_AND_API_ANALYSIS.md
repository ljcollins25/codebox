# GitHub Copilot Chat Extension - Authentication & API Analysis

## Overview

This document details how the VS Code GitHub Copilot Chat extension (version 0.36.2) authenticates users and submits AI prompts to the Copilot API.

---

## 1. Authentication Flow

### 1.1 GitHub OAuth Authentication

The extension uses VS Code's built-in authentication API to authenticate with GitHub. Authentication happens in multiple stages:

#### OAuth Scopes

The extension requests different OAuth scopes based on the permission level:

```javascript
// Minimal scopes (read-only)
Pie = ["user:email"]           // Basic email scope
cDt = ["read:user"]            // Read user info

// Full permission scopes  
nq = ["read:user", "user:email", "repo", "workflow"]  // Full repo access
```

#### Authentication Providers

The extension supports two authentication providers:
- `github` - Standard GitHub OAuth
- `github-enterprise` - GitHub Enterprise Server

### 1.2 Session Acquisition

```javascript
// Get GitHub session (simplified flow)
async function i1e(configurationService, options) {
    let authProvider = vF(configurationService);  // "github" or "github-enterprise"
    
    // Check for existing session with full scopes
    if (configurationService.getConfig(V.Shared.AuthPermissions) !== "minimal") {
        let session = await vscode.authentication.getSession(authProvider, nq, { silent: true });
        if (session) return session;
    }
    
    // Fall back to minimal scopes
    let session = await vscode.authentication.getSession(authProvider, Pie, { silent: true });
    return session;
}
```

### 1.3 Copilot Token Exchange

Once the GitHub OAuth token is obtained, it's exchanged for a **Copilot-specific token**:

```javascript
async fetchCopilotTokenFromGitHubToken(githubToken) {
    let requestOptions = {
        headers: {
            Authorization: `token ${githubToken}`,
            "X-GitHub-Api-Version": "2025-04-01"
        },
        retryFallbacks: true,
        expectJSON: true
    };
    return await this._capiClientService.makeRequest(requestOptions, { type: zn.CopilotToken });
}
```

**Token Endpoint:**
```
https://api.github.com/copilot_internal/v2/token
```

### 1.4 Anonymous/No-Auth Access (Free Tier)

For users without GitHub accounts (anonymous access):

```javascript
async fetchCopilotTokenFromDevDeviceId(devDeviceId) {
    let requestOptions = {
        headers: {
            "X-GitHub-Api-Version": "2025-04-01",
            "Editor-Device-Id": `${devDeviceId}`
        },
        retryFallbacks: true,
        expectJSON: true
    };
    return await this._capiClientService.makeRequest(requestOptions, { type: zn.CopilotNLToken });
}
```

**Token Endpoint:**
```
https://api.github.com/copilot_internal/v2/nltoken
```

### 1.5 Copilot Token Structure

The Copilot token response contains:

```javascript
class PT {  // CopilotToken
    token: string;           // The actual JWT/bearer token
    expires_at: number;      // Token expiration timestamp
    refresh_in: number;      // Seconds until refresh needed
    sku: string;            // "free_limited_copilot", "individual", "business", "enterprise"
    individual: boolean;     // Is individual subscription
    organization_list: [];   // Organization IDs with access
    enterprise_list: [];     // Enterprise IDs
    endpoints: {            // API endpoints (can be customized for enterprise)
        api: string;
        proxy: string;
        telemetry: string;
    };
    chat_enabled: boolean;   // Is chat feature enabled
    telemetry: string;       // "enabled" or "disabled"
    copilot_plan: string;    // Plan type
    username: string;        // GitHub username
}
```

---

## 2. API Endpoints

### 2.1 Base URLs

```javascript
// Default public endpoints
_capiBaseUrl = "https://api.githubcopilot.com"
_dotcomAPIUrl = "https://api.github.com"
_telemetryBaseUrl = "https://copilot-telemetry.githubusercontent.com"
_proxyBaseUrl = "https://copilot-proxy.githubusercontent.com"

// Enterprise endpoints are dynamically constructed
// e.g., https://api.your-enterprise.github.com
```

### 2.2 Chat Completion Endpoints

| Endpoint | URL | Purpose |
|----------|-----|---------|
| Chat Completions | `{capiBaseUrl}/chat/completions` | Main chat API (OpenAI-compatible) |
| Responses | `{capiBaseUrl}/responses` | Response handling |
| Messages | `{capiBaseUrl}/v1/messages` | Anthropic-style messages API |
| Embeddings | `{capiBaseUrl}/embeddings` | Code embeddings |
| Models | `{capiBaseUrl}/models` | Available models list |
| Auto Model | `{capiBaseUrl}/models/session` | Model selection |
| MCP Server | `{capiBaseUrl}/mcp/` | Model Context Protocol |

### 2.3 Additional Endpoints

| Endpoint | URL | Purpose |
|----------|-----|---------|
| Token | `{dotcomAPIUrl}/copilot_internal/v2/token` | Token exchange |
| User Info | `{dotcomAPIUrl}/copilot_internal/user` | User subscription info |
| Telemetry | `{telemetryBaseUrl}/telemetry` | Usage telemetry |
| Code Search | `{dotcomAPIUrl}/embeddings/code/search` | Code search |
| Content Exclusion | `{dotcomAPIUrl}/copilot_internal/content_exclusion` | .copilotignore rules |
| Agents | `{capiBaseUrl}/agents` | Remote agents |
| Cloud Agent | `{capiBaseUrl}/agents/swe` | Software Engineering agent |

---

## 3. Request Format

### 3.1 Request Headers

Every API request includes these headers:

```javascript
headers = {
    Authorization: `Bearer ${copilotToken}`,
    "X-Request-Id": generateUUID(),           // Unique request ID
    "X-Interaction-Type": interactionType,    // e.g., "chat", "completion"
    "OpenAI-Intent": interactionType,         // Same as interaction type
    "X-GitHub-Api-Version": "2025-05-01",     // API version
    "Content-Type": "application/json",
    
    // Additional headers for specific requests
    "Openai-Organization": "github-copilot",  // Organization identifier
    "X-Request-Id": requestId,                // Correlation ID
}
```

### 3.2 Chat Request Body

The chat completions endpoint uses an **OpenAI-compatible format**:

```javascript
{
    model: "gpt-4o",                    // or "claude-3.5-sonnet", etc.
    messages: [
        {
            role: "system",
            content: "You are a helpful assistant..."
        },
        {
            role: "user", 
            content: "How do I implement X?"
        }
    ],
    stream: true,                       // Enable streaming responses
    temperature: 0.1,                   // Model temperature
    max_tokens: 4096,                   // Max output tokens
    
    // Optional tool/function calling
    tools: [...],
    tool_choice: "auto"
}
```

### 3.3 Making Requests

```javascript
function $g(fetcher, telemetry, capiService, method, token, requestId, ...) {
    let headers = {
        Authorization: `Bearer ${token}`,
        "X-Request-Id": requestId,
        "X-Interaction-Type": interactionType,
        "OpenAI-Intent": interactionType,
        "X-GitHub-Api-Version": "2025-05-01",
        ...extraHeaders
    };
    
    let fetchOptions = {
        method: "POST",
        headers: headers,
        json: requestBody,
        timeout: 120000,  // 2 minute timeout
    };
    
    return fetcher.fetch(capiService.capiChatURL, fetchOptions);
}
```

---

## 4. Token Refresh

### 4.1 Token Expiration Handling

```javascript
async getCopilotToken(forceRefresh) {
    // Check if token exists and is not expired (with 5-minute buffer)
    if (!this.copilotToken || 
        this.copilotToken.expires_at < currentTime() - 60 * 5 || 
        forceRefresh) {
        
        // Refresh the token
        this.copilotToken = await this.authenticateAndGetToken();
    }
    return new PT(this.copilotToken);
}
```

### 4.2 Token Reset on Errors

```javascript
resetCopilotToken(httpStatusCode) {
    this._telemetryService.sendGHTelemetryEvent("auth.reset_token_" + httpStatusCode);
    this._logService.debug(`Resetting copilot token on HTTP error ${httpStatusCode}`);
    this.copilotToken = undefined;
}
```

---

## 5. Error Handling

### 5.1 Authentication Errors

| Error | Reason | Handling |
|-------|--------|----------|
| HTTP 401 | Invalid token | Prompt re-authentication |
| HTTP 403 | Rate limited | Wait and retry |
| `not_signed_up` | No Copilot access | Show sign-up prompt |
| `subscription_ended` | Subscription expired | Show renewal prompt |
| `chat_enabled: false` | Chat disabled | Show error message |

### 5.2 Network Error Recovery

```javascript
// Handle network errors and retry
if (networkError.code in ["ECONNRESET", "ETIMEDOUT", "ERR_NETWORK_CHANGED", 
                           "ERR_HTTP2_INVALID_SESSION", "ERR_HTTP2_STREAM_CANCEL"]) {
    // Disconnect all connections and retry
    await fetcher.disconnectAll();
    return fetcher.fetch(url, options);
}
```

---

## 6. Telemetry

### 6.1 Events Tracked

- `auth.new_login` - New authentication attempt
- `auth.new_token` - Successful token acquisition
- `auth.request_failed` - Token request failed
- `auth.github_login_failed` - GitHub OAuth failed
- `auth.invalid_token` - Token validation failed
- `auth.rate_limited` - Rate limit hit
- `networking.cancelRequest` - Request cancelled
- `networking.disconnectAll` - Network reconnection

### 6.2 Telemetry Endpoint

```
https://copilot-telemetry.githubusercontent.com/telemetry
```

---

## 7. Enterprise Configuration

For GitHub Enterprise Server:

```javascript
// Enterprise URL detection
_getDotComAPIUrl() {
    if (this._enterpriseUrlConfig) {
        let url = new URL(this._enterpriseUrlConfig);
        return `${url.protocol}//api.${url.hostname}${url.port ? ":" + url.port : ""}`;
    }
    return "https://api.github.com";
}

// Enterprise API URL
_getCAPIUrl(endpoints) {
    return endpoints?.api || "https://api.githubcopilot.com";
}
```

---

## 8. MCP (Model Context Protocol) Integration

The extension supports MCP servers:

```javascript
// Built-in GitHub MCP server
{
    type: "http",
    url: "https://api.githubcopilot.com/mcp/",
    tools: ["*"],
    isDefaultServer: true,
    headers: {
        Authorization: `Bearer ${session.accessToken}`,
        "X-MCP-Toolsets": toolsets,     // Optional toolset selection
        "X-MCP-Readonly": "true",       // Optional readonly mode
        "X-MCP-Lockdown": "true"        // Optional lockdown mode
    }
}
```

---

## 9. Key Files & Locations

| Item | Location |
|------|----------|
| Extension | `~/.vscode/extensions/github.copilot-chat-{version}/` |
| Main Code | `dist/extension.js` |
| Package Info | `package.json` |
| Copilot SDK | `node_modules/@github/copilot/sdk/index.js` |
| Session State | `~/.copilot/session-state/*.jsonl` |

---

## 10. Inline Code Completions (Ghost Text)

The base GitHub Copilot extension (`github.copilot`) handles inline code completions separately from chat.

### 10.1 Inline Completion Endpoints

```javascript
// Default endpoints
const ZB = {
    api: "https://api.githubcopilot.com",
    proxy: "https://copilot-proxy.githubusercontent.com",
    telemetry: "https://copilot-telemetry.githubusercontent.com",
    "origin-tracker": "https://origin-tracker.githubusercontent.com"
};
```

### 10.2 Inline Completion Request

The extension uses VS Code's `InlineCompletionItemProvider` API:

```javascript
registerLanguageProvider(selector) {
    return languages.registerInlineCompletionItemProvider(selector, {
        provideInlineCompletionItems: async (document, position, context, token) => {
            // Send request to language server
            return client.sendRequest(
                InlineCompletionRequest.type,
                client.code2ProtocolConverter.asInlineCompletionParams(document, position, context),
                token
            );
        }
    });
}
```

### 10.3 Completion Request Flow

1. **Document Context** → Gathers surrounding code context
2. **Prompt Construction** → Creates completion prompt with prefix/suffix
3. **API Request** → Sends to `api.githubcopilot.com/completions`
4. **Response Processing** → Filters and ranks completion suggestions

---

## 11. Summary

### Authentication Flow:
1. **GitHub OAuth** → User authenticates via VS Code's auth API
2. **Token Exchange** → OAuth token exchanged for Copilot token via `/copilot_internal/v2/token`
3. **Token Caching** → Copilot token cached with expiration tracking
4. **Auto-Refresh** → Token refreshed 5 minutes before expiration

### API Communication:
1. **Chat Endpoint**: `https://api.githubcopilot.com/chat/completions`
2. **Completions Endpoint**: `https://api.githubcopilot.com/completions`
3. **Format**: OpenAI-compatible chat/completion API
4. **Auth Header**: `Authorization: Bearer {copilot_token}`
5. **Streaming**: Server-Sent Events (SSE) for streaming responses

### Security Features:
- Token expiration and refresh
- Request ID tracking for debugging
- Telemetry for monitoring
- Enterprise URL customization
- Content exclusion rules (.copilotignore)

---

## 12. Quick Reference - cURL Examples

### Get Copilot Token (requires GitHub OAuth token):
```bash
curl -X GET "https://api.github.com/copilot_internal/v2/token" \
  -H "Authorization: token YOUR_GITHUB_TOKEN" \
  -H "X-GitHub-Api-Version: 2025-04-01"
```

### Chat Completion Request:
```bash
curl -X POST "https://api.githubcopilot.com/chat/completions" \
  -H "Authorization: Bearer YOUR_COPILOT_TOKEN" \
  -H "Content-Type: application/json" \
  -H "X-Request-Id: $(uuidgen)" \
  -H "X-GitHub-Api-Version: 2025-05-01" \
  -H "OpenAI-Intent: chat" \
  -d '{
    "model": "gpt-4o",
    "messages": [
      {"role": "system", "content": "You are a helpful coding assistant."},
      {"role": "user", "content": "How do I read a file in Python?"}
    ],
    "stream": true
  }'
```

### Get Available Models:
```bash
curl -X GET "https://api.githubcopilot.com/models" \
  -H "Authorization: Bearer YOUR_COPILOT_TOKEN" \
  -H "X-GitHub-Api-Version: 2025-05-01"
```
