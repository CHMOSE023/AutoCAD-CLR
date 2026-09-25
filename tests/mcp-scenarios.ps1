# AutoCADCLR MCP 实时场景测试：移植自 AutoCadMCP 的 scripts/test-mcp.ps1，改用统一工具，并对每一步断言结果。
# 需要一个已加载插件的 AutoCAD。stdio / HTTP × 旧协议 / 新协议四种组合各跑一遍；
# HTTP 以 --allow-lisp 启动（测 lisp），stdio 不带（测 lisp 不出现在工具列表、调用被拒）。
# 每种组合在 AutoCAD 里新建一张临时图纸，结束时丢弃并关闭，不改动其他已打开的图纸。
# 用法：powershell -ExecutionPolicy Bypass -File D:\AutoCADCLR\tests\mcp-scenarios.ps1 [-Acad 2020]
param([string]$Acad = "2020")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "mcp-client.ps1")
. (Join-Path $PSScriptRoot "acad-dialog.ps1")
$out = Join-Path (Split-Path -Parent $PSScriptRoot) "test-out"
New-Item -ItemType Directory $out -Force | Out-Null
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$script:fail = 0
$script:pass = 0

function Check([string]$title, [bool]$ok) {
    if ($ok) { $script:pass++ } else { Write-Host "FAIL  $script:tag $title" -ForegroundColor Red; $script:fail++ }
}
# 每次调用后检查 AutoCAD 有没有弹出“未经处理的异常”窗口：出现即报出是哪一步触发的
function CallTool([string]$name, $arguments) {
    $r = Invoke-McpTool $script:c $name $arguments $script:modern
    $dlg = Find-AcadErrorDialog $script:acadPid
    if ($dlg) { throw "AutoCAD 弹出错误窗口，触发步骤：$name $($arguments | ConvertTo-Json -Compress -Depth 5)`n$(Format-AcadErrorDialog $dlg)" }
    return $r
}
function StructOf($r) { $r.structuredContent }
function NodeOf($r) { $r.structuredContent.items[0].node }
function HandleOf($r) { (NodeOf $r).props.handle }
function IsOk($r) { (-not $r.isError) -and $r.structuredContent.ok }
function AddEnt([string]$type, [hashtable]$props, [string]$parent = "/model") { CallTool "add" @{ parent = $parent; type = $type; props = $props } }
function CountOf([string]$selector) { (StructOf (CallTool "query" @{ selector = $selector; limit = 1 })).items[0].matched }

$st = (& $script:mcpExe status --json 2>$null | Out-String) | ConvertFrom-Json
if (-not $st.ok) { Write-Host "连不上 AutoCAD：先在 AutoCAD 里 NETLOAD 插件，再运行本脚本。"; exit 2 }
$script:acadPid = [int]$st.data.pid

# xref 的源文件：离线新建一张小图
$xsrc = Join-Path $out "mcp-xref-src.dwg"
Remove-Item $xsrc -ErrorAction SilentlyContinue
& $script:mcpExe create $xsrc --acad $Acad | Out-Null
& $script:mcpExe add $xsrc /model --type circle --prop center=0,0 --prop radius=500 --acad $Acad | Out-Null

function Run-Scenarios([bool]$allowLisp) {
    $startDocs = (NodeOf (CallTool "get" @{ path = "/documents" })).children.Count
    $doc = (NodeOf (CallTool "add" @{ parent = "/documents"; type = "document" })).props.name
    Check "新建临时文档" ($doc -ne $null)

    # ---- 状态与图层
    Check "status 实时" ((StructOf (CallTool "status" @{})).data.mode -eq "live")
    Check "新建图层" (IsOk (AddEnt "layer" @{ name = "MCP-TEST"; color = 1 } "/layers"))
    Check "设为当前图层" ((NodeOf (CallTool "set" @{ path = "/"; props = @{ currentLayer = "MCP-TEST" } })).props.currentLayer -eq "MCP-TEST")

    # ---- 基本绘图、读取、移动、复制、删除
    $h1 = HandleOf (AddEnt "line" @{ start = "0,0"; end = "5000,3000" })
    Check "画线 / 圆 / 多段线 / 文字" ((IsOk (AddEnt "circle" @{ center = "2500,1500"; radius = 800 })) -and
        (IsOk (AddEnt "polyline" @{ points = "0,0;5000,0;5000,3000;0,3000"; closed = $true })) -and
        (IsOk (AddEnt "text" @{ text = "MCP TEST"; position = "0,3200"; height = 300 })))
    Check "按类型查询" ((CountOf "line") -ge 1)
    Check "按句柄读取" ((NodeOf (CallTool "get" @{ path = "/entity[@handle=$h1]" })).props.end -eq "5000,3000")
    Check "移动" ((NodeOf (CallTool "set" @{ path = "/entity[@handle=$h1]"; props = @{ move = "0,500" } })).props.start -eq "0,500")
    Check "复制（add from + move）" ((NodeOf (CallTool "add" @{ parent = "/model"; from = "/entity[@handle=$h1]"; props = @{ move = "0,300" } })).props.start -eq "0,800")
    Check "缩放到范围" (IsOk (CallTool "view" @{ action = "zoom" }))
    Check "删除" (IsOk (CallTool "remove" @{ path = "/entity[@handle=$h1]" }))

    # ---- 截图、另存
    $cap = CallTool "view" @{ action = "capture"; props = @{ maxWidth = 1200; region = "window" } }
    $img = $cap.content | Where-Object type -eq "image"
    Check "截图（整个窗口）" ($img -ne $null)
    if ($img) { [IO.File]::WriteAllBytes((Join-Path $out "mcp-capture-$script:slug.png"), [Convert]::FromBase64String($img.data)) }
    $tmp = Join-Path $out ("mcp-saveas-$script:slug-" + (Get-Date -Format "HHmmss") + ".dwg")
    Check "另存为" ((IsOk (CallTool "save" @{ saveAs = $tmp })) -and (Test-Path $tmp))
    $doc = Split-Path $tmp -Leaf  # save --as 后文档随之改名

    # ---- LISP
    if ($allowLisp) {
        Check "lisp 求值" ((StructOf (CallTool "lisp" @{ code = "(+ 1 2 3)" })).data.value -eq "6")
        CallTool "lisp" @{ code = "(defun mcp-selftest (n) (* n 10))" } | Out-Null
        Check "lisp 函数跨调用保留" ((StructOf (CallTool "lisp" @{ code = "(mcp-selftest 7)" })).data.value -eq "70")
        $e = CallTool "lisp" @{ code = "(/ 1 0)" }
        Check "lisp 错误被捕获" ($e.isError -and (StructOf $e).error.code -eq "lisp_error")
    } else {
        $tools = (Invoke-McpRpc $script:c "tools/list" @{} $script:modern).result.tools.name
        Check "未开放时工具列表没有 lisp" (-not ($tools -contains "lisp"))
        Check "未开放时调用 lisp 被拒" ((StructOf (CallTool "lisp" @{ code = "(+ 1 2)" })).error.code -eq "lisp_disabled")
    }

    # ---- 更多图元
    Check "圆弧 / 椭圆 / 点 / 多行文字" ((IsOk (AddEnt "arc" @{ center = "8000,0"; radius = 800; startAngle = 0; endAngle = 120 })) -and
        (IsOk (AddEnt "ellipse" @{ center = "8000,2000"; majorAxis = "1000,0"; ratio = 0.5 })) -and
        (IsOk (AddEnt "point" @{ position = "8000,3000" })) -and
        (IsOk (AddEnt "mtext" @{ text = "P2 mtext line1\Pline2"; position = "0,6000"; height = 300; width = 4000 })))

    # ---- 原生编辑：分解、打断、合并
    $rect = HandleOf (AddEnt "polyline" @{ points = "6000,8000;9000,8000;9000,11000;6000,11000"; closed = $true })
    Check "分解矩形得到 4 条线" ((StructOf (CallTool "edit" @{ action = "explode"; path = $rect })).items[0].matched -eq 4)
    $bk = HandleOf (AddEnt "line" @{ start = "0,12000"; end = "4000,12000" })
    Check "打断中间一段" ((StructOf (CallTool "edit" @{ action = "break"; path = $bk; props = @{ at = "1000,12000;2500,12000" } })).items[0].matched -eq 2)
    $j1 = HandleOf (AddEnt "line" @{ start = "0,13000"; end = "2000,13000" })
    $j2 = HandleOf (AddEnt "line" @{ start = "2000,13000"; end = "5000,13000" })
    $jr = StructOf (CallTool "edit" @{ action = "join"; path = "$j1;$j2" })
    Check "合并两条共线的线" ($jr.items[0].matched -eq 1 -and $jr.items[0].nodes[0].props.length -eq "5000")

    # ---- 命令式编辑：倒圆角、修剪
    $la = HandleOf (AddEnt "line" @{ start = "0,8000"; end = "3000,8000" })
    $lb = HandleOf (AddEnt "line" @{ start = "3000,8000"; end = "3000,11000" })
    Check "倒圆角" (IsOk (CallTool "edit" @{ action = "fillet"; path = $la; props = @{ with = $lb; radius = 400 } }))
    $cut = HandleOf (AddEnt "line" @{ start = "1000,6500"; end = "1000,7500" })
    $tg = HandleOf (AddEnt "line" @{ start = "-500,7000"; end = "5000,7000" })
    CallTool "edit" @{ action = "trim"; selector = "$tg@0,7000"; props = @{ edges = $cut } } | Out-Null
    Check "修剪掉拾取点所在的一段" ((NodeOf (CallTool "get" @{ path = "/entity[@handle=$tg]" })).props.start -eq "1000,7000")

    # ---- 标注、填充、选择器批量修改、测量
    Check "线性标注" (IsOk (AddEnt "dimension" @{ kind = "linear"; p1 = "0,0"; p2 = "5000,0"; dimLine = "2500,-800" }))
    $hr = HandleOf (AddEnt "polyline" @{ points = "11000,0;14000,0;14000,2000;11000,2000"; closed = $true })
    Check "填充" (IsOk (AddEnt "hatch" @{ boundary = $hr; pattern = "ANSI31"; scale = 50 }))
    Check "按选择器批量移动" ((StructOf (CallTool "set" @{ selector = "line[layer=MCP-TEST]"; props = @{ move = "0,200" } })).items[0].matched -ge 1)
    Check "测面积" ((NodeOf (CallTool "measure" @{ action = "area"; path = $hr })).props.totalArea -eq "6000000")
    Check "测距离" ((NodeOf (CallTool "measure" @{ action = "distance"; props = @{ from = "0,0"; to = "3000,4000" } })).props.distance -eq "5000")

    # ---- 线型与线宽
    Check "线型列表" ((NodeOf (CallTool "get" @{ path = "/linetypes" })).props.count -ge 3)
    $ax = NodeOf (AddEnt "layer" @{ name = "MCP-AXIS"; color = 4; linetype = "CENTER"; lineWeight = 0.13 } "/layers")
    Check "图层线型与线宽" ($ax.props.linetype -eq "CENTER" -and $ax.props.lineWeight -eq "0.13")
    Check "图层线宽 0.7" ((NodeOf (AddEnt "layer" @{ name = "MCP-WALL"; color = 2; lineWeight = 0.7 } "/layers")).props.lineWeight -eq "0.70")
    AddEnt "layer" @{ name = "MCP-ROOM"; color = 3 } "/layers" | Out-Null

    # ---- 阵列
    $seed = HandleOf (AddEnt "polyline" @{ points = "0,20000;400,20000;400,20400;0,20400"; closed = $true; layer = "MCP-WALL" })
    $before = CountOf "polyline"
    CallTool "edit" @{ action = "array"; path = $seed; props = @{ rows = 3; cols = 4; rowSpacing = 1000; colSpacing = 800 } } | Out-Null
    Check "矩形阵列 3x4 多出 11 个" ((CountOf "polyline") -eq $before + 11)
    $spoke = HandleOf (AddEnt "line" @{ start = "15000,25000"; end = "16500,25000" })
    $lines = CountOf "line"
    CallTool "edit" @{ action = "array"; path = $spoke; props = @{ center = "15000,25000"; count = 8; rotateItems = $true } } | Out-Null
    Check "环形阵列 8 份多出 7 个" ((CountOf "line") -eq $lines + 7)

    # ---- 改实体图层
    $stray = HandleOf (AddEnt "line" @{ start = "0,28000"; end = "3000,28000" })
    Check "改实体图层" ((NodeOf (CallTool "set" @{ path = $stray; props = @{ layer = "MCP-AXIS"; color = "bylayer" } })).props.layer -eq "MCP-AXIS")

    # ---- 图块：定义（原地替换）、bboxFromBase、插入
    $bsrc = HandleOf (AddEnt "polyline" @{ points = "10000,20000;12500,20000;12500,25000;10000,25000"; closed = $true })
    $blk = NodeOf (CallTool "add" @{ parent = "/blocks"; type = "block"; props = @{ name = "MCP-STALL"; entities = $bsrc; base = "11250,20000"; replace = $true } })
    Check "定义图块，bboxFromBase 相对基点" ($blk.props.bboxFromBase -eq "-1250,0;1250,5000")
    $ins = NodeOf (AddEnt "insert" @{ name = "MCP-STALL"; position = "20000,20000"; layer = "MCP-WALL" })
    Check "插入图块，占位 = 插入点 + bboxFromBase" ($ins.props.bbox -eq "18750,20000;21250,25000")

    # ---- 空间校验
    $rA = HandleOf (AddEnt "polyline" @{ points = "0,30000;4000,30000;4000,33000;0,33000"; closed = $true; layer = "MCP-ROOM" })
    $rB = HandleOf (AddEnt "polyline" @{ points = "4000,30000;8000,30000;8000,33000;4000,33000"; closed = $true; layer = "MCP-ROOM" })
    $rC = HandleOf (AddEnt "polyline" @{ points = "7000,30000;11000,30000;11000,33000;7000,33000"; closed = $true; layer = "MCP-ROOM" })
    $outline = HandleOf (AddEnt "polyline" @{ points = "-500,29500;9000,29500;9000,33500;-500,33500"; closed = $true })
    $ov = StructOf (CallTool "check" @{ action = "overlap"; selector = "polyline[layer=MCP-ROOM]" })
    Check "重叠：B 与 C 重叠 1000x3000" ($ov.items[0].node.props.result -eq "FAIL" -and $ov.items[0].nodes[0].props.size -eq "1000 x 3000")
    $in = StructOf (CallTool "check" @{ action = "inside"; selector = "polyline[layer=MCP-ROOM]"; props = @{ boundary = $outline } })
    Check "越界：C 向右超出 2000" ($in.items[0].nodes[0].props.outBy -eq "右 2000")
    Check "相邻：A 与 B 共边" ((NodeOf (CallTool "check" @{ action = "adjacent"; path = $rA; props = @{ with = $rB } })).props.result -eq "PASS")
    Check "不相邻：A 与 C 间距 3000" ((NodeOf (CallTool "check" @{ action = "adjacent"; path = $rA; props = @{ with = $rC; gap = 100 } })).props.result -eq "FAIL")
    Check "压在一起：B 与 C" ((NodeOf (CallTool "check" @{ action = "adjacent"; path = $rB; props = @{ with = $rC } })).props.result -eq "OVERLAP")

    # ---- 样条
    $spBefore = CountOf "spline"
    AddEnt "spline" @{ points = "16000,4000;17000,5200;18000,3800;19000,5000;20000,4200" } | Out-Null
    $sp2 = NodeOf (AddEnt "spline" @{ points = "16000,7000;17500,8200;19000,7000;17500,6000"; closed = $true })
    AddEnt "spline" @{ points = "21000,4000;22000,5200;23000,4000"; startTangent = "1,1"; endTangent = "1,-1" } | Out-Null
    AddEnt "spline" @{ method = "cv"; points = "16000,10000;17000,11500;18000,9500;19000,11000;20000,10000"; degree = 3 } | Out-Null
    $sp5 = NodeOf (AddEnt "spline" @{ method = "cv"; points = "21000,10000;23000,10000;23000,12000;21000,12000"; degree = 3; closed = $true })
    Check "样条 5 种写法" ((CountOf "spline") -eq $spBefore + 5)
    Check "闭合样条（拟合点 / 控制点）" ($sp2.props.closed -eq "true" -and $sp5.props.closed -eq "true")
    Check "样条参数错误被拒" (((AddEnt "spline" @{ method = "cv"; points = "0,0;100,100"; degree = 3 }).isError) -and
        ((AddEnt "spline" @{ method = "spiral"; points = "0,0;100,100" }).isError) -and
        ((AddEnt "spline" @{ points = "0,0;100,100"; degree = 99 }).isError))

    # ---- 标记与回滚
    $c0 = CountOf "circle"
    CallTool "mark" @{ label = "before-risky" } | Out-Null
    foreach ($x in 20000, 21000, 22000) { AddEnt "circle" @{ center = "$x,0"; radius = 500 } | Out-Null }
    Check "打标记后加 3 个圆" ((CountOf "circle") -eq $c0 + 3)
    CallTool "rollback" @{} | Out-Null
    Check "回滚到标记" ((CountOf "circle") -eq $c0)

    # ---- 系统变量与单位
    Check "常用系统变量" ((NodeOf (CallTool "get" @{ path = "/sysvars" })).children.Count -ge 20)
    $nv = CallTool "get" @{ path = "/sysvar[@name=NOSUCHVAR]" }
    Check "不存在的系统变量报错" ($nv.structuredContent.items[0].error.code -eq "not_found")
    Check "设系统变量" ((NodeOf (CallTool "set" @{ path = "/sysvar[@name=LTSCALE]"; props = @{ value = 1 } })).props.value -eq "1")
    Check "单位换算" ((NodeOf (CallTool "measure" @{ action = "convert"; props = @{ value = 1000; from = "mm"; to = "m" } })).props.value -eq "1")
    Check "设单位与精度" ((NodeOf (CallTool "set" @{ path = "/"; props = @{ units = "mm"; luprec = 2 } })).props.luprec -eq "2")

    # ---- 布局、视口、打印
    Check "布局列表" (IsOk (CallTool "get" @{ path = "/layouts" }))
    Check "新建布局" (IsOk (AddEnt "layout" @{ name = "MCP-P3"; paper = "A3"; landscape = $true; current = $true } "/layouts"))
    $vp = HandleOf (AddEnt "viewport" @{ center = "210,148"; width = 380; height = 250; scale = 100; viewCenter = "2500,1500" } "/layout[@name=MCP-P3]")
    Check "加视口" ($vp -ne $null)
    Check "改视口比例并锁定" ((NodeOf (CallTool "set" @{ path = "/entity[@handle=$vp]"; props = @{ scale = 200; locked = $true } })).props.locked -eq "true")
    Check "布局下有视口" ((NodeOf (CallTool "get" @{ path = "/layout[@name=MCP-P3]" })).props.viewports -ge 1)
    Check "打印设备列表" ((NodeOf (CallTool "get" @{ path = "/devices" })).props.count -ge 1)
    $pdf = Join-Path $out ("mcp-plot-$script:slug-" + (Get-Date -Format "HHmmss") + ".pdf")
    CallTool "plot" @{ layout = "MCP-P3"; props = @{ output = $pdf; paper = "A3"; landscape = $true } } | Out-Null
    Check "打印 PDF" (Test-Path $pdf)

    # ---- 外部参照
    Check "附着外部参照" (IsOk (AddEnt "xref" @{ path = $xsrc; position = "30000,0"; scale = 1 } "/xrefs"))
    Check "外部参照列表" ((NodeOf (CallTool "get" @{ path = "/xrefs" })).props.count -eq "1")
    Check "重载" (IsOk (CallTool "set" @{ selector = "xref[name~=mcp-xref-src]"; props = @{ reload = $true } }))
    Check "拆离" (IsOk (CallTool "remove" @{ selector = "xref[name~=mcp-xref-src]" }))

    # ---- 文档
    $n = (NodeOf (CallTool "get" @{ path = "/documents" })).children.Count
    $extra = (NodeOf (CallTool "add" @{ parent = "/documents"; type = "document" })).props.name
    Check "新建文档后文档数 +1" ((NodeOf (CallTool "get" @{ path = "/documents" })).children.Count -eq $n + 1)
    CallTool "remove" @{ path = "/document[@name=$extra]"; force = $true } | Out-Null
    CallTool "set" @{ path = "/document[@name=$doc]"; props = @{ current = $true } } | Out-Null
    Check "关闭后复原并切回" ((NodeOf (CallTool "get" @{ path = "/documents" })).children.Count -eq $n -and (StructOf (CallTool "status" @{})).data.activeDocument -eq $tmp)

    # ---- 截图（绘图区，先缩放）
    $cap3 = CallTool "view" @{ action = "capture"; props = @{ maxWidth = 1200; region = "drawing"; zoom = "extents" } }
    Check "截图（绘图区）" (($cap3.content | Where-Object type -eq "image") -ne $null)

    # ---- 收尾：切回模型空间、删布局、丢弃并关闭临时文档
    CallTool "set" @{ path = "/layout[@name=Model]"; props = @{ current = $true } } | Out-Null
    Check "删除布局" (IsOk (CallTool "remove" @{ path = "/layout[@name=MCP-P3]" }))
    CallTool "remove" @{ path = "/document[@name=$doc]"; force = $true } | Out-Null
    Check "关闭临时文档" ((NodeOf (CallTool "get" @{ path = "/documents" })).children.Count -eq $startDocs)
}

$port = 7156
foreach ($transport in "stdio", "http") {
    foreach ($modern in $false, $true) {
        $script:modern = $modern
        $script:slug = $transport + "-" + $(if ($modern) { "modern" } else { "legacy" })
        $script:tag = "[$transport/" + $(if ($modern) { "新协议" } else { "旧协议" }) + "]"
        $allowLisp = $transport -eq "http"
        $extra = @("--acad", $Acad) + $(if ($allowLisp) { @("--allow-lisp") } else { @() })
        $script:c = $(if ($transport -eq "stdio") { New-McpStdio $extra } else { New-McpHttp $port $extra; $port++ })
        $p0 = $script:pass; $f0 = $script:fail
        try {
            Open-McpSession $script:c $modern | Out-Null
            Run-Scenarios $allowLisp
        }
        catch {
            Check "未预期的异常：$($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber)" $false
            if (Find-AcadErrorDialog $script:acadPid) { Write-Host "AutoCAD 停在错误窗口上，后续组合无法继续"; break }
        }
        finally { Close-Mcp $script:c }
        Write-Host ("{0,-16} 通过 {1}，失败 {2}" -f $script:tag, ($script:pass - $p0), ($script:fail - $f0))
    }
}

Write-Host ""
Write-Host $(if ($script:fail -eq 0) { "全部通过（$script:pass 项）" } else { "$($script:fail) 项失败，$script:pass 项通过" })
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
