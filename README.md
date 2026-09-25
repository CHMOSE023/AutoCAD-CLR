# AutoCADCLR

面向 AI 智能体的 AutoCAD 命令行工具。设计参考 [OfficeCLI](https://github.com/iOfficeAI/OfficeCLI)：
少量通用动词 + 路径寻址 + 原子批处理 + 按需查询的 help，而不是几十个各自带 schema 的 MCP 工具。

```bash
acadclr add /model --type line --prop start=0,0 --prop end=5000,0 --prop layer=WALL
acadclr query "line[layer=WALL][length>=3000]"
acadclr set "line[layer=WALL]" --prop color=1
acadclr get "/entity[@handle=2A3]" --json
acadclr batch --input plan.json          # 几百条操作一次提交，任一失败整批回滚
```

## 架构

```
acadclr.exe ──┬─ 实时模式（默认）：命名管道 + 一行一个 JSON ──▶ AcadClr.Plugin.dll（NETLOAD 进 AutoCAD 界面）
              └─ 离线模式 --dwg：启动 accoreconsole.exe（无界面）──▶ 同一个插件，在后台 Database 中直接读写 DWG
AcadClr.Core.dll：协议、路径 / 选择器解析、属性 schema、help —— CLI 与插件共用
```

| 目录 | 内容 |
|---|---|
| `src/AcadClr.Core` | `Protocol.cs` 请求 / 响应，`PathSyntax.cs` 路径与选择器，`Schema.cs` 类型与属性定义（help 与校验的唯一来源），`Commands.cs` 命令与参数定义（命令行解析、help 的唯一来源），`Values.cs` 值解析 |
| `src/AcadClr.Plugin` | `Engine/Executor.cs` 执行批处理（外层事务 + 每条一个嵌套事务），`Engine/Mutate.cs` 增改，`Engine/Nodes.cs` 读取，`Host/` 管道服务、主线程调度、离线入口 |
| `src/AcadClr.Cli` | `acadclr.exe`：`Program.cs` 命令行 → JSON 参数，`Dispatcher.cs` 参数 → 请求并选择实时 / 离线传输，`Output.cs` 文本与 JSON 输出 |
| `tests` | `AcadClr.Tests` 单元测试（`dotnet test`，不需要 AutoCAD），`smoke.ps1` 离线冒烟测试 |

## 构建

需要 .NET SDK 8 或更高版本。

```bash
dotnet build AutoCADCLR.slnx -c Release
```

所有产物都输出到 `bin/Release/`：`acadclr.exe`、`AcadClr.Plugin.dll`、`AcadClr.Core.dll`、`Newtonsoft.Json.dll`，四个文件放在同一目录。

支持 AutoCAD 2014–2024（.NET Framework 版）。2025 起 AutoCAD 改用 .NET 8，需要另外编译一版插件。

## 使用前：把插件目录设为受信任位置

AutoCAD 2014 起有 `SECURELOAD` 安全加载机制，只能从受信任位置加载插件：

- **实时模式**：NETLOAD 时 AutoCAD 会弹出安全提示，选“加载一次”或“始终加载”即可。
- **离线模式**（`create`、`get`、`add`、`batch` 等）：accoreconsole 没法弹出提示，会直接拒绝加载。
  离线的 `lisp`、`script` 不需要插件，所以不受影响。其余命令必须先把 `bin\Release` 目录加入受信任位置：
  AutoCAD「选项 → 文件 → 受信任的位置」，添加该目录；或者在命令行执行 `TRUSTEDPATHS`。

## 实时模式

1. 在 AutoCAD 命令行执行 `NETLOAD`，选择 `bin\Release\AcadClr.Plugin.dll`。
   加载后插件会自动启动服务，并提示管道名。
2. 在终端运行 `acadclr status` 确认已连接。

插件命令：`ACADCLR_START` / `ACADCLR_STOP` / `ACADCLR_STATUS`。同时打开多个 AutoCAD 时，
`acadclr instances` 可以列出各个实例，默认连接最近启动的那个，用 `--pid` 指定其他实例。

## 离线模式

不打开 AutoCAD 界面，直接读写 DWG 文件（需要本机装有 AutoCAD 2014–2024，用它自带的 accoreconsole）：

```bash
acadclr create D:/work/plan.dwg
acadclr add plan.dwg /model --type circle --prop center=0,0 --prop radius=500
acadclr batch --dwg D:/work/plan.dwg --input plan.json
acadclr stats plan.dwg
```

- **选择 AutoCAD 版本**：`acadclr config acad 2014` 设置默认版本；单次调用可以用 `--acad 2020` 临时指定。
  运行 `acadclr config` 可以查看当前使用的版本和已安装的版本。
- **格式检查**：执行前会读取 DWG 文件头。如果文件格式高于所用 AutoCAD 能打开的版本
  （比如用 2014 打开 2018 格式），会直接报错 `unsupported_version`。
  这是因为 accoreconsole 打不开文件时，会静默退回空白的 Drawing1 继续执行。
  执行后还会核对实际打开的图纸，只有确认是目标文件才保存。
- **保持原格式**：保存时按文件原来的格式另存（例如 2013 格式仍存为 2013），不会被升级成所用 AutoCAD 的格式。
  所以用 2020 修改过的图纸，2014 仍然能打开。
- 命令的第一个参数如果以 `.dwg` 结尾，就等价于 `--dwg`。
- 修改会自动写回文件，同时生成 `.bak` 备份。原子批处理失败时文件保持不变。
- 每次调用都要启动一次 accoreconsole，大约需要 3–5 秒，所以大量操作应该合并成一次 `batch`。
- `--acad 2020` 或 `--acad <accoreconsole.exe 路径>` 可以指定 AutoCAD 版本，也可以设置环境变量 `ACADCLR_ACCORE`。

## 命令

| 命令 | 说明 |
|---|---|
| `status` | 连接状态与当前图形信息 |
| `get <path> [--depth N] [--limit N]` | 读取元素；`/model`、`/layers` 默认列出子元素 |
| `query <selector>` | CSS 风格选择器查询 |
| `add <parent> --type T --prop k=v …` | 添加元素；`--from <path>` 深度克隆一个实体（可以配合 `move`） |
| `set <path\|selector> --prop k=v …` | 修改属性；选择器必须带条件，否则需要加 `--force` |
| `remove <path\|selector>` | 删除；一次超过 30 个需要加 `--force` |
| `batch --input f.json \| --commands '<json>' \| 标准输入` | 批处理，默认原子执行 |
| `stats` | 按类型、图层统计实体，并给出图形范围 |
| `measure <动作> [目标]` | 测量：`distance` `area` `length` `convert`（单位换算） |
| `check <动作> <目标>` | 空间校验（按包围盒）：`overlap` 重叠、`inside` 越界、`adjacent` 相邻，结果 `PASS` / `FAIL` |
| `lisp "<expr>" \| --file f.lsp` | 执行 AutoLISP 并返回值；实时模式加 `--cmd` 走命令队列，离线模式加 `--save` 保存 |
| `script f.scr \| --text "..."` | 执行脚本；离线模式可以对多个 DWG 批量运行，文件名支持通配符 |
| `save [--as path]` | 保存（实时模式） |
| `create <file.dwg>` | 新建空白 DWG（离线，单位默认 mm） |
| `instances` | 列出加载了插件的 AutoCAD 实例 |
| `help [type\|命令] [--json]` | 查看类型的属性，或命令的参数（命令行写法与 JSON / MCP 参数名对照） |

全局选项：`--json`、`--dwg`、`--acad`、`--pid`、`--timeout`。其余选项属于各自的命令（`--best-effort`、`--stop-on-error` 只用于 `batch`，
`--force` 用于 `set`、`remove`、`edit`、`batch`），用错命令会直接报错；`acadclr help <命令>` 查看某个命令的全部参数。

退出码：`0` 成功，`1` 有操作失败，`2` 用法错误或无法连接。

## 路径与选择器

```
/                          文档（units、currentLayer 可 set）
/model                     模型空间
/model/line[3]             第 3 条直线（从 1 开始；[last()] 取最后一个）
/model/entity[@handle=2A3] 按句柄定位，增删实体后不会漂移（推荐）
/entity[@handle=2A3]       同上，简写
/layers                    图层表
/layer[@name=WALL]         图层
/xrefs                     外部参照表
/xref[@name=BASE]          外部参照
/blocks                    图块定义（bboxFromBase：相对基点的范围）
/block[@name=TREE]         图块定义
/linetypes                 线型表
/linetype[@name=CENTER]    线型
```

选择器：`line[layer=WALL][length>=3000]`、`entity[color=1]`、`text[text~=客厅]`、`layer[frozen=true]`。
支持的运算符有 `= != > < >= <= ~=`（`~=` 为包含匹配，不区分大小写）；多个 `[]` 之间是“与”的关系。

## 批处理

```json
[
  {"command":"add","parent":"/layers","type":"layer","props":{"name":"WALL","color":1,"lineWeight":0.5}},
  {"command":"add","parent":"/model","type":"polyline","props":{"points":"0,0;6000,0;6000,4000;0,4000","closed":true,"layer":"WALL"}},
  {"command":"add","parent":"/model","from":"$1","props":{"move":"7000,0"}},
  {"command":"set","path":"$2","props":{"color":3}}
]
```

- `$N` 引用第 N 条操作（从 0 开始）返回的路径，可以用于 `path`、`parent`、`from`。
- **在 Windows PowerShell 5.1 中**，传给外部程序的参数里的双引号会被吞掉，`--commands '<json>'` 因此会被破坏。
  请改用 `--input 文件`（或者用标准输入传入），LISP 用 `--file`。PowerShell 7、cmd、bash 不受影响。
- 执行方式默认是原子的：每条操作都会执行并报告结果，只要有一条失败，就回滚整批操作。
- `--best-effort` 保留成功的部分；`--stop-on-error` 遇到第一个失败就停止。

## 布局、视口与打印

```bash
acadclr get /layouts                                          # 布局列表：设备、纸张、方向、视口数、当前布局
acadclr add /layouts --type layout --prop name=A3 --prop device="DWG To PDF.pc3" --prop paper=full_bleed_A3 --prop landscape=true
acadclr add "/layout[@name=A3]" --type polyline --prop points="10,10;410,10;410,287;10,287" --prop closed=true   # 图纸空间的图框
acadclr add "/layout[@name=A3]" --type viewport --prop center=210,150 --prop width=380 --prop height=250 \
        --prop viewCenter=15000,10000 --prop scale=100 --prop locked=true                                         # 1:100 视口
acadclr set "/layout[@name=A3]" --prop current=true            # 切换当前布局；set 也可改 name / device / paper / landscape / plotStyle
acadclr remove "/layout[@name=布局2]"
acadclr get /devices                                          # 打印设备；get "/device[@name=DWG To PDF.pc3]" 查看纸张
acadclr plot "/layout[@name=A3]" --prop output=D:/out/A3.pdf  # 打印布局
acadclr plot plan.dwg Model --prop area=extents --prop paper=A3 --prop landscape=true --prop mono=true
```

- **实体画在哪个空间，由父路径决定**：`/model` 是模型空间，`/layout[@name=X]` 是该布局的图纸空间。图纸空间实体的路径形如
  `/layout[@name=A3]/line[@handle=..]`，查询时用 `[space=A3]` 过滤（默认只查模型空间）。
  AutoCADMCP 依赖"当前空间"这种隐式状态，曾出现过"以为画在布局上，其实进了模型空间"的问题，这里不会发生。
- 纸张可以写全名（`ISO_A3_(420.00_x_297.00_MM)`），也可以写简称（`A3`、`full_bleed_A3`），方向由 `landscape` 决定。
  布局的原点在可打印区域的左下角，贴边的图框请用 `full_bleed` 纸张，否则会被页边距裁掉。
- 打印范围只支持 `layout` 和 `extents`。window、display、limits 在 AutoCAD 2014 的打印引擎上打不出内容（AutoCADMCP 实测）。
  只出局部时，建一个布局，用视口框定范围。
- 新建 / 删除 / 重命名 / 切换布局、新建视口都是数据库级操作。和外部参照一样，含这类操作的 batch 会逐条执行，不支持回滚。
- **离线模式**：新建视口和打印需要真正的文档，会自动改用"文档模式"：accoreconsole 用 `/i` 打开图纸，插件直接在该文档上执行，
  有修改时再按原格式另存。在后台数据库里操作视口会让 accoreconsole 崩溃，实测如此。

## 外部参照

```bash
acadclr add /xrefs --type xref --prop path=D:/work/base.dwg --prop layer=XREF   # 附着（overlay=true 为覆盖）
acadclr get /xrefs                                     # 列出：路径、实际找到的文件、状态、插入句柄
acadclr query "xref[status=filenotfound]"              # 找丢失的参照
acadclr set "xref[status=filenotfound]" --prop path=D:/new/base.dwg   # 批量改路径并重载
acadclr set "/xref[@name=base]" --prop loaded=false    # 卸载；loaded=true 或 reload=true 为重载
acadclr set "/xref[@name=base]" --prop bind=insert     # 绑定为本地图块（bind 会给符号加 $0$ 前缀）
acadclr remove "/xref[@name=base]"                     # 拆离
```

相对路径按当前图形所在目录解析。附着、重载、卸载、绑定、拆离都是数据库级操作，
没法可靠地放进事务回滚。所以含这类操作的 batch 会逐条执行：成功的立即生效，结果里标记 `"atomic": false`。

## LISP 与脚本

```bash
acadclr lisp "(getvar \"DWGNAME\")"                      # 实时：直接求值（acedEvaluateLisp），同步返回值
acadclr lisp --cmd "(command \"_.ZOOM\" \"_E\")"          # 实时：含 (command ...) 时走命令队列
acadclr lisp plan.dwg --file count.lsp                   # 离线：执行 .lsp 文件，返回最后一个表达式的值
acadclr lisp plan.dwg --save "(command \"_.CIRCLE\" \"0,0\" 500)"   # 离线：执行后保存
acadclr script fix.scr "D:/drawings/*.dwg" --save        # 离线：对一批图纸逐个运行脚本并保存
```

- LISP 结果的编码按 AutoCAD 版本确定：2021 起为 UTF-8，之前为系统 ANSI。不能"先试 UTF-8 再退回"，
  因为 GBK 编码的中文常常恰好也是合法的 UTF-8（比如"实时"会被解成"ʵʱ"）。
- 返回值用 `vl-prin1-to-string` 表示，字符串会带引号，便于区分类型。LISP 出错时返回错误码 `lisp_error`，退出码为 1。
- `.lsp` 与 `.scr` 文件的编码会自动识别（UTF-8 或 GBK）。生成的脚本会按 AutoCAD 版本选择编码：2020 及以前用系统 ANSI，2021 起用 UTF-8。
- **离线的 `lisp` 和 `script` 不需要加载插件**：它们让 accoreconsole 直接打开 DWG 执行，所以不受 SECURELOAD 限制。
  每个文件大约需要 2–4 秒。不加 `--save` 就不会改动文件。
- 实时模式的 `script` 会把内容送进 AutoCAD 命令行异步执行，不等待结果。
- 脚本里的空行等于回车，会重复上一条命令。编写 `.scr` 时，只在确实需要结束命令的地方留空行。
- 执行前会检查括号与引号是否配对，不配对直接报 `lisp_syntax`。不完整的 LISP 送进 AutoCAD 命令行会让读取器一直等待续行，卡住 AutoCAD。
- 走命令队列时，代码会先压成一行（去掉注释）再发送。中间带换行时，末尾的回车会变成重复上一条命令的空回车，实测会导致 AutoCAD 崩溃。
- LISP 能执行任意代码，和在终端里运行程序的权限相同。命名管道只允许当前用户连接。以后如果加 MCP 端点，要给这一项单独加开关。

## 类型

| 类别 | 类型 |
|---|---|
| 基本图元 | `line` `circle` `arc` `polyline` `ellipse` `spline` `point` `xline` `ray` |
| 文字 | `text` `mtext`：文字含中文，而当前样式显示不了中文（SHX 字体且没配大字体）时，自动改用宋体样式 `SimSun_CJK` |
| 标注与填充 | `dimension`（`kind`=linear / aligned / angular / radius / diameter）、`leader`、`hatch` |
| 图块与参照 | `insert`、`xref` |
| 其他 | `layer`、`document` |

完整属性表可以运行 `acadclr help <type>` 查看。毫米单位的图纸给标注和引线设 `scale=100`（DIMSCALE 替代），
否则文字和箭头会非常小。

```bash
acadclr add /model --type dimension --prop kind=linear --prop p1=0,0 --prop p2=6000,0 --prop dimLine=3000,-800 --prop scale=100
acadclr add /model --type hatch --prop boundary=8A --prop pattern=AR-CONC --prop scale=2
acadclr add /model --type leader --prop points="0,0;500,500;1200,500" --prop text=C20混凝土 --prop height=250 --prop scale=100
```

## 编辑

`acadclr edit <动作> <目标> [--prop ...]`，可以运行 `acadclr help edit` 查看全部动作。目标可以是路径、句柄或选择器，多个用 `;` 分隔。

| 动作 | 说明 | 方式 |
|---|---|---|
| `offset` | 偏移；`side=x,y` 指定偏向哪一侧 | 原生，可放进 batch |
| `mirror` | 镜像；`keep=false` 原地镜像 | 原生 |
| `explode` | 分解 | 原生 |
| `break` | 打断；一点分成两段，两点删除中间段 | 原生 |
| `join` | 合并到第一个实体 | 原生 |
| `array` | 矩形阵列（rows/cols/rowSpacing/colSpacing/angle）或环形阵列（center/count/fillAngle/rotateItems） | 原生 |
| `trim` `extend` | 修剪 / 延伸；目标写成 `句柄@x,y`，拾取点决定处理哪一段 | AutoCAD 命令 |
| `fillet` `chamfer` | 倒圆角 / 倒角 | AutoCAD 命令 |

```json
{"command":"edit","action":"offset","path":"$0","props":{"distance":240,"side":"3000,2000"}}
```

标为"AutoCAD 命令"的动作通过 `(command)` 执行，不能放进 batch。实时模式走命令队列，离线模式由 accoreconsole 打开图纸执行并保存。
执行时临时修改的 TRIMMODE、FILLETRAD 等系统变量，结束后会恢复原值。

其他实体（hatch、dimension、spline……）可以查询，也可以修改公共属性：
`layer`、`color`、`linetype`、`lineWeight`、`move`、`rotate`、`scale`。

## 路线图

- 图块定义与列表、系统变量、多文档、单位换算、测量、撤销 / 标记回滚（第 3 批）
- `view png`：截图反馈
- `dump`：把现有图纸导出成可重放的 batch JSON
- 空间校验（重叠 / 越界 / 相邻）—— 可以从 AutoCADMCP 迁移过来
- MCP 端点：只暴露一个通用 `command` 工具
