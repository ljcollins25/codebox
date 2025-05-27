param (
    [Parameter(Mandatory = $true)]
    [string]$SourceUrl,       # e.g. https://<storage-account>.blob.core.windows.net/container/file.ext

    [Parameter(Mandatory = $false)]
    [string]$OutputPath       # Optional: Full path where file will be saved
)

# Set working directory to script location
$scriptDir = $PSScriptRoot
$azcopyExe = Join-Path $scriptDir "azcopy.exe"

# --- Step 1: Ensure azcopy.exe is available ---
if (-not (Test-Path $azcopyExe)) {
    Write-Host "azcopy.exe not found. Downloading AzCopy..."

    $downloadUrl = "https://aka.ms/downloadazcopy-v10-windows"
    $zipPath = Join-Path $scriptDir "azcopy.zip"
    $extractPath = Join-Path $scriptDir "azcopy_temp"

    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

    $exePath = Get-ChildItem -Path $extractPath -Recurse -Filter "azcopy.exe" | Select-Object -First 1
    if (-not $exePath) {
        Write-Error "Failed to locate azcopy.exe in extracted archive."
        exit 1
    }

    Copy-Item -Path $exePath.FullName -Destination $azcopyExe -Force

    # Cleanup
    Remove-Item $zipPath -Force
    Remove-Item $extractPath -Recurse -Force

    Write-Host "AzCopy downloaded to: $azcopyExe"
} else {
    Write-Host "azcopy.exe already present."
}

# --- Step 2: Perform azcopy login (browser prompt) ---
Write-Host "Logging into Azure via AzCopy (browser window will open)..."
& $azcopyExe login
if ($LASTEXITCODE -ne 0) {
    Write-Error "azcopy login failed."
    exit 1
}

# --- Step 3: Determine Output Path ---
if (-not $OutputPath) {
    $fileName = [System.IO.Path]::GetFileName($SourceUrl)
    $OutputPath = Join-Path $scriptDir $fileName
}

# --- Step 4: Perform download ---
Write-Host "Downloading file from: $SourceUrl"
& $azcopyExe copy $SourceUrl $OutputPath --overwrite=ifSourceNewer
if ($LASTEXITCODE -eq 0) {
    Write-Host "Download completed: $OutputPath"
} else {
    Write-Warning "AzCopy returned exit code $LASTEXITCODE"
}
