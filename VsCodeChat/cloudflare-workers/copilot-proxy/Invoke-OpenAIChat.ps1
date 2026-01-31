<#
.SYNOPSIS
    Calls an OpenAI-compatible chat completions API.

.DESCRIPTION
    A simple script to call any OpenAI-compatible API endpoint with a prompt.

.PARAMETER ApiUrl
    The base URL of the OpenAI-compatible API (default: http://localhost:8787/copilot/v1).

.PARAMETER Model
    The model to use (default: gpt-4o).

.PARAMETER Prompt
    The user prompt to send.

.PARAMETER SystemPrompt
    Optional system prompt.

.PARAMETER ApiKey
    The API key/token for authentication (required for most endpoints).

.PARAMETER Stream
    Enable streaming output (default: true).

.PARAMETER NoStream
    Disable streaming output.

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -Prompt "Hello!" -ApiKey "ghu_xxx"

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -ApiUrl "https://api.openai.com/v1" -Model "gpt-4" -Prompt "Explain recursion" -ApiKey $env:OPENAI_API_KEY

.EXAMPLE
    .\Invoke-OpenAIChat.ps1 -ApiUrl "https://copilot-proxy.ref12cf.workers.dev/copilot/v1" -Prompt "Hello" -ApiKey "ghu_xxx"
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
    [switch]$Stream,

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
    $messages = @()
    
    if ($SystemPrompt) {
        $messages += @{
            role    = "system"
            content = $SystemPrompt
        }
    }
    
    $messages += @{
        role    = "user"
        content = $Prompt
    }
    
    # Normalize API URL
    $baseUrl = $ApiUrl.TrimEnd('/')
    $endpoint = "$baseUrl/chat/completions"
    
    Write-Status "Endpoint: $endpoint" -Color DarkGray
    Write-Status "Model: $Model" -Color DarkGray
    Write-Status "Stream: $useStream" -Color DarkGray
    Write-Host ""
    
    # Build request body
    $body = @{
        model    = $Model
        messages = $messages
        stream   = $useStream
    } | ConvertTo-Json -Depth 10
    
    try {
        $request = [System.Net.HttpWebRequest]::Create($endpoint)
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
        
        Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
        
        $fullContent = ""
        
        if ($useStream) {
            # Process SSE response
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
                            $content = $chunk.choices[0].delta.content
                            if ($content) {
                                Write-Host $content -NoNewline
                                $fullContent += $content
                            }
                        }
                    }
                    catch {
                        # Ignore JSON parse errors for incomplete chunks
                    }
                }
            }
        }
        else {
            # Non-streaming response
            $responseBody = $reader.ReadToEnd()
            $json = $responseBody | ConvertFrom-Json
            if ($json.choices -and $json.choices.Count -gt 0) {
                $fullContent = $json.choices[0].message.content
                Write-Host $fullContent
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
