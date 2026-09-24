# 合并 AutoCADMCP 计划

把 [AutoCad_MCP](https://github.com/CHMOSE023/AutoCad_MCP)（`D:\AutoCADMCP`）的能力并入本仓库：
**一个插件 DLL + 一个 `acadclr.exe`，CLI 与 MCP 共用同一套 Engine**。

约定：
- 不建分支，在 `master` 上按步骤开发、按步骤提交。
- `D:\AutoCADMCP` 只作为参考源码，逐个功能移植进来，不导入其 git 历史；全部完成后归档。
- 插件内置 MCP Streamable HTTP，按 **2026-07-28 无状态规范**实现，**同时兼容 2025-11-25 及更早的有握手协议**。
- **保留原 85 个 MCP 工具名与参数**，作为稳定的对外接口；实现全部改为调用 Engine，不再各自操作 AutoCAD API。
- MCP 只支持实时模式（连接正在运行的 AutoCAD）；离线读写 DWG 仍由 CLI（accoreconsole）提供。

## 最终架构

```
MCP 客户端 ──Streamable HTTP（无状态）──▶ Mcp/ ── 85 个工具：参数 → Request ─┐
acadclr.exe ──命名管道────────────────▶ Host/PipeServer ─────────────────────┼─▶ Engine ─▶ AutoCAD API
acadclr.exe --dwg ──accoreconsole────▶ Host/离线入口（仅 CLI）──────────────┘

AcadClr.Core     协议、路径 / 选择器、Schema、help（CLI 与插件共用）
AcadClr.Plugin
  Host/          PipeServer、MainThread（唯一一份）、离线入口、发现文件
  Mcp/           HTTP 服务、JSON-RPC、协议版本适配、工具目录（85 个工具 → Request）
  Engine/        实体增删改查 + 动作动词（唯一实现）
  Safety/        只读模式、自动备份、mark / rollback、操作日志、token
AcadClr.Cli      acadclr.exe：参数解析、管道 / accoreconsole 传输、输出
```

原则：
- Engine 只认 `Request → Response`，不关心来源是 CLI 还是 MCP。
- 所有写操作都经过 Safety 入口，HTTP 与管道一视同仁。
- 实体用 `add / set / get / query / remove`；trim、fillet、measure、check、capture 这类“动作”用动词，不硬塞进路径模型。
- Schema（`Schema.cs`）是 CLI help 与校验的唯一来源；MCP 工具的 `inputSchema` 保持原样作为对外契约，由测试保证与 Schema 一致。
- 需要跨调用的状态用句柄作为普通参数传递（如 `doc`、`markId`），服务端不保存会话。

## 步骤 1：整理宿主层

- [ ] 处理未跟踪的 `src/AcadClr.Cli/Properties/`（提交或加入 `.gitignore`）
- [ ] `Host/MainThread`：吸收 MCP 版在 Idle 中补取主窗口句柄的逻辑
- [ ] `PipeServer`：ACL 只允许当前用户；请求支持超时，客户端断开时插件能感知
- [ ] 协议补充：图片结果（`capture_view`）、长耗时操作的超时参数
- [ ] 对比 MCP 的 `Lisp.cs`（命令队列桥：`SendStringToExecute` + 结果文件轮询）与 `Host/LispRunner.cs`，合成一份
- [ ] 发现文件增加 `httpPort` 字段，供多实例使用

验收：`tests/smoke.ps1` 全部通过。

## 步骤 2：补齐 Engine 能力

对照 `D:\AutoCADMCP\src\AcadMcp.Plugin\Acad\*.cs` 逐组移植，每组完成后更新 `Schema.cs`、help 和测试，然后提交。

| 分组 | 对应 MCP 工具 | CLR 现状 | 做法 |
|---|---|---|---|
| 绘图 | draw_line / circle / arc / polyline / text / mtext / point / ellipse / spline / xline / ray | 已有实体类型 | 对比参数与默认值，补齐缺的属性 |
| 标注填充 | dim_linear / aligned / angular / radius / diameter、leader、hatch | 已有 `dimension` `leader` `hatch` | 同上 |
| 查询 | query_entities、get_entity、select | 已有 `get` / `query` | `select` 的过滤条件并入选择器语法 |
| 修改 | move / rotate / scale / offset / mirror / explode / array_rect / array_polar / break_entity / join / erase_entity / set_entity_layer | 已有 | 对比行为差异，吸收 MCP 版的修复 |
| 修改（缺） | copy、trim、extend、fillet、chamfer | 无 | 新增 `edit` 动作 |
| 图层线型块 | list_layers / create_layer / set_current_layer、list_linetypes、list_blocks / define_block / insert_block | 有 `layer`、`insert` | 新增 `/linetypes`、`/blocks` 节点与 `block` 类型 |
| 布局打印外参 | list / create / delete / set_layout、list / add / set_viewport、list_plot_devices、set_page_setup、plot_pdf、list / attach / manage / bind_xref | 已有 | 对比两边实现，合成一份（MCP 的 `Plot.cs` 620 行，重点对比） |
| 测量校验 | measure_distance / measure_area、check_overlap / check_inside / check_adjacency | 无 | 新增 `measure`、`check` 动词 |
| 系统 | get_sysvars / set_sysvar、get_units / set_units、convert_length | 无 | 新增 `/sysvars`、`/units` 节点 |
| 视图 | capture_view、zoom_extents | 无 | 新增 `view` 动词，`view capture` 返回图片 |
| 文档 | list / activate / open / new / close_document、save、save_as | 部分（create / save） | 新增 `/documents` 节点 |
| 撤销与日志 | mark / rollback、undo、get_log、get_status | batch 原子回滚、status | 新增 `mark` / `rollback` / `undo` / `log` 动词 |
| 逃生口 | run_command、eval_lisp | 已有 `script` / `lisp` | 保持现状 |

建议顺序：修改（缺）→ 测量校验 → 视图 → 块与线型 → 系统 → 文档 → 撤销与日志 → 已有能力的差异对比。

验收：85 个工具的每项能力在 Engine 中都有实现，CLI 可以调用，`help` 能查到，`tests/` 下有对应用例。

## 步骤 3：安全层

- [ ] `Safety/`：只读模式（拒绝所有写操作）、写操作前自动备份、`eval_lisp` / `run_command` 默认关闭
- [ ] HTTP 可选 token 鉴权（`Authorization: Bearer`），管道靠 ACL
- [ ] mark / rollback 与操作日志移入 Safety，HTTP 与管道共用
- [ ] 插件命令统一为 `ACADCLR_*`：`START` / `STOP` / `STATUS` / `READONLY` / `LISP` / `TOKEN`

## 步骤 4：MCP HTTP 服务（插件内）

### 4.1 传输

- [ ] 只监听 `127.0.0.1`，端口从 7130 起自动找空闲端口，写入发现文件与命令行提示
- [ ] 避开 urlacl：改用 `TcpListener` 自行处理 HTTP，或验证 `localhost` 前缀在非管理员下可用（二选一，先实测）
- [ ] 校验 `Origin` 头，防 DNS rebinding
- [ ] `POST /mcp`：一个请求一个响应，默认返回 `application/json`；长耗时操作可用 SSE 响应流推送 `notifications/progress`
- [ ] `GET /mcp` 返回 405（旧规范允许；新规范已用 `subscriptions/listen` 取代）

### 4.2 2026-07-28 无状态规范

- [ ] 不需要握手：每个请求从 `_meta` 读取 `io.modelcontextprotocol/protocolVersion`、`clientCapabilities`、`clientInfo`
- [ ] 实现 `server/discover`：返回支持的协议版本、能力、服务端信息
- [ ] 每个结果带 `resultType: "complete"`，`_meta` 带 `io.modelcontextprotocol/serverInfo`
- [ ] `tools/list`：顺序固定，带 `ttlMs` 与 `cacheScope: "private"`
- [ ] 校验 `Mcp-Method` / `Mcp-Name` 请求头，不一致返回 `HeaderMismatch`（-32020）
- [ ] 不支持的版本返回 `UnsupportedProtocolVersion`（-32022），附带支持的版本列表
- [ ] 日志级别按请求 `_meta` 的 `io.modelcontextprotocol/logLevel`，未指定则不发 `notifications/message`
- [ ] `subscriptions/listen`：暂不支持，`server/discover` 中不声明（工具列表是固定的）

### 4.3 兼容旧协议（2025-11-25 / 2025-06-18 / 2025-03-26）

- [ ] 判定方式：请求 `_meta` 中有协议版本按新规范处理，否则按旧规范处理；逐请求判定，不保存状态
- [ ] 响应 `initialize`：协商版本（返回客户端请求的版本，不支持时返回最新的旧版本），不下发 `Mcp-Session-Id`
- [ ] 接受并忽略 `notifications/initialized`、`Mcp-Session-Id` 头
- [ ] 保留 `ping`
- [ ] 旧请求缺少 `Mcp-Method` / `Mcp-Name` 头时不报错
- [ ] 新增的结果字段（`resultType`、`ttlMs` 等）对旧客户端也照常返回，属于兼容的附加字段
- [ ] 错误码：旧客户端沿用原错误码

### 4.4 85 个工具改为调用 Engine

- [ ] `Mcp/ToolCatalog`：保留全部工具名、描述、`inputSchema`；每个工具的处理函数只做“参数 → `Request` → Engine → 结果格式化”
- [ ] 删除原 `Acad/*.cs` 中直接调用 AutoCAD API 的实现，不在 Mcp 目录下留任何绘图逻辑
- [ ] 工具结果格式保持与原插件一致（文本、JSON、图片），避免已有提示词失效
- [ ] 多文档：工具可选参数 `doc`（文档名或句柄），不传时用当前文档
- [ ] 测试：遍历 85 个工具，检查每个参数都能映射到 Engine（`Schema.cs` 中的属性或动词参数）

验收：
- Claude Code 以 `type: http` 连接，85 个工具全部可用
- 用旧协议客户端（带 `initialize`）和新协议客户端（带 `_meta`）各跑一遍工具测试，都通过
- 同时开两个 AutoCAD，分别在不同端口提供服务

## 步骤 5：测试与收尾

- [ ] `D:\AutoCADMCP\scripts\test-mcp.ps1` 移到 `tests/`，拆成旧协议与新协议两套调用（脚本保持 ASCII）
- [ ] `test/AcadMcp.ProtocolTest` 移到 `tests/`，补充 `server/discover`、版本协商、请求头校验、Origin 校验用例
- [ ] README 重写：CLI、MCP、离线模式三部分；`.mcp.json` 示例
- [ ] 版本号升到 0.2.0
- [ ] AutoCad_MCP 仓库 README 改为指向本仓库，然后在 GitHub 上归档
- [ ] （以后）AutoCAD 2025+ .NET 8：插件多目标编译 `net472;net8.0-windows`

## 风险

| 风险 | 应对 |
|---|---|
| 两份 Layouts / Plot 实现行为不同，移植时丢失 MCP 版的修复 | 逐文件对比；MCP 实测通过的场景都写成测试用例 |
| 85 个工具改走 Engine 后结果格式变化，影响已有提示词 | 以原插件输出为基准做对比测试 |
| 长耗时操作（打印、命令队列桥）超时 | 请求带超时；HTTP 用 SSE 推进度；客户端断开时插件能感知 |
| 新规范刚发布，客户端实现不一 | 新旧协议逐请求判定，两套测试都跑 |
| 移植期间 MCP 能力不完整 | 完成前继续用 `D:\AutoCADMCP` 的旧插件，两个插件不要同时加载（端口与命令会冲突） |
| DLL 被 AutoCAD 锁定，无法覆盖 | 构建前关闭 AutoCAD |

## 参考

- [MCP 2026-07-28 变更说明](https://modelcontextprotocol.io/specification/2026-07-28/changelog)
