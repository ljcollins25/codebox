# Add-PathToSystem.ps1

param (
    [string]$NewPath = $PSScriptRoot
)

# Set environment variable for the current process and Machine
foreach ($scope in @("Process", "Machine")) {
    # Get current system PATH
    $existingPath = [Environment]::GetEnvironmentVariable("Path", $scope)

    # Check if the path is already in PATH
    if ($existingPath.Split(';') -contains $NewPath) {
        Write-Host "Path '$NewPath' is already in the system PATH."
    } else {
        # Append new path
        $updatedPath = "$existingPath;$NewPath"

        # Set the new system PATH
        [Environment]::SetEnvironmentVariable("Path", $updatedPath, $scope)
        Write-Host "Path '$NewPath' has been added to the system PATH."

        # Inform user about session reload
        Write-Host "You may need to restart your terminal or log off/log on for changes to take effect."
    }
}
