# MCP 测试共用的客户端：启动 acadclr mcp（stdio 或 HTTP），按旧协议（不带 _meta）或新协议（_meta + 请求头）收发。
# 由 mcp.ps1、mcp-scenarios.ps1 用点号引入：. (Join-Path $PSScriptRoot "mcp-client.ps1")
$script:mcpExe = Join-Path (Split-Path -Parent $PSScriptRoot) "bin\Release\acadclr.exe"
$script:mcpId = 0
$script:mcpMeta = @{ "io.modelcontextprotocol/protocolVersion" = "2026-07-28"; "io.modelcontextprotocol/clientCapabilities" = @{}; "io.modelcontextprotocol/clientInfo" = @{ name = "acadclr-tests"; version = "1" } }

function New-McpStdio([string[]]$extra = @()) {
    $psi = New-Object Diagnostics.ProcessStartInfo $script:mcpExe, (@("mcp") + $extra -join " ")
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $p = [Diagnostics.Process]::Start($psi)
    $w = New-Object IO.StreamWriter($p.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false)); $w.AutoFlush = $true; $w.NewLine = "`n"
    return @{ Kind = "stdio"; Proc = $p; Writer = $w }
}

function New-McpHttp([int]$port, [string[]]$extra = @()) {
    $p = Start-Process -FilePath $script:mcpExe -ArgumentList (@("mcp", "--http", "--port", $port) + $extra) -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 800
    return @{ Kind = "http"; Proc = $p; Url = "http://127.0.0.1:$port/mcp" }
}

# 发一个请求，返回解析后的响应。$modern：带 _meta（HTTP 同时带 MCP-Protocol-Version / Mcp-Method / Mcp-Name）
function Invoke-McpRpc($c, [string]$method, $params, [bool]$modern) {
    $script:mcpId++
    if ($params -eq $null) { $params = @{} }
    if ($modern) { $params["_meta"] = $script:mcpMeta }
    $body = @{ jsonrpc = "2.0"; id = $script:mcpId; method = $method; params = $params } | ConvertTo-Json -Depth 30 -Compress
    if ($c.Kind -eq "stdio") {
        $c.Writer.WriteLine($body)
        return $c.Proc.StandardOutput.ReadLine() | ConvertFrom-Json
    }
    $h = @{ "Accept" = "application/json, text/event-stream" }
    if ($modern) {
        $h["MCP-Protocol-Version"] = "2026-07-28"; $h["Mcp-Method"] = $method
        if ($method -eq "tools/call") { $h["Mcp-Name"] = $params.name }
    }
    $r = Invoke-WebRequest -Uri $c.Url -Method POST -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8" -Headers $h -UseBasicParsing
    return [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json
}

# 调用工具，返回 result（content / structuredContent / isError）
function Invoke-McpTool($c, [string]$name, $arguments, [bool]$modern) {
    if ($arguments -eq $null) { $arguments = @{} }
    return (Invoke-McpRpc $c "tools/call" @{ name = $name; arguments = $arguments } $modern).result
}

function Close-Mcp($c) {
    if ($c.Kind -eq "stdio") { $c.Writer.Close(); $c.Proc.WaitForExit(15000) | Out-Null }
    else { Stop-Process -Id $c.Proc.Id -Force -ErrorAction SilentlyContinue }
}

# 旧协议先握手；新协议可直接调用
function Open-McpSession($c, [bool]$modern) {
    if ($modern) { return (Invoke-McpRpc $c "server/discover" @{} $true).result }
    return (Invoke-McpRpc $c "initialize" @{ protocolVersion = "2025-11-25"; capabilities = @{}; clientInfo = @{ name = "acadclr-tests"; version = "1" } } $false).result
}
