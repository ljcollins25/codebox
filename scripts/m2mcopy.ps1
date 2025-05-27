param (
    [Parameter(Mandatory=$true)]
    [string]$SourcePath,

    [Parameter(Mandatory=$true)]
    [string]$TargetMount
)

$mountRoot = "C:\mount"
$sourcePath = (Resolve-Path -LiteralPath $SourcePath).Path

# Ensure sourcePath is under C:\mount\
if (-not ($sourcePath -like "$mountRoot*")) {
    Write-Error "Source path must be under $mountRoot"
    exit 1
}

# Remove the mount root from the full path
$relativeToMount = $sourcePath.Substring($mountRoot.Length).TrimStart('\')

# Split into source mount name and relative path
$parts = $relativeToMount -split '\\', 2
$sourceMount = $parts[0]
$relativePath = if ($parts.Length -gt 1) { $parts[1] } else { "" }

Write-Host "Relative path: $relativePath"

# Target root is C:\mount\$TargetMount
$targetRoot = "C:\mount\$TargetMount"
$sourceRoot = "C:\mount\$sourceMount"

Write-Host "Source root: $sourceRoot"
Write-Host "Target root: $targetRoot"


# Path to the Copy-WithRelativePath script (assumes in same folder)
$copyScript = Join-Path -Path $PSScriptRoot -ChildPath "Copy-WithRelativePath.ps1"

# Call the copy script
& $copyScript -SourceRoot $sourceRoot -TargetRoot $targetRoot -RelativePath $relativePath
