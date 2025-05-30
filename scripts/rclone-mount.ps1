param (
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

cmd /C mkdir "C:\mount"

& rclone mount "${Name}:media\" "C:\mount\$Name" `
    --vfs-cache-mode full `
    --vfs-cache-max-size 10G `
    --vfs-cache-max-age 24h `
    @ExtraArgs
