# Git-PullHere.ps1

# Navigate to the script's directory
Set-Location -Path $PSScriptRoot

# Check if .git folder exists
if (-not (Test-Path ".git")) {
    Write-Error "This directory is not a Git repository: $PSScriptRoot"
    exit 1
}

# Run git pull
Write-Host "Running 'git pull' in $PSScriptRoot..."
git pull

# Check for success
if ($LASTEXITCODE -eq 0) {
    Write-Host "Git pull completed successfully."
} else {
    Write-Warning "Git pull failed with exit code $LASTEXITCODE"
}
