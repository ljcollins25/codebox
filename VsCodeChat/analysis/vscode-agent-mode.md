# VS Code Agent Mode Architecture

This document details how VS Code's Agent Mode works, based on analysis of the bundled JavaScript code in `workbench.desktop.main.js`.

## Overview

Agent Mode is one of three chat modes in VS Code Copilot:
- **Ask** (`Gi.Ask`) - Simple Q&A mode
- **Edit** (`Gi.Edit`) - Code editing mode  
- **Agent** (`Gi.Agent`) - Autonomous agent mode with tool calling

## Key Components

### 1. Chat Modes Enum

```javascript
var Gi;
(function(i){
  i.Ask="ask"
  i.Edit="edit"
  i.Agent="agent"
})(Gi||(Gi={}));
```

### 2. Core Services

| Service | Identifier | Purpose |
|---------|------------|---------|
| Chat Service | `ye("IChatService")` | Main chat session management |
| Agent Service | `ye("chatAgentService")` | Agent registration and invocation |
| Widget Service | `ye("chatWidgetService")` | UI widget management |
| Language Models | `ye("ILanguageModelsService")` | LLM provider integration |
| Tools Service | `ye("ILanguageModelToolsService")` | Tool registration and execution |
| Editing Service | `ye("chatEditingService")` | Code editing session management |

### 3. Context Keys

Context keys control UI state and command availability:

```javascript
ne.chatModeKind = new U("chatAgentKind", Gi.Ask)      // Current mode
ne.enabled = new U("chatIsEnabled", false)            // Chat availability
ne.requestInProgress = new U("chatSessionRequestInProgress", false)
ne.chatToolCount = new U("chatToolCount", 0)          // Active tools
ne.hasToolConfirmation = new U("chatHasToolConfirmation", false)
```

### 4. Agent Mode Detection

A model is suitable for agent mode if it supports tool calling:

```javascript
function suitableForAgentMode(model) {
  return (typeof model.capabilities?.agentMode === "undefined" || 
          model.capabilities.agentMode) && 
         !!model.capabilities?.toolCalling
}
```

### 5. Configuration Settings

```javascript
Ti.AgentEnabled = "chat.agent.enabled"           // Enable agent mode
Ti.ExtensionToolsEnabled = "chat.extensionTools.enabled"
Ti.AutoApproveEdits = "chat.tools.edits.autoApprove"
Ti.GlobalAutoApprove = "chat.tools.global.autoApprove"
Ti.CheckpointsEnabled = "chat.checkpoints.enabled"
Ti.ThinkingStyle = "chat.agent.thinkingStyle"
```

## Agent Invocation Flow

### 1. Request Processing

When a user sends a message in agent mode:

```
User Input → Chat Widget → Chat Service → Agent Service → LLM Provider
                                              ↓
                                        Tool Invocations ← Response Stream
```

### 2. Agent Implementation Registration

Extensions register agents via:

```javascript
this.t.registerAgentImplementation(agentId, {
  invoke: async (request, progress, history, token) => {
    // Agent logic here
    return { /* result */ }
  },
  setRequestTools: (requestId, tools) => { },
  provideFollowups: async (session, result, history, token) => [],
  provideChatTitle: (history, token) => Promise.resolve("Title"),
  provideChatSummary: (history, token) => Promise.resolve("Summary")
})
```

### 3. Tool Invocation Pattern

```javascript
// Tool invocation state machine
nl.StateKind = {
  WaitingForConfirmation: 0,
  Executing: 1,
  WaitingForPostApproval: 2,
  Completed: 3,
  Cancelled: 4
}

// Check if confirmed
function executionConfirmedOrDenied(invocation, reader) {
  const state = invocation.state.read(reader);
  if (state.type !== 0) {
    return state.type === 4 
      ? { type: state.reason } 
      : state.confirmed;
  }
}
```

## Tool System Architecture

### 1. Tool Registration

Tools are registered through the extension API:

```javascript
$registerTool(toolId) {
  const implementation = this.f.registerToolImplementation(toolId, {
    invoke: async (context, countTokens, progress, token) => {
      return await this.a.$invokeTool(context, token);
    },
    prepareToolInvocation: (context, token) => {
      return this.a.$prepareToolInvocation(toolId, context, token);
    }
  });
  this.b.set(toolId, implementation);
}
```

### 2. Tool Result Handling

```javascript
// Tool result structure
{
  content: "result string or structured data",
  toolMetadata: { /* additional metadata */ }
}

// Serialization for IPC
hxs(result) ? new Qm(result) : result
```

### 3. Tool Confirmation Flow

```javascript
// Wait for user confirmation
async function awaitConfirmation(invocation, token) {
  const confirmed = executionConfirmedOrDenied(invocation);
  if (confirmed) return Promise.resolve(confirmed);
  
  return new Promise(resolve => {
    // Watch for state changes
    Le(reader => {
      const result = executionConfirmedOrDenied(invocation, reader);
      if (result) resolve(result);
    });
  });
}
```

## Main Thread ↔ Extension Host Communication

### 1. Protocol Classes

```javascript
// Main thread side
class MainThreadChatAgents2 {
  // Handles agent invocation from extension host
  async $registerAgent(handle, extensionId, name, metadata, isDynamic) { }
  async $handleProgressChunk(requestId, progress) { }
  async $invokeAgent(handle, request, context, token) { }
}

// Extension host side (proxy)
class ExtHostChatAgents2 {
  $acceptFeedback(handle, result, action) { }
  $detectChatParticipant(handle, request, history, context, token) { }
}
```

### 2. Method Naming Convention

- `$methodName` - Called across process boundary
- Methods without `$` - Local to the process

## Chat Session Management

### 1. Session Model

```javascript
class ChatModel {
  constructor(options) {
    this.sessionId = options.sessionId || generateId();
    this.sessionResource = options.resource;
    this.requests = [];  // ChatRequestModel[]
    this.inputModel = new InputModel(options.inputState);
  }
  
  // Observables for reactive UI
  lastRequestObs = computed(() => this.requests.at(-1));
  requestInProgress = this.lastRequestObs.map(r => r?.response?.isInProgress ?? false);
  requestNeedsInput = this.lastRequestObs.map(r => r?.response?.isPendingConfirmation);
}
```

### 2. Editing Session Integration

```javascript
startEditingSession(isGlobal, transferSession) {
  this.editingSession = isGlobal 
    ? this.editingService.startOrContinueGlobalEditingSession(this)
    : this.editingService.createEditingSession(this);
    
  // Track request disablement
  Le(reader => {
    this.updateDisablement(this.editingSession.requestDisablement.read(reader));
  });
}
```

## Response Processing

### 1. Streaming Response Parts

Response content types:
- `textEditGroup` - Code changes
- `progressTask` - Long-running operations
- `toolInvocation` - Tool calls
- `inlineReference` - Code references
- `codeblockUri` - Generated code blocks

### 2. Progress Handling

```javascript
async $handleProgressChunk(requestId, progress) {
  const pending = this.n.get(requestId);
  if (!pending) return;
  
  for (const [part, taskId] of progress) {
    switch (part.kind) {
      case "notebookEdit":
        // Handle notebook edits
        break;
      case "textEdit":
      case "codeblockUri":
        part.uri = this.canonicalizer.asCanonicalUri(part.uri);
        break;
      case "progressTask":
        this.r.set(`${requestId}_${taskId}`, new ProgressTask(part.content));
        break;
    }
    pending.progress(part);
  }
}
```

## Entitlement System

Agent mode availability depends on user entitlements:

```javascript
// Entitlement levels
es.Free = 5       // Limited features
es.Pro = 6        // Full features
es.ProPlus = 7    // Enhanced features
es.Business = 8   // Organization features
es.Enterprise = 9 // Enterprise features

// Check if premium
function isPremium(entitlement) {
  return entitlement === es.Pro || 
         entitlement === es.ProPlus ||
         entitlement === es.Business || 
         entitlement === es.Enterprise;
}
```

## Menu Contributions

Agent mode adds menu items to:
- `ChatModePicker` - Mode selection dropdown
- `ChatInputAttachmentToolbar` - Attachment actions
- `ChatConfirmationMenu` - Tool confirmation dialogs
- `ChatEditingWidgetToolbar` - Editing session actions

## Summary

Agent Mode is implemented through:
1. **Service layer** - Chat, Agent, Tools, and LLM services
2. **Context keys** - Control UI state and command availability
3. **IPC protocol** - Main thread ↔ Extension host communication
4. **Observable state** - Reactive updates via `Oe()` and `Le()` primitives
5. **Tool confirmation** - State machine for user approval workflow
6. **Entitlements** - Feature gating based on subscription level
