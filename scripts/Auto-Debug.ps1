param(
    [string]$ExeName,
    [switch]$Disable
)

$regPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exeName"
$debuggerPath = "C:\Windows\system32\vsjitdebugger.exe"

if ($Disable) {
    if (Test-Path $regPath) {
        Remove-Item -Path $regPath -Recurse -Force
        Write-Host "❌ Debugger removed for $exeName"
    } else {
        Write-Host "ℹ️ Debugger not set for $exeName"
    }
}
else {
    if (-not (Test-Path $debuggerPath)) {
        Write-Error "Debugger not found at: $debuggerPath"
        exit 1
    }

    if (-not (Test-Path $regPath)) {
        New-Item -Path $regPath -Force | Out-Null
    }

    Set-ItemProperty -Path $regPath -Name "Debugger" -Value "`"$debuggerPath`""
    Write-Host "✅ Debugger attached to $exeName"
}
