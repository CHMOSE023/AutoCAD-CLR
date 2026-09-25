# AutoCADCLR 实时模式测试：需要一个已加载插件的 AutoCAD（acadclr status 能连上）。
# 用法：powershell -ExecutionPolicy Bypass -File D:\AutoCADCLR\tests\live.ps1
# 在 AutoCAD 里新建一张临时图纸做测试，结束时丢弃并关闭它，不改动其他已打开的图纸。
# 结果写入 test-out\live.log；每步打印 PASS / FAIL。
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root "bin\Release\acadclr.exe"
$out  = Join-Path $root "test-out"
$log  = Join-Path $out "live.log"
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory $out -Force | Out-Null
Set-Content $log "AutoCADCLR live test  $(Get-Date -Format s)" -Encoding UTF8
$script:fail = 0

function Run([string[]]$argv) {
    $text = & $exe @argv 2>&1 | Out-String
    Add-Content $log ("> acadclr " + ($argv -join " ")) -Encoding UTF8
    Add-Content $log $text.TrimEnd() -Encoding UTF8
    return $text
}

function Json([string[]]$argv) { (& $exe @argv --json 2>$null | Out-String) | ConvertFrom-Json }

function Check([string]$title, [bool]$ok) {
    if ($ok) { Write-Host "PASS  $title" } else { Write-Host "FAIL  $title" -ForegroundColor Red; $script:fail++ }
    Add-Content $log ("=== " + ($(if ($ok) { "PASS" } else { "FAIL" })) + "  $title`r`n") -Encoding UTF8
}

function Count([string]$selector) { (Json @("query", $selector)).items[0].matched }

$st = Json @("status")
if (-not $st.ok) { Write-Host "连不上 AutoCAD：先 NETLOAD 插件，再运行本脚本。"; exit 2 }
$before = (Json @("get", "/documents")).items[0].node.children.Count

# 新建临时图纸，后面的操作都在它上面
$doc = (Json @("add", "/documents", "--type", "document")).items[0].node.props.name
Check "新建文档 $doc" ($doc -ne $null)
Check "新文档成为当前文档" ((Json @("status")).data.activeDocument -eq $doc)

$r = Json @("batch", "--input", (Join-Path $PSScriptRoot "inspect.json"))
Check "批处理（测量与校验）" ($r.ok -and $r.succeeded -eq 18)

# 视图
$z = Json @("view", "zoom")
Check "view zoom 到图形范围" ($z.ok -and $z.data.zoom -eq "图形范围")
$png = Join-Path $out "live-capture.png"
Remove-Item $png -ErrorAction SilentlyContinue
$c = Json @("view", "capture", "polyline[layer=ROOM]", "--prop", "output=$png", "--prop", "maxWidth=800")
Check "view capture 缩放到目标并保存 PNG" ($c.ok -and (Test-Path $png) -and $c.images[0].width -le 800)

# --doc：操作非当前文档
$other = (Json @("add", "/documents", "--type", "document")).items[0].node.props.name
Run @("add", "/model", "--doc", $doc, "--type", "circle", "--prop", "center=0,0", "--prop", "radius=77") | Out-Null
Check "--doc 写入非当前文档" ((Count "circle[radius=77]") -eq 0 -and (Json @("query", "circle[radius=77]", "--doc", $doc)).items[0].matched -eq 1)
Run @("set", "/document[@name=$doc]", "--prop", "current=true") | Out-Null
Check "切换当前文档" ((Json @("status")).data.activeDocument -eq $doc)
Run @("remove", "/document[@name=$other]") | Out-Null
Check "关闭未修改的文档" ((Json @("get", "/documents")).items[0].node.children.name -notcontains $other)

# 撤销：插件的每次修改是一个撤销步，只读请求不产生撤销步
Run @("add", "/model", "--type", "circle", "--prop", "center=0,0", "--prop", "radius=111") | Out-Null
Run @("add", "/model", "--type", "circle", "--prop", "center=0,0", "--prop", "radius=222") | Out-Null
Run @("get", "/model") | Out-Null
Run @("undo") | Out-Null
Check "undo 1 撤销上一次修改（中间夹着查询）" ((Count "circle[radius=222]") -eq 0 -and (Count "circle[radius=111]") -eq 1)
$nr = Json @("rollback")
Check "没有标记时拒绝 rollback" ((-not $nr.ok) -and $nr.error.code -eq "no_mark")
Run @("mark", "A") | Out-Null
Run @("add", "/model", "--type", "circle", "--prop", "center=0,0", "--prop", "radius=333") | Out-Null
Run @("set", "circle[radius=111]", "--prop", "color=1") | Out-Null
Run @("rollback") | Out-Null
Check "rollback 回到标记" ((Count "circle[radius=333]") -eq 0 -and (Count "circle[radius=111][color=bylayer]") -eq 1)

# 原子批处理放弃时恢复系统变量与头变量
$comp = Join-Path $out "live-comp.json"
Set-Content $comp '[{"command":"set","path":"/sysvar[@name=PDMODE]","props":{"value":35}},{"command":"set","path":"/","props":{"ltscale":250}},{"command":"add","parent":"/model","type":"circle","props":{"raduis":1}}]' -Encoding UTF8
$pd = (Json @("get", "/sysvar[@name=PDMODE]")).items[0].node.props.value
Run @("batch", "--input", $comp) | Out-Null
Check "回滚后系统变量复原" ((Json @("get", "/sysvar[@name=PDMODE]")).items[0].node.props.value -eq $pd -and (Json @("get", "/")).items[0].node.props.ltscale -eq "1")

# 命令式编辑与打印
$ha = (Json @("add", "/model", "--type", "line", "--prop", "start=0,20000", "--prop", "end=10000,20000")).items[0].node.props.handle
$hb = (Json @("add", "/model", "--type", "line", "--prop", "start=5000,19000", "--prop", "end=5000,21000")).items[0].node.props.handle
Run @("edit", "trim", "$ha@8000,20000", "--prop", "edges=$hb") | Out-Null
Check "edit trim（命令队列）" ((Json @("get", "/entity[@handle=$ha]")).items[0].node.props.end -eq "5000,20000")
$pdf = Join-Path $out "live-model.pdf"
Remove-Item $pdf -ErrorAction SilentlyContinue
Run @("plot", "--prop", "output=$pdf", "--prop", "area=extents") | Out-Null
Check "plot 打印 PDF" (Test-Path $pdf)

# 收尾：丢弃并关闭临时图纸
$rm = Json @("remove", "/document[@name=$doc]")
Check "有修改时拒绝关闭" ((-not $rm.ok) -or $rm.items[0].status -eq "failed")
Run @("remove", "/document[@name=$doc]", "--force") | Out-Null
Check "丢弃修改并关闭，文档数复原" ((Json @("get", "/documents")).items[0].node.children.Count -eq $before)

Write-Host ""
Write-Host ($(if ($script:fail -eq 0) { "全部通过" } else { "$($script:fail) 项失败" }) + "，日志：$log")
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
