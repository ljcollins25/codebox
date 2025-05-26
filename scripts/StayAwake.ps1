# -------------------------------
# 1. Permanently disable hibernation (system-wide)
# -------------------------------
Write-Output "Disabling hibernation..."
powercfg -hibernate off

# -------------------------------
# 2. Set power plan to never sleep or hibernate (persistent)
# -------------------------------
Write-Output "Updating active power plan settings..."
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /change monitor-timeout-ac 0

# -------------------------------
# 3. Prevent sleep while script runs using SetThreadExecutionState
# -------------------------------
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Power {
    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(int esFlags);
}
"@

$esFlags = 0x80000000 -bor 0x00000001 -bor 0x00000002
[Power]::SetThreadExecutionState($esFlags)
Write-Output "Temporary system sleep/hibernation/display off prevention activated."

# -------------------------------
# 4. Optional: Simulate keypress every X minutes (fallback)
# -------------------------------
Add-Type -AssemblyName System.Windows.Forms

function Simulate-Keypress {
    [System.Windows.Forms.SendKeys]::SendWait("{SCROLLLOCK}")
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait("{SCROLLLOCK}")
    Write-Output "Simulated keypress to reset idle timer."
}

# -------------------------------
# Main loop to keep the script running
# -------------------------------
Write-Output "System is now locked in an 'awake' state. Press Ctrl+C to exit."

while ($true) {
    [Power]::SetThreadExecutionState($esFlags)  # Refresh keep-awake state
    Simulate-Keypress
    Start-Sleep -Seconds 300  # Every 5 minutes
}
