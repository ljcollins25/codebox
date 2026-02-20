# Publish-PoeBots.ps1
# Wrapper to run the Playwright-based publish-bots.mjs script.
#
# Usage:
#   .\Publish-PoeBots.ps1                        # All bots in .\bots
#   .\Publish-PoeBots.ps1 -Path .\bots\my-bot.yml  # Single bot
#   .\Publish-PoeBots.ps1 -Timeout 120           # Custom timeout (seconds)

[CmdletBinding()]
param(
    [string]$Path = (Join-Path $PSScriptRoot "bots"),
    [int]$Timeout = 60,
    [int]$DebugPort = 9222,
    [string]$ChromiumPath = "C:\Program Files\Chromium\Application\chrome.exe"
)

$scriptDir = $PSScriptRoot
$scriptFile = Join-Path $scriptDir "publish-bots.mjs"

if (-not (Test-Path $scriptFile)) {
    Write-Host "Error: publish-bots.mjs not found at $scriptFile" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $Path)) {
    Write-Host "Error: Path not found: $Path" -ForegroundColor Red
    exit 1
}

# ── Ensure Chromium is running with --remote-debugging-port ──

$launchedChromium = $false

# Check if any Chromium process has the debugging port argument
$debuggable = Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match "--remote-debugging-port=$DebugPort" }

if ($debuggable) {
    Write-Host "Chromium already running with --remote-debugging-port=$DebugPort" -ForegroundColor Green
} else {
    if (-not (Test-Path $ChromiumPath)) {
        Write-Host "Error: Chromium not found at $ChromiumPath" -ForegroundColor Red
        Write-Host "  Specify path with -ChromiumPath" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Launching Chromium with --remote-debugging-port=$DebugPort ..." -ForegroundColor Cyan
    Start-Process -FilePath $ChromiumPath -ArgumentList "--remote-debugging-port=$DebugPort"
    $launchedChromium = $true

    # Wait for CDP to be ready
    $ready = $false
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $null = Invoke-RestMethod -Uri "http://localhost:$DebugPort/json/version" -TimeoutSec 2 -ErrorAction Stop
            $ready = $true
            break
        } catch {}
    }
    if (-not $ready) {
        Write-Host "Error: Chromium CDP not responding on port $DebugPort after 7.5s" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Chromium ready on port $DebugPort" -ForegroundColor Green
    Write-Host ""
    Write-Host "  *** Log into poe.com in the browser, then re-run this script. ***" -ForegroundColor Yellow
    exit 0
}

# Resolve to absolute path for node
$Path = (Resolve-Path $Path).Path

$timeoutMs = $Timeout * 1000
$stdout = Join-Path $env:TEMP "publish-bots-stdout.txt"
$stderr = Join-Path $env:TEMP "publish-bots-stderr.txt"

Write-Host "Publishing bots from: $Path" -ForegroundColor Cyan
Write-Host "  Script:  $scriptFile" -ForegroundColor DarkGray
Write-Host "  Timeout: ${Timeout}s" -ForegroundColor DarkGray
Write-Host ""

$proc = Start-Process -FilePath "node" `
    -ArgumentList $scriptFile, $Path `
    -NoNewWindow -PassThru `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -WorkingDirectory $scriptDir

if (-not $proc.WaitForExit($timeoutMs)) {
    $proc.Kill()
    Write-Host ""
    Write-Host "KILLED after ${Timeout}s timeout" -ForegroundColor Red
    Write-Host ""
}

# Stream output
if (Test-Path $stdout) {
    Get-Content $stdout | ForEach-Object { Write-Host $_ }
}

if (Test-Path $stderr) {
    $errContent = Get-Content $stderr -Raw
    if ($errContent.Trim()) {
        Write-Host ""
        Write-Host "--- STDERR ---" -ForegroundColor Yellow
        Write-Host $errContent -ForegroundColor Red
    }
}

exit $proc.ExitCode
