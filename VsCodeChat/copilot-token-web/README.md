# Copilot API Gateway

A Cloudflare Worker that provides an OpenAI-compatible API powered by GitHub Copilot.

**No OAuth App required!** Uses GitHub's device flow with VS Code's public client ID.

## Features

- 🔐 **Device Flow Auth** - Sign in via GitHub device code (like VS Code does)
- 🔑 **Permanent API Keys** - Get a `sk-copilot-xxx` key that never expires
- 🔄 **Auto Token Refresh** - Copilot tokens refresh automatically in the background
- 🔌 **OpenAI Compatible** - Works with any OpenAI SDK, LangChain, etc.
- 🚀 **All Models** - Access GPT-4o, Claude 3.5 Sonnet, and more

## Quick Start

### 1. Deploy to Cloudflare

```bash
cd copilot-token-web
npm install

# Create KV namespaces
wrangler kv:namespace create SESSIONS
wrangler kv:namespace create DEVICE_FLOWS

# Update wrangler.toml with the IDs from above commands

# Deploy
npm run deploy
```

### 2. Get Your API Key

1. Visit your deployed worker URL
2. Click "Sign in with GitHub"
3. Enter the device code at github.com/login/device
4. Copy your `sk-copilot-xxx` API key

### 3. Use It

```python
from openai import OpenAI

client = OpenAI(
    api_key="sk-copilot-xxx",  # Your API key
    base_url="https://your-worker.workers.dev/v1"
)

response = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Hello!"}]
)
print(response.choices[0].message.content)
```

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /` | Home page with sign-in |
| `GET /login` | Start device flow auth |
| `GET /dashboard` | View your API key |
| `POST /v1/chat/completions` | Chat completions |
| `GET /v1/models` | List available models |
| `POST /v1/completions` | Code completions |
| `POST /v1/embeddings` | Embeddings |

## Usage Examples

### Python

```python
from openai import OpenAI

client = OpenAI(
    api_key="sk-copilot-xxx",
    base_url="https://your-worker.workers.dev/v1"
)

# Chat
response = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Explain async/await"}]
)

# Streaming
for chunk in client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Hello!"}],
    stream=True
):
    print(chunk.choices[0].delta.content or "", end="")
```

### Node.js

```javascript
import OpenAI from 'openai';

const client = new OpenAI({
    apiKey: 'sk-copilot-xxx',
    baseURL: 'https://your-worker.workers.dev/v1'
});

const response = await client.chat.completions.create({
    model: 'gpt-4o',
    messages: [{ role: 'user', content: 'Hello!' }]
});
```

### curl

```bash
curl https://your-worker.workers.dev/v1/chat/completions \
  -H "Authorization: Bearer sk-copilot-xxx" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o",
    "messages": [{"role": "user", "content": "Hello!"}]
  }'
```

## Available Models

Query the models endpoint to see what's available:

```bash
curl https://your-worker.workers.dev/v1/models \
  -H "Authorization: Bearer sk-copilot-xxx"
```

Typical models include:
- `gpt-4o` - GPT-4 Omni
- `gpt-4o-mini` - GPT-4 Omni Mini
- `claude-3.5-sonnet` - Claude 3.5 Sonnet
- `o1-preview` - OpenAI o1 Preview
- `o1-mini` - OpenAI o1 Mini

## How It Works

1. **Device Flow Authentication**: When you click "Sign in", the worker requests a device code from GitHub using VS Code's public OAuth client ID (`01ab8ac9400c4e429b23`).

2. **User Authorization**: You visit github.com/login/device and enter the code. GitHub authenticates you and grants the token to the worker.

3. **Session Creation**: The worker stores your GitHub token in KV storage and gives you a permanent API key (`sk-copilot-xxx`).

4. **API Proxying**: When you make API calls, the worker:
   - Validates your API key
   - Exchanges your GitHub token for a Copilot token (caches for ~25 min)
   - Proxies your request to `api.githubcopilot.com`
   - Returns the response

5. **Auto Refresh**: Copilot tokens expire every ~30 minutes. The worker automatically refreshes them using your stored GitHub token.

## Requirements

- **GitHub Copilot subscription** (Individual, Business, or Enterprise)
- **Cloudflare account** (free tier works)

## Local Development

```bash
npm install
npm run dev
```

Note: Device flow requires HTTPS, so local testing may require ngrok or similar.

## Security Notes

- GitHub tokens are stored in Cloudflare KV (encrypted at rest)
- API keys are randomly generated 24-byte tokens
- Sessions expire after 30 days of inactivity
- You can revoke access anytime via GitHub settings

## License

MIT
