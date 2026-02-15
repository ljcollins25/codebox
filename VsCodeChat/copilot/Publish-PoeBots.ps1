# Publish-PoeBots.ps1
# Creates/updates Poe API bots from OpenAI model JSON files
# Usage: .\Publish-PoeBots.ps1 -PoeKey <key> -ApiKey <key> [-BaseUrl <url>] [-Handle <template>] [-Description <template>] [-Path <path>] [-Public] [-Publish]

param(
    [Parameter(Mandatory = $true)]
    [string]$PoeKey,

    [Parameter(Mandatory = $true)]
    [string]$ApiKey,

    [Parameter()]
    [string]$BaseUrl = "https://api.openai.com/v1",

    [Parameter()]
    [string]$Handle = "VSC-{id}",

    [Parameter()]
    [string]$Description = "VSC: {name}",

    [Parameter()]
    [string]$Path = "models",

    [Parameter()]
    [switch]$Public,

    [Parameter()]
    [switch]$Publish
)

# Get script directory for relative paths
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Get-Location }

$FullPath = Join-Path $ScriptDir $Path
$BotsDir = Join-Path $ScriptDir "bots"

# Poe API base URL
$PoeApiUrl = "https://api.poe.com/bots"

function Expand-Template {
    param(
        [string]$Template,
        [PSObject]$Model
    )

    $result = $Template
    $result = $result -replace '\{id\}', $Model.id
    $result = $result -replace '\{name\}', $Model.name
    $result = $result -replace '\{version\}', $Model.version
    $result = $result -replace '\{vendor\}', $Model.vendor
    $result = $result -replace '\{family\}', $Model.capabilities.family
    return $result
}

function Get-InputModalities {
    param([PSObject]$Model)

    $modalities = @("text")

    if ($Model.capabilities.supports.vision -eq $true) {
        $modalities += "image"
    }

    return $modalities
}

function Get-SupportedFeatures {
    param([PSObject]$Model)

    $features = @()

    if ($Model.capabilities.supports.tool_calls -eq $true) {
        $features += "tools"
    }

    return $features
}

function Get-ApiType {
    param([PSObject]$Model)

    # Use responses_api if model supports it
    if ($Model.supported_endpoints -contains "/responses") {
        return "responses_api"
    }

    return "chat_completions_api"
}

function Build-BotPayload {
    param([PSObject]$Model)

    $handle = Expand-Template -Template $Handle -Model $Model
    $inputModalities = Get-InputModalities -Model $Model
    $supportedFeatures = Get-SupportedFeatures -Model $Model
    $apiType = Get-ApiType -Model $Model

    $apiSettings = @{
        model_name        = $Model.id
        base_url          = $BaseUrl
        api_key           = $ApiKey
        api_type          = $apiType
        input_modalities  = $inputModalities
        output_modalities = @("text")
    }

    # Add supported features if any
    if ($supportedFeatures.Count -gt 0) {
        $apiSettings.supported_features = $supportedFeatures
    }

    # Add max input tokens if available
    if ($Model.capabilities.limits.max_prompt_tokens) {
        $apiSettings.max_input_tokens = $Model.capabilities.limits.max_prompt_tokens
    }

    # Add context size if available
    if ($Model.capabilities.limits.max_context_window_tokens) {
        $apiSettings.context_size = $Model.capabilities.limits.max_context_window_tokens
    }

    $botDescription = Expand-Template -Template $Description -Model $Model

    $bot = @{
        handle           = $handle
        description      = $botDescription
        is_private       = -not $Public.IsPresent
        api_bot_settings = $apiSettings
    }

    return $bot
}

function Save-BotLocally {
    param(
        [PSObject]$Bot,
        [string]$ModelId
    )

    if (-not (Test-Path $BotsDir)) {
        New-Item -ItemType Directory -Path $BotsDir | Out-Null
    }

    # Create a copy with redacted sensitive fields
    $RedactedBot = $Bot | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $RedactedBot.api_bot_settings.base_url = "[REDACTED]"
    $RedactedBot.api_bot_settings.api_key = "[REDACTED]"

    $fileName = Join-Path $BotsDir "$ModelId.json"
    $RedactedBot | ConvertTo-Json -Depth 10 | Set-Content -Path $fileName -Encoding UTF8
    Write-Host "  Saved: $fileName" -ForegroundColor Gray
}

function Publish-BotToPoe {
    param([PSObject]$Bot)

    $headers = @{
        "Authorization" = "Bearer $PoeKey"
        "Content-Type"  = "application/json"
    }

    $body = $Bot | ConvertTo-Json -Depth 10

    try {
        $response = Invoke-RestMethod -Uri $PoeApiUrl -Method POST -Headers $headers -Body $body -ErrorAction Stop
        
        if ($response.create_bot_status -eq "success") {
            Write-Host "  Created bot on Poe" -ForegroundColor Green
        }
        elseif ($response.edit_bot_status -eq "success") {
            Write-Host "  Updated bot on Poe" -ForegroundColor Green
        }
        else {
            Write-Host "  Response: $($response | ConvertTo-Json -Compress)" -ForegroundColor Yellow
        }
        return $true
    }
    catch {
        $errorMessage = $_.Exception.Message
        if ($_.ErrorDetails.Message) {
            $errorMessage = $_.ErrorDetails.Message
        }
        Write-Host "  Error: $errorMessage" -ForegroundColor Red
        return $false
    }
}

# Validate path exists
if (-not (Test-Path $FullPath)) {
    Write-Error "Path not found: $FullPath"
    exit 1
}

# Get model files
if (Test-Path $FullPath -PathType Container) {
    $ModelFiles = Get-ChildItem -Path $FullPath -Filter "*.json"
}
else {
    $ModelFiles = @(Get-Item $FullPath)
}

if ($ModelFiles.Count -eq 0) {
    Write-Error "No model files found in: $FullPath"
    exit 1
}

Write-Host "Processing $($ModelFiles.Count) model file(s)..." -ForegroundColor Cyan
Write-Host "  Base URL: $BaseUrl" -ForegroundColor Gray
Write-Host "  Handle template: $Handle" -ForegroundColor Gray
Write-Host "  Public: $($Public.IsPresent)" -ForegroundColor Gray
Write-Host "  Publish to Poe: $($Publish.IsPresent)" -ForegroundColor Gray
Write-Host ""

$Created = 0
$Failed = 0

foreach ($File in $ModelFiles) {
    $Model = Get-Content -Raw $File.FullName | ConvertFrom-Json
    $BotHandle = Expand-Template -Template $Handle -Model $Model

    Write-Host "[$($Model.id)] -> $BotHandle" -ForegroundColor White

    $Bot = Build-BotPayload -Model $Model

    # Always save locally
    Save-BotLocally -Bot $Bot -ModelId $Model.id

    # Publish if requested
    if ($Publish.IsPresent) {
        $success = Publish-BotToPoe -Bot $Bot
        if ($success) {
            $Created++
        }
        else {
            $Failed++
        }
    }
    else {
        $Created++
    }
}

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Processed: $($ModelFiles.Count)"
Write-Host "  Succeeded: $Created"
if ($Failed -gt 0) {
    Write-Host "  Failed: $Failed" -ForegroundColor Red
}
