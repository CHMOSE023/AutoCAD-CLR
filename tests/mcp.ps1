# AutoCADCLR MCP 端到端测试：stdio / HTTP × 旧协议（initialize）/ 新协议（_meta + 请求头）四种组合各跑一遍。
# 用法：powershell -ExecutionPolicy Bypass -File D:\AutoCADCLR\tests\mcp.ps1 [-Acad 2020]
# 离线部分用 dwg 参数（不需要打开 AutoCAD）；检测到已加载插件的 AutoCAD 时，加测实时调用与截图。
# 更完整的实时场景（移植自 AutoCadMCP 的 test-mcp.ps1）见 mcp-scenarios.ps1。
param([string]$Acad = "2020")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "mcp-client.ps1")
$out = Join-Path (Split-Path -Parent $PSScriptRoot) "test-out"
New-Item -ItemType Directory $out -Force | Out-Null
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$script:fail = 0

function Check([string]$title, [bool]$ok) {
    if ($ok) { Write-Host "PASS  $title" } else { Write-Host "FAIL  $title" -ForegroundColor Red; $script:fail++ }
}

$live = $false
try { $live = ((& $script:mcpExe status --json 2>$null | Out-String) | ConvertFrom-Json).ok } catch { }
if ($live) { Write-Host "检测到运行中的 AutoCAD，加测实时调用" } else { Write-Host "没有运行中的 AutoCAD，只测离线" }

$port = 7146
foreach ($transport in "stdio", "http") {
    foreach ($modern in $false, $true) {
        $tag = "[$transport/" + $(if ($modern) { "新协议" } else { "旧协议" }) + "]"
        $c = $(if ($transport -eq "stdio") { New-McpStdio @("--acad", $Acad) } else { New-McpHttp $port @("--acad", $Acad); $port++ })
        try {
            $o = Open-McpSession $c $modern
            if ($modern) { Check "$tag server/discover" ($o.supportedVersions -contains "2026-07-28" -and $o.resultType -eq "complete") }
            else { Check "$tag initialize" ($o.protocolVersion -eq "2025-11-25") }
            $tools = (Invoke-McpRpc $c "tools/list" @{} $modern).result.tools
            Check "$tag tools/list（$($tools.Count) 个，不含 lisp）" ($tools.Count -ge 20 -and -not ($tools.name -contains "lisp"))

            $h = Invoke-McpTool $c "help" @{ topic = "polyline" } $modern
            Check "$tag help 查类型" ((-not $h.isError) -and $h.content[0].text -match "points")

            # 离线：新建图纸，用 batch 一次完成 SKILL.md 的示例，再查询、测量、校验
            $dwg = Join-Path $out ("mcp-" + $transport + "-" + $(if ($modern) { "modern" } else { "legacy" }) + ".dwg")
            Remove-Item $dwg -ErrorAction SilentlyContinue
            $cr = Invoke-McpTool $c "create" @{ dwg = $dwg } $modern
            Check "$tag create 离线新建" ((-not $cr.isError) -and (Test-Path $dwg))
            $items = @(
                @{ command = "add"; parent = "/layers"; type = "layer"; props = @{ name = "WALL"; color = 1; lineWeight = 0.5 } },
                @{ command = "add"; parent = "/model"; type = "polyline"; props = @{ points = "0,0;6000,0;6000,4000;0,4000"; closed = $true; layer = "WALL" } },
                @{ command = "add"; parent = "/model"; from = '$1'; props = @{ move = "7000,0" } },
                @{ command = "add"; parent = "/model"; type = "text"; props = @{ text = "客厅"; position = "3000,2000"; height = 350; justify = "mc"; layer = "TEXT" } }
            )
            $b = Invoke-McpTool $c "batch" @{ items = $items; dwg = $dwg } $modern
            Check "$tag batch（SKILL 示例 4 条）" ((-not $b.isError) -and $b.structuredContent.succeeded -eq 4)
            $q = Invoke-McpTool $c "query" @{ selector = "polyline[layer=WALL]"; dwg = $dwg } $modern
            Check "$tag query" ($q.structuredContent.items[0].matched -eq 2)
            $m = Invoke-McpTool $c "measure" @{ action = "area"; selector = "polyline[layer=WALL]"; dwg = $dwg } $modern
            Check "$tag measure 合计面积" ($m.structuredContent.items[0].node.props.totalArea -eq "48000000")
            $k = Invoke-McpTool $c "check" @{ action = "overlap"; selector = "polyline[layer=WALL]"; dwg = $dwg } $modern
            Check "$tag check 不重叠" ($k.structuredContent.items[0].node.props.result -eq "PASS")
            $bad = Invoke-McpTool $c "add" @{ type = "circle"; props = @{ raduis = 1 }; dwg = $dwg } $modern
            Check "$tag 属性写错返回 isError 与建议" ($bad.isError -and $bad.content[0].text -match "radius")

            if ($live) {
                $s = Invoke-McpTool $c "status" @{} $modern
                Check "$tag 实时 status" ((-not $s.isError) -and $s.structuredContent.data.mode -eq "live")
                $v = Invoke-McpTool $c "view" @{ action = "capture"; props = @{ maxWidth = 400 } } $modern
                Check "$tag 实时截图返回 image 内容块" ((-not $v.isError) -and ($v.content | Where-Object type -eq "image").mimeType -eq "image/png")
            }
        }
        catch { Check "$tag 未预期的异常：$($_.Exception.Message)" $false }
        finally { Close-Mcp $c }
    }
}

Write-Host ""
Write-Host $(if ($script:fail -eq 0) { "全部通过" } else { "$($script:fail) 项失败" })
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
