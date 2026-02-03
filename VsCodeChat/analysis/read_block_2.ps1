$f = "C:\Program Files\Microsoft VS Code\resources\app\out\vs\workbench\workbench.desktop.main.js"
$c = [System.IO.File]::ReadAllText($f)
$start = 8740000
$len = 5000
Write-Host $c.Substring($start, $len)
