# Copilot PowerShell Client

A PowerShell cmdlet that authenticates with GitHub Copilot using device flow and calls the chat completions API.

## Features

- 🔐 **Device Flow Auth** - Same authentication method VS Code uses
- 💾 **Token Caching** - GitHub token is cached for 30 days
- 📡 **Streaming Support** - Real-time streaming responses
- 📄 **Prompt Files** - Load complex prompts from JSON files

## Usage

### Simple Prompt

```powershell
.\Invoke-CopilotChat.ps1 -Prompt "Explain async/await in JavaScript"
```

### With Streaming

```powershell
.\Invoke-CopilotChat.ps1 -Prompt "Write a Python function to sort a list" -Stream
```

### From Prompt File

```powershell
.\Invoke-CopilotChat.ps1 -PromptFile .\sample-prompt.json
```

### Different Model

```powershell
.\Invoke-CopilotChat.ps1 -Prompt "Hello!" -Model "claude-3.5-sonnet"
```

## Prompt File Format

```json
{
    "model": "gpt-4o",
    "messages": [
        {
            "role": "system",
            "content": "You are a helpful coding assistant."
        },
        {
            "role": "user",
            "content": "Your question here"
        }
    ]
}
```

## Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `-Prompt` | Simple text prompt | - |
| `-PromptFile` | Path to JSON prompt file | - |
| `-Model` | Model to use | `gpt-4o` |
| `-TokenFile` | Where to cache GitHub token | `~/.copilot-token.json` |
| `-Stream` | Enable streaming output | `$false` |

## Authentication Flow

1. First run prompts you to visit `github.com/login/device`
2. Enter the displayed code
3. GitHub token is cached locally
4. Subsequent runs use the cached token

## Available Models

- `gpt-4o` - GPT-4 Omni
- `gpt-4o-mini` - GPT-4 Omni Mini  
- `claude-3.5-sonnet` - Claude 3.5 Sonnet
- `o1-preview` - OpenAI o1 Preview
- `o1-mini` - OpenAI o1 Mini

## Requirements

- PowerShell 5.1+ or PowerShell Core 7+
- GitHub account with Copilot subscription
