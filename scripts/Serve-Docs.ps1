<#
.SYNOPSIS
    Starts a local HTTP server for the docs/ site.

.DESCRIPTION
    Serves the docs/ folder on a local port using either:
      1. Python 3 http.server (preferred, widely available)
      2. Node.js npx serve
      3. dotnet-serve (if installed)
    The first available option is used automatically.

.PARAMETER Port
    The port to listen on. Default: 8080.

.PARAMETER DocsRoot
    Path to the docs folder. Default: ../docs relative to this script.

.PARAMETER Open
    Open the browser automatically after starting.

.EXAMPLE
    .\scripts\Serve-Docs.ps1
    .\scripts\Serve-Docs.ps1 -Port 3000 -Open
#>

[CmdletBinding()]
param(
    [int]$Port = 8080,
    [string]$DocsRoot = (Join-Path $PSScriptRoot '..\docs'),
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
$DocsRoot = Resolve-Path $DocsRoot
$url = "http://localhost:$Port"

Write-Host "Serving $DocsRoot on $url" -ForegroundColor Cyan
Write-Host "Press Ctrl+C to stop.`n" -ForegroundColor DarkGray

if ($Open) {
    # Fire-and-forget browser open after a small delay
    Start-Job -ScriptBlock {
        Start-Sleep -Seconds 2
        Start-Process $using:url
    } | Out-Null
}

# --- Try Python 3 ---
$python = Get-Command python3 -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python -ErrorAction SilentlyContinue }
if ($python) {
    try {
        $ver = & $python.Source --version 2>&1 | Out-String
        if ($ver -match 'Python 3') {
            Write-Host "Using Python 3 http.server" -ForegroundColor Green
            & $python.Source -m http.server $Port --directory $DocsRoot --bind 127.0.0.1
            return
        }
    }
    catch {
        # Python not actually available, fall through
    }
}

# --- Try Node.js npx serve ---
$npx = Get-Command npx -ErrorAction SilentlyContinue
if ($npx) {
    Write-Host "Using npx serve" -ForegroundColor Green
    & npx --yes serve $DocsRoot -l $Port --no-clipboard
    return
}

# --- Try dotnet-serve ---
$dotnetServe = Get-Command dotnet-serve -ErrorAction SilentlyContinue
if ($dotnetServe) {
    Write-Host "Using dotnet-serve" -ForegroundColor Green
    & dotnet-serve --directory $DocsRoot --port $Port
    return
}

# --- Fallback: built-in .NET HttpListener ---
Write-Host "Using built-in .NET HttpListener (no external tools found)" -ForegroundColor Yellow

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("$url/")
$listener.Start()
Write-Host "Listening on $url ..."

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $reqPath = $ctx.Request.Url.LocalPath.TrimStart('/')
        if (-not $reqPath) { $reqPath = 'index.html' }

        $filePath = Join-Path $DocsRoot $reqPath
        # If it's a directory, try index.html inside it
        if (Test-Path $filePath -PathType Container) {
            $filePath = Join-Path $filePath 'index.html'
        }

        if (Test-Path $filePath -PathType Leaf) {
            $bytes = [System.IO.File]::ReadAllBytes($filePath)
            $ext = [System.IO.Path]::GetExtension($filePath).ToLower()
            $mime = switch ($ext) {
                '.html' { 'text/html; charset=utf-8' }
                '.css'  { 'text/css; charset=utf-8' }
                '.js'   { 'application/javascript; charset=utf-8' }
                '.json' { 'application/json; charset=utf-8' }
                '.png'  { 'image/png' }
                '.jpg'  { 'image/jpeg' }
                '.svg'  { 'image/svg+xml' }
                '.ico'  { 'image/x-icon' }
                '.woff2'{ 'font/woff2' }
                default { 'application/octet-stream' }
            }
            $ctx.Response.ContentType = $mime
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.StatusCode = 200
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        else {
            $ctx.Response.StatusCode = 404
            $body = [System.Text.Encoding]::UTF8.GetBytes('404 Not Found')
            $ctx.Response.ContentType = 'text/plain'
            $ctx.Response.ContentLength64 = $body.Length
            $ctx.Response.OutputStream.Write($body, 0, $body.Length)
        }
        $ctx.Response.Close()
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
