---
name: acadclr
description: 用 acadclr 命令行读写 AutoCAD 图纸（DWG）：画线、圆、多段线、文字，管理图层，查询与批量修改实体。用户提到 AutoCAD、DWG、CAD 绘图时使用。
---

# acadclr

用来操作 AutoCAD 的命令行工具，有两种模式：

- **实时模式**：操作用户正在打开的 AutoCAD。
- **离线模式**：加 `--dwg <file>`，直接读写 DWG 文件，不需要打开 AutoCAD 界面。

## 先查 help，不要猜属性名

```bash
acadclr help              # 命令、路径语法、类型列表
acadclr help polyline     # 某类型的全部属性、可用操作、示例
```

## 工作流程

1. **确认环境**：先运行 `acadclr status`。如果返回 `not_connected`，提示用户在 AutoCAD 里 NETLOAD 插件，或者改用 `--dwg` 离线模式。
2. **看全貌**：运行 `acadclr stats`，了解实体数量、按类型和图层的分布、图形范围。
3. **定位**：用 `query` 找目标，例如 `acadclr query "polyline[layer=WALL][closed=true]"`。
   结果里的路径都带句柄（`/model/polyline[@handle=2A3]`），后续操作都用这个路径，删除或新增实体后它也不会漂移。
4. **修改**：一两处修改用 `add` / `set` / `remove`；**三条以上用 `batch`**：只有一次往返，而且原子执行。
5. **核对**：用 `get` 或 `query` 读回结果，重点检查 `bbox`、`length`、`area`，确认几何上是对的。

## 要点

- **Windows PowerShell 5.1 会吞掉传给外部程序的参数里的双引号**：`--commands '[{"op":...}]'` 和 `lisp '(getvar "X")'` 都会被破坏。
  在 5.1 中请把 JSON 写进文件用 `--input`，把 LISP 写进文件用 `--file`；PowerShell 7、cmd、bash 没有这个问题。

- 单位跟随图纸，看 `get /` 的 `units`，新建的图默认是 mm。角度一律用度，逆时针为正。
- 坐标写成 `x,y`；多段线顶点写成 `x,y;x,y;…`。
- 实体指定的图层不存在时会自动创建；要控制颜色和线宽，就先 `add /layers --type layer`，再画图。
- 大多数属性保持 bylayer，只在图层上设颜色、线型、线宽。这才是规范的 CAD 做法。
- `set` 或 `remove` 用选择器时必须带条件；`remove` 一次超过 30 个元素需要加 `--force`。先 `query` 看一眼匹配数量再动手。
- batch 里用 `"$N"` 引用第 N 条操作（从 0 开始）生成的路径：

```json
[
  {"command":"add","parent":"/layers","type":"layer","props":{"name":"WALL","color":1,"lineWeight":0.5}},
  {"command":"add","parent":"/model","type":"polyline","props":{"points":"0,0;6000,0;6000,4000;0,4000","closed":true,"layer":"WALL"}},
  {"command":"add","parent":"/model","from":"$1","props":{"move":"7000,0"}},
  {"command":"add","parent":"/model","type":"text","props":{"text":"客厅","position":"3000,2000","height":350,"justify":"mc","layer":"TEXT"}}
]
```

```bash
acadclr batch --input plan.json            # 实时模式
acadclr batch --dwg plan.dwg --input plan.json   # 离线模式，自动写回文件
```

- 离线模式每次调用都要启动一次 accoreconsole（约 3–5 秒），所以要尽量合并成一次 batch。

## 标注、填充、引线与编辑

- 类型：`ellipse` `spline` `xline` `ray` `hatch` `dimension`（kind=linear|aligned|angular|radius|diameter）`leader`。毫米单位的图纸给标注和引线加 `scale=100`。
- `acadclr help edit` 查看编辑动作。`offset` `mirror` `explode` `break` `join` `array` 可以放进 batch：
  `{"command":"edit","action":"array","path":"$0","props":{"rows":3,"cols":4,"rowSpacing":8400,"colSpacing":8400}}`
- `trim` `extend` `fillet` `chamfer` 是 AutoCAD 命令，只能单独调用。目标写成 `句柄@x,y`，拾取点落在要剪掉或要延伸的那一段上：
  `acadclr edit trim "8D@13500,2000" --prop edges=8B`

## 图块与线型

- `get /blocks` 列出块定义。**插入前先看 `bboxFromBase`**：它是相对基点的范围，插入点 + 该范围 = 实际占位（基点在底边的车位块很容易插反）。
- 定义块：`add /blocks --type block --prop name=车位 --prop entities="$0;$1" --prop base=1250,0`；加 `replace=true` 把源实体原地换成块参照。
- 插入：`add /model --type insert --prop name=车位 --prop position=10000,0 --prop layer=PARK`（记得指定图层）。
  带属性的块用 `attributes=NO=A-101;AREA=36.5` 赋值，没给的取默认值；`set` 同一属性可修改。
- 线型：`get /linetypes`；图层 / 实体用到的线型会自动加载，也可 `add /linetypes --type linetype --prop name=DASHED`。
  点划线在大比例图上看不出间隔时，调 LTSCALE。
- 被参照的块、被使用的线型不能 `remove`，错误信息会告诉你怎么找到使用者。

## 单位与系统变量

- `get /` 看 `units`（INSUNITS）、`ltscale`、`dimscale`、`lunits` / `luprec` 等；用 `set / --prop ltscale=100` 修改。
  改 units 不会缩放已有几何，只影响插入图块 / 外部参照的缩放。
- 其他系统变量：`get "/sysvar[@name=PDMODE]"`、`set "/sysvar[@name=PDMODE]" --prop value=3`；`get /sysvars` 列出常用变量。
  离线模式读写系统变量要用 accoreconsole 打开图纸，比普通离线操作慢一些。

## 测量与校验（只读，可放进 batch）

- `measure area|length <目标>`：面积 / 周长、长度，选择器命中多个时给出合计。`measure distance --prop from=x,y --prop to=x,y`；
  `measure convert --prop value=3.6 --prop from=m --prop to=mm`（from 缺省为图形单位）。
- **面积对不代表位置对**。画完房间、车位这类布局后用 `check` 核对，结果看 `result=PASS/FAIL`：
  - `check overlap "polyline[layer=ROOM]"`：两两重叠（共边不算）
  - `check inside "polyline[layer=ROOM]" --prop boundary=<外轮廓句柄>`：是否越界，`outBy` 给出各方向超出的距离
  - `check adjacent <A> --prop with=<B> --prop gap=240`：是否相邻（gap 为允许的墙厚 / 走廊宽度）
- 判定按包围盒，矩形房间很准；斜放或异形实体可能误报，FAIL 时 `get` 实体复核。

## 布局、视口与打印

- 实体画在哪个空间，由父路径决定：`/model` 或 `/layout[@name=A3]`。不存在"当前空间"这个概念，切换当前布局也不影响 add 的去向。
- 出图的典型流程：
  1. `add /layouts --type layout --prop name=A3 --prop paper=full_bleed_A3 --prop landscape=true`
  2. 往 `/layout[@name=A3]` 里画图框和标题栏（单位是图纸毫米）
  3. 加视口：`--type viewport --prop center=… width=… height=… viewCenter=<模型坐标> scale=100 locked=true`
  4. `acadclr plot "/layout[@name=A3]" --prop output=D:/out/A3.pdf`
  5. 打开 PDF 核对结果
- 打印范围只有 layout（默认）和 extents。只出局部时，用视口框定。
- 查看纸张名：`get "/device[@name=DWG To PDF.pc3]"`；查询图纸空间：`query "entity[space=A3]"`。

## 外部参照

`get /xrefs` 列出外部参照；`query "xref[status=filenotfound]"` 找出丢失的参照。
修改：`set "/xref[@name=X]"`，可用属性有 `path=`（改路径并重载）、`loaded=false|true`、`reload=true`、`bind=insert|bind`。
`remove` 为拆离。这些操作不能回滚，执行前先 `get` 确认目标。

## LISP 与脚本（逃生舱）

结构化命令覆盖不到的功能（标注、填充、布局出图、系统变量……），用 LISP：

```bash
acadclr lisp "(getvar \"LTSCALE\")"                            # 实时，同步返回值
acadclr lisp --cmd "(command \"_.HATCH\" ...)"                  # 实时，含 (command) 时加 --cmd
acadclr lisp plan.dwg --save "(command \"_.DIMLINEAR\" ...)"    # 离线，修改后加 --save 保存
acadclr script fix.scr "D:/drawings/*.dwg" --save              # 离线，批量处理多张图
```

- 优先用结构化命令（add/set/query），因为它们可校验、可回滚，返回值也有结构。只有做不到的事才用 LISP。
- 多个表达式会按顺序执行，返回最后一个的值。出错时返回 `lisp_error`，附带 AutoCAD 的错误信息。
- 离线 `lisp` 默认不保存；确认结果正确后，再带 `--save` 执行真正要改的操作。
- 写 `.scr` 脚本时注意：空行等于回车，会重复上一条命令。
- 实时模式的修改不会自动保存，完成后运行 `acadclr save`；新图用 `acadclr save --as <path>`。
- 加 `--json` 可以得到结构化输出。出错时读 `error.code` 和 `error.suggestion` 自行修正。
- 常见错误码：`not_found`、`invalid_value`、`unsupported_property`、`missing_property`、`locked_layer`、`unscoped_selector`、`too_many`。
