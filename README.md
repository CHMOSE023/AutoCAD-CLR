# AutoCADCLR

面向 AI 智能体的 AutoCAD 工具：一套命令，两种用法。

- **命令行** `acadclr`：给能跑终端命令的智能体（和人）用。
- **MCP server** `acadclr mcp`：给 MCP 客户端用。**工具就是命令，名字、参数都相同**，说明、校验、错误提示、日志完全一致。

设计参考 [OfficeCLI](https://github.com/iOfficeAI/OfficeCLI)：少量通用动词 + 路径寻址 + 原子批处理 + 按需查询的 help，
而不是几十个各自带 schema 的工具。

```bash
acadclr add /model --type line --prop start=0,0 --prop end=5000,0 --prop layer=WALL
acadclr query "line[layer=WALL][length>=3000]"
acadclr set "line[layer=WALL]" --prop color=1
acadclr check overlap "polyline[layer=ROOM]"         # 房间有没有画重
acadclr batch --input plan.json                      # 几百条操作一次提交，任一失败整批回滚
acadclr stats --dwg D:/work/plan.dwg                 # 不开 AutoCAD，离线读写 DWG
```

同样的调用写成 MCP：`add {"parent":"/model","type":"line","props":{"start":"0,0","end":"5000,0","layer":"WALL"}}`。

支持 AutoCAD 2014–2024（.NET Framework 版）。当前版本 0.2.0。

## 快速开始

### 1. 构建

需要 .NET SDK 8 或更高版本。

```bash
dotnet build AutoCADCLR.slnx -c Release
```

产物在 `bin/Release/`：`acadclr.exe`、`AcadClr.Plugin.dll`、`AcadClr.Core.dll`、`Newtonsoft.Json.dll`，四个文件放在同一目录。

### 2. 把插件目录设为受信任位置

AutoCAD 2014 起有 `SECURELOAD`，只从受信任位置加载插件。在 AutoCAD「选项 → 文件 → 受信任的位置」里添加 `bin\Release`
（或命令行 `TRUSTEDPATHS`）。实时模式 NETLOAD 时也可以在安全提示里选“加载一次”；**离线模式必须设置**，
accoreconsole 没法弹提示，会直接拒绝加载（离线的 `lisp` / `script` 不需要插件，不受影响）。

### 3. 选一种用法

**实时模式**（操作正在打开的 AutoCAD）：在 AutoCAD 命令行执行 `NETLOAD`，选择 `bin\Release\AcadClr.Plugin.dll`，
插件自动启动服务。然后在终端运行 `acadclr status` 确认已连接。

**离线模式**（不开 AutoCAD 界面，直接读写 DWG，需要本机装有 AutoCAD 2014–2024）：

```bash
acadclr create D:/work/plan.dwg
acadclr batch --dwg D:/work/plan.dwg --input plan.json
acadclr get plan.dwg /layers          # 第一个参数以 .dwg 结尾等价于 --dwg
```

**MCP**：stdio（由客户端拉起）

```json
{
  "mcpServers": {
    "acadclr": { "type": "stdio", "command": "D:\\AutoCADCLR\\bin\\Release\\acadclr.exe", "args": ["mcp"] }
  }
}
```

或 HTTP（常驻，多个客户端共用）：先运行 `acadclr mcp --http`，或在 AutoCAD 里执行 `ACADCLR_MCP`，再配置

```json
{
  "mcpServers": {
    "acadclr": { "type": "http", "url": "http://127.0.0.1:7140/mcp" }
  }
}
```

给智能体的使用说明见 [SKILL.md](SKILL.md)。

## 命令

| 命令 | 说明 |
|---|---|
| `status` | 连接状态与当前图形信息（含只读、LISP 开关状态） |
| `get <path> [--depth N] [--limit N]` | 读取元素；`/model`、`/layers` 等默认列出子元素 |
| `query <selector>` | CSS 风格选择器查询 |
| `add <parent> --type T --prop k=v …` | 添加元素；`--from <path>` 深度克隆一个实体（可配合 `move`） |
| `set <目标> --prop k=v …` | 修改属性（含 `move` / `rotate` / `scale`）；选择器必须带条件，否则加 `--force` |
| `remove <目标>` | 删除；一次超过 30 个需要 `--force` |
| `edit <动作> <目标>` | 偏移 镜像 分解 打断 合并 阵列 修剪 延伸 倒圆角 倒角 |
| `measure <动作> [目标]` | 测量：`distance` `area` `length` `convert`（单位换算） |
| `check <动作> <目标>` | 空间校验（包围盒）：`overlap` 重叠、`inside` 越界、`adjacent` 相邻，结果 `PASS` / `FAIL` |
| `batch` | 批处理（`--input 文件` / `--commands '<json>'` / 标准输入），默认原子执行 |
| `stats` | 按类型、图层统计实体，给出图形范围 |
| `view zoom\|capture [目标]` | 缩放视图、截图为 PNG（实时模式） |
| `plot [布局]` | 打印到 PDF |
| `mark [标签]` / `rollback` / `undo [N]` | 打撤销标记、回到标记、撤销 N 步（实时模式） |
| `lisp "<expr>" \| --file f.lsp` | 执行 AutoLISP 并返回值；实时加 `--cmd` 走命令队列，离线加 `--save` 保存 |
| `script f.scr \| --text "..."` | 执行脚本；离线可对多个 DWG（支持通配符）批量运行 |
| `save [--as path]` | 保存（实时模式）；`--as` 后文档随之改为新文件 |
| `create <file.dwg>` | 新建空白 DWG（离线，单位默认 mm） |
| `instances` | 列出加载了插件的 AutoCAD 实例 |
| `log [N]` | 查看操作日志的最后 N 行 |
| `help [类型\|命令\|edit <动作>]` | 类型的属性、命令的参数（命令行写法与 JSON / MCP 参数名对照）；`--json` 输出结构 |
| `mcp [--http]` | 启动 MCP server（见“MCP”） |
| `config [acad <年份\|auto>]` | 离线模式默认使用的 AutoCAD 版本 |

**目标**：路径、句柄或 `$N`（多个用 `;` 分隔，如 `8A;8B`），或选择器（`line[layer=WALL]`）。

**公共选项**：`--json`（结构化输出）、`--dwg <file>`（离线）、`--acad <年份|路径>`（离线用哪个 AutoCAD）、
`--pid <进程号>`（多个 AutoCAD 时选实例）、`--doc <文件名>`（实时模式下操作指定的已打开文档，不必先切换）、`--timeout 秒`。
其余选项属于各自的命令，用错会直接报错；`acadclr help <命令>` 列出某个命令的全部参数。

**退出码**：`0` 成功，`1` 有操作失败，`2` 用法错误或无法连接。

**命令行与 MCP 的对应**：工具名 = 命令名；位置参数写成 `path` / `parent` / `selector` / `action` 等（见 `help <命令>`），
`--prop k=v` 写进 `props`，其他 `--xxx` 去掉前缀（`--best-effort` → `bestEffort`）。

## 路径与选择器

```
/                          当前文档（units、ltscale、currentLayer … 可 set）
/model                     模型空间
/model/line[3]             第 3 条直线（从 1 开始；[last()] 取最后一个）
/model/entity[@handle=2A3] 按句柄定位，增删实体后不会漂移（推荐）
/entity[@handle=2A3]       同上，简写（set / remove / edit 等的目标还可以直接写句柄 2A3）
/layers  /layer[@name=WALL]
/linetypes  /linetype[@name=CENTER]
/blocks  /block[@name=TREE]                  图块定义（bboxFromBase：相对基点的范围）
/layouts  /layout[@name=A3]  /layout[@name=A3]/viewport[1]
/devices  /device[@name=DWG To PDF.pc3]      打印设备与纸张
/xrefs  /xref[@name=BASE]
/sysvars  /sysvar[@name=PDMODE]              系统变量（set 用 value=）
/documents  /document[@name=plan.dwg]        打开的图形（实时模式）
```

选择器：`line[layer=WALL][length>=3000]`、`entity[color=1]`、`text[text~=客厅]`、`insert[name=TREE]`、`layer[frozen=true]`。
运算符 `= != > < >= <= ~=`（`~=` 为不区分大小写的包含）；多个 `[]` 之间是“与”。
按窗口选：`entity[inside=0,0;6000,4000]`（包围盒完全在窗口内）、`entity[crossing=…]`（与窗口相交），可与其他条件组合。

## 批处理

```json
[
  {"command":"add","parent":"/layers","type":"layer","props":{"name":"WALL","color":1,"lineWeight":0.5}},
  {"command":"add","parent":"/model","type":"polyline","props":{"points":"0,0;6000,0;6000,4000;0,4000","closed":true,"layer":"WALL"}},
  {"command":"add","parent":"/model","from":"$1","props":{"move":"7000,0"}},
  {"command":"set","path":"$2","props":{"color":3}},
  {"command":"check","action":"overlap","selector":"polyline[layer=WALL]"}
]
```

- `$N` 引用第 N 条操作（从 0 开始）返回的路径，可用于 `path`、`parent`、`from` 以及 `edit` 的目标。
- 默认原子执行：每条都执行并报告结果，只要有一条失败就回滚整批。`--best-effort` 保留成功的部分，`--stop-on-error` 遇到失败就停。
- 可进 batch 的命令：`get` `query` `add` `set` `remove` `edit`（命令式动作除外） `measure` `check` `stats`。
- **Windows PowerShell 5.1** 会吞掉传给外部程序的参数里的双引号，`--commands '<json>'` 因此会被破坏：
  改用 `--input 文件` 或标准输入，LISP 用 `--file`。PowerShell 7、cmd、bash 不受影响。

## 功能

### 类型

| 类别 | 类型 |
|---|---|
| 基本图元 | `line` `circle` `arc` `polyline` `ellipse` `spline` `point` `xline` `ray` |
| 文字 | `text` `mtext`：含中文而当前样式显示不了中文时，自动改用宋体样式 |
| 标注与填充 | `dimension`（`kind`=linear / aligned / angular / radius / diameter）、`leader`、`hatch` |
| 图块 | `block`（定义）、`insert`（参照，`attributes` 赋属性值） |
| 布局与打印 | `layout`、`viewport` |
| 其他 | `layer`、`linetype`、`xref`、`sysvar`、`document` |

完整属性表：`acadclr help <类型>`。毫米单位的图纸给标注和引线设 `scale=100`，否则文字和箭头会非常小。

### 编辑

| 动作 | 说明 | 方式 |
|---|---|---|
| `offset` | 偏移；`side=x,y` 指定偏向哪一侧 | 原生，可进 batch |
| `mirror` | 镜像；`keep=false` 原地镜像 | 原生 |
| `explode` | 分解 | 原生 |
| `break` | 打断；一点分成两段，两点删除中间段 | 原生 |
| `join` | 合并到第一个实体 | 原生 |
| `array` | 矩形阵列（rows/cols/rowSpacing/colSpacing/angle）或环形阵列（center/count/fillAngle/rotateItems） | 原生 |
| `trim` `extend` | 修剪 / 延伸；目标写成 `句柄@x,y`，拾取点决定处理哪一段 | AutoCAD 命令 |
| `fillet` `chamfer` | 倒圆角 / 倒角 | AutoCAD 命令 |

AutoCAD 命令式的动作不能进 batch：实时模式走命令队列（参数由插件生成代码，不受 LISP 开关影响），
离线模式由 accoreconsole 打开图纸执行并保存。临时修改的 TRIMMODE、FILLETRAD 等系统变量结束后恢复原值。

### 测量与校验

`measure area|length <目标>` 给出每个实体的面积 / 周长或长度及合计；`measure distance --prop from=x,y --prop to=x,y`；
`measure convert --prop value=3.6 --prop from=m --prop to=mm`（from 缺省为图形单位）。

**面积对不代表位置对**：`check overlap`（两两重叠，共边不算）、`check inside --prop boundary=<外轮廓>`（越界及各方向超出距离）、
`check adjacent <A> --prop with=<B> --prop gap=240`（是否相邻）。判定按包围盒，矩形房间很准，斜放或异形实体可能误报。

### 图块、线型、单位与系统变量

- `get /blocks` 看块定义，**插入前看 `bboxFromBase`**：插入点 + 该范围 = 实际占位。
  用已有实体定义块：`add /blocks --type block --prop name=TREE --prop entities="8A;8B" --prop base=0,0`（`replace=true` 原地替换）。
- 线型：`get /linetypes`；图层 / 实体用到时自动从 acadiso.lin 加载。被参照的块、被使用的线型不能删除。
- 单位是文档属性：`set / --prop units=mm --prop ltscale=100`（改 units 不缩放已有几何）。
- 系统变量：`get "/sysvar[@name=PDMODE]"`、`set "/sysvar[@name=PDMODE]" --prop value=3`。离线读写系统变量会改用文档模式，稍慢。

### 视图、文档与撤销（实时模式）

- `view capture --prop zoom=extents --prop maxWidth=1200`：截图（MCP 直接返回图片，命令行存 PNG，缺省只截绘图区）。
- `get /documents`；`add /documents --type document --prop path=D:\a.dwg` 打开（不给 path 则新建），
  `set "/document[@name=a.dwg]" --prop current=true` 切换，`remove "/document[@name=a.dwg]"` 关闭（有未保存修改要 `--force`）。
  其他命令加 `--doc a.dwg` 可直接操作非当前文档。
- 每次 acadclr 修改是 AutoCAD 里的一个撤销步（名为 ACADCLR），查询不占撤销步。`undo N` 或 Ctrl+Z 撤销；
  试探性修改前 `mark`，不满意 `rollback` 回到标记（没有标记时拒绝，避免撤销整张图）。

### 布局、视口与打印

```bash
acadclr add /layouts --type layout --prop name=A3 --prop paper=full_bleed_A3 --prop landscape=true
acadclr add "/layout[@name=A3]" --type polyline --prop points="10,10;410,10;410,287;10,287" --prop closed=true   # 图框
acadclr add "/layout[@name=A3]" --type viewport --prop center=210,150 --prop width=380 --prop height=250 \
        --prop viewCenter=15000,10000 --prop scale=100 --prop locked=true                                         # 1:100 视口
acadclr plot "/layout[@name=A3]" --prop output=D:/out/A3.pdf
acadclr plot plan.dwg Model --prop area=extents --prop paper=A3 --prop landscape=true --prop mono=true
```

- **实体画在哪个空间，由父路径决定**：`/model` 或 `/layout[@name=X]`，不依赖“当前空间”。图纸空间实体用 `[space=A3]` 查询。
- 纸张可写全名或简称（`A3`、`full_bleed_A3`）；布局原点在可打印区域左下角，贴边的图框请用 `full_bleed` 纸张。
- 打印范围只支持 `layout` 和 `extents`（window、display、limits 在 2014 的打印引擎上打不出内容）；只出局部就建布局用视口框定。
- 布局的增删改、新建视口、外部参照操作是数据库级操作：含这类操作的 batch 逐条执行，不支持回滚（结果标 `"atomic": false`）。

### 外部参照

```bash
acadclr add /xrefs --type xref --prop path=D:/work/base.dwg --prop layer=XREF   # 附着（overlay=true 为覆盖）
acadclr query "xref[status=filenotfound]"                                        # 找丢失的参照
acadclr set "xref[status=filenotfound]" --prop path=D:/new/base.dwg             # 改路径并重载
acadclr set "/xref[@name=base]" --prop bind=insert                              # 绑定；loaded=false 卸载，reload=true 重载
acadclr remove "/xref[@name=base]"                                              # 拆离
```

### LISP 与脚本

```bash
acadclr lisp "(getvar \"DWGNAME\")"                      # 实时：直接求值，同步返回值
acadclr lisp --cmd "(command \"_.ZOOM\" \"_E\")"          # 实时：含 (command ...) 时走命令队列
acadclr lisp plan.dwg --file count.lsp                   # 离线：返回最后一个表达式的值；加 --save 保存
acadclr script fix.scr "D:/drawings/*.dwg" --save        # 离线：对一批图纸逐个运行脚本并保存
```

- 优先用结构化命令：可校验、可回滚、结果有结构。LISP 用于它们覆盖不到的事。
- 执行前检查括号与引号是否配对（不配对报 `lisp_syntax`，否则会让 AutoCAD 命令行一直等续行）；走命令队列时先压成一行。
- LISP 结果按 AutoCAD 版本解码（2021 起 UTF-8，之前系统 ANSI）；`.lsp` / `.scr` 文件编码自动识别。
- 实时的 `script` 异步执行，不等待结果；脚本里的空行等于回车，会重复上一条命令。
- LISP 能执行任意代码：AutoCAD 里 `ACADCLR_LISP` 可关闭，MCP 默认不开放（见“安全”）。

## MCP

- **协议**：按 MCP **2026-07-28** 无状态规范实现（`server/discover`、每个请求自带 `_meta`、HTTP 校验
  `MCP-Protocol-Version` / `Mcp-Method` / `Mcp-Name`），同时兼容 **2025-11-25 / 2025-06-18 / 2025-03-26**（`initialize` 握手、`ping`）。
  逐请求判定，服务端不保存会话；跨调用的状态（文档、标记）都是普通参数。
- **工具**：由命令表生成，与命令一一对应（`config`、`mcp` 除外）；`action` 参数带枚举，只读 / 破坏性注解来自命令表。
  工具说明很短，类型与属性让模型用 `help` 工具按需查询。
- **结果**：`structuredContent` 是完整的 JSON 结果，`content` 是同一 JSON 的文本；参数写错、目标不存在等返回 `isError: true`
  与修正建议（模型可据此自行改正）；`view capture` 的截图作为 image 内容块返回。
- **选项**：`--http [--port 7140]`（只监听 127.0.0.1，校验 Host / Origin）、`--token T`（HTTP 要求 `Authorization: Bearer T`，
  也可用环境变量 `ACADCLR_MCP_TOKEN`）、`--read-only`（写操作工具不出现、调用也拒绝）、
  `--allow-lisp`（开放 `lisp` / `script`，**默认不开放**）、`--acad 2020`（离线用的 AutoCAD）。
- 多个 AutoCAD 时一个 `acadclr mcp` 就够，工具参数 `pid` 选实例；给 `dwg` 参数则离线读写，不需要打开 AutoCAD。
- 客户端取消（stdio `notifications/cancelled`、HTTP 断开）时关闭到插件的管道，AutoCAD 里尚未开始的操作不再执行。
- 暂不支持：SSE 进度推送（结果一次返回）、`subscriptions/listen`（工具列表固定）。

从 [AutoCad_MCP](https://github.com/CHMOSE023/AutoCad_MCP) 迁移：85 个旧工具的等价写法见 [对照表](docs/migrate-from-autocad-mcp.md)。

## 离线模式

- **选择 AutoCAD 版本**：`acadclr config acad 2014` 设默认；单次 `--acad 2020` 或 `--acad <accoreconsole.exe 路径>`；也可设环境变量 `ACADCLR_ACCORE`。
- **格式检查**：执行前读 DWG 文件头，格式高于所用 AutoCAD 能打开的版本时直接报 `unsupported_version`
  （accoreconsole 打不开文件时会静默退回空白的 Drawing1）；执行后核对实际打开的图纸，确认是目标文件才保存。
- **保持原格式**：按文件原来的格式另存，2020 改过的 2013 格式图纸 2014 仍能打开。修改自动写回并生成 `.bak`；原子批处理失败时文件不变。
- 新建视口、打印、读写系统变量需要真正的文档，自动改用“文档模式”（accoreconsole `/i` 打开图纸，插件在该文档上执行）。
- 每次调用启动一次 accoreconsole（约 2–5 秒），大量操作请合并成一次 `batch`。

## 安全

- **只读模式**：AutoCAD 里执行 `ACADCLR_READONLY` 切换。开启后拒绝一切修改图形的请求（`read_only`），查询、测量、校验、截图、打印照常；
  开启后远程无法再关闭它。MCP 另有 `--read-only`，两者独立。
- **LISP 开关**：`ACADCLR_LISP` 切换是否接受 `lisp` / `script`（`lisp_disabled`）；命令式编辑不受影响。MCP 默认不开放，`--allow-lisp` 开放。
- 开关保存在 `%LOCALAPPDATA%\AutoCADCLR\settings.json`，重启后仍有效，`acadclr status` 显示状态。它们防的是误操作，不是恶意绕过。
- **写前备份**：每张图在本次 AutoCAD 会话里第一次被修改前，插件把磁盘上最后保存的版本复制到 `%LOCALAPPDATA%\AutoCADCLR\backups`（结果的 `backup` 字段给出路径）。
- **操作日志**：命令行、MCP、离线的每次调用记一行到 `%LOCALAPPDATA%\AutoCADCLR\logs\acadclr-日期.log`（来源、目标、命令、结果、耗时、参数摘要、错误、备份）；`acadclr log 50` 查看。
- 命名管道只允许当前用户连接；MCP HTTP 只监听 127.0.0.1，校验 Host / Origin，可选 token。

插件命令：`ACADCLR_START` / `ACADCLR_STOP` / `ACADCLR_STATUS` / `ACADCLR_READONLY` / `ACADCLR_LISP` / `ACADCLR_MCP`（拉起 / 停止 `acadclr mcp --http`）。

## 架构

```
MCP 客户端 ──stdio / Streamable HTTP──▶ acadclr mcp ─┐
acadclr <命令> …（命令行）──────────────────────────┼─▶ Dispatcher ─┬─ 命名管道 ──▶ AcadClr.Plugin（AutoCAD 里）
                                                     │               └─ accoreconsole ──▶ 同一个插件（离线，后台数据库或文档模式）
AcadClr.Core：命令表、类型与属性（Schema）、路径 / 选择器、协议 —— 命令行、MCP、插件共用
```

| 目录 | 内容 |
|---|---|
| `src/AcadClr.Core` | `Commands.cs` 命令与参数（命令行解析、help、MCP 工具目录的唯一来源），`Schema.cs` 类型、属性与动作，`PathSyntax.cs` 路径与选择器，`Protocol.cs` 请求 / 响应，`CommandEdits.cs` 命令式编辑的 LISP 生成 |
| `src/AcadClr.Plugin` | `Engine/` 实体增删改查、测量校验、图块线型、系统变量；`Host/` 管道服务、主线程调度、文档、视图、打印、离线入口；`Safety/` 只读、LISP 开关、写前备份、标记回滚 |
| `src/AcadClr.Cli` | `acadclr.exe`：`Program.cs` 命令行 → JSON 参数，`Dispatcher.cs` 参数 → 请求并选择传输，`Transports.cs` 管道 / accoreconsole，`OpLog.cs` 操作日志，`Output.cs` 输出，`Mcp/` MCP server |

## 测试

| 测试 | 需要 | 内容 |
|---|---|---|
| `dotnet test tests/AcadClr.Tests` | 无 | 命令表、命令行解析、Dispatcher、写操作判定与策略、MCP 协议与 HTTP 传输、迁移文档示例（约 300 个） |
| `tests/smoke.ps1 [-Acad 2020]` | AutoCAD（离线） | 离线冒烟：批处理、编辑、测量校验、图块、系统变量、布局打印、外部参照 |
| `tests/live.ps1` | 已加载插件的 AutoCAD | 实时：视图、文档、撤销、回滚补偿、安全层 |
| `tests/mcp.ps1 [-Acad 2020]` | AutoCAD（离线）；实时部分可选 | MCP：stdio / HTTP × 旧协议 / 新协议 |
| `tests/mcp-scenarios.ps1` | 已加载插件的 AutoCAD | MCP 实时场景（移植自 AutoCadMCP 的 test-mcp.ps1），每步断言，并检测 AutoCAD 错误窗口 |

实时测试都在 AutoCAD 里新建临时图纸，结束时丢弃并关闭，不改动其他已打开的图纸。

## 路线图

- `dump`：把现有图纸导出成可重放的 batch JSON
- AutoCAD 2025+（.NET 8）：插件多目标编译 `net472;net8.0-windows`，`acadclr.exe` 不受影响
- MCP：长耗时操作的 SSE 进度推送

合并过程见 [合并计划](docs/PLAN-merge-mcp.md)。
