<#
.SYNOPSIS
Register a name on the tunnel bus and keep a stock chisel client connected (Windows, Linux, macOS).
.EXAMPLE
./tunnel-bus-provider.ps1 -Bus https://tunnel-bus.X.workers.dev -Name myapp -Port 3000
The admin token comes from -Token or $env:TUNNEL_BUS_ADMIN_TOKEN.
Behind Cloudflare Access set CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET (a service token); they are sent
on the registration call and to chisel (--header). -DryRun prints the headers and chisel arguments (for tests).
#>
param(
  [string]$Bus = $env:TUNNEL_BUS_URL,
  [Parameter(Mandatory)][string]$Name,
  [Parameter(Mandatory)][int]$Port,
  [string]$Token = $env:TUNNEL_BUS_ADMIN_TOKEN,
  [string]$TargetHost = 'localhost',
  [string]$ChiselVersion = '1.10.1',
  [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if (-not $Bus -or -not $Token) { throw 'need -Bus and a token (-Token or TUNNEL_BUS_ADMIN_TOKEN)' }
$Bus = $Bus.TrimEnd('/')
$id = $env:CF_ACCESS_CLIENT_ID; $secret = $env:CF_ACCESS_CLIENT_SECRET
if ([bool]$id -ne [bool]$secret) { throw 'set both CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET' }
$access = [ordered]@{}
$chiselHeaderArgs = @()
if ($id) {
  $access['CF-Access-Client-Id'] = $id; $access['CF-Access-Client-Secret'] = $secret
  foreach ($k in $access.Keys) { $chiselHeaderArgs += @('--header', "$($k): $($access[$k])") }
}
if ($DryRun) {
  foreach ($k in $access.Keys) { Write-Output "REGISTER-HEADER: $($k): $($access[$k])" }
  Write-Output "REGISTER-URL: $Bus/_api/register"
  Write-Output ("CHISEL-ARGS: client --keepalive 25s --auth USER:PASS " + (($chiselHeaderArgs -join ' ') + " $Bus/_chisel R:PORT:${TargetHost}:$Port").TrimStart())
  return
}

function Get-Chisel {
  $c = Get-Command chisel -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  $isWin = $PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows
  $os = if ($isWin) { 'windows' } elseif ($IsMacOS) { 'darwin' } else { 'linux' }
  $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) { 'Arm64' { 'arm64' } default { 'amd64' } }
  $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } elseif ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME } else { Join-Path $HOME '.cache' }
  $dir = Join-Path $base 'tunnel-bus'
  $exe = Join-Path $dir ("chisel-$ChiselVersion" + $(if ($isWin) { '.exe' } else { '' }))
  if (-not (Test-Path $exe)) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $ext = if ($isWin) { 'zip' } else { 'gz' }
    $url = "https://github.com/jpillora/chisel/releases/download/v$ChiselVersion/chisel_${ChiselVersion}_${os}_${arch}.$ext"
    $dl = Join-Path $dir "chisel.$ext"
    Write-Host "downloading $url"
    Invoke-WebRequest $url -OutFile $dl
    if ($isWin) {
      $tmp = Join-Path $dir 'unzip'
      Expand-Archive $dl -DestinationPath $tmp -Force
      Move-Item (Join-Path $tmp 'chisel.exe') $exe -Force
      Remove-Item $tmp -Recurse -Force
    } else {
      $in = [IO.File]::OpenRead($dl); $gz = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
      $out = [IO.File]::Create($exe); $gz.CopyTo($out); $out.Close(); $gz.Close(); $in.Close()
      chmod +x $exe
    }
    Remove-Item $dl -Force
  }
  return $exe
}

$chisel = Get-Chisel
while ($true) {
  try {
    $r = Invoke-RestMethod -Method Post -Uri "$Bus/_api/register" -ContentType 'application/json' `
      -Headers (@{ Authorization = "Bearer $Token" } + $access) -Body (@{ name = $Name } | ConvertTo-Json)
    Write-Host "registered: $Bus/$Name/  (server port $($r.port))"
    Start-Sleep -Seconds 1 # let chisel reload its authfile
    & $chisel client --keepalive 25s --auth "$($r.user):$($r.password)" @chiselHeaderArgs "$Bus/_chisel" "R:$($r.port):${TargetHost}:$Port"
    Write-Host 'chisel exited; re-registering in 3s'
  } catch { Write-Host "error: $($_.Exception.Message); retrying in 5s"; Start-Sleep -Seconds 2 }
  Start-Sleep -Seconds 3
}
