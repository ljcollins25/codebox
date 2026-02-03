param(
    [string]$Query
)

$f = "C:\Program Files\Microsoft VS Code\resources\app\out\vs\workbench\workbench.desktop.main.js"
$c = [System.IO.File]::ReadAllText($f)
$queries = if ($Query) { @($Query) } else { @("vut") }

foreach ($q in $queries) {
    $index = 0
    $count = 0
    while (($index = $c.IndexOf($q, $index, [System.StringComparison]::Ordinal)) -ge 0 -and $count -lt 10) {
        Write-Host "`n=== Match for $q at $index ==="
        $start = [Math]::Max(0, $index - 100)
        $len = [Math]::Min($c.Length - $start, 200)
        Write-Host $c.Substring($start, $len)
        $index += $q.Length
        $count++
    }
}
