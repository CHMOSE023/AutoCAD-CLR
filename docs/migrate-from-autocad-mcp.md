# 从 AutoCadMCP 迁移

[AutoCad_MCP](https://github.com/CHMOSE023/AutoCad_MCP) 的 85 个工具已经并入 AutoCADCLR，但**不保留原工具名**：
MCP 工具与 `acadclr` 命令一一对应，名字相同、参数相同（见 [合并计划](PLAN-merge-mcp.md)）。本文逐个给出旧工具的等价写法。

## 读法

表里写的是命令行形式。换成 MCP 调用时，工具名就是命令名，参数同名：

| 命令行 | MCP `tools/call` |
|---|---|
| `acadclr add /model --type circle --prop center=0,0 --prop radius=500` | `add {"parent":"/model","type":"circle","props":{"center":"0,0","radius":500}}` |
| `acadclr set "line[layer=WALL]" --prop color=1` | `set {"selector":"line[layer=WALL]","props":{"color":1}}` |
| `acadclr edit offset 8A --prop distance=240` | `edit {"action":"offset","path":"8A","props":{"distance":240}}` |
| `acadclr remove "/entity[@handle=8A]" --force` | `remove {"path":"/entity[@handle=8A]","force":true}` |

规则：位置参数写成 `path` / `parent` / `selector` / `action`（`acadclr help <命令>` 列出对照），`--prop k=v` 写进 `props`，
`--xxx` 选项去掉前缀（`--best-effort` → `bestEffort`）。

## 与旧插件的主要区别

- **实体落在哪个空间由父路径决定**：旧插件画在“当前空间”（随 CTAB 变化，切到布局后就画进了图纸空间）；
  现在 `add /model …` 一定在模型空间，`add "/layout[@name=A3]" …` 一定在该布局，切换当前布局不影响去向。
- **目标用路径、句柄或选择器**：`/entity[@handle=8A]`、裸句柄 `8A`、`8A;8B;8C`、`line[layer=WALL]`。
  旧插件的 `handle` / `handles` / `useSelection` 三种写法都由它覆盖；`select` 不再需要，条件直接写进选择器。
- **多条操作用 `batch`**：一次往返、默认原子执行（任一失败整批回滚），`"$N"` 引用第 N 条的结果路径。
  旧插件只能逐个工具调用，失败了留下半成品。
- **结果是结构化的节点**：`path (type) key=value …`；MCP 的 `structuredContent` 就是完整的 JSON 结果。
- **同一套命令也能离线用**：加 `dwg` 参数直接读写 DWG 文件，不需要打开 AutoCAD（视图、文档、撤销除外）。
- **类型与属性按需查询**：`acadclr help` / `help <类型>` / `help <命令>`，不必把 85 个工具说明常驻在上下文里。

## 对照表

### 状态、图层、线型

| 旧工具 | 等价写法 |
|---|---|
| `get_status` | `status` |
| `list_layers` | `get /layers` |
| `create_layer` | `add /layers --type layer --prop name=WALL --prop color=1 --prop linetype=CENTER --prop lineWeight=0.5`（已存在则用 `set "/layer[@name=WALL]" …`） |
| `set_current_layer` | `set / --prop currentLayer=WALL` |
| `list_linetypes` | `get /linetypes` |

### 绘图（`add /model --type …`，画到布局用 `add "/layout[@name=A3]" …`）

| 旧工具 | 等价写法 |
|---|---|
| `draw_line` | `add /model --type line --prop start=0,0 --prop end=5000,0 --prop layer=WALL` |
| `draw_circle` | `add /model --type circle --prop center=0,0 --prop radius=500` |
| `draw_arc` | `add /model --type arc --prop center=0,0 --prop radius=500 --prop startAngle=0 --prop endAngle=90` |
| `draw_polyline` | `add /model --type polyline --prop points="0,0;6000,0;6000,4000;0,4000" --prop closed=true` |
| `draw_ellipse` | `add /model --type ellipse --prop center=0,0 --prop majorAxis=3000,0 --prop ratio=0.5` |
| `draw_spline` | `add /model --type spline --prop points="0,0;1000,800;2000,0" --prop method=fit`（`closed`、`degree`、`startTangent`、`endTangent` 同名） |
| `draw_point` | `add /model --type point --prop position=0,0` |
| `draw_text` | `add /model --type text --prop text=客厅 --prop position=3000,2000 --prop height=350` |
| `draw_mtext` | `add /model --type mtext --prop text="第一行\P第二行" --prop position=0,0 --prop height=250 --prop width=4000` |
| `draw_xline` | `add /model --type xline --prop position=0,0 --prop direction=1,0` |
| `draw_ray` | `add /model --type ray --prop position=0,0 --prop direction=0,1` |

### 标注、引线、填充

| 旧工具 | 等价写法 |
|---|---|
| `dim_linear` | `add /model --type dimension --prop kind=linear --prop p1=0,0 --prop p2=6000,0 --prop dimLine=3000,-800 --prop scale=100`（`rotation=90` 为竖向） |
| `dim_aligned` | 同上，`kind=aligned` |
| `dim_angular` | `add /model --type dimension --prop kind=angular --prop vertex=0,0 --prop p1=1000,0 --prop p2=0,1000 --prop dimLine=700,700` |
| `dim_radius` / `dim_diameter` | `add /model --type dimension --prop kind=radius --prop target=8E --prop dimLine=3500,2500`（`kind=diameter`） |
| `leader` | `add /model --type leader --prop points="0,0;500,500;1200,500" --prop text=说明 --prop height=250` |
| `hatch` | `add /model --type hatch --prop boundary="8A;8B" --prop pattern=ANSI31 --prop scale=50 --prop angle=0` |

### 查询

| 旧工具 | 等价写法 |
|---|---|
| `query_entities` | `query "line[layer=WALL]" --limit 500`（类型写 `entity` 表示任意类型；只查某布局：`query "entity[space=A3]"`） |
| `get_entity` | `get "/entity[@handle=8A]"` |
| `select` | 不需要：把条件写进选择器，直接给 set / remove / edit 当目标。按窗口选：`entity[inside=0,0;6000,4000]`（完全在内）、`entity[crossing=…]`（相交） |

### 修改（旧的 `handle` / `handles` / `useSelection` 都换成目标：路径、句柄、`a;b;c` 或选择器）

| 旧工具 | 等价写法 |
|---|---|
| `move` | `set "line[layer=A]" --prop move=1000,0` |
| `rotate` | `set 8A --prop rotate=90 --prop base=0,0` |
| `scale` | `set 8A --prop scale=2 --prop base=0,0` |
| `set_entity_layer` | `set "8A;8B" --prop layer=WALL --prop color=bylayer --prop lineWeight=0.35` |
| `erase_entity` | `remove "8A;8B"`（选择器命中超过 30 个要 `--force`）；也可 `remove "line[layer=TMP]"` |
| `copy` | 一份：`add /model --from 8A --prop move=1000,0`；多份等距：`edit array 8A --prop cols=5 --prop colSpacing=1000` |
| `array_rect` | `edit array 8A --prop rows=3 --prop cols=4 --prop rowSpacing=8400 --prop colSpacing=8400 --prop angle=0` |
| `array_polar` | `edit array 8A --prop center=0,0 --prop count=8 --prop fillAngle=360 --prop rotateItems=true` |
| `offset` | `edit offset 8A --prop distance=240 --prop side=100,100` |
| `mirror` | `edit mirror 8A --prop axis="0,0;0,1000" --prop keep=true` |
| `explode` | `edit explode 8A` |
| `break_entity` | `edit break 8D --prop at="1000,2000;3000,2000"` |
| `join` | `edit join "8A;8B;8C"` |
| `trim` | `edit trim "8D@13500,2000" --prop edges=8B`（拾取点落在要剪掉的那段上） |
| `extend` | `edit extend "95@0,900" --prop boundary=8A` |
| `fillet` | `edit fillet 8D --prop with=95 --prop radius=300` |
| `chamfer` | `edit chamfer 8D --prop with=95 --prop d1=200 --prop d2=200` |

### 图块

| 旧工具 | 等价写法 |
|---|---|
| `list_blocks` | `get /blocks`（`bboxFromBase` 是相对基点的范围） |
| `define_block` | `add /blocks --type block --prop name=TREE --prop entities="8A;8B" --prop base=0,0`（`keepSource=false` → `replace=true`） |
| `insert_block` | `add /model --type insert --prop name=TREE --prop position=5000,0 --prop scale=1 --prop rotation=0 --prop layer=TREE`（带属性的块加 `attributes=NO=A-01`） |

### 测量与校验

| 旧工具 | 等价写法 |
|---|---|
| `measure_distance` | `measure distance --prop from=0,0 --prop to=3000,4000` |
| `measure_area` | `measure area 8A`（选择器命中多个时给出合计） |
| `convert_length` | `measure convert --prop value=3.6 --prop from=m --prop to=mm`（from 缺省为图形单位） |
| `check_overlap` | `check overlap "polyline[layer=ROOM]" --prop minArea=0` |
| `check_inside` | `check inside "polyline[layer=ROOM]" --prop boundary=8A` |
| `check_adjacency` | `check adjacent 8A --prop with=8B --prop gap=240` |

### 系统变量与单位

| 旧工具 | 等价写法 |
|---|---|
| `get_sysvars` | `get /sysvars`（常用清单）；指定变量：`get "/sysvar[@name=DIMTXT]"` |
| `set_sysvar` | `set "/sysvar[@name=PDMODE]" --prop value=3` |
| `get_units` | `get /`（`units` `measurement` `lunits` `luprec` `aunits` `auprec` `ltscale` `dimscale`） |
| `set_units` | `set / --prop units=mm --prop luprec=0` |

### 布局、视口、打印

| 旧工具 | 等价写法 |
|---|---|
| `list_layouts` | `get /layouts` |
| `create_layout` | `add /layouts --type layout --prop name=A3 --prop device="DWG To PDF.pc3" --prop paper=A3 --prop landscape=true`（`setCurrent` → `current=true`） |
| `set_layout` | `set "/layout[@name=A3]" --prop current=true`（只切换显示；实体画到哪里由父路径决定） |
| `delete_layout` | `remove "/layout[@name=A3]"` |
| `list_viewports` | `get "/layout[@name=A3]"`（子元素含视口）或 `query "viewport[space=A3]"` |
| `add_viewport` | `add "/layout[@name=A3]" --type viewport --prop center=210,148.5 --prop width=380 --prop height=260 --prop viewCenter=15000,10000 --prop scale=100 --prop locked=true` |
| `set_viewport` | `set "/entity[@handle=2B1]" --prop scale=50 --prop viewCenter=… --prop on=true --prop locked=false` |
| `list_plot_devices` | `get /devices`；纸张：`get "/device[@name=DWG To PDF.pc3]"` |
| `set_page_setup` | `set "/layout[@name=A3]" --prop device="DWG To PDF.pc3" --prop paper=A3 --prop landscape=true` |
| `plot_pdf` | `plot "/layout[@name=A3]" --prop output=D:\out\A3.pdf --prop paper=A3 --prop landscape=true --prop area=layout --prop scale=fit --prop mono=true` |

### 外部参照

| 旧工具 | 等价写法 |
|---|---|
| `list_xrefs` | `get /xrefs` |
| `attach_xref` | `add /xrefs --type xref --prop path=D:\work\base.dwg --prop position=0,0 --prop scale=1 --prop overlay=false --prop layer=XREF` |
| `manage_xrefs` | 重载 `set "/xref[@name=base]" --prop reload=true`；卸载 `--prop loaded=false`；拆离 `remove "/xref[@name=base]"`；全部：选择器 `xref` 加 `--force` |
| `bind_xref` | `set "/xref[@name=base]" --prop bind=insert`（`insertBind=false` → `bind=bind`） |

### 视图、文档、撤销（实时模式）

| 旧工具 | 等价写法 |
|---|---|
| `zoom_extents` | `view zoom` |
| `capture_view` | `view capture --prop zoom=extents --prop maxWidth=1200 --prop region=window`（缺省 `region=drawing`，只截绘图区） |
| `list_documents` | `get /documents` |
| `activate_document` | `set "/document[@name=plan.dwg]" --prop current=true`；或不切换，在其他命令上加 `--doc plan.dwg` |
| `open_document` | `add /documents --type document --prop path=D:\work\plan.dwg --prop readOnly=false` |
| `new_document` | `add /documents --type document --prop template=acadiso.dwt` |
| `close_document` | `remove "/document[@name=plan.dwg]"`（有未保存修改时先 `save --doc plan.dwg`，或加 `--force` 丢弃） |
| `save` | `save` |
| `save_as` | `save --as D:\work\plan.dwg` |
| `mark` | `mark 改前` |
| `rollback` | `rollback` |
| `undo` | `undo 3` |

### 逃生口与日志

| 旧工具 | 等价写法 |
|---|---|
| `eval_lisp` | `lisp "(getvar \"LTSCALE\")"`；含 `(command …)` 时加 `--cmd` |
| `run_command` | `script --text "_.REGEN "` |
| `get_log` | 尚未提供：操作日志在合并计划步骤 3（安全层）实现 |
