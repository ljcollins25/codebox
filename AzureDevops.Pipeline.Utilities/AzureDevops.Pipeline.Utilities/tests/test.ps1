Write-Host $args
Write-Host @args
Write-Host $args.Count

$hasArgs = $args.Count -gt 0

$args | % { "arg: $_" }