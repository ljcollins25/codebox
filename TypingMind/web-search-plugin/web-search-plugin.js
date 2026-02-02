/**
 * TypingMind Web Search Plugin
 * 
 * Uses an OpenAI-compatible API with web search bots to perform searches.
 * The search query is sent as a user message, and the bot returns search results.
 * 
 * Configuration (set in plugin settings):
 * - apiKey: API key for the OpenAI-compatible service
 * - apiBaseUrl: Base URL for the API (default: "https://api.poe.com/v1")
 * - searchBot: Which bot to use for web search (default: "Gray-Search")
 */

// ============================================================
// AVAILABLE WEB SEARCH BOTS
// Add or remove bots from this list as needed
// ============================================================
const WEB_SEARCH_BOTS = [
  "Gray-Search",
  "Web-Search",
  "Claude-3.5-Sonnet-Search",
  "GPT-4o-Search",
  "Gemini-2.0-Flash-Search"
];

/**
 * Perform a web search using an OpenAI-compatible API with a search bot
 */
async function performWebSearch(query, apiKey, apiBaseUrl, searchBot) {
  const response = await fetch(`${apiBaseUrl}/chat/completions`, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${apiKey}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      model: searchBot,
      messages: [
        { role: 'user', content: query }
      ],
      stream: false
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`API error (${response.status}): ${errorText}`);
  }

  const data = await response.json();
  const content = data.choices?.[0]?.message?.content;
  
  if (!content) {
    throw new Error('No response received from search bot');
  }

  return content;
}

// Main plugin function
async function webSearch(params, userSettings) {
  const {
    query,
    searchBot: overrideBot
  } = params;

  const {
    apiKey,
    apiBaseUrl = 'https://api.poe.com/v1',
    searchBot = 'Gray-Search'
  } = userSettings;

  // Allow params to override settings
  const bot = overrideBot || searchBot;

  if (!query || typeof query !== 'string' || query.trim() === '') {
    throw new Error('Please provide a valid search query.');
  }

  if (!apiKey) {
    throw new Error('API key is required. Please configure it in the plugin settings.');
  }

  // Perform search
  const searchResults = await performWebSearch(query, apiKey, apiBaseUrl, bot);

  return searchResults;
}

// TypingMind Plugin Export
const plugin = {
  id: 'web-search',
  name: 'Web Search',
  description: 'Search the web using an OpenAI-compatible API with web search bots. Returns results for the LLM to use in answering questions.',
  version: '2.0.0',
  
  // Plugin parameters (what the AI can pass to the function)
  parameters: {
    type: 'object',
    properties: {
      query: {
        type: 'string',
        description: 'The search query to look up on the web'
      },
      searchBot: {
        type: 'string',
        enum: WEB_SEARCH_BOTS,
        description: 'Override which search bot to use (optional)'
      }
    },
    required: ['query']
  },
  
  // User settings (configured in plugin settings UI)
  userSettings: [
    {
      name: 'apiKey',
      label: 'API Key',
      type: 'password',
      description: 'API key for the OpenAI-compatible service'
    },
    {
      name: 'apiBaseUrl',
      label: 'API Base URL',
      type: 'text',
      default: 'https://api.poe.com/v1',
      description: 'Base URL for the OpenAI-compatible API'
    },
    {
      name: 'searchBot',
      label: 'Search Bot',
      type: 'select',
      options: WEB_SEARCH_BOTS.map(bot => ({ label: bot, value: bot })),
      default: 'Gray-Search',
      description: 'Which bot to use for web searches'
    }
  ],
  
  // Main execution function
  action: webSearch
};

// Export for TypingMind
if (typeof module !== 'undefined' && module.exports) {
  module.exports = plugin;
}
