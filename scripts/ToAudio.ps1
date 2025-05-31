$inputFolder = "D:\Media\Conversion\download"
$outputFolder = "D:\Media\Conversion\middle"

mkdir $outputFolder -Force

Get-ChildItem -Path $inputFolder -Filter *.mp4 | ForEach-Object {
    $input = $_.FullName
    $baseName = $_.BaseName
    $outputPattern = "$outputFolder\$baseName.m4a"

    ffmpeg -i $input -vn -acodec copy $outputPattern

    Remove-Item $input
}