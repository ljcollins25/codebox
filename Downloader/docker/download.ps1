<#
.SYNOPSIS
    Download videos using yt-dlp with Brave browser cookies from the Docker container.

.DESCRIPTION
    Wraps the 'docker compose run' download flow. Launches Brave to refresh cookies,
    then runs yt-dlp with the provided arguments.

.PARAMETER Url
    The video URL to download.

.PARAMETER OutputFolder
    Subfolder under ./out/ to save downloads into (default: downloads).

.PARAMETER TargetUrl
    The site to navigate to for cookie refresh (default: https://youtube.com).

.PARAMETER BrowseWait
    Seconds to wait for the page to load before grabbing cookies (default: 3).

.PARAMETER YtDlpArgs
    Additional arguments passed directly to yt-dlp.

.EXAMPLE
    .\download.ps1 "https://youtube.com/watch?v=xxx"

.EXAMPLE
    .\download.ps1 "https://youtube.com/watch?v=xxx" -OutputFolder "music"

.EXAMPLE
    .\download.ps1 "https://youtube.com/watch?v=xxx" -TargetUrl "https://youtube.com" -BrowseWait 5

.EXAMPLE
    .\download.ps1 "https://youtube.com/watch?v=xxx" -YtDlpArgs "--format","bestvideo+bestaudio"
#>

param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Url,

    [string]$OutDir = 'downloads',

    [string]$TargetUrl,

    [int]$BrowseWait,

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$YtDlpArgs
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot

try {
    $env:TARGET_URL = if ($TargetUrl) { $TargetUrl } else { $null }
    $env:BROWSE_WAIT = if ($BrowseWait) { "$BrowseWait" } else { $null }
    $env:OUTPUT_FOLDER = $OutDir
    $env:COMPOSE_PROFILES = 'download'

    $args = @('compose', 'run', '--rm', 'download', $Url)
    if ($YtDlpArgs) {
        $args += $YtDlpArgs
    }

    & docker @args
}
finally {
    Remove-Item Env:\TARGET_URL -ErrorAction SilentlyContinue
    Remove-Item Env:\BROWSE_WAIT -ErrorAction SilentlyContinue
    Remove-Item Env:\OUTPUT_FOLDER -ErrorAction SilentlyContinue
    Remove-Item Env:\COMPOSE_PROFILES -ErrorAction SilentlyContinue
    Pop-Location
}
