# Get-PoeModels.ps1
# Lists all available models from the Poe Creator API and saves to models.json.
#
# Usage:
#   .\Get-PoeModels.ps1                          # No auth (works, endpoint is public)
#   .\Get-PoeModels.ps1 -ApiKey "sk_..."          # With API key
#   .\Get-PoeModels.ps1 -OutFile "custom.json"    # Custom output path

[CmdletBinding()]
param(
    [string]$ApiKey,
    [string]$OutFile = (Join-Path $PSScriptRoot "models.json")
)

$url = "https://api.poe.com/v1/models"

$headers = @{
    "Accept" = "application/json"
}
if ($ApiKey) {
    $headers["Authorization"] = "Bearer $ApiKey"
}

try {
    Write-Host "Fetching models from $url ..." -ForegroundColor Cyan

    $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get -ErrorAction Stop

    $models = $response.data
    if (-not $models) {
        Write-Warning "No models returned from API."
        exit 1
    }

    Write-Host "  $($models.Count) models returned" -ForegroundColor Green

    # Pretty-print to file
    $json = $response | ConvertTo-Json -Depth 10
    Set-Content -Path $OutFile -Value $json -Encoding utf8

    Write-Host "  Saved to $OutFile" -ForegroundColor Green
}
catch {
    Write-Host "Error: $_" -ForegroundColor Red
    exit 1
}
