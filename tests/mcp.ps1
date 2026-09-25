# AutoCADCLR MCP 端到端测试：stdio / HTTP × 旧协议（initialize）/ 新协议（_meta + 请求头）四种组合各跑一遍。
# 用法：powershell -ExecutionPolicy Bypass -File D:\AutoCADCLR\tests\mcp.ps1 [-Acad 2020]
# 离线部分用 dwg 参数（不需要打开 AutoCAD）；检测到已加载插件的 AutoCAD 时，加测实时调用与截图。
param([string]$Acad = "2020")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root "bin\Release\acadclr.exe"
$out  = Join-Path $root "test-out"
New-Item -ItemType Directory $out -Force | Out-Null
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$script:fail = 0
$script:id = 0

function Check([string]$title, [bool]$ok) {
    if ($ok) { Write-Host "PASS  $title" } else { Write-Host "FAIL  $title" -ForegroundColor Red; $script:fail++ }
}

# ---------------------------------------------------------------- 客户端
$meta = @{ "io.modelcontextprotocol/protocolVersion" = "2026-07-28"; "io.modelcontextprotocol/clientCapabilities" = @{}; "io.modelcontextprotocol/clientInfo" = @{ name = "mcp.ps1"; version = "1" } }

function NewStdio {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe, "mcp --acad $Acad"
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $p = [Diagnostics.Process]::Start($psi)
    $w = New-Object IO.StreamWriter($p.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false)); $w.AutoFlush = $true; $w.NewLine = "`n"
    return @{ Kind = "stdio"; Proc = $p; Writer = $w }
}

function NewHttp([int]$port) {
    $p = Start-Process -FilePath $exe -ArgumentList "mcp", "--http", "--port", $port, "--acad", $Acad -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 800
    return @{ Kind = "http"; Proc = $p; Url = "http://127.0.0.1:$port/mcp" }
}

# 发一个请求，返回解析后的响应；$modern 决定是否带 _meta（HTTP 同时带请求头）
function Rpc($c, [string]$method, $params, [bool]$modern) {
    $script:id++
    if ($params -eq $null) { $params = @{} }
    if ($modern) { $params["_meta"] = $meta }
    $body = @{ jsonrpc = "2.0"; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 20 -Compress
    if ($c.Kind -eq "stdio") {
        $c.Writer.WriteLine($body)
        $line = $c.Proc.StandardOutput.ReadLine()
        return $line | ConvertFrom-Json
    }
    $h = @{ "Accept" = "application/json, text/event-stream" }
    if ($modern) {
        $h["MCP-Protocol-Version"] = "2026-07-28"; $h["Mcp-Method"] = $method
        if ($method -eq "tools/call") { $h["Mcp-Name"] = $params.name }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $r = Invoke-WebRequest -Uri $c.Url -Method POST -Body $bytes -ContentType "application/json; charset=utf-8" -Headers $h -UseBasicParsing
    return [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json
}

function Tool($c, [string]$name, $arguments, [bool]$modern) {
    $r = Rpc $c "tools/call" @{ name = $name; arguments = $arguments } $modern
    return $r.result
}

function Close($c) {
    if ($c.Kind -eq "stdio") { $c.Writer.Close(); $c.Proc.WaitForExit(15000) | Out-Null }
    else { Stop-Process -Id $c.Proc.Id -Force }
}

$live = $false
try { $live = ((& $exe status --json 2>$null | Out-String) | ConvertFrom-Json).ok } catch { }
if ($live) { Write-Host "检测到运行中的 AutoCAD，加测实时调用" } else { Write-Host "没有运行中的 AutoCAD，只测离线" }

# ---------------------------------------------------------------- 四种组合
$port = 7146
foreach ($transport in "stdio", "http") {
    foreach ($modern in $false, $true) {
        $era = $(if ($modern) { "新协议" } else { "旧协议" })
        $tag = "[$transport/$era]"
        $c = $(if ($transport -eq "stdio") { NewStdio } else { NewHttp $port; $port++ })
        try {
            if ($modern) {
                $d = Rpc $c "server/discover" @{} $true
                Check "$tag server/discover" ($d.result.supportedVersions -contains "2026-07-28" -and $d.result.resultType -eq "complete")
            } else {
                $i = Rpc $c "initialize" @{ protocolVersion = "2025-11-25"; capabilities = @{}; clientInfo = @{ name = "mcp.ps1"; version = "1" } } $false
                Check "$tag initialize" ($i.result.protocolVersion -eq "2025-11-25")
            }
            $tools = (Rpc $c "tools/list" @{} $modern).result.tools
            Check "$tag tools/list（$($tools.Count) 个，不含 lisp）" ($tools.Count -ge 20 -and -not ($tools.name -contains "lisp"))

            $h = Tool $c "help" @{ topic = "polyline" } $modern
            Check "$tag help 查类型" ((-not $h.isError) -and $h.content[0].text -match "points")

            # 离线：新建图纸，用 batch 一次完成 SKILL.md 的示例，再查询、测量、校验
            $dwg = Join-Path $out ("mcp-" + $transport + "-" + $(if ($modern) { "modern" } else { "legacy" }) + ".dwg")
            Remove-Item $dwg -ErrorAction SilentlyContinue
            $cr = Tool $c "create" @{ dwg = $dwg } $modern
            Check "$tag create 离线新建" ((-not $cr.isError) -and (Test-Path $dwg))
            $items = @(
                @{ command = "add"; parent = "/layers"; type = "layer"; props = @{ name = "WALL"; color = 1; lineWeight = 0.5 } },
                @{ command = "add"; parent = "/model"; type = "polyline"; props = @{ points = "0,0;6000,0;6000,4000;0,4000"; closed = $true; layer = "WALL" } },
                @{ command = "add"; parent = "/model"; from = '$1'; props = @{ move = "7000,0" } },
                @{ command = "add"; parent = "/model"; type = "text"; props = @{ text = "客厅"; position = "3000,2000"; height = 350; justify = "mc"; layer = "TEXT" } }
            )
            $b = Tool $c "batch" @{ items = $items; dwg = $dwg } $modern
            Check "$tag batch（SKILL 示例 4 条）" ((-not $b.isError) -and $b.structuredContent.succeeded -eq 4)
            $q = Tool $c "query" @{ selector = "polyline[layer=WALL]"; dwg = $dwg } $modern
            Check "$tag query" ($q.structuredContent.items[0].matched -eq 2)
            $m = Tool $c "measure" @{ action = "area"; selector = "polyline[layer=WALL]"; dwg = $dwg } $modern
            Check "$tag measure 合计面积" ($m.structuredContent.items[0].node.props.totalArea -eq "48000000")
            $k = Tool $c "check" @{ action = "overlap"; selector = "polyline[layer=WALL]"; dwg = $dwg } $modern
            Check "$tag check 不重叠" ($k.structuredContent.items[0].node.props.result -eq "PASS")
            $bad = Tool $c "add" @{ type = "circle"; props = @{ raduis = 1 }; dwg = $dwg } $modern
            Check "$tag 属性写错返回 isError 与建议" ($bad.isError -and $bad.content[0].text -match "radius")

            if ($live) {
                $s = Tool $c "status" @{} $modern
                Check "$tag 实时 status" ((-not $s.isError) -and $s.structuredContent.data.mode -eq "live")
                $v = Tool $c "view" @{ action = "capture"; props = @{ maxWidth = 400 } } $modern
                Check "$tag 实时截图返回 image 内容块" ((-not $v.isError) -and ($v.content | Where-Object type -eq "image").mimeType -eq "image/png")
            }
        }
        catch { Check "$tag 未预期的异常：$($_.Exception.Message)" $false }
        finally { Close $c }
    }
}

Write-Host ""
Write-Host $(if ($script:fail -eq 0) { "全部通过" } else { "$($script:fail) 项失败" })
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
