$baseFolder = "D:\Media\Conversion"
$inputFolder = "$baseFolder\source"
$intermediateFolder = "$baseFolder\middle"
$outputFolder = "$baseFolder\target"
mkdir $outputFolder -Force
mkdir $intermediateFolder -Force

# Duration in seconds for 29:30
$segmentDuration = 1770
# Video resolution (smaller black screen)
$videoSize = "640x360"

# Default value for audio file extension (empty by default)
$audioExt = "m4a"  # Can be left empty to set default behavior

# Step 1: Move video files to intermediate folder
Get-ChildItem -Path $inputFolder -Include *.mp4, *.avi, *.mkv -File -Recurse | ForEach-Object {
    $input = $_.FullName
    $base = $_.BaseName
    $intermediateFilePath = Join-Path -Path $intermediateFolder -ChildPath $_.Name
    
    Write-Host "Processing file: $input"
    Write-Host "Moving to intermediate folder: $intermediateFilePath"
    # Move the file to the intermediate folder
    . cmd /c move "$input" "$intermediateFilePath"

    # Set audio file extension based on file type if not manually set
    if ($audioExt -eq "") {
        if ($input -match "\.mp4$") {
            $audioExt = "m4a"  
        } else {
            $audioExt = "mp3"
        }
    }

    try {
        $tempAudioPattern = "$outputFolder\$base-audio-%03d.$audioExt"

        # Step 2: Always extract segmented audio (always transcoded to the selected audio extension)
        ffmpeg -i "${intermediateFilePath}" -f segment -segment_time $segmentDuration -vn -acodec aac $tempAudioPattern

        # Step 3: Add blank video to each audio segment
        Get-ChildItem -Path $outputFolder -Filter "$base-audio-*$audioExt" | ForEach-Object {
            $audio = $_.FullName
            $segId = $_.BaseName -replace '^.*audio-', ''
            $outputFile = "$outputFolder\$base-$segId.mp4"
            ffmpeg -f lavfi -i "color=size=${videoSize}:rate=30:duration=$segmentDuration" -i $audio -map 1:a -map 0:v -shortest -c:v libx264 -preset ultrafast -crf 30 -c:a copy "$outputFile"
        }
    } catch {
        Write-Host "Error processing file ${input}: $_"
    } finally {
        # Clean up temporary audio files
        Get-ChildItem -Path $outputFolder -Filter "$base-audio-*$audioExt" | ForEach-Object {
            Remove-Item $_.FullName
        }
    }
}

