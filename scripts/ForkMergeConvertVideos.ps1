# Variables (set these as needed)
$splitFactor = 5

# Ensure ffmpeg is in PATH or specify full path
$ffmpeg = if ($env:FFMPEG_PATH) { $env:FFMPEG_PATH } else { "ffmpeg" }

# Recreate output directory
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $outputDir | Out-Null

# Create subdirectories
$splitDir = Join-Path $outputDir "splits"
$convertedDir = Join-Path $outputDir "converted"
New-Item -ItemType Directory -Path $splitDir, $convertedDir | Out-Null

# Get video duration in seconds
$durationOutput = & $ffmpeg -i $videoFile 2>&1 | Select-String "Duration"
if ($durationOutput -match "Duration: (\d+):(\d+):(\d+)\.(\d+)") {
    $hours = [int]$matches[1]
    $minutes = [int]$matches[2]
    $seconds = [int]$matches[3]
    $totalDuration = ($hours * 3600) + ($minutes * 60) + $seconds
} else {
    throw "Could not determine video duration."
}

# Calculate segment duration
$segmentDuration = [math]::Ceiling($totalDuration / $splitFactor)

# Split the video
for ($i = 0; $i -lt $splitFactor; $i++) {
    $startTime = $i * $segmentDuration
    $partPath = Join-Path $splitDir ("part_$i.mp4")
    & $ffmpeg -y -ss $startTime -i $videoFile -t $segmentDuration -c copy $partPath
}

# Convert parts in parallel
Get-ChildItem $splitDir -Filter "part_*.mp4" | ForEach-Object -Parallel {
    $ffmpeg = "ffmpeg"
    $inputFile = $_.FullName
    $outputFile = Join-Path $using:convertedDir ($_.BaseName + "_converted.mkv")

    & $ffmpeg -y -i $inputFile -c:v libx265 -c:a libopus $outputFile
}

# Wait briefly to ensure all converted files are flushed
Start-Sleep -Seconds 1

# Create concat list file
$concatListPath = Join-Path $outputDir "concat_list.txt"
Get-ChildItem $convertedDir -Filter "*_converted.mkv" | Sort-Object Name | ForEach-Object {
    "file '$($_.FullName)'" 
} | Set-Content -Path $concatListPath -Encoding ASCII

# Output joined filename
$joinedOutput = Join-Path $outputDir "joined_output.mkv"

# Run ffmpeg concat
& $ffmpeg -y -f concat -safe 0 -i $concatListPath -c copy $joinedOutput

Write-Host "`nJoined video created at:"
Write-Host $joinedOutput

# Get file sizes
$originalSizeBytes = (Get-Item $videoFile).Length
$splitSizeBytes = (Get-ChildItem $splitDir -File | Measure-Object -Property Length -Sum).Sum
$convertedSizeBytes = (Get-ChildItem $convertedDir -File | Measure-Object -Property Length -Sum).Sum

# Convert sizes to MB
$originalSizeMB = [math]::Round($originalSizeBytes / 1MB, 2)
$splitSizeMB = [math]::Round($splitSizeBytes / 1MB, 2)
$convertedSizeMB = [math]::Round($convertedSizeBytes / 1MB, 2)

# Print summary
Write-Host "`n=== Size Summary ==="
Write-Host "Original video file:           $originalSizeMB MB"
Write-Host "Total size of split files:     $splitSizeMB MB"
Write-Host "Total size of converted files: $convertedSizeMB MB"