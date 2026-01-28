# GitHub Copilot API Proxy - Cloudflare Worker

A Cloudflare Worker that proxies requests to the GitHub Copilot API.

## Architecture

```
┌──────────────┐      ┌────────────────────┐      ┌─────────────────────────┐
│   Client     │ ──── │  Cloudflare Worker │ ──── │  api.githubcopilot.com  │
│  (VS Code,   │      │     (Proxy)        │      │  (Copilot API)          │
│   App, etc)  │      └────────────────────┘      └─────────────────────────┘
└──────────────┘
       │
       │ Auth handled separately
       ▼
┌──────────────────┐
│ GitHub OAuth     │
│ (Browser-based)  │
└──────────────────┘
```

## Authentication Flow

**Important:** Copilot tokens are short-lived (~30 minutes). Authentication must happen separately:

1. **Client authenticates with GitHub OAuth** (requires browser interaction)
2. **Client exchanges GitHub token for Copilot token** via `/token` endpoint or directly
3. **Client passes Copilot token** to this worker in `Authorization: Bearer <token>` header

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/chat/completions` | Chat completions (OpenAI-compatible) |
| POST | `/completions` | Code completions |
| POST | `/v1/messages` | Messages API (Anthropic-style) |
| GET | `/models` | List available models |
| POST | `/token` | Exchange GitHub OAuth token for Copilot token |
| GET | `/health` | Health check |

## Usage

### 1. Install dependencies

```bash
npm install
```

### 2. Run locally

```bash
npm run dev
```

### 3. Deploy to Cloudflare

```bash
npm run deploy
```

## Example Requests

### Chat Completion

```bash
curl -X POST https://your-worker.workers.dev/chat/completions \
  -H "Authorization: Bearer YOUR_COPILOT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o",
    "messages": [
      {"role": "user", "content": "Write a hello world in Python"}
    ],
    "stream": false
  }'
```

### Exchange GitHub Token for Copilot Token

```bash
curl -X POST https://your-worker.workers.dev/token \
  -H "Content-Type: application/json" \
  -d '{
    "github_token": "gho_your_github_oauth_token"
  }'
```

## Getting a Copilot Token

### Option 1: Via GitHub OAuth (Recommended)

1. Create a GitHub OAuth App
2. Authenticate user and get OAuth token with scopes: `read:user`, `user:email`
3. Exchange for Copilot token:

```bash
curl https://api.github.com/copilot_internal/v2/token \
  -H "Authorization: token YOUR_GITHUB_OAUTH_TOKEN" \
  -H "X-GitHub-Api-Version: 2025-04-01"
```

### Option 2: From VS Code (Development only)

1. Open VS Code with Copilot extension
2. Open Developer Tools (Help > Toggle Developer Tools)
3. Run: `await vscode.authentication.getSession('github', ['user:email'])`
4. Use the token to exchange for Copilot token

## Environment Variables

| Variable | Description |
|----------|-------------|
| `COPILOT_TOKEN` | (Optional) Default Copilot token - not recommended, expires quickly |
| `ALLOWED_ORIGINS` | (Optional) CORS allowed origins, defaults to `*` |

## Limitations

1. **Token Expiration**: Copilot tokens expire in ~30 minutes. The worker cannot refresh them automatically.
2. **No OAuth Flow**: The worker cannot perform GitHub OAuth - this requires browser interaction.
3. **Rate Limits**: Subject to GitHub Copilot rate limits based on your subscription.

## Security Considerations

- Never commit Copilot tokens to source control
- Use HTTPS only
- Consider implementing additional authentication for the worker itself
- Restrict `ALLOWED_ORIGINS` in production
