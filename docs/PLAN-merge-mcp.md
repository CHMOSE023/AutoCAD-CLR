# 合并 AutoCADMCP 计划

把 [AutoCad_MCP](https://github.com/CHMOSE023/AutoCad_MCP)（`D:\AutoCADMCP`）的能力并入本仓库：
**一个插件 DLL + 一个 `acadclr.exe`，CLI 与 MCP 共用同一套命令、同一套参数、同一套传输、同一个 Engine**。

约定：
- 不建分支，在 `master` 上按步骤开发、按步骤提交。
- `D:\AutoCADMCP` 只作为**能力清单与参考源码**，逐个功能移植进来，不导入其 git 历史；全部完成后归档。
- **MCP server 放在 `acadclr.exe` 里（`acadclr mcp`）**，经命名管道转发给插件；插件只保留管道一个入口，不内置 HTTP。
- MCP 支持 stdio 与 Streamable HTTP 两种传输，按 **2026-07-28 无状态规范**实现，**同时兼容 2025-11-25 及更早的有握手协议**。
- **不保留原 85 个 MCP 工具名与参数**。MCP 工具与 CLI 命令一一对应，名字相同、参数相同，都是 `Request` / `BatchItem` 的另一种写法。
- MCP 与 CLI 能力一致：实时模式（命名管道）与离线模式（`dwg` 参数，走 accoreconsole）都可用。

## 为什么不保留 85 个工具

| | 保留 85 个工具 | 与 CLI 统一 |
|---|---|---|
| 对外接口 | 两套：CLI 动词 + 85 个工具，各自的参数名、默认值、错误格式 | 一套：`get / query / add / set / remove / edit / batch …` |
| 工具目录 | 85 个 `inputSchema` 常驻上下文，占大量 token | 十几个工具，类型与属性靠 `help` 按需查询 |
| 维护 | 每个工具一段“参数 → Request”映射代码 + 一致性测试；Engine 加能力要同时改映射 | 工具参数就是 `BatchItem` 字段，零映射；Engine 加类型 / 属性，CLI 与 MCP 同时可用 |
| 原子批处理、`$N` 引用、选择器 | 85 个工具逐个调用，拿不到 | `batch` 工具直接可用 |
| 提示词与 SKILL | 两份 | 一份：`SKILL.md` 的例子换成 JSON 就是 MCP 调用 |

代价：依赖旧工具名的提示词需要迁移。用一张“旧工具 → 新调用”对照表（步骤 5）解决，旧插件在迁移期内继续可用。

## 为什么 MCP server 放在 acadclr.exe 而不是插件里

| | 插件内 | `acadclr mcp`（采用） |
|---|---|---|
| 插件入口 | 管道 + HTTP 两个，HTTP 还要单独做 ACL / Origin / token | 只有管道，插件保持精简 |
| 离线 DWG | MCP 用不了 | 与 CLI 相同，走 accoreconsole |
| 多实例 | 每个 AutoCAD 一个端口，客户端配多个 server | 一个 server，按 `pid` 选实例，复用 `instances` 的发现逻辑 |
| 升级 | DLL 被 AutoCAD 锁定，改协议层要重启 AutoCAD | 替换 exe 即可 |
| 稳定性 | HTTP 解析、SSE 长连接跑在 AutoCAD 进程里 | 协议层出错不影响 AutoCAD |
| 统一调用 | 插件里多一层“工具 → Request” | `tools/call` → `Request` → 与 CLI 同一个 `LiveTransport` / `OfflineTransport` |
| 额外进程 | 无 | 有：stdio 由客户端自动拉起；HTTP 需常驻（可由插件命令 `ACADCLR_MCP` 代为拉起） |

2026-07-28 无状态规范每个请求自带版本与能力、服务端不保存会话，正适合这种“收一个转发一个”的代理结构。

## 最终架构

```
MCP 客户端 ──stdio ──────────────┐
MCP 客户端 ──Streamable HTTP ────┼─▶ acadclr mcp ─┐
acadclr <verb> …（命令行）───────────────────────┼─▶ Request ─┬─ LiveTransport ──命名管道──▶ 插件 Host/PipeServer ─┐
                                                 │            └─ OfflineTransport ──accoreconsole──▶ 插件 离线入口 ──┼─▶ Safety ─▶ Engine ─▶ AutoCAD API
                                                 │                                                                   │
AcadClr.Core     协议、路径 / 选择器、Schema、Commands（命令表）、help —— CLI、MCP、插件共用
AcadClr.Cli      acadclr.exe
  Program        命令行参数 → Request（按命令表解析）
  Mcp/           JSON-RPC、新旧协议适配、stdio / HTTP 传输、工具目录（由 Commands 生成，不手写）
  Transports     LiveTransport（管道）/ OfflineTransport（accoreconsole）—— CLI 与 MCP 共用
  Output         文本 / JSON 输出（仅 CLI）
AcadClr.Plugin
  Host/          PipeServer、MainThread（唯一一份）、离线入口、发现文件
  Safety/        只读模式、自动备份、mark / rollback、操作日志 —— 管道入口统一把关
  Engine/        实体增删改查 + 动作动词（唯一实现，含原先在 CLI 端组装的 trim / fillet / plot）
```

原则：
- **一个命令表**：`Core/Commands.cs` 定义每个命令的名字、说明、参数（名字、类型、是否必填、对应 `BatchItem` / `Request` 字段）、是否写操作。
  CLI 的参数解析与 `help`、MCP 的 `tools/list` 都从它生成；不存在只有 CLI 有或只有 MCP 有的业务命令。
- **参数同名同义**：CLI 的 `--prop k=v` ↔ MCP 的 `props: {k: v}`；位置参数 ↔ `path` / `parent` / `selector`；
  `--best-effort` ↔ `bestEffort`；`--dwg` ↔ `dwg`；`--pid` ↔ `pid`。MCP `batch` 工具的 `items` 与 `acadclr batch --input` 的文件内容完全相同。
- **一套传输**：MCP 与 CLI 调用同一个 `LiveTransport` / `OfflineTransport`，实例选择、超时、离线格式检查与另存行为完全一致。
- **Engine 只认 `Request → Response`**，不关心来源。`Program.cs` 只做命令行解析和输出格式化；
  需要在插件外完成的组装（trim / fillet 拼 LISP、离线打印的脚本）放在 `Dispatcher`，CLI 与 MCP 共用。
- **一套结果与错误**：MCP 返回的 `structuredContent` 就是 `Response` JSON；错误码、`suggestion` 与 CLI `--json` 完全一致。
- `Schema.cs` 仍是类型、属性、动作的唯一来源；`props` 在工具 schema 中是开放对象，校验在 Engine 里做，出错带 `suggestion`。
- **安全分两层**：插件 Safety 管“能不能写”（只读、备份、回滚），所有入口一视同仁；`acadclr mcp` 的 HTTP 层管“谁能连”（Origin、token）。
- 实体用 `add / set / get / query / remove`；trim、fillet、measure、check、view 这类“动作”用动词，不硬塞进路径模型。
- 需要跨调用的状态用句柄作为普通参数传递（如 `doc`、`markId`），服务端不保存会话。

## 命令与 MCP 工具对照

| 命令（= MCP 工具名） | 参数（CLI 写法 → MCP `arguments`） | 可进 batch |
|---|---|---|
| `status` | — | |
| `help` | `[type\|命令]` → `topic` | |
| `instances` | — | |
| `get` | `<path> --depth --limit` → `path, depth, limit` | ✓ |
| `query` | `<selector> --limit` → `selector, limit` | ✓ |
| `add` | `<parent> --type --from --prop` → `parent, type, from, props` | ✓ |
| `set` | `<path\|selector> --prop --force` → `path \| selector, props, force` | ✓ |
| `remove` | `<path\|selector> --force` → `path \| selector, force` | ✓ |
| `edit` | `<action> <target> --prop` → `action, path \| selector, props` | ✓（命令类动作除外） |
| `batch` | `--input f.json --best-effort --stop-on-error` → `items, bestEffort, stopOnError` | — |
| `stats` | — | ✓ |
| `plot` | `<layout> --prop` → `layout, props` | |
| `save` | `--as` → `saveAs` | |
| `create` | `<file.dwg>` → `dwg` | |
| `lisp` | `"<code>" --file --cmd --save` → `code, file, commandQueue, save` | |
| `script` | `<file> --text --dwg… --save` → `file, code, dwg（数组）, save` | |
| 新增：`measure` `check` `view` `mark` `rollback` `undo` `log` | 见步骤 2 | 按动词定 |

公共参数（所有工具可选）：`dwg`（离线操作该文件）、`pid`（实时模式选实例，不填连最近启动的）、`doc`（实例内的文档名，不填用当前文档）、`timeout`（秒）。
只属于命令行的：`config`、`mcp`、`--json`、`--acad`（MCP 用 `acadclr mcp --acad` 启动参数或 `ACADCLR_ACCORE` 环境变量）。

## 步骤 1：整理宿主层

- [x] 处理未跟踪的 `src/AcadClr.Cli/Properties/`（已不存在，无需处理）
- [x] `Host/MainThread`：吸收 MCP 版在 Idle 中补取主窗口句柄的逻辑（主线程用 `Application.MainWindow.Handle`，管道线程只在未取到时用进程 API 临时顶上）
- [x] `PipeServer`：ACL 只允许当前用户；请求支持超时，客户端断开时插件能感知
- [x] 协议补充：图片结果（`view capture`）、长耗时操作的超时参数
- [x] 对比 MCP 的 `Lisp.cs`（命令队列桥：`SendStringToExecute` + 结果文件轮询）与 `Host/LispRunner.cs`，合成一份（`eval_lisp` 开关与日志归步骤 3）
- [x] 删除 `InstanceInfo.HttpPort`：插件不再提供 HTTP，实例发现只需管道名

验收：`tests/smoke.ps1` 全部通过。（2026-09-25 用 `-Acad 2020` 通过；2014 的受信任位置未包含本仓库 `bin\Release`，离线插件无法加载）

## 步骤 2：统一命令层，补齐 Engine 能力

### 2.1 命令表与统一入口

- [x] 新增 `Core/Commands.cs`：命令名、说明、参数定义、是否写操作；`Program.cs` 的 `switch` 改为按命令表解析，`help` 的命令列表由它生成；
      新增 `acadclr help <命令>`，列出命令行写法与 JSON / MCP 参数名的对照
- [x] 抽出 `Cli/Dispatcher`：`(命令名, JObject 参数) → Response`，内部决定走 `LiveTransport` 还是 `OfflineTransport`；
      CLI 把命令行参数转成 JObject 后调用它，MCP 直接把 `arguments` 交给它；`Prepare` 只构造请求、不连接，供测试比对
- [x] `edit trim / extend / fillet / chamfer`：从 `Program.cs` 移到 `Dispatcher`，**不进插件**。
      原因：MCP server 已定在 `acadclr.exe`，`Dispatcher` 就是 CLI 与 MCP 共用的一层；而离线执行要靠 accoreconsole 脚本上下文
      （插件命令里不能再跑 `(command ...)`），放进插件反而要拆成实时 / 离线两处
- [x] `plot`：布局名解析、属性校验改用 `Schema.PlotLayoutName` / `Schema.CheckPlotProps`，CLI 与插件共用一份（删掉 CLI 端的正则解析）
- [ ] ~~`lisp` / `script` / `save` / `status` 统一成 `BatchItem.Command`~~：暂不做。统一的对外接口是 `Dispatcher` 的 (命令, 参数)，
      `Request.Kind` 只是插件的线协议；这几个命令都不能进 batch，改线协议只有风险没有收益。等 mark / rollback 等新动词需要时再议
- [x] 测试：新增 `tests/AcadClr.Tests`（xunit，90 个用例）：命令表一致性、命令行 → JSON 参数、两种写法构造出**相同的** `Request`、
      参数错误的提示；`tests/smoke.ps1 -Acad 2020` 输出与重构前逐行一致

### 2.2 补齐能力

以 `D:\AutoCADMCP` 的 85 个工具作为**能力清单**（不是接口），逐组确认 Engine 已覆盖；
对照 `D:\AutoCADMCP\src\AcadMcp.Plugin\Acad\*.cs` 移植缺的部分，每组完成后更新 `Schema.cs`、help 和测试，然后提交。

| 分组 | 原 MCP 工具（能力来源） | CLR 现状 | 在统一命令中的形式 |
|---|---|---|---|
| 绘图 | draw_line / circle / arc / polyline / text / mtext / point / ellipse / spline / xline / ray | 已有实体类型 | `add --type …`；对比默认值，补齐缺的属性 |
| 标注填充 | dim_linear / aligned / angular / radius / diameter、leader、hatch | 已有 | 同上 |
| 查询 | query_entities、get_entity、select | 已有 `get` / `query` | `select` 的过滤条件并入选择器语法 |
| 修改 | move / rotate / scale / offset / mirror / explode / array_rect / array_polar / break_entity / join / erase_entity / set_entity_layer | 已有 | `set --prop move=…`、`edit …`、`remove`；吸收 MCP 版的修复 |
| 修改（缺） | copy、trim、extend、fillet、chamfer | 已有（trim 等在 `Dispatcher`） | `add --from`（copy）、`edit` |
| 图层线型块 | list_layers / create_layer / set_current_layer、list_linetypes、list_blocks / define_block / insert_block | 有 `layer`、`insert` | 新增 `/linetypes`、`/blocks` 节点与 `block` 类型；当前图层用 `set / --prop currentLayer=` |
| 布局打印外参 | list / create / delete / set_layout、list / add / set_viewport、list_plot_devices、set_page_setup、plot_pdf、list / attach / manage / bind_xref | 已有 | `get/add/set/remove /layouts`、`plot`；对比 MCP 的 `Plot.cs`（620 行），合成一份 |
| 测量校验 | measure_distance / measure_area、check_overlap / check_inside / check_adjacency | 无 | 新增 `measure`、`check` 动词 |
| 系统 | get_sysvars / set_sysvar、get_units / set_units、convert_length | 部分（`/` 的 units） | 新增 `/sysvars` 节点（`get` / `set`）；convert_length 放进 `measure` |
| 视图 | capture_view、zoom_extents | 无 | 新增 `view` 动词，`view capture` 返回图片（仅实时模式） |
| 文档 | list / activate / open / new / close_document、save、save_as | 部分（create / save） | 新增 `/documents` 节点：`get` 列表、`add` 新建 / 打开、`set current=true` 激活、`remove` 关闭 |
| 撤销与日志 | mark / rollback、undo、get_log、get_status | batch 原子回滚、status | 新增 `mark` / `rollback` / `undo` / `log` 动词 |
| 逃生口 | run_command、eval_lisp | 已有 `script` / `lisp` | 保持现状 |

建议顺序：命令表与统一入口 → 修改（缺）→ 测量校验 → 视图 → 块与线型 → 系统 → 文档 → 撤销与日志 → 已有能力的差异对比。

进度：
- [x] 修改（缺）：copy = `add --from … --prop move=`（多份用 `edit array`）；trim / extend / fillet / chamfer 已有，无需移植
- [x] 测量校验：`measure distance|area|length|convert`、`check overlap|inside|adjacent`（Engine/Inspect.cs，只读、可进 batch）；
      `measure area|length` 对多个实体给出合计，是 MCP 版没有的；check 判定逻辑与 MCP 版相同（包围盒、容差 1）
- [ ] 视图
- [ ] 块与线型
- [ ] 系统
- [ ] 文档
- [ ] 撤销与日志
- [ ] 已有能力的差异对比

验收：85 个工具的每项能力都能用统一命令完成，CLI 可以调用，`help` 能查到，`tests/` 下有对应用例；
对照表（步骤 5）每一行都有可运行的等价调用。

## 步骤 3：安全层

插件侧（`Safety/`，所有入口共用）：
- [ ] 只读模式（拒绝所有写操作）、写操作前自动备份
- [ ] 写操作判定来自命令表（每个命令 / 动作标注是否写），不按工具逐个维护
- [ ] mark / rollback 与操作日志
- [ ] 插件命令统一为 `ACADCLR_*`：`START` / `STOP` / `STATUS` / `READONLY` / `LISP`（开关 lisp / script）/ `MCP`（拉起或停止 `acadclr mcp --http` 子进程）

`acadclr mcp` 侧：
- [ ] `lisp` / `script` 默认不出现在 `tools/list`，启动参数 `--allow-lisp` 才开放
- [ ] `--read-only` 启动参数：写类工具不出现在 `tools/list`，调用也拒绝（插件只读模式之外的第二道防线）
- [ ] HTTP 可选 token 鉴权（`Authorization: Bearer`，`--token` 或环境变量 `ACADCLR_MCP_TOKEN`）；stdio 不需要

## 步骤 4：`acadclr mcp`

### 4.1 启动与传输

```
acadclr mcp                                  # stdio（客户端自动拉起）
acadclr mcp --http [--port 7140]             # Streamable HTTP，常驻
            [--token T] [--read-only] [--allow-lisp] [--acad 2020]
```

- [ ] stdio：一行一个 JSON-RPC 消息；stdout 只写协议消息，日志一律写 stderr
- [ ] HTTP：只监听 `127.0.0.1`，端口默认 7140（旧插件占用 7130），被占用时报错并提示 `--port`（不自动换端口，避免客户端配置失效）
- [ ] 避开 urlacl：用 `TcpListener` 自行处理 HTTP，或验证 `HttpListener` 的 `localhost` 前缀在非管理员下可用（二选一，先实测）
- [ ] 校验 `Origin` 头，防 DNS rebinding
- [ ] `POST /mcp`：一个请求一个响应，默认返回 `application/json`；长耗时操作（打印、离线批处理）可用 SSE 响应流推送 `notifications/progress`
- [ ] `GET /mcp` 返回 405（旧规范允许；新规范已用 `subscriptions/listen` 取代）
- [ ] 并发：同一实例 / 同一 DWG 的请求串行（插件主线程本来就串行，离线同一文件不能并发打开）；不同实例、不同文件可并行
- [ ] 客户端断开（HTTP 连接关闭、stdio 取消通知）时取消对应的管道请求

### 4.2 2026-07-28 无状态规范

- [ ] 不需要握手：每个请求从 `_meta` 读取 `io.modelcontextprotocol/protocolVersion`、`clientCapabilities`、`clientInfo`
- [ ] 实现 `server/discover`：返回支持的协议版本、能力、服务端信息
- [ ] 每个结果带 `resultType: "complete"`，`_meta` 带 `io.modelcontextprotocol/serverInfo`
- [ ] `tools/list`：顺序固定，带 `ttlMs` 与 `cacheScope: "private"`
- [ ] HTTP 校验 `Mcp-Method` / `Mcp-Name` 请求头，不一致返回 `HeaderMismatch`（-32020）
- [ ] 不支持的版本返回 `UnsupportedProtocolVersion`（-32022），附带支持的版本列表
- [ ] 日志级别按请求 `_meta` 的 `io.modelcontextprotocol/logLevel`，未指定则不发 `notifications/message`
- [ ] `subscriptions/listen`：暂不支持，`server/discover` 中不声明（工具列表是固定的）

### 4.3 兼容旧协议（2025-11-25 / 2025-06-18 / 2025-03-26）

- [ ] 判定方式：请求 `_meta` 中有协议版本按新规范处理，否则按旧规范处理；逐请求判定，不保存状态（stdio 与 HTTP 相同）
- [ ] 响应 `initialize`：协商版本（返回客户端请求的版本，不支持时返回最新的旧版本），不下发 `Mcp-Session-Id`
- [ ] 接受并忽略 `notifications/initialized`、`Mcp-Session-Id` 头
- [ ] 保留 `ping`
- [ ] 旧请求缺少 `Mcp-Method` / `Mcp-Name` 头时不报错
- [ ] 新增的结果字段（`resultType`、`ttlMs` 等）对旧客户端也照常返回，属于兼容的附加字段
- [ ] 错误码：旧客户端沿用原错误码

### 4.4 工具目录由命令表生成

- [ ] `Mcp/Tools.cs`：遍历 `Commands`（跳过 `config`、`mcp`）生成 `tools/list`；`inputSchema` 由参数定义生成，`props` / `items` 为开放对象，附带公共参数 `dwg` / `pid` / `doc` / `timeout`
- [ ] 工具说明保持简短，统一提示“类型与属性用 `help` 工具查询”，对应 SKILL.md 的“先查 help，不要猜属性名”
- [ ] `tools/call`：`arguments` 原样交给 2.1 的 `Dispatcher`；**Mcp 目录下没有按工具分支的代码**
- [ ] 结果：`structuredContent` = `Response` JSON，`content` 为同一 JSON 的文本（兼容不读 structuredContent 的客户端）；
      `view capture` 的图片放 `image` 内容块；`Response.ok=false` 时 `isError: true`
- [ ] 连接失败（没有运行中的 AutoCAD）返回 `isError: true` + `not_connected`，与 CLI 相同的 `suggestion`（NETLOAD 插件或改用 `dwg` 参数）
- [ ] 工具注解由命令表生成：只读命令 `readOnlyHint`，`remove` / `rollback` 等 `destructiveHint`
- [ ] 测试：`tools/list` 与 `acadclr help --json` 的命令、参数一致；同一操作经 CLI 与 MCP 执行，`Response` 相同

验收：
- Claude Code 分别以 stdio（`command: acadclr, args: [mcp]`）和 `type: http` 连接，全部工具可用；用 `batch` 工具一次完成 SKILL.md 中的示例
- 用旧协议客户端（带 `initialize`）和新协议客户端（带 `_meta`）各跑一遍工具测试，都通过
- 同时开两个 AutoCAD，一个 `acadclr mcp` 通过 `pid` 分别操作
- 不开 AutoCAD，通过 `dwg` 参数离线读写一张图

## 步骤 5：测试、迁移与收尾

- [ ] `D:\AutoCADMCP\scripts\test-mcp.ps1` 移到 `tests/`，改为调用统一工具，拆成旧协议与新协议两套、stdio 与 HTTP 两种传输（脚本保持 ASCII）
- [ ] `test/AcadMcp.ProtocolTest` 移到 `tests/`，补充 `server/discover`、版本协商、请求头校验、Origin 校验、token 用例
- [ ] 新增 `docs/migrate-from-autocad-mcp.md`：85 个旧工具逐个给出等价调用（MCP JSON 与 CLI 各一行），例如
      `draw_circle{center,radius}` → `add{parent:"/model",type:"circle",props:{center,radius}}` / `acadclr add /model --type circle --prop …`
- [ ] SKILL.md 改为 CLI 与 MCP 共用：说明“参数同名”规则后只写一种例子
- [ ] README 重写：统一命令、CLI、MCP（stdio 与 HTTP 两种 `.mcp.json` 示例）、离线模式
- [ ] 版本号升到 0.2.0
- [ ] AutoCad_MCP 仓库 README 改为指向本仓库与迁移文档，然后在 GitHub 上归档
- [ ] （以后）AutoCAD 2025+ .NET 8：插件多目标编译 `net472;net8.0-windows`；`acadclr.exe` 不受影响

## 风险

| 风险 | 应对 |
|---|---|
| 旧工具名失效，依赖它的提示词与用户配置不能直接用 | 迁移对照表；迁移期旧插件继续可用；新工具说明里给出最常用的对应写法 |
| 工具少、`props` 是开放对象，模型可能猜错属性名 | 工具说明引导先调用 `help`；Engine 校验并在错误里给出 `suggestion`（与 CLI 相同） |
| 多一个进程：HTTP 模式需要常驻 `acadclr mcp` | 推荐 stdio（客户端自动拉起）；HTTP 模式可由 `ACADCLR_MCP` 从 AutoCAD 里拉起，随 AutoCAD 退出 |
| MCP 走离线模式时每次调用启动 accoreconsole（3–5 秒） | 工具说明提示合并成一次 `batch`；进度用 `notifications/progress` 推送 |
| 多个 MCP 客户端 / CLI 同时操作同一实例 | 插件主线程串行执行；Safety 的操作日志记录来源（cli / mcp-stdio / mcp-http） |
| 命令行改为按命令表解析后行为变化 | 已用单元测试固定命令行 → 参数的映射；冒烟测试输出与重构前逐行比对（2.1 已通过） |
| 两份 Layouts / Plot 实现行为不同，移植时丢失 MCP 版的修复 | 逐文件对比；MCP 实测通过的场景都写成测试用例 |
| 长耗时操作（打印、命令队列桥）超时 | 请求带超时；HTTP 用 SSE 推进度；客户端断开时取消管道请求 |
| 新规范刚发布，客户端实现不一 | 新旧协议逐请求判定，两套测试都跑 |
| 移植期间 MCP 能力不完整 | 完成前继续用 `D:\AutoCADMCP` 的旧插件；新旧插件不要同时加载（插件命令冲突），`acadclr mcp` 默认端口 7140，避开旧插件的 7130 |
| DLL 被 AutoCAD 锁定，无法覆盖 | 构建前关闭 AutoCAD；`acadclr.exe` 更新不受影响（HTTP 模式需先停掉常驻进程） |

## 参考

- [MCP 2026-07-28 变更说明](https://modelcontextprotocol.io/specification/2026-07-28/changelog)
