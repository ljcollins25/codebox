<#
.SYNOPSIS
Downloads metadata from a YouTube playlist or channel and saves it as a JSON array.

.PARAMETER OutputFile
Path to the JSON file to write.

.PARAMETER Source
A single YouTube playlist or channel URL.

.PARAMETER LiveType
Optional: Filter for live content.
  - "was_live": only videos that were streamed live.
  - "never_live": only videos that were never streamed live.
  - "all": no filtering (default).

.PARAMETER AdditionalArgs
Optional: Additional arguments to pass directly to yt-dlp (e.g. "--playlist-end 10").

.EXAMPLES

# 1. Watch Later playlist (requires cookies.txt in script folder)
.\Download-YouTubeMetadata.ps1 `
  -OutputFile "watch_later.json" `
  -Source "https://www.youtube.com/playlist?list=WL"

# 2. Public playlist
.\Download-YouTubeMetadata.ps1 `
  -OutputFile "playlist.json" `
  -Source "https://www.youtube.com/playlist?list=PLrEnWoR732-BHrPp_Pm8_VleD68f9s14-"

# 3. Channel: all videos
.\Download-YouTubeMetadata.ps1 `
  -OutputFile "channel_all.json" `
  -Source "https://www.youtube.com/@Veritasium"

# 4. Channel: only live streams (Live tab)
.\Download-YouTubeMetadata.ps1 `
  -OutputFile "channel_live.json" `
  -Source "https://www.youtube.com/@Veritasium" `
  -LiveType "was_live"

# 5. Channel: only non-live videos, max 10
.\Download-YouTubeMetadata.ps1 `
  -OutputFile "channel_nonlive.json" `
  -Source "https://www.youtube.com/@Veritasium" `
  -LiveType "never_live" `
  -AdditionalArgs "--playlist-end 10"
#>

param (
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,

    [Parameter(Mandatory = $true, Position = 1)]
    [string]$OutputFile,

    [string]$CookiesPath,

    [ValidateSet("was_live", "never_live", "all")]
    [string]$LiveType = "all",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalArgs
)

# Optional: path to cookies.txt for authenticated access
if (-not $CookiesPath) {
  $parentDir = Split-Path -Parent $OutputFile
  $CookiesPath = "$parentDir\cookies.txt"
}
$useCookies = Test-Path $cookiesPath


Write-Host "Parameters:"
Write-Host "  Source: $Source"
Write-Host "  OutputFile: $OutputFile"
Write-Host "  CookiesPath: $CookiesPath"
Write-Host "  LiveType: $LiveType"
Write-Host "  AdditionalArgs: $($AdditionalArgs -join ', ')"

# Temporary file for JSONL output
$tempFile = New-TemporaryFile

Write-Host "📺 Processing source: $Source"

$args = @(
    "--flat-playlist",
    "--no-warnings",
    "--print",
    "%()j"
    "-o",
    $OutputFile
)

# Add live-type filtering
switch ($LiveType) {
    "was_live"    { $args += @("--match-filter", "live_status = 'was_live'") }
    "never_live"  { $args += @("--match-filter", "live_status != 'was_live'") }
    default       { }  # no filter
}

# Add cookies if present
if ($useCookies) {
    $args += @("--cookies", $CookiesPath)
}

# Add user-supplied extra args and the source URL
$args += $AdditionalArgs
$args += $Source

# Run yt-dlp
Write-Host "yt-dlp $($args -join ' ')"
# return


Write-Host "📦 Writing output to $OutputFile..."
yt-dlp @args

# Convert JSONL to a single JSON array
# v$jsonArray = Get-Content $tempFile | ForEach-Object { $_ | ConvertFrom-Json }
# v$jsonArray | ConvertTo-Json -Depth 10 | Set-Content $OutputFile -Encoding UTF8

# Remove-Item $tempFile
Write-Host "✅ Done: Metadata saved to $OutputFile"
