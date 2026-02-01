# VS Code Language Model Integration

This document details how VS Code integrates with Language Models (LLMs) for chat functionality.

## Service Architecture

### ILanguageModelsService

Main service identifier: `ye("ILanguageModelsService")`

```javascript
interface ILanguageModelsService {
  // Model discovery
  getLanguageModelIds(): string[];
  lookupLanguageModel(id: string): LanguageModelInfo | undefined;
  selectLanguageModels(selector: ModelSelector): Promise<string[]>;
  
  // Model provider registration
  registerLanguageModelProvider(vendor: string, provider: LanguageModelProvider): IDisposable;
  
  // Chat requests
  sendChatRequest(modelId: string, extensionId: ExtensionIdentifier, messages: ChatMessage[], options: ChatRequestOptions, token: CancellationToken): Promise<ChatResponse>;
  
  // Token counting
  computeTokenLength(modelId: string, input: string, token: CancellationToken): Promise<number>;
  
  // Model preferences
  updateModelPickerPreference(modelId: string, visible: boolean): void;
  
  // Events
  onDidChangeLanguageModels: Event<string>;
}
```

### Model Metadata

```javascript
interface LanguageModelInfo {
  id: string;              // Unique identifier
  name: string;            // Display name
  vendor: string;          // Provider (e.g., "copilot")
  family: string;          // Model family (e.g., "gpt-4")
  version: string;         // Model version
  tokens: number;          // Context window size
  isUserSelectable: boolean;
  capabilities: {
    agentMode?: boolean;   // Supports agent mode
    toolCalling?: boolean; // Supports tools
  };
}
```

## Provider Registration

### Extension Point

Language model providers are registered via extension point:

```javascript
Kks = extensionPoints.registerExtensionPoint({
  extensionPoint: "languageModelChatProviders",
  jsonSchema: {
    description: "Language model chat providers",
    oneOf: [zXt, { type: "array", items: zXt }]
  },
  activationEventsGenerator: function*(values) {
    for (const value of values) {
      yield `onLanguageModelChatProvider:${value.vendor}`;
    }
  }
});
```

### Provider Interface

```javascript
interface LanguageModelProvider {
  onDidChange: Event<void>;
  
  provideLanguageModelChatInfo(
    options: { silent?: boolean },
    token: CancellationToken
  ): Promise<LanguageModelChatInfo[]>;
  
  sendChatRequest(
    modelId: string,
    fromExtensionId: ExtensionIdentifier,
    messages: ChatMessage[],
    options: ChatRequestOptions,
    token: CancellationToken
  ): Promise<ChatResponse>;
  
  provideTokenCount(
    modelId: string,
    input: string,
    token: CancellationToken
  ): Promise<number>;
}
```

## Chat Message Format

### Message Types

```javascript
// Message roles
UXt = {
  System: 0,
  User: 1,
  Assistant: 2
}

// Content types  
wM = {
  Assistant: 0,
  User: 1,
  Extension: 2
}
```

### Message Content

```javascript
interface ChatMessageContent {
  type: "text" | "image_url" | "tool_call" | "tool_result";
  
  // For text
  text?: string;
  
  // For images
  image_url?: {
    data: Uint8Array;
    mimeType: "image/png" | "image/jpeg" | "image/gif" | "image/webp" | "image/bmp";
  };
  
  // For tool calls
  toolCallId?: string;
  functionName?: string;
  parameters?: object;
  
  // For tool results
  result?: string;
}
```

## Request/Response Flow

### Sending a Request

```javascript
async sendChatRequest(modelId, extensionId, messages, options, token) {
  const provider = this.providers.get(this.models.get(modelId)?.vendor || "");
  if (!provider) {
    throw new Error(`Chat provider for model ${modelId} is not registered.`);
  }
  
  return provider.sendChatRequest(modelId, extensionId, messages, options, token);
}
```

### Response Streaming

```javascript
interface ChatResponse {
  result: Promise<void>;                    // Completion promise
  stream: AsyncIterable<ChatResponsePart>;  // Streaming parts
}

// Response parts
type ChatResponsePart = 
  | { type: "text"; text: string }
  | { type: "tool_call"; toolCallId: string; functionName: string; parameters: object }
  | { type: "usage"; promptTokens: number; completionTokens: number };
```

## Extension Host Communication

### Main Thread Handler

```javascript
class MainThreadLanguageModels {
  constructor(context, languageModelsService, logService) {
    this.proxy = context.getProxy(ExtHostChatProvider);
    this.service = languageModelsService;
    
    // Forward model changes to extension host
    this.service.onDidChangeLanguageModels((vendor) => {
      this.proxy.$onLMProviderChange(vendor);
    });
  }
  
  async $tryStartChatRequest(extensionId, modelId, requestId, messages, options, token) {
    let response;
    try {
      response = await this.service.sendChatRequest(modelId, extensionId, messages.value, options, token);
    } catch (error) {
      this.logService.error("[CHAT] request FAILED", extensionId.value, requestId, error);
      throw error;
    }
    
    // Stream response parts to extension host
    (async () => {
      try {
        for await (const part of response.stream) {
          await this.proxy.$acceptResponsePart(requestId, new Transferable(part));
        }
        this.proxy.$acceptResponseDone(requestId, undefined);
      } catch (error) {
        this.proxy.$acceptResponseDone(requestId, serializeError(error));
      }
    })();
    
    // Also wait for overall completion
    Promise.allSettled([response.result]).then(
      () => this.logService.debug("[CHAT] extension request DONE"),
      (error) => this.logService.error("[CHAT] extension request ERRORED", error)
    );
  }
}
```

### Extension Host Proxy

```javascript
// Methods called by extension host
$registerLanguageModelProvider(vendor: string): void;
$unregisterProvider(vendor: string): void;
$selectChatModels(selector: ModelSelector): Promise<string[]>;
$tryStartChatRequest(extensionId, modelId, requestId, messages, options, token): void;
$countTokens(modelId, input, token): Promise<number>;
$reportResponsePart(requestId, part): void;
$reportResponseDone(requestId, error?): void;
```

## Model Selection

### Selector Interface

```javascript
interface ModelSelector {
  vendor?: string;   // Filter by provider
  family?: string;   // Filter by model family
  version?: string;  // Filter by version
  id?: string;       // Match specific model
}
```

### Selection Logic

```javascript
async selectLanguageModels(selector, interactive) {
  // Activate providers if specific vendor requested
  if (selector.vendor) {
    await this.resolveModels(selector.vendor, !interactive);
  } else {
    const vendors = Array.from(this.vendorRegistry.keys());
    await Promise.all(vendors.map(v => this.resolveModels(v, !interactive)));
  }
  
  // Filter models by selector criteria
  const matches = [];
  for (const [modelId, metadata] of this.models) {
    if (selector.vendor !== undefined && metadata.vendor !== selector.vendor) continue;
    if (selector.family !== undefined && metadata.family !== selector.family) continue;
    if (selector.version !== undefined && metadata.version !== selector.version) continue;
    if (selector.id !== undefined && metadata.id !== selector.id) continue;
    
    matches.push(modelId);
  }
  
  return matches;
}
```

## Model Capabilities

### Agent Mode Check

```javascript
function suitableForAgentMode(model) {
  // Default to supporting agent mode unless explicitly disabled
  const agentSupported = typeof model.capabilities?.agentMode === "undefined" 
    || model.capabilities.agentMode;
    
  // Must support tool calling
  const toolsSupported = !!model.capabilities?.toolCalling;
  
  return agentSupported && toolsSupported;
}
```

### Qualified Name Matching

```javascript
function asQualifiedName(model) {
  return `${model.name} (${model.vendor})`;
}

function matchesQualifiedName(name, model) {
  // Copilot vendor uses short names
  if (model.vendor === "copilot" && name === model.name) {
    return true;
  }
  return name === asQualifiedName(model);
}
```

## User Preferences

### Model Picker Preferences

```javascript
updateModelPickerPreference(modelId, visible) {
  const model = this.models.get(modelId);
  if (!model) {
    this.logService.warn(`Cannot update preference for unknown model ${modelId}`);
    return;
  }
  
  this.preferences[modelId] = visible;
  
  // Persist or clear preference
  if (visible === model.isUserSelectable) {
    delete this.preferences[modelId];
  }
  
  this.storageService.store("chatModelPickerPreferences", this.preferences, StorageScope.PROFILE);
  this.onDidChangeLanguageModelsEmitter.fire(model.vendor);
}
```

### Enterprise Restrictions

```javascript
// Reset preferences for business/enterprise users
if (this.entitlementService.entitlement === Entitlement.Business ||
    this.entitlementService.entitlement === Entitlement.Enterprise) {
  if (!this.entitlementService.isInternal) {
    this.preferences = {};
    this.storageService.store("chatModelPickerPreferences", this.preferences, StorageScope.PROFILE);
  }
}
```

## Authentication Integration

### Provider Authentication

```javascript
class AuthenticatedLanguageModelProvider {
  constructor(id, label, accountLabel) {
    this.id = id;
    this.label = label;
    this.accountLabel = accountLabel;
  }
  
  async getSessions(scopes) {
    if (scopes === undefined && !this.session) return [];
    return this.session ? [this.session] : [await this.createSession(scopes || [])];
  }
  
  async createSession(scopes) {
    this.session = {
      id: "fake-session",
      account: { id: this.id, label: this.accountLabel },
      accessToken: "fake-access-token",
      scopes: scopes
    };
    this.onDidChangeSessionsEmitter.fire({ added: [this.session], changed: [], removed: [] });
    return this.session;
  }
}
```

## Summary

The Language Model integration provides:

1. **Provider Registration** - Extensions register LLM backends
2. **Model Discovery** - Available models exposed to UI and extensions
3. **Request Handling** - Streaming chat request/response
4. **IPC Protocol** - Efficient cross-process communication
5. **Capability Detection** - Feature checks for agent/tool support
6. **User Preferences** - Model visibility customization
7. **Authentication** - Provider-specific auth handling
