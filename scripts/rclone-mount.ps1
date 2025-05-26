param (
    [Parameter(Mandatory = $true)]
    [string]$Name
)

& rclone mount "${Name}:media\" "C:\mount\$Name" --network-mode --vfs-cache-mode full --vfs-cache-max-size 10G --vfs-cache-max-age 24h
