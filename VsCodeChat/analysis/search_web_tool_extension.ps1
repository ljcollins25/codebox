$f = "C:\Users\lancec\.vscode\extensions\github.copilot-chat-0.36.2\dist\extension.js"
$c = [System.IO.File]::ReadAllText($f)
$queries = @("google", "bing", "webSearch", "web_search", "search_web", "google.com", "bing.com")

foreach ($q in $queries) {
    $index = 0
    $count = 0
    while (($index = $c.IndexOf($q, $index, [System.StringComparison]::OrdinalIgnoreCase)) -ge 0 -and $count -lt 10) {
        Write-Host "`n=== Match for $q at $index ==="
        $start = [Math]::Max(0, $index - 300)
        $len = [Math]::Min($c.Length - $start, 600)
        Write-Host $c.Substring($start, $len)
        $index += $q.Length
        $count++
    }
}
