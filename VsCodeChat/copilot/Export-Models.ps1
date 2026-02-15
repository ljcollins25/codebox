# Export-Models.ps1
# Exports individual model JSON files from models.json, excluding models that match filter terms
# Usage: .\Export-Models.ps1 [-Url <url>]

param(
    [Parameter(Position = 0)]
    [string]$Url
)

# Static filter list - models containing these terms (case insensitive) in id or name will be excluded
$ExcludeTerms = @(
    "Internal",
    "-0",
    "embedding"
    # Add more terms here as needed
)

# Get script directory for relative paths
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Get-Location }

$ModelsJsonPath = Join-Path $ScriptDir "models.json"
$OutputDir = Join-Path $ScriptDir "models"

# Fetch from URL or read local file
if ($Url) {
    Write-Host "Fetching models from: $Url" -ForegroundColor Cyan
    try {
        $ModelsJson = Invoke-RestMethod -Uri $Url -ErrorAction Stop
        # Also save to local file for reference
        $ModelsJson | ConvertTo-Json -Depth 10 | Set-Content -Path $ModelsJsonPath -Encoding UTF8
        Write-Host "Saved models.json locally" -ForegroundColor Cyan
    }
    catch {
        Write-Error "Failed to fetch models from URL: $_"
        exit 1
    }
}
else {
    # Validate source file exists
    if (-not (Test-Path $ModelsJsonPath)) {
        Write-Error "models.json not found at: $ModelsJsonPath. Provide a URL or place models.json in the script directory."
        exit 1
    }
    $ModelsJson = Get-Content -Raw $ModelsJsonPath | ConvertFrom-Json
}

# Clear and recreate output directory
if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
    Write-Host "Cleared directory: $OutputDir" -ForegroundColor Cyan
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null
Write-Host "Created directory: $OutputDir" -ForegroundColor Cyan

# Load models
$ModelsJson = Get-Content -Raw $ModelsJsonPath | ConvertFrom-Json

$Created = 0
$Skipped = 0

foreach ($Model in $ModelsJson.data) {
    $ModelId = $Model.id
    $ModelName = $Model.name

    # Check if model should be excluded
    $ShouldExclude = $false
    foreach ($Term in $ExcludeTerms) {
        if ($ModelId -like "*$Term*" -or $ModelName -like "*$Term*") {
            $ShouldExclude = $true
            Write-Host "Skipped: $ModelId (matches filter: '$Term')" -ForegroundColor Yellow
            break
        }
    }

    if ($ShouldExclude) {
        $Skipped++
        continue
    }

    # Export model to individual file
    $FileName = Join-Path $OutputDir "$ModelId.json"
    $Model | ConvertTo-Json -Depth 10 | Set-Content -Path $FileName -Encoding UTF8
    Write-Host "Created: $ModelId.json" -ForegroundColor Green
    $Created++
}

Write-Host "`nSummary:" -ForegroundColor Cyan
Write-Host "  Created: $Created files"
Write-Host "  Skipped: $Skipped files"
