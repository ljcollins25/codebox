<#
.SYNOPSIS
  Creates a dynamically expanding VHDX, formats it as ReFS, mounts it, and returns the root path (e.g. "R:\").
.DESCRIPTION
  Needs an elevated session with the Hyper-V PowerShell module or diskpart. Uses diskpart (always present on
  Windows Server) to create and attach the VHDX, then Storage cmdlets to partition and format it.
  The path is written to the pipeline; with -GitHubEnv it is also appended to $env:GITHUB_ENV as FSDEDUP_REFS_TEST_ROOT.
#>
[CmdletBinding()]
param(
    [string]$VhdPath = (Join-Path ($env:RUNNER_TEMP ?? $env:TEMP) 'fsdedup-refs-test.vhdx'),
    [int]$SizeGB = 4,
    [string]$DriveLetter = 'R',
    [switch]$GitHubEnv
)
$ErrorActionPreference = 'Stop'

if (Test-Path $VhdPath) { & (Join-Path $PSScriptRoot 'Remove-RefsTestVolume.ps1') -VhdPath $VhdPath | Out-Null }
$sizeMb = $SizeGB * 1024
$script = Join-Path ([IO.Path]::GetTempPath()) 'fsdedup-create-vdisk.txt'
Set-Content -Path $script -Encoding ascii -Value @(
    "create vdisk file=`"$VhdPath`" maximum=$sizeMb type=expandable",
    "attach vdisk")
$out = diskpart /s $script
if ($LASTEXITCODE -ne 0) { throw "diskpart failed: $out" }
Remove-Item $script -ErrorAction SilentlyContinue

$disk = Get-DiskImage -ImagePath $VhdPath | Get-Disk
Initialize-Disk -Number $disk.Number -PartitionStyle GPT -ErrorAction Stop
$part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter $DriveLetter
Format-Volume -Partition $part -FileSystem ReFS -NewFileSystemLabel 'FsDedupTest' -Confirm:$false -Force | Out-Null
$root = "${DriveLetter}:\"
if ((Get-Volume -DriveLetter $DriveLetter).FileSystemType -ne 'ReFS') { throw "Volume $root is not ReFS" }
if ($GitHubEnv -and $env:GITHUB_ENV) { "FSDEDUP_REFS_TEST_ROOT=$root" | Out-File -Append -Encoding utf8 $env:GITHUB_ENV }
$root
