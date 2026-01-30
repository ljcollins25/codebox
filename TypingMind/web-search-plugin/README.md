# TypingMind Web Search Plugin

A web search plugin for TypingMind that supports multiple search providers. The search results are returned to the LLM, which uses them to answer your questions (similar to how Poe's Web-Search bot works).

## Features

- 🔍 **Multiple Search Providers**
  - **Serper.dev** - Google Search results via API
  - **Brave Search** - Privacy-focused search alternative

- 📊 **Rich Results**
  - Organic search results with snippets
  - Knowledge panels
  - Featured snippets/answer boxes
  - "People Also Ask" questions

## How It Works

1. You ask a question that requires web search
2. The plugin searches the web using Serper.dev or Brave Search
3. Search results are returned to the LLM (the model you're chatting with)
4. The LLM uses the search results to formulate a comprehensive answer

This is similar to how Poe's Web-Search bot operates - the LLM receives search context and uses it to answer your questions.

## Installation

### Option 1: Import JSON (Recommended)

1. Open TypingMind
2. Go to **Plugins** → **Custom Plugins**
3. Click **Import Plugin**
4. Select `web-search-plugin.json`
5. Configure your API keys in the plugin settings

### Option 2: Manual Setup

1. Copy the code from `web-search-plugin.js`
2. Create a new custom plugin in TypingMind
3. Paste the implementation code
4. Configure the settings

## Configuration

| Setting | Description | Default |
|---------|-------------|---------|
| `searchProvider` | Choose between `serper` (Google) or `brave` | `serper` |
| `serperApiKey` | Your Serper.dev API key (if using Serper) | - |
| `braveApiKey` | Your Brave Search API key (if using Brave) | - |
| `numResults` | Number of search results to return | `10` |

## Getting API Keys

### Serper.dev (Google Search)
1. Go to [serper.dev](https://serper.dev)
2. Sign up for a free account
3. Get your API key from the dashboard
4. **Free tier:** 2,500 queries/month

### Brave Search
1. Go to [brave.com/search/api](https://brave.com/search/api)
2. Sign up for the Search API
3. Get your API key
4. **Free tier:** 2,000 queries/month

## Usage Examples

### Basic Search
```
What are the latest developments in AI regulation?
```

### With Provider Override
```
Use Brave to search for privacy-focused email providers
```

## Output Format

The plugin returns results in a structured format that the LLM can easily process:

```markdown
# Web Search Results

**Query:** your query
**Provider:** Google (via Serper)

---

## Knowledge Panel: Topic
Description and attributes...

## Featured Answer
Direct answer from search...

### 1. Result Title
URL: https://example.com
Snippet text...

### 2. Result Title
URL: https://example.com
Snippet text...

## Related Questions
**Q: Related question?**
A: Answer...
```

## Troubleshooting

### "API key is required" Error
- Make sure you've configured the correct API key in plugin settings
- Check that the key is entered without extra spaces

### "API error (401)" 
- Your API key may be invalid or expired
- Regenerate your API key from the provider dashboard

### "API error (429)"
- You've exceeded your rate limit
- Wait a few minutes or upgrade your plan

## License

MIT License - Feel free to modify and distribute!
