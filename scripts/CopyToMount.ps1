param (
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,      # e.g. D:\Media\Videos

    [Parameter(Mandatory = $true)]
    [string]$MountName        # e.g. MediaMount
)

# Set source root (must end with backslash)
$sourceRoot = "D:\"

# Ensure path is rooted at D:\
if (-not ($SourcePath.ToLower().StartsWith($sourceRoot.ToLower()))) {
    Write-Error "Source path must start with $sourceRoot"
    exit 1
}

# Compute relative path (strip off D:\)
$relativePath = $SourcePath.Substring($sourceRoot.Length)

# Target root is C:\mount\$MountName
$targetRoot = "C:\mount\$MountName"

# Path to the Copy-WithRelativePath script (assumes in same folder)
$copyScript = Join-Path -Path $PSScriptRoot -ChildPath "Copy-WithRelativePath.ps1"

# Call the copy script
& $copyScript -SourceRoot $sourceRoot -TargetRoot $targetRoot -RelativePath $relativePath
