<#
.SYNOPSIS
    Regenerates docs/apps.json by scanning sub-directories of docs/ for index.html.

.DESCRIPTION
    Walks each child folder under docs/. If the folder contains an index.html it is
    treated as an app. The script pulls a title from the first H1 in spec.md
    (if present) and a description from the paragraph after ## Overview.

    Output is written to docs/apps.json.

.EXAMPLE
    .\scripts\Regenerate-Apps.ps1
    .\scripts\Regenerate-Apps.ps1 -DocsRoot "Q:\src\codebox\docs"
#>

[CmdletBinding()]
param(
    [string]$DocsRoot
)

if (-not $DocsRoot) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $DocsRoot = Join-Path $scriptDir '..\docs'
}

$ErrorActionPreference = 'Stop'
$DocsRoot = Resolve-Path $DocsRoot

Write-Host "Scanning $DocsRoot for apps..." -ForegroundColor Cyan

# Icon lookup by keyword pattern
$IconTable = @(
    @{ Pattern = 'azure|storage|explorer'; Icon = [string][char]0x2601 }
    @{ Pattern = 'pdf|doc|sign|edit';      Icon = [string][char]0x270F }
    @{ Pattern = 'video|youtube';          Icon = [string][char]0x25B6 }
    @{ Pattern = 'chat|message';           Icon = [string][char]0x25C6 }
    @{ Pattern = 'search';                 Icon = [string][char]0x25CB }
)
$DefaultIcon = [string][char]0x25A0

$apps = [System.Collections.ArrayList]::new()

Get-ChildItem -Path $DocsRoot -Directory | ForEach-Object {
    $dir = $_
    $indexPath = Join-Path $dir.FullName 'index.html'
    if (-not (Test-Path $indexPath)) {
        Write-Verbose "Skipping $($dir.Name) - no index.html"
        return
    }

    $id = $dir.Name
    $name = $id
    $description = ''

    # Try to extract info from spec.md
    $specPath = Join-Path $dir.FullName 'spec.md'
    if (Test-Path $specPath) {
        $lines = Get-Content $specPath -Encoding UTF8 -TotalCount 30
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]

            # First H1 becomes the title
            if ($line -match '^#\s+(.+)$' -and $name -eq $id) {
                $raw = $Matches[1]
                # Strip from first separator (dash, em-dash, en-dash, pipe) onward
                $cleaned = $raw -replace '[\u2014\u2013]', '-'
                $idx = $cleaned.IndexOf(' - ')
                if ($idx -gt 0) {
                    $name = $cleaned.Substring(0, $idx).Trim()
                }
                else {
                    $name = $cleaned.Trim()
                }
                continue
            }

            # First non-empty line after ## Overview is description
            if ($line -match '^##\s+Overview') {
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    $candidate = $lines[$j].Trim()
                    if ($candidate -and -not $candidate.StartsWith('#') -and $candidate -ne '---') {
                        $description = $candidate
                        break
                    }
                }
            }
        }
    }

    # Pick icon
    $icon = $DefaultIcon
    foreach ($entry in $IconTable) {
        if ($id -match $entry.Pattern) {
            $icon = $entry.Icon
            break
        }
    }

    # Build tags from folder name segments
    $segments = @($id) + @($id -split '[-_]' | Where-Object { $_.Length -gt 2 })
    $tags = @($segments | Select-Object -Unique)

    $null = $apps.Add([ordered]@{
        id          = $id
        name        = $name
        description = $description
        path        = "$id/"
        icon        = $icon
        tags        = $tags
    })

    Write-Host "  + $id -> $name" -ForegroundColor Green
}

$outPath = Join-Path $DocsRoot 'apps.json'
$json = $apps | ConvertTo-Json -Depth 4
# Ensure UTF-8 without BOM
[System.IO.File]::WriteAllText($outPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Wrote $($apps.Count) app(s) to $outPath" -ForegroundColor Cyan
