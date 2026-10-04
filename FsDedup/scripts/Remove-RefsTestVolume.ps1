<#
.SYNOPSIS
  Detaches and deletes the VHDX made by New-RefsTestVolume.ps1.
#>
[CmdletBinding()]
param([string]$VhdPath = (Join-Path ($env:RUNNER_TEMP ?? $env:TEMP) 'fsdedup-refs-test.vhdx'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $VhdPath)) { return }
$script = Join-Path ([IO.Path]::GetTempPath()) 'fsdedup-detach-vdisk.txt'
Set-Content -Path $script -Encoding ascii -Value @("select vdisk file=`"$VhdPath`"", 'detach vdisk')
diskpart /s $script | Out-Null
Remove-Item $script -ErrorAction SilentlyContinue
Remove-Item $VhdPath -Force
