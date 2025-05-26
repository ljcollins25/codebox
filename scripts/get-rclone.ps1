# Download-Rclone.ps1

# URL of the rclone.exe binary
$downloadUrl = "https://github.com/Xollective/PublicToolsRelease/releases/download/latest/rclone.exe"

# Destination path (same as the script's directory)
$scriptDir = $PSScriptRoot
$destinationPath = Join-Path -Path $scriptDir -ChildPath "rclone.exe"

Write-Host "Downloading rclone.exe to: $destinationPath"

try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $destinationPath -UseBasicParsing
    Write-Host "Download complete."
} catch {
    Write-Error "Download failed: $_"
}
