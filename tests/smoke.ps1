# AutoCADCLR 离线冒烟测试
# 用法：powershell -ExecutionPolicy Bypass -File D:\AutoCADCLR\tests\smoke.ps1 [-Acad 2014]
# 结果写入 test-out\smoke.log。只操作 test-out 目录里的测试副本，不改动任何原始图纸。
param([string]$Acad = "2014")

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root "bin\Release\acadclr.exe"
$out  = Join-Path $root "test-out"
$log  = Join-Path $out "smoke.log"
$src  = "D:\AutoCADMCP_DWG\Dwg\2008一注-客运站-一层平面图.dwg"   # 2013 格式（AC1027）的测试底图

[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null
Copy-Item $src (Join-Path $out "b.dwg")
$new = Join-Path $out "新建.dwg"
$b   = Join-Path $out "b.dwg"

function Step([string]$title, [string[]]$argv) {
    Add-Content $log "=== $title" -Encoding UTF8
    Add-Content $log ("> acadclr " + ($argv -join " ")) -Encoding UTF8
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $text = & $exe @argv --acad $Acad 2>&1 | Out-String
    Add-Content $log $text.TrimEnd() -Encoding UTF8
    Add-Content $log "[exit=$LASTEXITCODE  $([int]$sw.Elapsed.TotalMilliseconds)ms]`r`n" -Encoding UTF8
}

Set-Content $log "AutoCADCLR smoke test  $(Get-Date -Format s)  acad=$Acad" -Encoding UTF8
$tp = (Get-ItemProperty "HKCU:\Software\Autodesk\AutoCAD\R19.1\ACAD-D001:804\Profiles\<<未命名配置>>\Variables" -ErrorAction SilentlyContinue).TRUSTEDPATHS
Add-Content $log "TRUSTEDPATHS(2014)=$tp`r`n" -Encoding UTF8

Step "新建"            @("create", $new)
Step "状态"            @("status", $new)
Step "批处理 12 条"     @("batch", "--dwg", $new, "--input", (Join-Path $PSScriptRoot "plan.json"))
Step "查询"            @("query", $new, "polyline[layer=WALL][area>=1000000]")
Step "选择器修改"       @("set", $new, "circle[layer=WALL]", "--prop", "radius=600")
# Windows PowerShell 5.1 会吞掉传给外部程序的参数里的双引号，含引号的 JSON / LISP 一律经文件传入
$rollback = Join-Path $out "rollback.json"
Set-Content $rollback '[{"op":"add","parent":"/model","type":"circle","props":{"center":"0,0","radius":99}},{"op":"add","parent":"/model","type":"circle","props":{"raduis":1}}]' -Encoding UTF8
$lsp = Join-Path $out "info.lsp"
Set-Content $lsp '(list (getvar "ACADVER") (getvar "DWGNAME"))' -Encoding UTF8

Step "原子回滚"        @("batch", "--dwg", $new, "--input", $rollback)
Step "回滚后查询"       @("query", $new, "circle[radius=99]")
Step "统计"            @("stats", $new)
Step "图层"            @("get", $new, "/layers")
Step "外部参照 附着"    @("add", $new, "/xrefs", "--type", "xref", "--prop", "path=b.dwg", "--prop", "layer=XREF")
Step "外部参照 卸载"    @("set", $new, "/xref[@name=b]", "--prop", "loaded=false")
Step "外部参照 重载"    @("set", $new, "/xref[@name=b]", "--prop", "reload=true")
Step "外部参照 绑定"    @("set", $new, "/xref[@name=b]", "--prop", "bind=insert")
Step "修改已有 2013 图" @("add", $b, "/model", "--type", "circle", "--prop", "center=0,0", "--prop", "radius=321")

# ---- 第 1 批：新类型（椭圆 / 样条 / 构造线 / 射线 / 填充 / 标注 / 引线 / 中文样式）与原生编辑 ----
$n1 = Join-Path $out "第1批.dwg"
Step "第1批 新建"        @("create", $n1)
Step "第1批 新类型"      @("batch", "--dwg", $n1, "--input", (Join-Path $PSScriptRoot "batch1.json"))
Step "第1批 原生编辑"    @("batch", "--dwg", $n1, "--input", (Join-Path $PSScriptRoot "edits.json"))
Step "第1批 统计"        @("stats", $n1)

# ---- 第 1 批：命令式编辑（经 LISP，不依赖插件） ----
$geo = Join-Path $out "geo.lsp"
Set-Content $geo @'
(defun mk (a b) (entmakex (list '(0 . "LINE") (cons 10 a) (cons 11 b))) (cdr (assoc 5 (entget (entlast)))))
(list (mk '(100000 0 0) '(110000 0 0)) (mk '(105000 -2000 0) '(105000 2000 0))
      (mk '(120000 0 0) '(125000 0 0)) (mk '(120000 0 0) '(120000 5000 0))
      (mk '(130000 0 0) '(135000 0 0)) (mk '(130000 0 0) '(130000 5000 0))
      (mk '(140000 3000 0) '(140000 1000 0)) (mk '(138000 0 0) '(145000 0 0)))
'@ -Encoding UTF8
$raw = & $exe lisp $n1 --file $geo --save --acad $Acad 2>&1 | Out-String
Add-Content $log "=== 命令式编辑 测试几何`r`n$($raw.Trim())" -Encoding UTF8
$h = [regex]::Matches($raw, '"([0-9A-F]+)"') | ForEach-Object { $_.Groups[1].Value }
if ($h.Count -ge 8) {
    Step "trim"    @("edit", $n1, "trim", "$($h[0])@108000,0", "--prop", "edges=$($h[1])")
    Step "extend"  @("edit", $n1, "extend", "$($h[6])@140000,1200", "--prop", "boundary=$($h[7])")
    Step "fillet"  @("edit", $n1, "fillet", $h[2], "--prop", "with=$($h[3])", "--prop", "radius=1000")
    Step "chamfer" @("edit", $n1, "chamfer", $h[4], "--prop", "with=$($h[5])", "--prop", "d1=500")
    $chk = Join-Path $out "chk.lsp"
    Set-Content $chk ('(mapcar (function (lambda (h) (list h (cdr (assoc 10 (entget (handent h)))) (cdr (assoc 11 (entget (handent h))))))) (list "' + ($h[0], $h[2], $h[4], $h[6] -join '" "') + '"))') -Encoding UTF8
    Step "命令式编辑 核对" @("lisp", $n1, "--file", $chk)
} else {
    Add-Content $log "（没有拿到测试几何的句柄，跳过命令式编辑）" -Encoding UTF8
}
Step "离线 LISP"       @("lisp", $b, "--file", $lsp)

# ---- 第 2 批：布局 / 视口（文档模式）/ 打印 ----
$n2 = Join-Path $out "第2批.dwg"
# 测量与校验：已知几何（共边、1000x1000 重叠、越界、相邻），结果与 inspect.json 注释核对
$n3 = Join-Path $out "测量校验.dwg"
Step "测量校验 新建"      @("create", $n3)
Step "测量校验"           @("batch", "--dwg", $n3, "--input", (Join-Path $PSScriptRoot "inspect.json"))

# 图块与线型：定义块（含 replace）、插入、改名、属性（属性定义用 LISP entmake 造）、删除保护
$n4 = Join-Path $out "图块.dwg"
Step "图块 新建"          @("create", $n4)
Step "图块 定义与插入"    @("batch", "--dwg", $n4, "--input", (Join-Path $PSScriptRoot "blocks.json"))
Step "图块 属性定义"      @("lisp", $n4, "--file", (Join-Path $PSScriptRoot "attdef.lsp"), "--save")
Step "图块 插入带属性"    @("add", $n4, "/model", "--type", "insert", "--prop", "name=ROOMTAG", "--prop", "position=3000,3000", "--prop", "attributes=NO=A-101;AREA=36.5")
Step "图块 改属性"        @("set", $n4, "insert[name=ROOMTAG]", "--prop", "attributes=NO=A-102")
Step "图块 删除被参照的块" @("remove", $n4, "/block[@name=树]")
Step "图块 列表"          @("get", $n4, "/blocks")

Step "第2批 新建"          @("create", $n2)
Step "第2批 布局与视口"    @("batch", "--dwg", $n2, "--input", (Join-Path $PSScriptRoot "layouts.json"))
Step "第2批 布局列表"      @("get", $n2, "/layouts")
Step "第2批 图纸空间查询"  @("query", $n2, "entity[space=A3-平面]")
Step "第2批 打印布局"      @("plot", $n2, "A3-平面", "--prop", "output=$(Join-Path $out 'A3-平面.pdf')")
Step "第2批 打印模型"      @("plot", $n2, "Model", "--prop", "area=extents", "--prop", "paper=A3", "--prop", "landscape=true", "--prop", "mono=true", "--prop", "output=$(Join-Path $out 'model.pdf')")
Step "第2批 中文返回值"    @("lisp", $n2, "--file", $lsp)

$h1 = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($new)[0..5])
$h2 = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($b)[0..5])
Add-Content $log "文件格式：新建.dwg=$h1  b.dwg=$h2" -Encoding UTF8
Write-Host "完成，日志：$log"
