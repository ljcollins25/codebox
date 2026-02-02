# TypingMind Web Search Plugin

A web search plugin for TypingMind that uses an OpenAI-compatible API with web search bots.

## How It Works

1. You ask a question that requires web search
2. The plugin sends your query to a web search bot via an OpenAI-compatible API
3. The search bot returns results
4. The LLM (the model you're chatting with) uses these results to answer your question

## Available Search Bots

Edit the `WEB_SEARCH_BOTS` array in [web-search-plugin.js](web-search-plugin.js) to add or remove bots:

```javascript
const WEB_SEARCH_BOTS = [
  "Gray-Search",
  "Web-Search",
  "Claude-3.5-Sonnet-Search",
  "GPT-4o-Search",
  "Gemini-2.0-Flash-Search"
];
```

## Installation

### Option 1: Import JSON (Recommended)

1. Open TypingMind
2. Go to **Plugins** → **Custom Plugins**
3. Click **Import Plugin**
4. Select `web-search-plugin.json`
5. Configure your API key and settings

### Option 2: Manual Setup

1. Copy the code from `web-search-plugin.js`
2. Create a new custom plugin in TypingMind
3. Paste the implementation code
4. Configure the settings

## Configuration

| Setting | Description | Default |
|---------|-------------|---------|
| `apiKey` | API key for the OpenAI-compatible service | - |
| `apiBaseUrl` | Base URL for the API | `https://api.poe.com/v1` |
| `searchBot` | Which bot to use for searches | `Gray-Search` |

## Usage Examples

### Basic Search
```
What are the latest developments in AI?
```

### Override Search Bot
The LLM can override the default bot when calling the plugin:
```
Use GPT-4o-Search to find information about quantum computing
```

## Troubleshooting

### "API key is required" Error
- Make sure you've configured your API key in plugin settings

### "API error (401)" 
- Your API key may be invalid or expired
- Check that the API key is correct

### "API error (429)"
- You've exceeded your rate limit
- Wait a bit or check your plan limits

### "No response received from search bot"
- The bot may not have returned any content
- Try a different search bot

## Adding New Bots

To add a new search bot:

1. Open `web-search-plugin.js`
2. Find the `WEB_SEARCH_BOTS` array at the top
3. Add your bot name to the array
4. Update `web-search-plugin.json` if you want it in the dropdown

## License

MIT License - Feel free to modify and distribute!
