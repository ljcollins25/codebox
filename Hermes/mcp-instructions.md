# Hermes as MCP Server

This document describes how to use Hermes as an MCP (Model Context Protocol) server for AI assistants.

## Overview

Hermes can operate as an MCP server, allowing AI assistants (like Claude, GitHub Copilot, etc.) to execute filesystem, process, and system operations through a standardized protocol.

## Server Modes

### HTTP Mode (Default)

Run the HTTP server:

```bash
hermes-server
# or with custom URL
hermes-server --urls http://localhost:5000
```

The server exposes:
- `POST /execute` - Execute a Verb
- `GET /verbs` - List available Verbs
- `GET /schema/{verb}` - Get Verb schema
- `GET /health` - Health check

### Stdio Mode

Run in stdio mode for direct process communication:

```bash
hermes-server --stdio [workspace-path]
```

In stdio mode:
- Requests are sent as JSON lines on stdin
- Responses are returned as JSON lines on stdout
- Diagnostic messages go to stderr

## CLI MCP Mode

The CLI can also serve as an MCP tool:

```bash
hermes --mcp [workspace-path]
```

Or use the execute command for single operations:

```bash
hermes execute '{"verb":"help","arguments":{}}'
hermes execute < request.json
```

## MCP Configuration

### VS Code / GitHub Copilot

Add to your `.vscode/mcp.json` or user settings:

```json
{
  "servers": {
    "hermes": {
      "type": "stdio",
      "command": "hermes-server",
      "args": ["--stdio", "${workspaceFolder}"]
    }
  }
}
```

Or using the CLI:

```json
{
  "servers": {
    "hermes": {
      "type": "stdio", 
      "command": "hermes",
      "args": ["--mcp", "${workspaceFolder}"]
    }
  }
}
```

### Claude Desktop

Add to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "hermes": {
      "command": "hermes-server",
      "args": ["--stdio", "/path/to/workspace"]
    }
  }
}
```

### HTTP Integration

For HTTP-based MCP integration, start the server and configure the client to use the HTTP endpoint:

```json
{
  "servers": {
    "hermes": {
      "type": "http",
      "url": "http://localhost:5000"
    }
  }
}
```

## Protocol Format

### Request Format

```json
{
  "verb": "fs.read-file",
  "arguments": {
    "path": "/path/to/file.txt"
  }
}
```

With request ID (for correlation):

```json
{
  "id": "request-123",
  "verb": "fs.read-file", 
  "arguments": {
    "path": "/path/to/file.txt"
  }
}
```

### Response Format

Success:

```json
{
  "succeeded": true,
  "result": {
    "content": "file contents here"
  }
}
```

With request ID:

```json
{
  "id": "request-123",
  "result": {
    "succeeded": true,
    "result": {
      "content": "file contents here"
    }
  }
}
```

Error:

```json
{
  "succeeded": false,
  "errorMessage": "File not found: /path/to/file.txt"
}
```

## Available Verbs

Use the `help` verb to discover available operations:

```json
{"verb": "help", "arguments": {}}
```

### Filesystem Verbs

| Verb | Description |
|------|-------------|
| `fs.read-file` | Read file contents |
| `fs.write-file` | Write content to a file |
| `fs.list-directory` | List directory contents |
| `fs.file-info` | Get file metadata |
| `fs.create-directory` | Create a directory |
| `fs.delete` | Delete a file or directory |
| `fs.move` | Move/rename a file or directory |
| `fs.copy` | Copy a file or directory |

### Process Verbs

| Verb | Description |
|------|-------------|
| `proc.run` | Execute a command |
| `proc.which` | Find executable in PATH |
| `proc.shell` | Run a shell command |

### System Verbs

| Verb | Description |
|------|-------------|
| `sys.get-environment` | Get environment variable |
| `sys.set-environment` | Set environment variable |
| `sys.time` | Get current time |
| `sys.hostname` | Get system hostname |

### Help Verb

| Verb | Description |
|------|-------------|
| `help` | List verbs or get help for a specific verb |

## Security Considerations

⚠️ **Important**: Hermes provides powerful system access capabilities. When deploying:

1. **Network Security**: In HTTP mode, ensure the server is not exposed to untrusted networks
2. **Workspace Isolation**: Use workspace-specific output directories
3. **Command Restrictions**: Consider limiting `proc.run` and `proc.shell` in production
4. **Authentication**: Add authentication layer for production HTTP deployments

## Examples

### Read a File

```json
{
  "verb": "fs.read-file",
  "arguments": {
    "path": "src/main.ts"
  }
}
```

### List Directory

```json
{
  "verb": "fs.list-directory",
  "arguments": {
    "path": ".",
    "recursive": true,
    "pattern": "*.ts"
  }
}
```

### Run a Command

```json
{
  "verb": "proc.run",
  "arguments": {
    "executable": "git",
    "arguments": ["status"],
    "workingDirectory": "."
  }
}
```

### Write a File

```json
{
  "verb": "fs.write-file",
  "arguments": {
    "path": "output.txt",
    "content": "Hello, World!"
  }
}
```

## Troubleshooting

### Server Won't Start

1. Check that .NET 10 runtime is installed
2. Verify the workspace path exists
3. Check for port conflicts (HTTP mode)

### Command Execution Fails

1. Use `proc.which` to verify the executable exists
2. Check working directory permissions
3. Review the `errorMessage` in the response

### File Operations Fail

1. Verify path permissions
2. Check if path is within allowed workspace
3. Ensure parent directories exist for write operations
