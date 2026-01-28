<#
.SYNOPSIS
    Authenticates with GitHub Copilot using device flow and calls the chat completions API.

.DESCRIPTION
    This cmdlet performs the full GitHub Copilot authentication flow:
    1. Initiates GitHub device flow authentication
    2. Waits for user to authorize at github.com/login/device
    3. Exchanges the device code for a GitHub token
    4. Gets a Copilot API token
    5. Calls the chat completions API

.PARAMETER PromptFile
    Path to a JSON file containing the chat messages.

.PARAMETER Prompt
    A simple text prompt (alternative to PromptFile).

.PARAMETER Model
    The model to use (default: gpt-4o).

.PARAMETER TokenFile
    Path to cache the GitHub token for reuse (default: ~/.copilot-token.json).

.PARAMETER Stream
    Enable streaming output.

.PARAMETER TokenOnly
    Just print the Copilot token and exit (for use with other tools).

.EXAMPLE
    .\Invoke-CopilotChat.ps1 -Prompt "Explain async/await in JavaScript"

.EXAMPLE
    .\Invoke-CopilotChat.ps1 -TokenOnly

.EXAMPLE
    .\Invoke-CopilotChat.ps1 -PromptFile .\sample-prompt.json -Model "claude-3.5-sonnet"

.EXAMPLE
    .\Invoke-CopilotChat.ps1 -Prompt "Hello!" -Stream
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$PromptFile,

    [Parameter(Mandatory = $false)]
    [string]$Prompt,

    [Parameter(Mandatory = $false)]
    [string]$Model = "gpt-4o",

    [Parameter(Mandatory = $false)]
    [string]$TokenFile = (Join-Path $env:USERPROFILE ".copilot-token.json"),

    [Parameter(Mandatory = $false)]
    [switch]$Stream,

    [Parameter(Mandatory = $false)]
    [switch]$TokenOnly
)

# ============================================================================
# MAIN LOGIC
# ============================================================================

function Main {
    # Validate input
    if (-not $Prompt -and -not $PromptFile -and -not $TokenOnly) {
        Write-Error-Message "Please provide either -Prompt, -PromptFile, or -TokenOnly"
        exit 1
    }
    
    # Build messages array
    if ($PromptFile) {
        if (-not (Test-Path $PromptFile)) {
            Write-Error-Message "Prompt file not found: $PromptFile"
            exit 1
        }
        $promptData = Get-Content $PromptFile -Raw | ConvertFrom-Json
        
        if ($promptData.messages) {
            $script:messages = $promptData.messages
        }
        else {
            $script:messages = @($promptData)
        }
        
        # Override model if specified in file
        if ($promptData.model -and -not $PSBoundParameters.ContainsKey('Model')) {
            $script:Model = $promptData.model
        }
    }
    elseif ($Prompt) {
        $script:messages = @(
            @{
                role    = "user"
                content = $Prompt
            }
        )
    }
    
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║           GitHub Copilot Chat - PowerShell                ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
    Write-Host ""
    
    # Get GitHub token (cached or via device flow)
    $githubToken = Get-GitHubToken -TokenFile $TokenFile
    
    # Get Copilot token
    $copilotToken = Get-CopilotToken -GitHubToken $githubToken
    
    # If TokenOnly, just print the token and exit
    if ($TokenOnly) {
        Write-Host "[Start Token]"
        Write-Host $copilotToken
        Write-Host "[End Token]"
        return $copilotToken
    }
    
    # Call API
    $result = Invoke-ChatCompletion -CopilotToken $copilotToken -Messages $messages -Model $Model -Stream $Stream
    
    Write-Success "Done!"
    
    return $result
}

# ============================================================================
# HELPER FUNCTIONS
# ============================================================================

# VS Code's public OAuth client ID
$GITHUB_CLIENT_ID = "01ab8ac9400c4e429b23"
$GITHUB_SCOPES = "read:user"

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

function Start-DeviceFlow {
    Write-Status "Starting GitHub device flow authentication..."
    
    $body = @{
        client_id = $GITHUB_CLIENT_ID
        scope     = $GITHUB_SCOPES
    }
    
    try {
        $response = Invoke-RestMethod -Uri "https://github.com/login/device/code" -Method Post -Body $body -ContentType "application/x-www-form-urlencoded" -Headers @{ Accept = "application/json" }
        return $response
    }
    catch {
        Write-Error-Message "Failed to start device flow: $_"
        throw
    }
}

function Wait-ForAuthorization {
    param(
        [string]$DeviceCode,
        [int]$Interval,
        [int]$ExpiresIn
    )
    
    $startTime = Get-Date
    $endTime = $startTime.AddSeconds($ExpiresIn)
    
    while ((Get-Date) -lt $endTime) {
        Start-Sleep -Seconds $Interval
        
        $body = @{
            client_id   = $GITHUB_CLIENT_ID
            device_code = $DeviceCode
            grant_type  = "urn:ietf:params:oauth:grant-type:device_code"
        }
        
        try {
            $response = Invoke-RestMethod -Uri "https://github.com/login/oauth/access_token" -Method Post -Body $body -ContentType "application/x-www-form-urlencoded" -Headers @{ Accept = "application/json" }
            
            if ($response.access_token) {
                return $response.access_token
            }
            elseif ($response.error -eq "authorization_pending") {
                Write-Host "." -NoNewline -ForegroundColor DarkGray
            }
            elseif ($response.error -eq "slow_down") {
                $Interval += 5
            }
            elseif ($response.error -eq "expired_token") {
                Write-Error-Message "Device code expired. Please try again."
                throw "Device code expired"
            }
            elseif ($response.error -eq "access_denied") {
                Write-Error-Message "Authorization was denied."
                throw "Access denied"
            }
            else {
                Write-Error-Message "Unknown error: $($response.error)"
                throw $response.error
            }
        }
        catch [System.Net.WebException] {
            # Network error, keep trying
            Write-Host "." -NoNewline -ForegroundColor DarkGray
        }
    }
    
    Write-Error-Message "Authorization timed out."
    throw "Timeout"
}

function Get-GitHubToken {
    param([string]$TokenFile)
    
    # Check for cached token
    if (Test-Path $TokenFile) {
        try {
            $cached = Get-Content $TokenFile -Raw | ConvertFrom-Json
            if ($cached.github_token -and $cached.expires_at) {
                $expiresAt = [DateTime]::Parse($cached.expires_at)
                if ($expiresAt -gt (Get-Date).AddMinutes(5)) {
                    Write-Status "Using cached GitHub token"
                    return $cached.github_token
                }
            }
        }
        catch {
            # Ignore cache errors
        }
    }
    
    # Start device flow
    $deviceFlow = Start-DeviceFlow
    
    Write-Host ""
    Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  To sign in, visit: " -NoNewline
    Write-Host $deviceFlow.verification_uri -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  And enter code:    " -NoNewline
    Write-Host $deviceFlow.user_code -ForegroundColor Green -BackgroundColor DarkGray
    Write-Host ""
    Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Yellow
    Write-Host ""
    
    # Try to open browser
    try {
        Start-Process $deviceFlow.verification_uri
        Write-Status "Opened browser automatically"
    }
    catch {
        Write-Status "Please open the URL manually" -Color Yellow
    }
    
    Write-Host "Waiting for authorization" -NoNewline -ForegroundColor DarkGray
    
    $token = Wait-ForAuthorization -DeviceCode $deviceFlow.device_code -Interval $deviceFlow.interval -ExpiresIn $deviceFlow.expires_in
    
    Write-Host ""
    Write-Success "GitHub authorization successful!"
    
    # Cache the token (GitHub tokens don't expire by default, but we'll set a long expiry)
    $cacheData = @{
        github_token = $token
        expires_at   = (Get-Date).AddDays(30).ToString("o")
    } | ConvertTo-Json
    
    $cacheData | Set-Content $TokenFile -Force
    Write-Status "Token cached to $TokenFile"
    
    return $token
}

function Get-CopilotToken {
    param([string]$GitHubToken)
    
    Write-Status "Getting Copilot API token..."
    
    $headers = @{
        Authorization        = "token $GitHubToken"
        Accept               = "application/json"
        "Editor-Version"     = "vscode/1.85.0"
        "Editor-Plugin-Version" = "copilot/1.0.0"
        "User-Agent"         = "GitHubCopilotChat/1.0.0"
    }
    
    try {
        $response = Invoke-RestMethod -Uri "https://api.github.com/copilot_internal/v2/token" -Method Get -Headers $headers
        Write-Success "Got Copilot token (expires in ~30 minutes)"
        return $response.token
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 401) {
            Write-Error-Message "GitHub token is invalid or expired. Deleting cached token."
            if (Test-Path $TokenFile) { Remove-Item $TokenFile -Force }
            throw "Invalid GitHub token"
        }
        elseif ($statusCode -eq 403) {
            Write-Error-Message "You don't have access to GitHub Copilot. Please check your subscription."
            throw "No Copilot access"
        }
        else {
            Write-Error-Message "Failed to get Copilot token: $_"
            throw
        }
    }
}

function Invoke-ChatCompletion {
    param(
        [string]$CopilotToken,
        [array]$Messages,
        [string]$Model,
        [bool]$Stream
    )
    
    Write-Status "Calling Copilot chat completions API..."
    Write-Status "Model: $Model" -Color DarkGray
    
    $body = @{
        model    = $Model
        messages = $Messages
        stream   = $Stream
    } | ConvertTo-Json -Depth 10
    
    try {
        # Use HttpWebRequest to properly handle Authorization header with semicolons
        $request = [System.Net.HttpWebRequest]::Create("https://api.githubcopilot.com/chat/completions")
        $request.Method = "POST"
        $request.ContentType = "application/json"
        $request.Accept = "application/json"
        $request.UserAgent = "GitHubCopilotChat/1.0.0"
        
        # Set Authorization header directly (handles special characters properly)
        $request.Headers.Add("Authorization", "Bearer $CopilotToken")
        $request.Headers.Add("Editor-Version", "vscode/1.85.0")
        $request.Headers.Add("Editor-Plugin-Version", "copilot-chat/1.0.0")
        $request.Headers.Add("Openai-Organization", "github-copilot")
        $request.Headers.Add("Copilot-Integration-Id", "vscode-chat")
        
        $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
        $request.ContentLength = $bodyBytes.Length
        $requestStream = $request.GetRequestStream()
        $requestStream.Write($bodyBytes, 0, $bodyBytes.Length)
        $requestStream.Close()
        
        $response = $request.GetResponse()
        $responseStream = $response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($responseStream)
        
        if ($Stream) {
            # Streaming response
            Write-Host ""
            Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
            
            $fullContent = ""
            while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                if ($line.StartsWith("data: ")) {
                    $data = $line.Substring(6)
                    if ($data -eq "[DONE]") {
                        break
                    }
                    try {
                        $chunk = $data | ConvertFrom-Json
                        if ($chunk.choices -and $chunk.choices[0].delta.content) {
                            $content = $chunk.choices[0].delta.content
                            Write-Host $content -NoNewline
                            $fullContent += $content
                        }
                    }
                    catch {
                        # Ignore JSON parse errors for incomplete chunks
                    }
                }
            }
            
            $reader.Close()
            $responseStream.Close()
            $response.Close()
            
            Write-Host ""
            Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
            Write-Host ""
            
            return $fullContent
        }
        else {
            # Non-streaming response
            $responseBody = $reader.ReadToEnd()
            $reader.Close()
            $responseStream.Close()
            $response.Close()
            
            $result = $responseBody | ConvertFrom-Json
            
            Write-Host ""
            Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
            Write-Host $result.choices[0].message.content
            Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
            Write-Host ""
            
            Write-Status "Tokens: $($result.usage.prompt_tokens) prompt + $($result.usage.completion_tokens) completion = $($result.usage.total_tokens) total" -Color DarkGray
            
            return $result.choices[0].message.content
        }
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
