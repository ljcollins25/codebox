# Get-PoeBots.ps1
# Lists all bots from Poe API and saves to bots.json
# Usage: .\Get-PoeBots.ps1 -PoeKey <key>

param(
    [Parameter(Mandatory = $true)]
    [string]$PoeKey
)

# Get script directory for relative paths
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Get-Location }

$OutputFile = Join-Path $ScriptDir "bots.json"

# Poe API base URL
$PoeApiUrl = "https://api.poe.com/bots"

$headers = @{
    "Authorization" = "Bearer $PoeKey"
}

Write-Host "Fetching bots from Poe API..." -ForegroundColor Cyan

try {
    $response = Invoke-RestMethod -Uri $PoeApiUrl -Method GET -Headers $headers -ErrorAction Stop
    
    $botCount = $response.bots.Count
    Write-Host "Found $botCount bot(s)" -ForegroundColor Green
    
    # Save to file
    $response | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputFile -Encoding UTF8
    Write-Host "Saved to: $OutputFile" -ForegroundColor Green
    
    # Display summary
    if ($botCount -gt 0) {
        Write-Host ""
        Write-Host "Bots:" -ForegroundColor Cyan
        foreach ($bot in $response.bots) {
            $visibility = if ($bot.is_private) { "private" } else { "public" }
            $apiType = if ($bot.api_bot_settings.api_type) { $bot.api_bot_settings.api_type } else { "n/a" }
            Write-Host "  $($bot.handle) [$visibility] - $apiType" -ForegroundColor White
        }
    }
}
catch {
    $errorMessage = $_.Exception.Message
    if ($_.ErrorDetails.Message) {
        $errorMessage = $_.ErrorDetails.Message
    }
    Write-Error "Failed to fetch bots: $errorMessage"
    exit 1
}
