<#
.SYNOPSIS
    Calls an OpenAI-compatible chat completions API with optional tool support.

.DESCRIPTION
    A script to call any OpenAI-compatible API endpoint with a prompt.
    Supports function/tool calling with built-in test tools.

.PARAMETER ApiUrl
    The base URL of the OpenAI-compatible API (default: https://copilot-proxy.ref12cf.workers.dev/copilot/v1).

.PARAMETER Model
    The model to use (default: gpt-4o).

.PARAMETER Prompt
    The user prompt to send.

.PARAMETER SystemPrompt
    Optional system prompt.

.PARAMETER ApiKey
    The API key/token for authentication (required for most endpoints).

.PARAMETER NoStream
    Disable streaming output (streaming is on by default).

.PARAMETER UseTools
    Enable tool/function calling with built-in test tools.

.PARAMETER ListTools
    Ask the model what tools it has available.

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -Prompt "Hello!" -ApiKey "ghu_xxx"

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -Prompt "What tools do you have?" -UseTools -ApiKey "ghu_xxx"

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -Prompt "What time is it?" -UseTools -ApiKey "ghu_xxx"

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -Prompt "Calculate 15 * 7 + 3" -UseTools -ApiKey "ghu_xxx"

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -Prompt "Roll 3 dice" -UseTools -ApiKey "ghu_xxx"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ApiUrl = "https://copilot-proxy.ref12cf.workers.dev/copilot/v1",

    [Parameter(Mandatory = $false)]
    [string]$Model = "gpt-4o",

    [Parameter(Mandatory = $true)]
    [string]$Prompt,

    [Parameter(Mandatory = $false)]
    [string]$SystemPrompt,

    [Parameter(Mandatory = $false)]
    [string]$ApiKey,

    [Parameter(Mandatory = $false)]
    [switch]$NoStream,

    [Parameter(Mandatory = $false)]
    [switch]$UseTools
)

# ============================================================================
# TOOL DEFINITIONS
# ============================================================================

$script:ToolDefinitions = @(
    @{
        type     = "function"
        function = @{
            name        = "get_current_time"
            description = "Get the current date and time"
            parameters  = @{
                type       = "object"
                properties = @{
                    timezone = @{
                        type        = "string"
                        description = "Optional timezone (e.g., 'UTC', 'America/New_York'). Defaults to local time."
                    }
                }
                required   = @()
            }
        }
    },
    @{
        type     = "function"
        function = @{
            name        = "calculate"
            description = "Perform a mathematical calculation. Supports basic arithmetic operations."
            parameters  = @{
                type       = "object"
                properties = @{
                    expression = @{
                        type        = "string"
                        description = "The mathematical expression to evaluate (e.g., '2 + 2', '15 * 7', 'sqrt(16)')"
                    }
                }
                required   = @("expression")
            }
        }
    },
    @{
        type     = "function"
        function = @{
            name        = "roll_dice"
            description = "Roll one or more dice and return the results"
            parameters  = @{
                type       = "object"
                properties = @{
                    count = @{
                        type        = "integer"
                        description = "Number of dice to roll (default: 1)"
                    }
                    sides = @{
                        type        = "integer"
                        description = "Number of sides on each die (default: 6)"
                    }
                }
                required   = @()
            }
        }
    },
    @{
        type     = "function"
        function = @{
            name        = "get_weather"
            description = "Get mock weather information for a location (test tool - returns fake data)"
            parameters  = @{
                type       = "object"
                properties = @{
                    location = @{
                        type        = "string"
                        description = "The city or location to get weather for"
                    }
                }
                required   = @("location")
            }
        }
    }
)

# ============================================================================
# TOOL IMPLEMENTATIONS
# ============================================================================

function Invoke-Tool {
    param(
        [string]$Name,
        [hashtable]$Arguments
    )
    
    switch ($Name) {
        "get_current_time" {
            $tz = $Arguments.timezone
            if ($tz -eq "UTC") {
                $time = [DateTime]::UtcNow
                return @{
                    time     = $time.ToString("yyyy-MM-dd HH:mm:ss")
                    timezone = "UTC"
                } | ConvertTo-Json
            }
            else {
                $time = Get-Date
                return @{
                    time     = $time.ToString("yyyy-MM-dd HH:mm:ss")
                    timezone = [System.TimeZoneInfo]::Local.DisplayName
                } | ConvertTo-Json
            }
        }
        "calculate" {
            $expr = $Arguments.expression
            try {
                # Safe math evaluation using PowerShell
                $result = Invoke-Expression ($expr -replace '[^0-9+\-*/().sqrt\s]', '')
                return @{
                    expression = $expr
                    result     = $result
                } | ConvertTo-Json
            }
            catch {
                return @{
                    expression = $expr
                    error      = "Failed to evaluate: $_"
                } | ConvertTo-Json
            }
        }
        "roll_dice" {
            $count = if ($Arguments.count) { [int]$Arguments.count } else { 1 }
            $sides = if ($Arguments.sides) { [int]$Arguments.sides } else { 6 }
            $rolls = @()
            for ($i = 0; $i -lt $count; $i++) {
                $rolls += Get-Random -Minimum 1 -Maximum ($sides + 1)
            }
            return @{
                dice    = "${count}d${sides}"
                rolls   = $rolls
                total   = ($rolls | Measure-Object -Sum).Sum
            } | ConvertTo-Json
        }
        "get_weather" {
            $location = $Arguments.location
            # Mock weather data
            $conditions = @("Sunny", "Cloudy", "Partly Cloudy", "Rainy", "Snowy")
            $temp = Get-Random -Minimum 20 -Maximum 90
            return @{
                location    = $location
                temperature = "${temp}°F"
                condition   = $conditions | Get-Random
                note        = "This is mock data for testing purposes"
            } | ConvertTo-Json
        }
        default {
            return @{ error = "Unknown tool: $Name" } | ConvertTo-Json
        }
    }
}

# ============================================================================
# HELPER FUNCTIONS
# ============================================================================

function Write-Status {
    param([string]$Message, [string]$Color = "Cyan")
    Write-Host "► " -NoNewline -ForegroundColor $Color
    Write-Host $Message
}

function Write-Error-Message {
    param([string]$Message)
    Write-Host "✗ " -NoNewline -ForegroundColor Red
    Write-Host $Message -ForegroundColor Red
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ " -NoNewline -ForegroundColor Green
    Write-Host $Message
}

function Write-ToolCall {
    param([string]$Name, [string]$Args)
    Write-Host "🔧 " -NoNewline -ForegroundColor Yellow
    Write-Host "Tool call: " -NoNewline -ForegroundColor Yellow
    Write-Host "$Name" -NoNewline -ForegroundColor Cyan
    Write-Host "($Args)" -ForegroundColor DarkGray
}

function Write-ToolResult {
    param([string]$Result)
    Write-Host "   → " -NoNewline -ForegroundColor Green
    Write-Host $Result -ForegroundColor DarkGray
}

function Invoke-ChatRequest {
    param(
        [string]$Endpoint,
        [array]$Messages,
        [string]$Model,
        [bool]$Stream,
        [string]$ApiKey,
        [array]$Tools
    )
    
    $bodyObj = @{
        model    = $Model
        messages = $Messages
        stream   = $Stream
    }
    
    if ($Tools -and $Tools.Count -gt 0) {
        $bodyObj.tools = $Tools
        $bodyObj.tool_choice = "auto"
    }
    
    $body = $bodyObj | ConvertTo-Json -Depth 20
    
    $request = [System.Net.HttpWebRequest]::Create($Endpoint)
    $request.Method = "POST"
    $request.ContentType = "application/json"
    $request.Accept = "application/json"
    $request.UserAgent = "OpenAI-Chat-Client/1.0"
    
    if ($ApiKey) {
        $request.Headers.Add("Authorization", "Bearer $ApiKey")
    }
    
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $request.ContentLength = $bodyBytes.Length
    $requestStream = $request.GetRequestStream()
    $requestStream.Write($bodyBytes, 0, $bodyBytes.Length)
    $requestStream.Close()
    
    $response = $request.GetResponse()
    $responseStream = $response.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($responseStream)
    
    $result = @{
        content    = ""
        tool_calls = @()
    }
    
    if ($Stream) {
        # Accumulate tool calls from streaming
        $toolCallsById = @{}
        
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            
            if ($line.StartsWith("data: ")) {
                $data = $line.Substring(6)
                if ($data -eq "[DONE]") {
                    break
                }
                try {
                    $chunk = $data | ConvertFrom-Json
                    if ($chunk.choices -and $chunk.choices.Count -gt 0) {
                        $delta = $chunk.choices[0].delta
                        
                        # Handle content
                        if ($delta.content) {
                            Write-Host $delta.content -NoNewline
                            $result.content += $delta.content
                        }
                        
                        # Handle tool calls (streamed incrementally)
                        if ($delta.tool_calls) {
                            foreach ($tc in $delta.tool_calls) {
                                $idx = $tc.index
                                if (-not $toolCallsById.ContainsKey($idx)) {
                                    $toolCallsById[$idx] = @{
                                        id       = ""
                                        name     = ""
                                        arguments = ""
                                    }
                                }
                                if ($tc.id) { $toolCallsById[$idx].id = $tc.id }
                                if ($tc.function.name) { $toolCallsById[$idx].name = $tc.function.name }
                                if ($tc.function.arguments) { $toolCallsById[$idx].arguments += $tc.function.arguments }
                            }
                        }
                    }
                }
                catch {
                    # Ignore parse errors
                }
            }
        }
        
        # Convert accumulated tool calls to array
        foreach ($key in $toolCallsById.Keys | Sort-Object) {
            $result.tool_calls += $toolCallsById[$key]
        }
    }
    else {
        # Non-streaming response
        $responseBody = $reader.ReadToEnd()
        $json = $responseBody | ConvertFrom-Json
        if ($json.choices -and $json.choices.Count -gt 0) {
            $msg = $json.choices[0].message
            if ($msg.content) {
                $result.content = $msg.content
            }
            if ($msg.tool_calls) {
                foreach ($tc in $msg.tool_calls) {
                    $result.tool_calls += @{
                        id        = $tc.id
                        name      = $tc.function.name
                        arguments = $tc.function.arguments
                    }
                }
            }
        }
    }
    
    $reader.Close()
    $responseStream.Close()
    $response.Close()
    
    return $result
}

# ============================================================================
# MAIN LOGIC
# ============================================================================

function Main {
    $useStream = -not $NoStream
    
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║           OpenAI-Compatible Chat Client                   ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
    Write-Host ""
    
    # Build messages array
    $messages = [System.Collections.ArrayList]@()
    
    if ($SystemPrompt) {
        [void]$messages.Add(@{
            role    = "system"
            content = $SystemPrompt
        })
    }
    
    [void]$messages.Add(@{
        role    = "user"
        content = $Prompt
    })
    
    # Normalize API URL
    $baseUrl = $ApiUrl.TrimEnd('/')
    $endpoint = "$baseUrl/chat/completions"
    
    Write-Status "Endpoint: $endpoint" -Color DarkGray
    Write-Status "Model: $Model" -Color DarkGray
    Write-Status "Stream: $useStream" -Color DarkGray
    if ($UseTools) {
        Write-Status "Tools: Enabled ($(($script:ToolDefinitions).Count) available)" -Color DarkGray
    }
    Write-Host ""
    
    $tools = if ($UseTools) { $script:ToolDefinitions } else { $null }
    
    Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
    
    try {
        $maxIterations = 10  # Prevent infinite loops
        $iteration = 0
        $finalContent = ""
        
        while ($iteration -lt $maxIterations) {
            $iteration++
            
            $result = Invoke-ChatRequest -Endpoint $endpoint -Messages $messages -Model $Model -Stream $useStream -ApiKey $ApiKey -Tools $tools
            
            # If there are tool calls, process them
            if ($result.tool_calls -and $result.tool_calls.Count -gt 0) {
                Write-Host ""
                
                # Add assistant message with tool calls to conversation
                $assistantMsg = @{
                    role       = "assistant"
                    content    = $null
                    tool_calls = @()
                }
                
                foreach ($tc in $result.tool_calls) {
                    $assistantMsg.tool_calls += @{
                        id       = $tc.id
                        type     = "function"
                        function = @{
                            name      = $tc.name
                            arguments = $tc.arguments
                        }
                    }
                }
                
                [void]$messages.Add($assistantMsg)
                
                # Execute each tool and add results
                foreach ($tc in $result.tool_calls) {
                    Write-ToolCall -Name $tc.name -Args $tc.arguments
                    
                    $args = @{}
                    if ($tc.arguments) {
                        try {
                            $args = $tc.arguments | ConvertFrom-Json -AsHashtable
                        }
                        catch {
                            $args = @{}
                        }
                    }
                    
                    $toolResult = Invoke-Tool -Name $tc.name -Arguments $args
                    Write-ToolResult -Result $toolResult
                    
                    [void]$messages.Add(@{
                        role         = "tool"
                        tool_call_id = $tc.id
                        content      = $toolResult
                    })
                }
                
                Write-Host ""
                # Continue loop to get next response
            }
            else {
                # No tool calls, we have the final response
                $finalContent = $result.content
                break
            }
        }
        
        if (-not $useStream -and $finalContent) {
            Write-Host $finalContent
        }
        
        Write-Host ""
        Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
        Write-Host ""
        Write-Success "Done!"
        
        return $finalContent
    }
    catch [System.Net.WebException] {
        $errorResponse = $_.Exception.Response
        if ($errorResponse) {
            $errorStream = $errorResponse.GetResponseStream()
            $errorReader = New-Object System.IO.StreamReader($errorStream)
            $errorBody = $errorReader.ReadToEnd()
            $errorReader.Close()
            Write-Host ""
            Write-Error-Message "API call failed ($([int]$errorResponse.StatusCode)): $errorBody"
        }
        else {
            Write-Error-Message "API call failed: $_"
        }
        throw
    }
    catch {
        Write-Error-Message "API call failed: $_"
        throw
    }
}

# ============================================================================
# RUN
# ============================================================================

try {
    Main
}
catch {
    Write-Error-Message "Error: $_"
    exit 1
}
