# VS Code Web Tool Deep Dive

Analysis of the implementation of web-related tools in VS Code and GitHub Copilot Chat.

## 1. Core Tool: Fetch Web Page (`@web`)

The "Fetch Web Page" tool is a native VS Code tool used to extract content from specific URIs.

### Implementation Details
- **Internal ID**: `vscode_fetchWebPage_internal` (found as `vut` in `workbench.desktop.main.js`).
- **Registration**: Registered in the `ILanguageModelToolsService` during workbench initialization.
- **Service Mapping**:
  - Code: `var _In = { id: vut, displayName: "Fetch Web Page", ... }`
  - Logic: Implemented by a class (minified as `AAt`).
- **Input Schema**:
  ```json
  {
    "type": "object",
    "properties": {
      "urls": {
        "type": "array",
        "items": { "type": "string" },
        "description": "The URLs to fetch content from."
      }
    },
    "required": ["urls"]
  }
  ```

### Capabilities
- **Web Content**: Supports `http` and `https` schemes. Uses `IWebContentExtractorService` (minified `kot`) to scrape text.
- **File System**: Supports other schemes (like `file://`), allowing the LLM to read local workspace files through the same interface.
- **Binary Detection**: Specifically detects image types (`.png`, `.jpg`, `.gif`, etc.) and returns them as `tooldata` (blob references).
- **Confirmation Flow**: Includes a complex confirmation contribution (`TAt`) that:
  - Validates URLs against trusted domains.
  - Checks chat history for previously mentioned URLs to minimize redundancy.
  - Provides "Auto-approval" flags (`allowAutoConfirm: true`).

---

## 2. Experimental Search Tool: `web_search`

While `@web` is for fetching known URLs, VS Code (via Copilot Chat) implements a specific search capability for certain models.

### Anthropic/Claude Implementation
Found in `github.copilot-chat` extension's `extension.js`:
- **Experiment Key**: `AnthropicWebSearchToolEnabled`.
- **Tool Type**: `web_search_20250305`.
- **Functionality**:
  - Dynamically injected into the tool list if the experiment is enabled.
  - Supports search parameters like `allowed_domains`, `blocked_domains`, and `user_location`.
  - Result format: `web_search_tool_result` containing an array of search results (URL, title, age, and encrypted content).

---

## 3. Tool Discovery Pattern in Bundles

Finding these tools in minified code requires searching for:
1.  **Constants**: `vscode_fetchWebPage_internal`, `web_search`.
2.  **Service IDs**: `LanguageModelToolsService` (ID `wr` in current version).
3.  **Registration Calls**: `this.D(e.registerTool(_In, n))`.

## 4. Interaction Model
1.  **Prompt Reference**: The user mentions `@web` or provides a URL.
2.  **Agent Logic**: The `Gi.Agent` (Agent Mode) determines if a tool call is needed.
3.  **Calling**: If a tool is called, the `ILanguageModelToolsService` executes the `invoke` method of the registered tool.
4.  **Content Extraction**: For `@web`, the extraction service parses the DOM and returns a text representation to the LLM.
5.  **Result Injection**: The result is appended to the message history, often with a "Tool Result" part.
