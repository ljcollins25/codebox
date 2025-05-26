# Copy-WithRelativePath.ps1

param (
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$TargetRoot,

    [Parameter(Mandatory = $true)]
    [string]$RelativePath
)

# Build full source and target paths
$SourcePath = Join-Path -Path $SourceRoot -ChildPath $RelativePath
$TargetPath = Join-Path -Path $TargetRoot -ChildPath $RelativePath

# Ensure the source path exists
if (-not (Test-Path -Path $SourcePath)) {
    Write-Error "Source path does not exist: $SourcePath"
    exit 1
}

# Create target directory if it doesn't exist
if (-not (Test-Path -Path $TargetPath)) {
    New-Item -Path $TargetPath -ItemType Directory -Force | Out-Null
}

# Run robocopy
$rc = robocopy $SourcePath $TargetPath /E /COPY:DAT /R:3 /W:5

# robocopy returns exit codes using a bitmask, 0 and 1 are generally OK
if ($LASTEXITCODE -le 1) {
    Write-Host "Robocopy completed successfully."
} else {
    Write-Warning "Robocopy returned exit code $LASTEXITCODE"
}
