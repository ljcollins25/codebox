# Add-PathToSystem.ps1

param (
    [Parameter(Mandatory = $true)]
    [string]$NewPath
)

# Get current system PATH
$existingPath = [Environment]::GetEnvironmentVariable("Path", "Machine")

# Check if the path is already in PATH
if ($existingPath.Split(';') -contains $NewPath) {
    Write-Host "Path '$NewPath' is already in the system PATH."
} else {
    # Append new path
    $updatedPath = "$existingPath;$NewPath"

    # Set the new system PATH
    [Environment]::SetEnvironmentVariable("Path", $updatedPath, "Machine")
    Write-Host "Path '$NewPath' has been added to the system PATH."

    # Notify user that a reboot or logoff may be required
    Write-Host "You may need to restart your session or log off for changes to take full effect."
}
