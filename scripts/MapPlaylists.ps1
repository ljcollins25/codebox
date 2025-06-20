# URL for the current user's playlists (library). Adjust if necessary.
$playlistUrl = "https://www.youtube.com/feed/playlists"

# Run yt-dlp with options to dump playlist information in JSON.
# Note: Make sure yt-dlp is in your PATH or provide its full path.
$ytDlpOutput = yt-dlp $playlistUrl --flat-playlist --dump-json --cookies-from-browser chromium

# Split the output into individual JSON objects (one per line).
$lines = $ytDlpOutput -split "`n"

# Build a map (hashtable) of playlist title -> id.
$playlistMap = @{}
foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }

    try {
        $entry = $trimmed | ConvertFrom-Json
        # Assuming the JSON has properties "title" and "id"
        $playlistMap[$entry.title] = $entry.id
    }
    catch {
        Write-Warning "Failed to parse JSON: $trimmed"
    }
}

# Write the playlist map to playlists.json next to the current script file
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputPath = Join-Path $scriptDir 'playlists.json'
$playlistMap | ConvertTo-Json -Depth 3 | Set-Content -Path $outputPath -Encoding UTF8