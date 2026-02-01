# VS Code Chat Tool System Architecture

This document details the tool calling system in VS Code's Copilot Chat.

## Overview

The tool system enables Language Models to invoke external capabilities (file operations, terminal commands, code analysis, etc.) during chat interactions.

## Core Interfaces

### Tool Definition

```javascript
// Tool metadata structure
{
  id: string,                 // Unique identifier
  displayName: string,        // UI display name  
  toolReferenceName: string,  // How users reference it (#toolname)
  tags: string[],             // Categorization
  userDescription: string,    // Help text for users
  modelDescription: string,   // Description sent to LLM
  inputSchema: object,        // JSON Schema for parameters
  source: object              // Extension providing the tool
}
```

### Tool Invocation Context

```javascript
{
  callId: string,           // Unique invocation ID
  toolId: string,           // Which tool to call
  parameters: object,       // Parsed arguments from LLM
  requestId: string,        // Parent chat request
  chatSessionResource: URI  // Session identifier
}
```

## Service Architecture

### ILanguageModelToolsService

The main tools service (`ye("ILanguageModelToolsService")`):

```javascript
interface ILanguageModelToolsService {
  // Tool discovery
  getTools(): Iterable<ToolData>;
  getTool(id: string): ToolData | undefined;
  
  // Tool invocation
  invokeTool(context: ToolContext, countTokens: TokenCounter, token: CancellationToken): Promise<ToolResult>;
  
  // Registration
  registerToolImplementation(id: string, impl: ToolImplementation): IDisposable;
  
  // Events
  onDidChangeTools: Event<void>;
}
```

### Tool Implementation Interface

```javascript
interface ToolImplementation {
  invoke(
    context: ToolContext,
    countTokens: (input: string, token: CancellationToken) => Promise<number>,
    progress: IProgress<ToolProgress>,
    token: CancellationToken
  ): Promise<ToolResult>;
  
  prepareToolInvocation?(
    context: PrepareToolContext, 
    token: CancellationToken
  ): Promise<PrepareResult | undefined>;
}
```

## Tool Invocation State Machine

### States

```javascript
nl.StateKind = {
  WaitingForConfirmation: 0,  // Awaiting user approval
  Executing: 1,               // Currently running
  WaitingForPostApproval: 2,  // Needs post-execution approval
  Completed: 3,               // Successfully finished
  Cancelled: 4                // User or system cancelled
}
```

### State Transitions

```
                    ┌─────────────────┐
                    │   Tool Called   │
                    └────────┬────────┘
                             │
            ┌────────────────▼───────────────┐
            │   WaitingForConfirmation (0)   │
            └────────────────┬───────────────┘
                             │
            ┌────────────────┼────────────────┐
            │                │                │
    ┌───────▼──────┐ ┌───────▼──────┐ ┌───────▼──────┐
    │  Approved    │ │   Denied     │ │   Timeout    │
    └───────┬──────┘ └───────┬──────┘ └───────┬──────┘
            │                │                │
    ┌───────▼──────┐         │        ┌───────▼──────┐
    │ Executing(1) │         │        │ Cancelled(4) │
    └───────┬──────┘         │        └──────────────┘
            │                │
    ┌───────▼──────┐         │
    │ Completed(3) │◄────────┘
    └──────────────┘
```

## Confirmation System

### Confirmation Types

```javascript
// Confirmation decision reasons
vZt = {
  Denied: 0,                 // User rejected
  ConfirmationNotNeeded: 1,  // Auto-approved tool
  Setting: 2,                // Approved via settings
  LmServicePerTool: 3,       // LM service approved
  UserAction: 4,             // User explicitly approved
  Skipped: 5                 // Confirmation skipped
}
```

### Auto-Approval Settings

```javascript
Ti.GlobalAutoApprove = "chat.tools.global.autoApprove"
Ti.AutoApproveEdits = "chat.tools.edits.autoApprove"
Ti.EligibleForAutoApproval = "chat.tools.eligibleForAutoApproval"
```

### Awaiting Confirmation

```javascript
async function awaitConfirmation(invocation, token) {
  // Check if already decided
  const confirmed = executionConfirmedOrDenied(invocation);
  if (confirmed) return Promise.resolve(confirmed);
  
  const disposables = new DisposableStore();
  return new Promise(resolve => {
    // Handle cancellation
    if (token) {
      disposables.add(token.onCancellationRequested(() => {
        resolve({ type: 0 }); // Denied
      }));
    }
    
    // Watch for state changes
    disposables.add(autorun(reader => {
      const result = executionConfirmedOrDenied(invocation, reader);
      if (result) {
        disposables.dispose();
        resolve(result);
      }
    }));
  }).finally(() => disposables.dispose());
}
```

## Tool Result Handling

### Result Structure

```javascript
{
  content: string | StructuredContent,  // Result data
  toolMetadata: {                       // Optional metadata
    // Tool-specific data
  }
}
```

### Serialization for IPC

Tool results cross the extension host boundary via `Qm` (Transferable wrapper):

```javascript
async $invokeTool(context, token) {
  const result = await this.toolsService.invokeTool(
    revive(context),
    (input, token) => this.a.$countTokensForInvocation(context.callId, input, token),
    token ?? CancellationToken.None
  );
  
  const dto = { content: result.content, toolMetadata: result.toolMetadata };
  
  // Check if needs transfer optimization
  return hasTransferables(result) ? new Qm(dto) : dto;
}
```

## Built-in Tool Categories

Based on code analysis, VS Code includes tools for:

1. **File Operations** - Read, write, search files
2. **Terminal** - Execute shell commands
3. **Code Analysis** - Find definitions, references
4. **Workspace** - Project-wide operations
5. **Diagnostics** - Error and warning access

## Extension Host Communication

### Main Thread Handler

```javascript
class MainThreadLanguageModelTools {
  constructor(context, toolsService) {
    this.proxy = context.getProxy(ExtHostLanguageModelTools);
    this.toolsService = toolsService;
    
    // Notify extension host of tool changes
    this.toolsService.onDidChangeTools(() => {
      this.proxy.$onDidChangeTools(this.getToolsDto());
    });
  }
  
  getToolsDto() {
    return Array.from(this.toolsService.getTools()).map(tool => ({
      id: tool.id,
      displayName: tool.displayName,
      toolReferenceName: tool.toolReferenceName,
      // ... other properties
    }));
  }
  
  async $invokeTool(context, token) {
    return await this.toolsService.invokeTool(revive(context), countTokens, token);
  }
}
```

### Extension Host Proxy

```javascript
class ExtHostLanguageModelTools {
  $onDidChangeTools(tools) {
    // Update local tool cache
    this.availableTools = tools;
    this.onDidChangeToolsEmitter.fire();
  }
  
  async $invokeTool(context, token) {
    const impl = this.toolImplementations.get(context.toolId);
    if (!impl) throw new Error(`Tool not found: ${context.toolId}`);
    
    return await impl.invoke(context, token);
  }
}
```

## Token Counting

Tools can request token counts to manage context limits:

```javascript
// During tool execution
async invoke(context, countTokens, progress, token) {
  const result = await this.doWork(context.parameters);
  
  // Check if result is too large
  const tokenCount = await countTokens(result, token);
  if (tokenCount > MAX_TOKENS) {
    return this.summarize(result);
  }
  
  return { content: result };
}
```

## Progress Reporting

Tools report progress during execution:

```javascript
interface ToolProgress {
  content: string;      // Progress message
  // Additional progress data
}

// Usage in tool implementation
async invoke(context, countTokens, progress, token) {
  progress.report({ content: "Starting analysis..." });
  
  await this.step1();
  progress.report({ content: "Step 1 complete" });
  
  await this.step2();
  progress.report({ content: "Finalizing..." });
  
  return { content: "Done" };
}
```

## Tool References in Chat Input

Users can reference tools with `#toolname` syntax:

```javascript
// Parsed input structure
class P0 {  // Tool part
  constructor(range, editorRange, toolName, toolId, displayName, icon) {
    this.kind = "tool";
    this.range = range;
    this.toolName = toolName;
    this.toolId = toolId;
    // ...
  }
  
  get promptText() {
    return `#${this.toolName}`;
  }
  
  toVariableEntry() {
    return {
      kind: "tool",
      id: this.toolId,
      name: this.toolName,
      range: this.range,
      value: undefined,
      icon: this.icon
    };
  }
}
```

## Toolsets

Tools can be grouped into toolsets:

```javascript
class cj {  // Toolset part
  constructor(range, editorRange, id, name, icon, tools) {
    this.kind = "toolset";
    this.id = id;
    this.name = name;
    this.tools = tools;  // Array of tool entries
  }
  
  toVariableEntry() {
    return {
      kind: "toolset",
      id: this.id,
      name: this.name,
      value: this.tools
    };
  }
}
```

## Summary

The VS Code tool system provides:

1. **Registration API** - Extensions register tool implementations
2. **Discovery** - Tools are visible in UI and to LLM
3. **Invocation** - LLM calls tools with structured parameters
4. **Confirmation** - User approval workflow with auto-approve options
5. **Progress** - Real-time status updates during execution
6. **Results** - Structured results returned to LLM context
7. **IPC** - Efficient cross-process communication via serialization
