<#
.SYNOPSIS
    Calls a Poe Server Bot API endpoint.

.DESCRIPTION
    A script to call any Poe Server Bot endpoint with a prompt.
    Generates spoofed user, conversation, and bot IDs for testing.

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

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://example.com/poe" -Prompt "Hello!"

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://copilot-proxy.ref12cf.workers.dev/poe/server" -Prompt "Say hello" -AccessKey "gho_xxx"

.EXAMPLE
    .\Invoke-PoeServerBot.ps1 -ServerUrl "https://copilot-proxy.ref12cf.workers.dev/poe/server?model=claude-sonnet-4" -Prompt "Hello"
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
    [switch]$NoStream
)

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

function Main {
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║           Poe Server Bot Client                           ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
    Write-Host ""
    
    # Generate IDs if not provided
    $userId = if ($UserId) { $UserId } else { "u-" + (New-RandomId) }
    $conversationId = if ($ConversationId) { $ConversationId } else { "c-" + (New-RandomId) }
    $messageId = "m-" + (New-RandomId)
    $requestToken = "r-" + (New-RandomId)
    $botQueryId = "b-" + (New-RandomId)
    
    Write-Status "Server URL: $ServerUrl" -Color DarkGray
    Write-Status "User ID: $userId" -Color DarkGray
    Write-Status "Conversation ID: $conversationId" -Color DarkGray
    Write-Host ""
    
    # Build the Poe query request
    $timestamp = Get-UnixTimestampMicros
    
    $queryMessage = @{
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
    }
    
    $poeRequest = @{
        version            = "1.1"
        type               = "query"
        conversation_id    = $conversationId
        user_id            = $userId
        message_id         = "r-" + (New-RandomId) + "-" + [guid]::NewGuid().ToString("N").Substring(0, 32)
        query              = @($queryMessage)
        skip_system_prompt = $false
        logit_bias         = @{}
        language_code      = "en"
        metadata           = ""
        request_token      = $requestToken
        users              = @(
            @{
                id   = $userId
                name = $null
            }
        )
        bot_query_id       = $botQueryId
    }
    
    $body = $poeRequest | ConvertTo-Json -Depth 20
    
    try {
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
        
        Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
        
        $fullContent = ""
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
                                $fullContent += $json.text
                            }
                        }
                        "replace_response" {
                            if ($json.text) {
                                # Clear and replace
                                Write-Host "`r" -NoNewline
                                Write-Host $json.text -NoNewline
                                $fullContent = $json.text
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
                            # Metadata event, usually contains suggested_replies, content_type, etc.
                            if ($json.suggested_replies) {
                                Write-Host ""
                                foreach ($reply in $json.suggested_replies) {
                                    Write-Host "  💬 Suggested: " -NoNewline -ForegroundColor Cyan
                                    Write-Host $reply -ForegroundColor DarkGray
                                }
                            }
                        }
                        "tool_call" {
                            Write-Host ""
                            Write-Host "🔧 " -NoNewline -ForegroundColor Yellow
                            Write-Host "Tool call: " -NoNewline -ForegroundColor Yellow
                            if ($json.function) {
                                Write-Host "$($json.function.name)" -NoNewline -ForegroundColor Cyan
                                Write-Host "($($json.function.arguments))" -ForegroundColor DarkGray
                            } else {
                                Write-Host ($json | ConvertTo-Json -Compress) -ForegroundColor DarkGray
                            }
                        }
                        default {
                            # Unknown event type, show raw if it has content
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
        
        Write-Host ""
        Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
        Write-Host ""
        Write-Success "Done!"
        
        return $fullContent
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
