$f = "C:\Program Files\Microsoft VS Code\resources\app\out\vs\workbench\workbench.desktop.main.js"
$c = [System.IO.File]::ReadAllText($f)
$start = 12478000
$len = 10000
Write-Host $c.Substring($start, $len)
