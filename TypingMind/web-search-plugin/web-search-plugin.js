/**
 * TypingMind Web Search Plugin
 * 
 * Supports:
 * - Serper.dev Google Search API
 * - Brave Search API
 * 
 * The search results are returned to the LLM which uses them to answer the user's query.
 * 
 * Configuration (set in plugin settings):
 * - searchProvider: "serper" | "brave" (default: "serper")
 * - serperApiKey: Your Serper.dev API key
 * - braveApiKey: Your Brave Search API key
 * - numResults: Number of search results to return (default: 10)
 */

async function searchWithSerper(query, apiKey, numResults = 10) {
  const response = await fetch('https://google.serper.dev/search', {
    method: 'POST',
    headers: {
      'X-API-KEY': apiKey,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      q: query,
      num: numResults
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`Serper API error (${response.status}): ${errorText}`);
  }

  const data = await response.json();
  
  const results = [];
  
  // Add knowledge graph if available
  if (data.knowledgeGraph) {
    results.push({
      type: 'knowledgeGraph',
      title: data.knowledgeGraph.title || '',
      description: data.knowledgeGraph.description || '',
      attributes: data.knowledgeGraph.attributes || {}
    });
  }
  
  // Add answer box if available
  if (data.answerBox) {
    results.push({
      type: 'answerBox',
      title: data.answerBox.title || '',
      answer: data.answerBox.answer || data.answerBox.snippet || '',
      link: data.answerBox.link || ''
    });
  }
  
  // Add organic results
  if (data.organic) {
    data.organic.forEach((item, index) => {
      results.push({
        type: 'organic',
        position: index + 1,
        title: item.title || '',
        link: item.link || '',
        snippet: item.snippet || '',
        date: item.date || ''
      });
    });
  }
  
  // Add people also ask
  if (data.peopleAlsoAsk) {
    results.push({
      type: 'peopleAlsoAsk',
      questions: data.peopleAlsoAsk.map(q => ({
        question: q.question,
        answer: q.snippet,
        link: q.link
      }))
    });
  }

  return {
    provider: 'Google (via Serper)',
    query: query,
    results: results
  };
}

async function searchWithBrave(query, apiKey, numResults = 10) {
  const params = new URLSearchParams({
    q: query,
    count: numResults.toString()
  });

  const response = await fetch(`https://api.search.brave.com/res/v1/web/search?${params}`, {
    method: 'GET',
    headers: {
      'Accept': 'application/json',
      'Accept-Encoding': 'gzip',
      'X-Subscription-Token': apiKey
    }
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`Brave Search API error (${response.status}): ${errorText}`);
  }

  const data = await response.json();
  
  const results = [];
  
  // Add infobox if available (similar to knowledge graph)
  if (data.infobox) {
    results.push({
      type: 'knowledgeGraph',
      title: data.infobox.title || '',
      description: data.infobox.description || '',
      attributes: data.infobox.attributes || {}
    });
  }
  
  // Add featured snippet if available
  if (data.featured_snippet) {
    results.push({
      type: 'answerBox',
      title: data.featured_snippet.title || '',
      answer: data.featured_snippet.description || '',
      link: data.featured_snippet.url || ''
    });
  }
  
  // Add web results
  if (data.web?.results) {
    data.web.results.forEach((item, index) => {
      results.push({
        type: 'organic',
        position: index + 1,
        title: item.title || '',
        link: item.url || '',
        snippet: item.description || '',
        date: item.age || ''
      });
    });
  }
  
  // Add FAQ results
  if (data.faq?.results) {
    results.push({
      type: 'peopleAlsoAsk',
      questions: data.faq.results.map(q => ({
        question: q.question,
        answer: q.answer,
        link: q.url
      }))
    });
  }

  return {
    provider: 'Brave Search',
    query: query,
    results: results
  };
}

function formatResultsForLLM(searchData) {
  let output = `# Web Search Results\n\n`;
  output += `**Query:** ${searchData.query}\n`;
  output += `**Provider:** ${searchData.provider}\n\n`;
  output += `---\n\n`;
  
  searchData.results.forEach(result => {
    if (result.type === 'knowledgeGraph') {
      output += `## Knowledge Panel: ${result.title}\n\n`;
      output += `${result.description}\n\n`;
      if (result.attributes && Object.keys(result.attributes).length > 0) {
        Object.entries(result.attributes).forEach(([key, value]) => {
          output += `- **${key}:** ${value}\n`;
        });
        output += '\n';
      }
    } else if (result.type === 'answerBox') {
      output += `## Featured Answer\n\n`;
      if (result.title) output += `**${result.title}**\n\n`;
      output += `${result.answer}\n\n`;
      if (result.link) {
        output += `Source: ${result.link}\n\n`;
      }
    } else if (result.type === 'organic') {
      output += `### ${result.position}. ${result.title}\n`;
      output += `URL: ${result.link}\n`;
      output += `${result.snippet}\n`;
      if (result.date) {
        output += `Date: ${result.date}\n`;
      }
      output += '\n';
    } else if (result.type === 'peopleAlsoAsk') {
      output += `## Related Questions\n\n`;
      result.questions.forEach(q => {
        output += `**Q: ${q.question}**\n`;
        output += `A: ${q.answer}\n`;
        if (q.link) output += `Source: ${q.link}\n`;
        output += '\n';
      });
    }
  });
  
  return output;
}

// Main plugin function
async function webSearch(params, userSettings) {
  const {
    query,
    searchProvider: overrideProvider,
    numResults: overrideNumResults
  } = params;

  const {
    searchProvider = 'serper',
    serperApiKey,
    braveApiKey,
    numResults = 10
  } = userSettings;

  // Allow params to override settings
  const provider = overrideProvider || searchProvider;
  const resultCount = Math.min(Math.max(overrideNumResults || numResults, 1), 20);

  if (!query || typeof query !== 'string' || query.trim() === '') {
    throw new Error('Please provide a valid search query.');
  }

  // Validate API keys
  if (provider === 'serper' && !serperApiKey) {
    throw new Error('Serper API key is required. Please configure it in the plugin settings.');
  }
  if (provider === 'brave' && !braveApiKey) {
    throw new Error('Brave Search API key is required. Please configure it in the plugin settings.');
  }

  // Perform search
  let searchData;
  if (provider === 'brave') {
    searchData = await searchWithBrave(query, braveApiKey, resultCount);
  } else {
    searchData = await searchWithSerper(query, serperApiKey, resultCount);
  }

  // Format and return results for the LLM to use
  return formatResultsForLLM(searchData);
}

// TypingMind Plugin Export
const plugin = {
  id: 'web-search-multi',
  name: 'Web Search (Serper/Brave)',
  description: 'Search the web using Serper.dev (Google) or Brave Search API. Returns results for the LLM to use in answering questions.',
  version: '1.0.0',
  
  // Plugin parameters (what the AI can pass to the function)
  parameters: {
    type: 'object',
    properties: {
      query: {
        type: 'string',
        description: 'The search query to look up on the web'
      },
      searchProvider: {
        type: 'string',
        enum: ['serper', 'brave'],
        description: 'Override the default search provider (optional)'
      },
      numResults: {
        type: 'number',
        description: 'Number of results to return (optional, default: 10)'
      }
    },
    required: ['query']
  },
  
  // User settings (configured in plugin settings UI)
  userSettings: [
    {
      name: 'searchProvider',
      label: 'Default Search Provider',
      type: 'select',
      options: [
        { label: 'Google (via Serper.dev)', value: 'serper' },
        { label: 'Brave Search', value: 'brave' }
      ],
      default: 'serper',
      description: 'Choose your preferred search provider'
    },
    {
      name: 'serperApiKey',
      label: 'Serper.dev API Key',
      type: 'password',
      description: 'Your API key from serper.dev (required for Google search)'
    },
    {
      name: 'braveApiKey',
      label: 'Brave Search API Key',
      type: 'password',
      description: 'Your API key from brave.com/search/api (required for Brave search)'
    },
    {
      name: 'numResults',
      label: 'Number of Results',
      type: 'number',
      default: 10,
      description: 'Default number of search results to return (1-20)'
    }
  ],
  
  // Main execution function
  action: webSearch
};

// Export for TypingMind
if (typeof module !== 'undefined' && module.exports) {
  module.exports = plugin;
}
