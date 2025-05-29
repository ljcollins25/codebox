param (
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

mkdir "C:\mount\$Name"

& rclone mount "${Name}:media\" "C:\mount\$Name" `
    --network-mode `
    --vfs-cache-mode full `
    --vfs-cache-max-size 10G `
    --vfs-cache-max-age 24h `
    @ExtraArgs
