# TypingMind Tool Result Message Ordering Bug

## Summary
When using TypingMind with Claude models via the Copilot proxy, tool calls fail with an Anthropic API validation error because TypingMind drops the assistant's `tool_use` message from conversation history.

## Error Message
```
messages.0.content.1: unexpected `tool_use_id` found in `tool_result` blocks: toolu_vrtx_01VaFVboEG1WuJyCQg2goyao. 
Each `tool_result` block must have a corresponding `tool_use` block in the previous message.
```

## Root Cause
The Anthropic API requires this exact message sequence for tool calling:

```
1. user       → (user prompt)
2. assistant  → (tool_use block with id)
3. user       → (tool_result block referencing that id)
4. assistant  → (final response)
```

TypingMind is sending:
```json
{
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "Can you put a Monaco editor into the web app builder" },
    { "role": "tool", "tool_call_id": "toolu_vrtx_01VaFVboEG1WuJyCQg2goyao", "content": "..." },
    { "role": "user", "content": "Test" }
  ]
}
```

**Missing**: The `assistant` message between the first `user` and the `tool` message that would contain:
```json
{
  "role": "assistant",
  "content": [
    {
      "type": "tool_use",
      "id": "toolu_vrtx_01VaFVboEG1WuJyCQg2goyao",
      "name": "render_web_app",
      "input": { ... }
    }
  ]
}
```

## Why This Happens
TypingMind's `render_html` / `render_web_app` plugin likely:
1. Intercepts the assistant's tool_use response
2. Renders the HTML directly to the user
3. Discards/doesn't persist the assistant message containing the tool_use
4. Only stores the tool_result

When the user sends a follow-up message, the conversation history is incomplete.

## Potential Fixes

### 1. TypingMind Fix (Recommended)
TypingMind needs to preserve the assistant message containing tool_use blocks in conversation history.

### 2. Proxy-Level Fix (Workaround)
In `copilot-proxy`, reconstruct missing tool_use blocks by:
- Detecting orphaned tool_result messages
- Inserting a synthetic assistant message with the corresponding tool_use

```typescript
function fixToolResultOrdering(messages: Message[]): Message[] {
  const fixed: Message[] = [];
  for (const msg of messages) {
    if (msg.role === 'tool' && msg.tool_call_id) {
      // Check if previous message is assistant with matching tool_use
      const prev = fixed[fixed.length - 1];
      if (prev?.role !== 'assistant' || !hasToolUse(prev, msg.tool_call_id)) {
        // Insert synthetic assistant message
        fixed.push({
          role: 'assistant',
          content: [{
            type: 'tool_use',
            id: msg.tool_call_id,
            name: msg.name || 'unknown_tool',
            input: {}
          }]
        });
      }
    }
    fixed.push(msg);
  }
  return fixed;
}
```

### 3. User Workaround
Clear conversation history in TypingMind after tool use completes.

## Evidence
Captured request from Azure Blob Storage:
- Blob: `copilot/requests/2026-02-03T18-16-18-454Z-request.json`
- Shows the malformed message array with orphaned tool_result

## Related Files
- `cloudflare-workers/copilot-proxy/src/copilot.ts` - Proxy that could implement fix
- `cloudflare-workers/url-proxy/src/index.ts` - Logging proxy that captured the evidence
