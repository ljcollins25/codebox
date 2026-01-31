<#
.SYNOPSIS
    Calls a Poe Server Bot API endpoint with optional tool support.

.DESCRIPTION
    A script to call any Poe Server Bot endpoint with a prompt.
    Generates spoofed user, conversation, and bot IDs for testing.
    Supports function/tool calling with built-in test tools.

.PARAMETER ServerUrl
    The URL of the Poe Server Bot endpoint.

.PARAMETER Prompt
    The user prompt to send.

.PARAMETER ConversationId
    Optional conversation ID (generates random if not provided).

.PARAMETER UserId
    Optional user ID (generates random if not provided).

.PARAMETER AccessKey
    Optional access key for authentication.

.PARAMETER NoStream
    Disable streaming output (streaming is on by default).

.PARAMETER UseTools
    Enable tool/function calling with built-in test tools.

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://example.com/poe" -Prompt "Hello!"

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://copilot-proxy.ref12cf.workers.dev/poe/server" -Prompt "Say hello" -AccessKey "gho_xxx"

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://copilot-proxy.ref12cf.workers.dev/poe/server?model=claude-sonnet-4" -Prompt "Hello"

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://copilot-proxy.ref12cf.workers.dev/poe/server" -Prompt "What time is it?" -UseTools -AccessKey "gho_xxx"

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://copilot-proxy.ref12cf.workers.dev/poe/server" -Prompt "Roll 3 dice" -UseTools -AccessKey "gho_xxx"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,

    [Parameter(Mandatory = $true)]
    [string]$Prompt,

    [Parameter(Mandatory = $false)]
    [string]$ConversationId,

    [Parameter(Mandatory = $false)]
    [string]$UserId,

    [Parameter(Mandatory = $false)]
    [string]$AccessKey,

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

function New-RandomId {
    param([string]$Prefix = "")
    $chars = "abcdefghijklmnopqrstuvwxyz0123456789"
    $id = -join ((1..44) | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
    return "$Prefix$id"
}

function Get-UnixTimestampMicros {
    $epoch = [DateTime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $now = [DateTime]::UtcNow
    return [long](($now - $epoch).TotalMilliseconds * 1000)
}

# ============================================================================
# MAIN LOGIC
# ============================================================================

function Invoke-PoeRequest {
    param(
        [string]$ServerUrl,
        [array]$QueryMessages,
        [string]$UserId,
        [string]$ConversationId,
        [string]$AccessKey,
        [array]$Tools,
        [array]$ToolResults
    )
    
    $messageId = "m-" + (New-RandomId)
    $requestToken = "r-" + (New-RandomId)
    $botQueryId = "b-" + (New-RandomId)
    
    $poeRequest = @{
        version            = "1.1"
        type               = "query"
        conversation_id    = $ConversationId
        user_id            = $UserId
        message_id         = "r-" + (New-RandomId) + "-" + [guid]::NewGuid().ToString("N").Substring(0, 32)
        query              = $QueryMessages
        skip_system_prompt = $false
        logit_bias         = @{}
        language_code      = "en"
        metadata           = ""
        request_token      = $requestToken
        users              = @(
            @{
                id   = $UserId
                name = $null
            }
        )
        bot_query_id       = $botQueryId
    }
    
    if ($Tools -and $Tools.Count -gt 0) {
        $poeRequest.tools = $Tools
    }
    
    if ($ToolResults -and $ToolResults.Count -gt 0) {
        $poeRequest.tool_results = $ToolResults
    }
    
    $body = $poeRequest | ConvertTo-Json -Depth 20
    
    $request = [System.Net.HttpWebRequest]::Create($ServerUrl)
    $request.Method = "POST"
    $request.ContentType = "application/json"
    $request.Accept = "text/event-stream"
    $request.UserAgent = "Poe-Client/1.0"
    
    if ($AccessKey) {
        $request.Headers.Add("Authorization", "Bearer $AccessKey")
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
    
    $currentEvent = ""
    
    while (-not $reader.EndOfStream) {
        $line = $reader.ReadLine()
        
        # Track event type
        if ($line.StartsWith("event: ")) {
            $currentEvent = $line.Substring(7).Trim()
            continue
        }
        
        if ($line.StartsWith("data: ")) {
            $data = $line.Substring(6)
            
            try {
                $json = $data | ConvertFrom-Json
                
                switch ($currentEvent) {
                    "text" {
                        if ($json.text) {
                            Write-Host $json.text -NoNewline
                            $result.content += $json.text
                        }
                    }
                    "replace_response" {
                        if ($json.text) {
                            Write-Host "`r" -NoNewline
                            Write-Host $json.text -NoNewline
                            $result.content = $json.text
                        }
                    }
                    "suggested_reply" {
                        Write-Host ""
                        Write-Host "  💬 Suggested: " -NoNewline -ForegroundColor Cyan
                        Write-Host $json.text -ForegroundColor DarkGray
                    }
                    "error" {
                        Write-Host ""
                        Write-Error-Message "Error: $($json.text)"
                        if ($json.allow_retry) {
                            Write-Host "  (Retry allowed)" -ForegroundColor Yellow
                        }
                    }
                    "done" {
                        # Stream complete
                    }
                    "meta" {
                        if ($json.suggested_replies) {
                            Write-Host ""
                            foreach ($reply in $json.suggested_replies) {
                                Write-Host "  💬 Suggested: " -NoNewline -ForegroundColor Cyan
                                Write-Host $reply -ForegroundColor DarkGray
                            }
                        }
                    }
                    "tool_call" {
                        # Accumulate tool calls for processing
                        $result.tool_calls += @{
                            id        = $json.id
                            name      = $json.function.name
                            arguments = $json.function.arguments
                        }
                    }
                    default {
                        if ($currentEvent -and $data -ne "{}") {
                            Write-Host ""
                            Write-Host "  [$currentEvent]: " -NoNewline -ForegroundColor DarkGray
                            Write-Host $data -ForegroundColor DarkGray
                        }
                    }
                }
            }
            catch {
                # Not JSON or parse error, skip
            }
        }
    }
    
    $reader.Close()
    $responseStream.Close()
    $response.Close()
    
    return $result
}

function Main {
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║           Poe Server Bot Client                           ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
    Write-Host ""
    
    # Generate IDs if not provided
    $userId = if ($UserId) { $UserId } else { "u-" + (New-RandomId) }
    $conversationId = if ($ConversationId) { $ConversationId } else { "c-" + (New-RandomId) }
    
    Write-Status "Server URL: $ServerUrl" -Color DarkGray
    Write-Status "User ID: $userId" -Color DarkGray
    Write-Status "Conversation ID: $conversationId" -Color DarkGray
    if ($UseTools) {
        Write-Status "Tools: Enabled ($(($script:ToolDefinitions).Count) available)" -Color DarkGray
    }
    Write-Host ""
    
    # Build initial user message
    $timestamp = Get-UnixTimestampMicros
    $messageId = "m-" + (New-RandomId)
    
    $queryMessages = [System.Collections.ArrayList]@()
    [void]$queryMessages.Add(@{
        role         = "user"
        sender_id    = $userId
        sender       = @{
            id   = $userId
            name = $null
        }
        content      = $Prompt
        parameters   = @{}
        content_type = "text/markdown"
        timestamp    = $timestamp
        message_id   = $messageId
        feedback     = @()
        attachments  = @()
        metadata     = $null
        referenced_message = $null
        reactions    = @()
    })
    
    $tools = if ($UseTools) { $script:ToolDefinitions } else { $null }
    
    Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
    
    try {
        $maxIterations = 10  # Prevent infinite loops
        $iteration = 0
        $finalContent = ""
        $toolResults = $null
        
        while ($iteration -lt $maxIterations) {
            $iteration++
            
            $result = Invoke-PoeRequest -ServerUrl $ServerUrl -QueryMessages $queryMessages -UserId $userId -ConversationId $conversationId -AccessKey $AccessKey -Tools $tools -ToolResults $toolResults
            
            # If there are tool calls, process them
            if ($result.tool_calls -and $result.tool_calls.Count -gt 0) {
                Write-Host ""
                
                # Add bot message with tool calls to conversation
                $botTimestamp = Get-UnixTimestampMicros
                $botMessageId = "m-" + (New-RandomId)
                
                $toolCallsForMessage = @()
                foreach ($tc in $result.tool_calls) {
                    $toolCallsForMessage += @{
                        id       = $tc.id
                        type     = "function"
                        function = @{
                            name      = $tc.name
                            arguments = $tc.arguments
                        }
                    }
                }
                
                [void]$queryMessages.Add(@{
                    role         = "bot"
                    content      = $result.content
                    content_type = "text/markdown"
                    timestamp    = $botTimestamp
                    message_id   = $botMessageId
                    tool_calls   = $toolCallsForMessage
                    feedback     = @()
                    attachments  = @()
                })
                
                # Execute each tool and prepare results
                $toolResults = @()
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
                    
                    $toolResults += @{
                        role         = "tool"
                        tool_call_id = $tc.id
                        content      = $toolResult
                    }
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
