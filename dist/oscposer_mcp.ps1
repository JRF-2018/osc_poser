# OscPoser MCP bridge (stdio -> HTTP). For MCP clients that can only start a program and talk over stdin/stdout
# (for example Claude Desktop's config, or Claude Code running inside WSL). It forwards each JSON-RPC line to
# OscPoser's http://127.0.0.1:<port>/mcp and writes the answer back as one line. Needs only Windows PowerShell.
# Start OscPoser.exe with -api first. Clients that speak HTTP can connect directly instead:
#   claude mcp add --transport http oscposer http://127.0.0.1:9330/mcp
# With this bridge:
#   claude mcp add oscposer -- powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\path\to\oscposer_mcp.ps1"
#
# OscPoser の MCP の中継(stdio → HTTP)。標準入出力でしか話せない MCP の相手(Claude Desktop の設定や、WSL の中の
# Claude Code など)のために、1 行ずつの JSON-RPC を OscPoser の http://127.0.0.1:<ポート>/mcp に渡し、答えを 1 行で返す。
# Windows の PowerShell だけで動く。先に OscPoser.exe を -api 付きで起動しておく。
param([int]$Port = 9330)
$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
$stdout = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
$stdout.AutoFlush = $true
$url = "http://127.0.0.1:$Port/mcp"

while ($true) {
    $line = $stdin.ReadLine()
    if ($null -eq $line) { break }
    if ($line.Trim() -eq "") { continue }
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.Method = "POST"
        $req.ContentType = "application/json"
        $req.Accept = "application/json, text/event-stream"
        $req.Timeout = 130000
        $req.ReadWriteTimeout = 130000
        $bytes = $utf8.GetBytes($line)
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
        $res = $req.GetResponse()
        $r = New-Object System.IO.StreamReader($res.GetResponseStream(), $utf8)
        $text = $r.ReadToEnd(); $r.Close(); $res.Close()
        if ($text.Trim() -ne "") { $stdout.WriteLine(($text -replace "`r?`n", " ")) }
    }
    catch {
        # OscPoser is not running, etc. Answer requests (with an id) with a JSON-RPC error.
        # OscPoser が動いていないなど。要求(id があるもの)には JSON-RPC のエラーで答える
        $id = $null
        try { $id = ($line | ConvertFrom-Json).id } catch { }
        if ($null -ne $id) {
            $msg = "Cannot reach OscPoser (is it running with -api $Port?) / OscPoser に届きません(-api $Port で起動していますか): " + $_.Exception.Message
            $err = @{ jsonrpc = "2.0"; id = $id; error = @{ code = -32000; message = $msg } } | ConvertTo-Json -Compress -Depth 5
            $stdout.WriteLine($err)
        }
    }
}
