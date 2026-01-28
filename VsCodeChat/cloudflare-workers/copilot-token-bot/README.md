# Copilot Token Bot

A Poe server bot (Cloudflare Worker) that handles GitHub device flow authentication and returns Copilot tokens.

## Features

- GitHub device flow authentication
- Caches GitHub token in KV (30 day lifetime)
- Caches Copilot token in KV (25 minute lifetime, refreshes before 30 min expiry)
- Authorization header protection
- Returns conversation_id and user_id from Poe request

## Setup

### 1. Install dependencies

```bash
npm install
```

### 2. Create KV namespace

```bash
npm run kv:create
```

Copy the returned namespace ID and update `wrangler.toml`:

```toml
[[kv_namespaces]]
binding = "TOKEN_CACHE"
id = "YOUR_ACTUAL_KV_NAMESPACE_ID"
```

For local dev, also create a preview namespace:

```bash
npm run kv:create:preview
```

### 3. Configure authorization secret

Edit `wrangler.toml` to change the auth secret:

```toml
[vars]
AUTH_SECRET = "your-secure-secret-here"
```

Or use Wrangler secrets for production:

```bash
wrangler secret put AUTH_SECRET
```

### 4. Deploy

```bash
npm run deploy
```

### 5. Register on Poe

1. Go to [creator.poe.com](https://creator.poe.com)
2. Create a new server bot
3. Set the server URL to your Worker URL
4. Set the Access Key to your `AUTH_SECRET` value

## Usage Flow

1. **First message**: Bot starts GitHub device flow
   - Returns verification URL and user code
   - User visits GitHub and enters the code

2. **Say "check"**: Bot polls for authorization completion
   - If authorized, caches GitHub token (30 days)
   - If still pending, shows status

3. **Subsequent messages**: Bot returns Copilot token
   - Fetches/caches Copilot token (25 min)
   - Returns JSON with token, expiry, conversation_id, user_id

## Response Format

```json
{
  "copilot_token": "tid=...",
  "copilot_expires_at": "2026-01-28T12:30:00.000Z",
  "conversation_id": "c-...",
  "user_id": "u-...",
  "github_token_expires_at": "2026-02-27T12:00:00.000Z"
}
```

## Security

- Only requests with `Authorization: Bearer <AUTH_SECRET>` or `Authorization: <AUTH_SECRET>` are accepted
- Tokens are stored in Cloudflare KV with TTL expiration
- GitHub token is cached per-worker, not per-user (single-user design)

## Local Development

```bash
npm run dev
```

Test with curl:

```bash
curl -X POST http://localhost:8787/ \
  -H "Authorization: Bearer 111111" \
  -H "Content-Type: application/json" \
  -d '{"conversation_id": "test-conv", "user_id": "test-user", "query": [{"content": "hello"}]}'
```
