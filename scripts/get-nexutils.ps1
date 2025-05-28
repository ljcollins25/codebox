# Download-Rclone.ps1

# URL of the rclone.exe binary
$downloadUrl = "https://github.com/ljcollins25/codebox/releases/download/latest/nexutils.exe"

# Destination path (same as the script's directory)
$scriptDir = $PSScriptRoot
$destinationPath = Join-Path -Path $scriptDir -ChildPath "nexutils.exe"

Write-Host "Downloading nexutils.exe to: $destinationPath"

try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $destinationPath -UseBasicParsing
    Write-Host "Download complete."
} catch {
    Write-Error "Download failed: $_"
}
