param (
    [string]$Playlist = "WL",
    [string]$TargetPath = "C:\mount\youtube"
)

$ErrorActionPreference = 'Stop'

cmd /C mkdir $TargetPath

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$playlistsFile = Join-Path $scriptDir "playlists.json"

if (Test-Path $playlistsFile) {
    $playlists = Get-Content $playlistsFile | ConvertFrom-Json -AsHashtable
    if ($playlists.ContainsKey($Playlist)) {
        $PlaylistId = $playlists[$Playlist]
    }
    elseif ($Playlist -eq "WL") {
        $PlaylistId = $Playlist
    }
    else {
        throw "Unknown playlist $Playlist"
    }
}

yt-dlp --cookies "$TargetPath\cookies.txt" --flat-playlist -J "https://www.youtube.com/playlist?list=$PlaylistId" > "$TargetPath\$Playlist.json"
