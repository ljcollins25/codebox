$f = (Get-ChildItem -Path "C:\Users\*" -Filter "extension.js" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*github.copilot-chat*" })[0].FullName
Write-Host "File: $f"
$c = [System.IO.File]::ReadAllText($f)
$queries = @("web_search", "AnthropicWebSearchToolEnabled")

foreach ($q in $queries) {
    $index = 0
    $count = 0
    while (($index = $c.IndexOf($q, $index, [System.StringComparison]::Ordinal)) -ge 0 -and $count -lt 5) {
        Write-Host "`n=== Match for $q at $index ==="
        $start = [Math]::Max(0, $index - 300)
        $len = [Math]::Min($c.Length - $start, 600)
        Write-Host $c.Substring($start, $len)
        $index += $q.Length
        $count++
    }
}
