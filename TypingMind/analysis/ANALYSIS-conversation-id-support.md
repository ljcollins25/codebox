# Analysis: Adding Conversation ID Support to TypingMind

## Overview

This document analyzes what would be required to add support for passing conversation IDs to models in TypingMind, enabling stateful conversations with external APIs that support conversation persistence.

## Current State

### TypingMind Plugin Architecture

Currently, TypingMind plugins receive:
- `params` - Parameters passed by the LLM when invoking the plugin
- `userSettings` - User-configured settings for the plugin

Plugins do **not** currently receive:
- Conversation ID / Chat ID
- Message history
- Session metadata
- User identity information

### How TypingMind Manages Conversations

TypingMind maintains conversations locally:
1. Each conversation has a unique ID stored in the browser (IndexedDB/localStorage)
2. Message history is sent to the LLM API on each request
3. Plugins are stateless - they execute and return results without conversation context

## Use Cases for Conversation ID Support

### 1. Poe API Conversation Persistence
Poe's API supports `conversation_id` to maintain context across messages:
```javascript
{
  model: "GPT-4o",
  messages: [...],
  conversation_id: "conv_abc123"  // Optional: reuse existing conversation
}
```
This allows the bot to "remember" previous interactions without resending full history.

### 2. Multi-Turn Search/Research
A search plugin could build context over multiple queries:
- First search: "What is quantum computing?"
- Follow-up: "Tell me more about qubits" (bot remembers context)

### 3. Cost Optimization
Instead of sending full message history each time, pass conversation ID to let the provider manage state.

### 4. Provider-Specific Features
Some providers offer features tied to conversation IDs:
- Conversation analytics
- Context caching
- Memory/knowledge persistence

## Implementation Options

### Option 1: TypingMind Core Change (Recommended)

**What's needed:**
TypingMind would need to expose conversation context to plugins via an extended callback signature:

```javascript
// Current
async function pluginAction(params, userSettings) { }

// Proposed
async function pluginAction(params, userSettings, context) {
  const { conversationId, messageId, messageHistory } = context;
}
```

**Pros:**
- Clean, standardized approach
- All plugins benefit
- Consistent API

**Cons:**
- Requires TypingMind core update
- Breaking change for existing plugin API (though could be backward compatible)

**Effort:** Requires contribution to TypingMind or feature request to maintainers

### Option 2: Plugin-Managed State (Workaround)

Plugins can maintain their own conversation mapping using browser storage:

```javascript
// Generate/retrieve conversation ID based on TypingMind's internal state
function getOrCreateConversationId() {
  // Use a hash of the first message or timestamp as a pseudo-conversation ID
  const storageKey = 'plugin-conversation-map';
  const map = JSON.parse(localStorage.getItem(storageKey) || '{}');
  
  // Problem: No reliable way to identify current conversation
  // Could use: window.location.hash, DOM inspection, or message content hash
  
  return conversationId;
}
```

**Pros:**
- Works without TypingMind changes
- Plugin can implement immediately

**Cons:**
- Hacky - relies on implementation details that may change
- No guaranteed way to identify current conversation
- State can get out of sync
- Doesn't survive browser data clear

### Option 3: User-Provided Conversation ID

Add conversation ID as a user setting or parameter:

```javascript
// As a parameter the LLM can pass
parameters: {
  query: { type: 'string' },
  conversationId: { 
    type: 'string',
    description: 'Optional conversation ID for maintaining context'
  }
}

// Or as a user setting
userSettings: [
  {
    name: 'conversationId',
    label: 'Conversation ID',
    type: 'text',
    description: 'Manually set a conversation ID for this session'
  }
]
```

**Pros:**
- Simple to implement
- Works now

**Cons:**
- Poor UX - users must manage IDs manually
- LLM may not consistently pass the ID
- Doesn't automatically track conversations

### Option 4: Hybrid - Plugin Requests Context

Plugin could attempt to access TypingMind's internal state:

```javascript
async function webSearch(params, userSettings) {
  // Try to access TypingMind's conversation context
  // This depends on TypingMind's internal implementation
  const tmContext = window.__TYPINGMIND_CONTEXT__ || {};
  const conversationId = tmContext.currentConversationId;
  
  // Fall back to generating our own
  if (!conversationId) {
    // Generate based on available info
  }
}
```

**Pros:**
- Could work if TypingMind exposes context
- Graceful fallback

**Cons:**
- Depends on undocumented internals
- May break with updates

### Option 5: TypingMind Extensions (Best Workaround)

TypingMind supports **Extensions** - custom JavaScript code that runs when the app loads with full access to browser storage.

**Key Capabilities:**
- Full access to **localStorage** (app settings/preferences)
- Full access to **IndexedDB** (chat messages and user-generated data)
- DOM access via `data-element-id` attributes
- Extensions sync across devices (on paid plans)
- Code runs once at app start

**Implementation Approach:**

An extension can expose conversation context to plugins via a global object:

```javascript
// Extension code - runs at app start
(async function() {
  // Open TypingMind's IndexedDB
  const dbRequest = indexedDB.open('TypingMindDB'); // Actual DB name may vary
  
  dbRequest.onsuccess = function(event) {
    const db = event.target.result;
    
    // Create global context object for plugins to access
    window.__TM_EXTENSION_CONTEXT__ = {
      db: db,
      
      // Helper to get current conversation ID
      getCurrentConversationId: function() {
        // Parse from URL hash or DOM
        const hash = window.location.hash;
        const match = hash.match(/chat\/([a-zA-Z0-9-]+)/);
        return match ? match[1] : null;
      },
      
      // Helper to get conversation data
      getConversation: async function(conversationId) {
        return new Promise((resolve, reject) => {
          const transaction = db.transaction(['chats'], 'readonly');
          const store = transaction.objectStore('chats');
          const request = store.get(conversationId);
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
      },
      
      // Get message history for a conversation
      getMessages: async function(conversationId) {
        const conv = await this.getConversation(conversationId);
        return conv?.messages || [];
      }
    };
    
    console.log('TypingMind Extension: Context API initialized');
  };
})();
```

**Plugin Usage:**

```javascript
async function webSearch(params, userSettings) {
  // Access extension-provided context
  const ctx = window.__TM_EXTENSION_CONTEXT__;
  
  if (ctx) {
    const conversationId = ctx.getCurrentConversationId();
    const messages = await ctx.getMessages(conversationId);
    
    // Now we have full conversation context!
    console.log('Conversation ID:', conversationId);
    console.log('Message count:', messages.length);
  }
  
  // ... proceed with search
}
```

**Pros:**
- Works today without TypingMind core changes
- Full access to all conversation data
- Can provide rich context (messages, metadata, etc.)
- Clean separation: extension provides context, plugin uses it
- Syncs across devices

**Cons:**
- Requires user to install both extension AND plugin
- IndexedDB schema is undocumented and may change
- Extension must be kept in sync with TypingMind updates
- Requires investigation to discover actual DB structure

**How to Add Extensions:**
1. Go to TypingMind Settings → Extensions
2. Add new extension with custom JavaScript code
3. The code runs once when the app loads

**Important Note:** 
Per TypingMind documentation: *"We don't have a documented data model, and it can change from time to time. Please use at your own risk."*

This means the extension code may need updates if TypingMind changes its internal data structure.

## Recommended Approach

### Short-term (Plugin-side)

1. **Add optional `conversationId` parameter** that the LLM can pass:
```javascript
parameters: {
  query: { type: 'string', required: true },
  conversationId: { type: 'string', description: 'Conversation ID for context' }
}
```

2. **Store provider conversation IDs** mapped to plugin-generated session IDs:
```javascript
const sessionId = params.conversationId || generateSessionId();
const providerConvId = await getOrCreateProviderConversation(sessionId);
```

3. **Include conversation ID in API calls** when the provider supports it:
```javascript
body: JSON.stringify({
  model: searchBot,
  messages: [{ role: 'user', content: query }],
  conversation_id: providerConvId,  // Poe-specific
  stream: false
})
```

### Long-term (TypingMind Feature Request)

Submit a feature request to TypingMind to expose conversation context to plugins:

**Proposed Plugin Context API:**
```typescript
interface PluginContext {
  // Conversation info
  conversationId: string;
  conversationTitle?: string;
  
  // Message context
  currentMessageId: string;
  messageHistory: Array<{
    role: 'user' | 'assistant' | 'system';
    content: string;
    timestamp: number;
  }>;
  
  // Optional: Full conversation metadata
  metadata?: {
    createdAt: number;
    model: string;
    tags?: string[];
  };
}

// Plugin signature
type PluginAction = (
  params: Record<string, any>,
  userSettings: Record<string, any>,
  context: PluginContext
) => Promise<string>;
```

## Implementation for Web Search Plugin

Here's how we could update the current plugin to support conversation IDs:

```javascript
// Add to plugin parameters
parameters: {
  type: 'object',
  properties: {
    query: {
      type: 'string',
      description: 'The search query'
    },
    conversationId: {
      type: 'string', 
      description: 'Optional: Conversation ID for maintaining search context across queries'
    }
  },
  required: ['query']
}

// Update performWebSearch
async function performWebSearch(query, apiKey, apiBaseUrl, searchBot, conversationId) {
  const body = {
    model: searchBot,
    messages: [{ role: 'user', content: query }],
    stream: false
  };
  
  // Add conversation_id if provided (Poe API supports this)
  if (conversationId) {
    body.conversation_id = conversationId;
  }
  
  const response = await fetch(`${apiBaseUrl}/chat/completions`, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${apiKey}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(body)
  });
  
  // ... rest of implementation
}
```

## Poe API Specifics

According to Poe's API documentation, conversation persistence works as follows:

1. **First request** - Don't include `conversation_id`, API returns one in response
2. **Subsequent requests** - Include the returned `conversation_id` to continue conversation
3. **New conversation** - Omit `conversation_id` to start fresh

The response includes:
```json
{
  "id": "chatcmpl-xxx",
  "conversation_id": "conv_abc123",
  "choices": [...]
}
```

## Conclusion

| Approach | Effort | Reliability | UX | Works Today |
|----------|--------|-------------|-----|-------------|
| TypingMind core change | High | High | Best | No |
| Plugin-managed state | Medium | Low | Medium | Yes |
| User-provided ID | Low | High | Poor | Yes |
| Hybrid (undocumented APIs) | Medium | Medium | Medium | Maybe |
| **Extensions + Plugin** | Medium | Medium-High | Good | **Yes** |

**Recommendation:** 
1. **Immediate:** Implement an Extension that exposes conversation context to a global object
2. **Plugin:** Update the plugin to check for extension-provided context, with fallback to `conversationId` parameter
3. **Long-term:** Submit a feature request to TypingMind for native conversation context support
4. Design the plugin to gracefully upgrade when TypingMind adds native support

**Why Extensions are the Best Current Solution:**
- Provides actual conversation IDs from TypingMind's data store
- Clean separation of concerns (extension provides context, plugin consumes it)
- Works today without waiting for TypingMind updates
- Can be incrementally improved as we learn more about the IndexedDB schema

## Next Steps

1. [ ] Investigate TypingMind's IndexedDB schema (store names, key structure)
2. [ ] Create a minimal extension that exposes conversation context
3. [ ] Add `conversationId` parameter to plugin schema as fallback
4. [ ] Update plugin to use extension context when available
5. [ ] Update API calls to include conversation_id when available
6. [ ] Test with Poe API conversation persistence
7. [ ] Document usage for end users (extension + plugin installation)
8. [ ] Submit TypingMind feature request for native support

## Appendix: Investigating IndexedDB Schema

To discover the actual IndexedDB structure, run this in the browser console on TypingMind:

```javascript
// List all IndexedDB databases
const databases = await indexedDB.databases();
console.log('Databases:', databases);

// For each database, list object stores
for (const dbInfo of databases) {
  const request = indexedDB.open(dbInfo.name);
  request.onsuccess = (event) => {
    const db = event.target.result;
    console.log(`\nDatabase: ${dbInfo.name}`);
    console.log('Object Stores:', Array.from(db.objectStoreNames));
    db.close();
  };
}
```

This will reveal the actual database and store names used by TypingMind, which can then be used in the extension code.
