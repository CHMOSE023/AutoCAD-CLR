using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AcadClr.Core
{
    [Flags]
    public enum Verbs
    {
        None = 0,
        Add = 1,
        Set = 2,
        Get = 4,
        AddSet = Add | Set,
        All = Add | Set | Get,
    }

    public sealed class PropDef
    {
        public string Name { get; }
        public string Kind { get; }
        public Verbs Verbs { get; }
        public bool Required { get; }
        public string Description { get; }
        public string? Example { get; }

        public PropDef(string name, string kind, Verbs verbs, string description, string? example = null, bool required = false)
        {
            Name = name; Kind = kind; Verbs = verbs; Description = description; Example = example; Required = required;
        }
    }

    public sealed class ActionDef
    {
        /// <summary>所属动词：edit、measure、check。</summary>
        public string Verb { get; set; } = "edit";
        public string Name { get; }
        public string Description { get; }
        public string Target { get; }

        /// <summary>false：不需要目标实体（measure distance / convert）。</summary>
        public bool NeedsTarget { get; set; } = true;

        /// <summary>true：通过 AutoCAD 交互命令实现，不能在事务 / batch 中执行。</summary>
        public bool UsesCommand { get; }

        public List<PropDef> Props { get; }
        public string[] Examples { get; }

        public ActionDef(string name, string description, string target, bool usesCommand, IEnumerable<PropDef> props, params string[] examples)
        {
            Name = name; Description = description; Target = target; UsesCommand = usesCommand; Props = props.ToList(); Examples = examples;
        }

        public PropDef CheckProp(string prop)
        {
            var def = Props.FirstOrDefault(p => string.Equals(p.Name, prop, StringComparison.OrdinalIgnoreCase));
            if (def != null) return def;
            var near = Schema.Suggest(prop, Props.Select(p => p.Name));
            throw new CliError("unsupported_property", $"{Verb} {Name} 没有属性 “{prop}”。",
                (near != null ? $"是否想用 {near}？" : "") + $"运行 acadclr help {Verb} {Name} 查看。");
        }

        /// <summary>校验属性名并检查必填项，返回规范名 → 值。</summary>
        public Dictionary<string, string> CheckProps(IEnumerable<KeyValuePair<string, string>> props)
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in props) map[CheckProp(kv.Key).Name] = kv.Value;
            var missing = Props.Where(p => p.Required && !map.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            if (missing.Count > 0)
                throw new CliError("missing_property", $"{Verb} {Name} 缺少属性：{string.Join("、", missing)}。", $"运行 acadclr help {Verb} {Name}");
            return map;
        }
    }

    public sealed class TypeDef
    {
        public string Name { get; }
        public string Parent { get; }
        public string Description { get; }
        public bool IsEntity { get; }
        public List<PropDef> Props { get; }
        public string[] Examples { get; }

        public TypeDef(string name, string parent, string description, bool isEntity, IEnumerable<PropDef> props, params string[] examples)
        {
            Name = name; Parent = parent; Description = description; IsEntity = isEntity; Props = props.ToList(); Examples = examples;
        }

        public PropDef? Find(string prop) =>
            Props.FirstOrDefault(p => string.Equals(p.Name, prop, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 元素类型与属性的唯一定义处。CLI 的 help 与插件的属性校验都读这里，保证两边一致。
    /// </summary>
    public static class Schema
    {
        private static PropDef P(string n, string k, Verbs v, string d, string? ex = null, bool req = false) => new PropDef(n, k, v, d, ex, req);

        /// <summary>所有实体共有的属性。</summary>
        public static readonly PropDef[] CommonEntityProps =
        {
            P("handle", "string", Verbs.Get, "实体句柄（十六进制），跨增删保持稳定"),
            P("layer", "string", Verbs.All, "所在图层；图层不存在时自动创建", "layer=WALL"),
            P("color", "color", Verbs.All, "颜色：bylayer / byblock / 1-255 / red…white / #RRGGBB / r,g,b", "color=1"),
            P("linetype", "string", Verbs.All, "线型名；未加载时自动从 acadiso.lin 加载", "linetype=CENTER"),
            P("lineWeight", "lineweight", Verbs.All, "线宽（毫米，就近取标准档）或 bylayer / byblock / default", "lineWeight=0.35"),
            P("bbox", "points", Verbs.Get, "包围盒 minX,minY;maxX,maxY"),
            P("move", "vector", Verbs.AddSet, "平移 dx,dy[,dz]（在其他属性之后执行）", "move=1000,0"),
            P("rotate", "angle", Verbs.AddSet, "绕 base 旋转（度，逆时针为正）", "rotate=90"),
            P("scale", "number", Verbs.AddSet, "绕 base 等比缩放", "scale=2"),
            P("base", "point", Verbs.AddSet, "rotate / scale 的基点；缺省为实体包围盒中心", "base=0,0"),
        };

        private static TypeDef Entity(string name, string desc, PropDef[] own, params string[] examples) =>
            new TypeDef(name, "/model", desc, true, own.Concat(CommonEntityProps), examples);

        public static readonly List<TypeDef> Types = new List<TypeDef>
        {
            Entity("line", "直线", new[]
            {
                P("start", "point", Verbs.All, "起点", "start=0,0", true),
                P("end", "point", Verbs.All, "终点", "end=5000,0", true),
                P("length", "number", Verbs.Get, "长度"),
                P("angle", "angle", Verbs.Get, "方向角（度）"),
            }, "acadclr add /model --type line --prop start=0,0 --prop end=5000,0 --prop layer=WALL"),

            Entity("circle", "圆", new[]
            {
                P("center", "point", Verbs.All, "圆心", "center=0,0", true),
                P("radius", "number", Verbs.All, "半径", "radius=500", true),
                P("diameter", "number", Verbs.Get, "直径"),
                P("area", "number", Verbs.Get, "面积"),
            }, "acadclr add /model --type circle --prop center=0,0 --prop radius=500"),

            Entity("arc", "圆弧（从 startAngle 逆时针到 endAngle）", new[]
            {
                P("center", "point", Verbs.All, "圆心", "center=0,0", true),
                P("radius", "number", Verbs.All, "半径", "radius=500", true),
                P("startAngle", "angle", Verbs.All, "起始角（度）", "startAngle=0", true),
                P("endAngle", "angle", Verbs.All, "终止角（度）", "endAngle=90", true),
                P("length", "number", Verbs.Get, "弧长"),
            }, "acadclr add /model --type arc --prop center=0,0 --prop radius=500 --prop startAngle=0 --prop endAngle=90"),

            Entity("polyline", "轻量多段线（LWPOLYLINE）", new[]
            {
                P("points", "points", Verbs.All, "顶点列表 x,y;x,y;…", "points=0,0;5000,0;5000,3000;0,3000", true),
                P("closed", "bool", Verbs.All, "是否闭合", "closed=true"),
                P("width", "number", Verbs.All, "全局宽度", "width=0"),
                P("count", "number", Verbs.Get, "顶点数"),
                P("length", "number", Verbs.Get, "总长"),
                P("area", "number", Verbs.Get, "面积（闭合时）"),
            }, "acadclr add /model --type polyline --prop points=\"0,0;5000,0;5000,3000;0,3000\" --prop closed=true"),

            Entity("text", "单行文字（TEXT）", new[]
            {
                P("text", "string", Verbs.All, "文字内容", "text=客厅", true),
                P("position", "point", Verbs.All, "插入点（justify 非 left 时为对齐点）", "position=0,0", true),
                P("height", "number", Verbs.All, "字高；缺省取 TEXTSIZE", "height=350"),
                P("rotation", "angle", Verbs.All, "旋转角（度）", "rotation=0"),
                P("style", "string", Verbs.All, "文字样式名", "style=Standard"),
                P("justify", "justify", Verbs.All, "对齐：left center right ml mc mr tl tc tr bl bc br", "justify=mc"),
            }, "acadclr add /model --type text --prop text=客厅 --prop position=2500,1500 --prop height=350 --prop justify=mc"),

            Entity("mtext", "多行文字（MTEXT），\\P 换行", new[]
            {
                P("text", "string", Verbs.All, "内容（支持 MTEXT 格式码，\\P 换行）", "text=第一行\\P第二行", true),
                P("position", "point", Verbs.All, "插入点", "position=0,0", true),
                P("height", "number", Verbs.All, "字高；缺省取 TEXTSIZE", "height=350"),
                P("width", "number", Verbs.All, "文字框宽度，0 为不换行", "width=3000"),
                P("rotation", "angle", Verbs.All, "旋转角（度）"),
                P("style", "string", Verbs.All, "文字样式名"),
                P("justify", "justify", Verbs.All, "附着点：tl tc tr ml mc mr bl bc br", "justify=tl"),
            }, "acadclr add /model --type mtext --prop text=\"说明\\P第二行\" --prop position=0,0 --prop height=250 --prop width=4000"),

            Entity("point", "点", new[]
            {
                P("position", "point", Verbs.All, "位置", "position=0,0", true),
            }),

            Entity("insert", "块参照（插入已定义的图块）", new[]
            {
                P("name", "string", Verbs.All, "块名（必须已在图中定义）", "name=DOOR", true),
                P("position", "point", Verbs.All, "插入点", "position=0,0", true),
                P("scale", "number", Verbs.All, "统一比例（get 时非统一比例返回 x,y,z）", "scale=1"),
                P("rotation", "angle", Verbs.All, "旋转角（度）", "rotation=0"),
                P("attributes", "string", Verbs.All, "属性值 TAG=值;TAG=值；add 时按块定义补建属性，未给的取默认值", "attributes=NO=A-01;AREA=36"),
            }, "acadclr add /model --type insert --prop name=DOOR --prop position=1000,0 --prop layer=DOOR",
               "acadclr get /blocks                        # 先看块的 bboxFromBase：插入点 + 该范围 = 实际占位"),

            Entity("ellipse", "椭圆 / 椭圆弧", new[]
            {
                P("center", "point", Verbs.All, "中心", "center=0,0", true),
                P("majorAxis", "vector", Verbs.All, "长轴端点相对中心的向量", "majorAxis=3000,0", true),
                P("ratio", "number", Verbs.All, "短轴与长轴之比，(0,1]", "ratio=0.5", true),
                P("startAngle", "angle", Verbs.All, "起始参数角（度），椭圆弧用", "startAngle=0"),
                P("endAngle", "angle", Verbs.All, "终止参数角（度）", "endAngle=180"),
                P("length", "number", Verbs.Get, "周长 / 弧长"),
                P("area", "number", Verbs.Get, "面积（完整椭圆）"),
            }, "acadclr add /model --type ellipse --prop center=0,0 --prop majorAxis=3000,0 --prop ratio=0.5"),

            Entity("spline", "样条曲线（NURBS）", new[]
            {
                P("points", "points", Verbs.Add | Verbs.Get, "拟合点（method=fit，曲线穿过每个点）或控制点（method=cv）", "points=0,0;1000,800;2000,0;3000,600", true),
                P("method", "string", Verbs.Add | Verbs.Get, "fit（默认）或 cv", "method=fit"),
                P("closed", "bool", Verbs.Add | Verbs.Get, "是否闭合"),
                P("degree", "number", Verbs.Add | Verbs.Get, "阶次 1-11，默认 3"),
                P("fitTolerance", "number", Verbs.Add, "拟合公差，默认 0"),
                P("startTangent", "vector", Verbs.Add, "起点切向（仅 fit，须与 endTangent 成对）", "startTangent=1,0"),
                P("endTangent", "vector", Verbs.Add, "终点切向", "endTangent=1,0"),
                P("length", "number", Verbs.Get, "曲线长度"),
            }, "acadclr add /model --type spline --prop points=\"0,0;1000,800;2000,0;3000,600\""),

            Entity("xline", "构造线（两端无限）", new[]
            {
                P("position", "point", Verbs.All, "通过的点", "position=0,0", true),
                P("direction", "vector", Verbs.All, "方向", "direction=1,1", true),
            }),

            Entity("ray", "射线（一端无限）", new[]
            {
                P("position", "point", Verbs.All, "起点", "position=0,0", true),
                P("direction", "vector", Verbs.All, "方向", "direction=1,0", true),
            }),

            Entity("hatch", "图案填充", new[]
            {
                P("boundary", "paths", Verbs.Add, "边界实体（须闭合）：路径或句柄，多个用 ; 分隔", "boundary=8A;8B", true),
                P("pattern", "string", Verbs.All, "图案名，默认 ANSI31；SOLID 为实心填充", "pattern=AR-CONC"),
                P("scale", "number", Verbs.All, "图案比例，默认 1", "scale=50"),
                P("angle", "angle", Verbs.All, "图案角度（度）", "angle=45"),
                P("associative", "bool", Verbs.Add | Verbs.Get, "是否关联边界，默认 true"),
                P("area", "number", Verbs.Get, "填充面积"),
                P("loops", "number", Verbs.Get, "边界环数"),
            }, "acadclr add /model --type hatch --prop boundary=8A --prop pattern=ANSI31 --prop scale=50"),

            Entity("dimension", "标注。kind：linear 线性 / aligned 对齐 / angular 角度 / radius 半径 / diameter 直径", new[]
            {
                P("kind", "string", Verbs.Add | Verbs.Get, "linear / aligned / angular / radius / diameter", "kind=linear", true),
                P("p1", "point", Verbs.All, "第一点（线性、对齐：尺寸界线原点；角度：第一条边上的点）", "p1=0,0"),
                P("p2", "point", Verbs.All, "第二点", "p2=6000,0"),
                P("vertex", "point", Verbs.Add | Verbs.Get, "角度标注的顶点"),
                P("dimLine", "point", Verbs.All, "尺寸线（或弧、半径文字）经过的点", "dimLine=3000,-800", true),
                P("target", "path", Verbs.Add, "半径 / 直径标注的圆或圆弧：路径或句柄", "target=8E"),
                P("rotation", "angle", Verbs.Add | Verbs.Set | Verbs.Get, "线性标注的尺寸线角度，0 水平、90 竖直"),
                P("text", "string", Verbs.All, "文字替代；空为测量值，<> 代表测量值", "text=<>（墙厚）"),
                P("style", "string", Verbs.All, "标注样式名", "style=ISO-25"),
                P("scale", "number", Verbs.All, "全局比例（DIMSCALE 替代），毫米图纸 1:100 常用 100", "scale=100"),
                P("measurement", "number", Verbs.Get, "测量值（角度为度）"),
            }, "acadclr add /model --type dimension --prop kind=linear --prop p1=0,0 --prop p2=6000,0 --prop dimLine=3000,-800 --prop scale=100",
               "acadclr add /model --type dimension --prop kind=radius --prop target=8E --prop dimLine=3500,2500 --prop scale=100"),

            Entity("leader", "引线 + 文字注释", new[]
            {
                P("points", "points", Verbs.Add | Verbs.Get, "引线顶点，第一个为箭头端", "points=0,0;500,500;1200,500", true),
                P("text", "string", Verbs.Add, "注释文字（多行文字，\\P 换行）", "text=C20 混凝土", true),
                P("height", "number", Verbs.Add, "文字高度；缺省取 TEXTSIZE", "height=250"),
                P("scale", "number", Verbs.All, "箭头等的全局比例（DIMSCALE 替代），毫米图纸 1:100 常用 100", "scale=100"),
                P("annotation", "string", Verbs.Get, "注释多行文字的句柄"),
            }, "acadclr add /model --type leader --prop points=\"0,0;500,500;1200,500\" --prop text=说明 --prop height=250"),

            new TypeDef("viewport", "/layout[@name=...]", "浮动视口：开在布局图纸上的窗口，按比例显示模型空间的一块区域", true, new[]
            {
                P("center", "point", Verbs.All, "视口中心（图纸坐标，毫米）", "center=210,148.5", true),
                P("width", "number", Verbs.All, "宽（图纸毫米）", "width=380", true),
                P("height", "number", Verbs.All, "高（图纸毫米）", "height=260", true),
                P("viewCenter", "point", Verbs.All, "对准的模型空间点", "viewCenter=15000,10000"),
                P("scale", "number", Verbs.All, "比例 1:N 的 N（1 图纸毫米 = N 个图形单位）", "scale=100"),
                P("on", "bool", Verbs.All, "是否打开（显示内容）"),
                P("locked", "bool", Verbs.All, "是否锁定（防止误改比例与视图）", "locked=true"),
                P("viewHeight", "number", Verbs.Get, "视口内显示的模型高度"),
            }.Concat(CommonEntityProps.Where(p => p.Name != "rotate" && p.Name != "scale")),
               "acadclr add \"/layout[@name=A3]\" --type viewport --prop center=210,148.5 --prop width=380 --prop height=260 --prop viewCenter=15000,10000 --prop scale=100 --prop locked=true"),

            new TypeDef("layout", "/layouts", "布局（图纸空间）及其页面设置。新建、删除、重命名、切换是数据库级操作，不参与 batch 回滚", false, new[]
            {
                P("name", "string", Verbs.All, "布局名（set 即重命名）", "name=A3-平面", true),
                P("current", "bool", Verbs.All, "设为当前布局（只能设 true；Model 为模型空间）", "current=true"),
                P("device", "string", Verbs.All, "打印设备，默认 DWG To PDF.pc3", "device=DWG To PDF.pc3"),
                P("paper", "string", Verbs.All, "纸张：完整纸张名，或 A4 / A3 / A1 这类简称（模糊匹配）", "paper=A3"),
                P("landscape", "bool", Verbs.All, "横向", "landscape=true"),
                P("plotStyle", "string", Verbs.All, "打印样式表（ctb / stb）", "plotStyle=monochrome.ctb"),
                P("paperSize", "string", Verbs.Get, "纸张尺寸（毫米）"),
                P("tabOrder", "number", Verbs.Get, "标签顺序"),
                P("viewports", "number", Verbs.Get, "浮动视口数"),
                P("entities", "number", Verbs.Get, "图纸空间实体数（不含视口）"),
            }, "acadclr add /layouts --type layout --prop name=A3 --prop paper=A3 --prop landscape=true",
               "acadclr set \"/layout[@name=A3]\" --prop current=true",
               "acadclr add \"/layout[@name=A3]\" --type polyline --prop points=\"10,10;410,10;410,287;10,287\" --prop closed=true"),

            new TypeDef("device", "/devices", "打印设备（只读）：get /devices 列出设备，get \"/device[@name=...]\" 列出纸张与打印样式表", false, new[]
            {
                P("name", "string", Verbs.Get, "设备名"),
                P("media", "string", Verbs.Get, "该设备支持的纸张名（; 分隔）"),
                P("styleSheets", "string", Verbs.Get, "可用打印样式表（; 分隔）"),
            }, "acadclr get /devices", "acadclr get \"/device[@name=DWG To PDF.pc3]\""),

            new TypeDef("layer", "/layers", "图层", false, new[]
            {
                P("name", "string", Verbs.All, "图层名（set 即重命名）", "name=WALL", true),
                P("color", "color", Verbs.All, "颜色（ACI 或真彩色）", "color=1"),
                P("linetype", "string", Verbs.All, "线型；未加载时自动加载", "linetype=CENTER"),
                P("lineWeight", "lineweight", Verbs.All, "线宽（毫米）", "lineWeight=0.35"),
                P("on", "bool", Verbs.All, "是否打开", "on=true"),
                P("frozen", "bool", Verbs.All, "是否冻结", "frozen=false"),
                P("locked", "bool", Verbs.All, "是否锁定", "locked=false"),
                P("plot", "bool", Verbs.All, "是否打印", "plot=true"),
                P("current", "bool", Verbs.All, "设为当前图层（只能设 true）", "current=true"),
            }, "acadclr add /layers --type layer --prop name=WALL --prop color=1 --prop lineWeight=0.5",
               "acadclr set \"/layer[@name=WALL]\" --prop locked=true"),

            new TypeDef("block", "/blocks", "图块定义。add 用已有实体定义新块；remove 只能删除没有被参照的块", false, new[]
            {
                P("name", "string", Verbs.All, "块名（set 即重命名）", "name=TREE", true),
                P("entities", "paths", Verbs.Add, "组成块的实体：路径、句柄或 $N，多个用 ; 分隔（复制进块定义）", "entities=8A;8B", true),
                P("base", "point", Verbs.Add | Verbs.Get, "基点：插入时与插入点重合的位置，默认 0,0", "base=1000,0"),
                P("replace", "bool", Verbs.Add, "true：删除源实体，并在基点处插入该块（源实体被块替换）", "replace=true"),
                P("description", "string", Verbs.All, "说明"),
                P("count", "number", Verbs.Get, "块内实体数"),
                P("bboxFromBase", "points", Verbs.Get, "相对基点的范围 minX,minY;maxX,maxY：插入点 + 该范围 = 实际占位"),
                P("size", "string", Verbs.Get, "宽 x 高"),
                P("references", "number", Verbs.Get, "被插入的次数"),
                P("attributes", "string", Verbs.Get, "属性定义的标记，; 分隔"),
            }, "acadclr add /blocks --type block --prop name=TREE --prop entities=\"8A;8B\" --prop base=0,0",
               "acadclr add /model --type insert --prop name=TREE --prop position=5000,0"),

            new TypeDef("linetype", "/linetypes", "线型。add 从 acadiso.lin / acad.lin 加载；图层、实体用到时也会自动加载", false, new[]
            {
                P("name", "string", Verbs.Add | Verbs.Get, "线型名", "name=CENTER", true),
                P("description", "string", Verbs.Get, "说明"),
                P("patternLength", "number", Verbs.Get, "一个图案周期的长度（图形单位）；大比例图上看不出间隔时调 LTSCALE"),
                P("current", "bool", Verbs.Get, "是否为当前线型"),
            }, "acadclr add /linetypes --type linetype --prop name=DASHED", "acadclr query \"linetype[name~=CENTER]\""),

            new TypeDef("xref", "/xrefs", "外部参照（DWG）。附着 / 重载 / 卸载 / 绑定 / 拆离是数据库级操作，不参与 batch 回滚", false, new[]
            {
                P("path", "string", Verbs.All, "参照文件路径；相对路径按当前图形所在目录解析。set 即改路径并重载", "path=D:\\work\\base.dwg", true),
                P("name", "string", Verbs.All, "参照名（缺省取文件名）；set 即重命名", "name=BASE"),
                P("overlay", "bool", Verbs.Add | Verbs.Get, "true 为覆盖（overlay），false 为附着（attach）", "overlay=true"),
                P("position", "point", Verbs.Add, "插入点", "position=0,0"),
                P("scale", "number", Verbs.Add, "插入比例", "scale=1"),
                P("rotation", "angle", Verbs.Add, "插入旋转角（度）"),
                P("layer", "string", Verbs.Add, "插入到的图层", "layer=XREF"),
                P("loaded", "bool", Verbs.Set | Verbs.Get, "false 卸载，true 重新加载", "loaded=false"),
                P("reload", "bool", Verbs.Set, "true 立即重载", "reload=true"),
                P("bind", "string", Verbs.Set, "绑定为本地图块：bind（符号加 $0$ 前缀）或 insert（不加前缀）", "bind=insert"),
                P("status", "string", Verbs.Get, "resolved / unloaded / unreferenced / filenotfound / unresolved"),
                P("resolvedPath", "string", Verbs.Get, "实际找到的文件（找不到为空）"),
                P("inserts", "number", Verbs.Get, "插入次数"),
                P("insertHandles", "string", Verbs.Get, "各个块参照的句柄"),
            }, "acadclr add /xrefs --type xref --prop path=D:\\work\\base.dwg --prop layer=XREF",
               "acadclr get /xrefs",
               "acadclr set \"/xref[@name=base]\" --prop reload=true",
               "acadclr set \"xref[status=filenotfound]\" --prop path=D:\\new\\base.dwg",
               "acadclr remove \"/xref[@name=base]\"    # 拆离"),

            new TypeDef("document", "/documents", "文档。/ 是当前文档（或 --doc 指定的文档）；/documents 列出 AutoCAD 里打开的全部图形（仅实时模式）", false, new[]
            {
                P("file", "string", Verbs.Get, "文件路径（未保存过的新图为空）"),
                P("name", "string", Verbs.Get, "文件名：/document[@name=...] 与 --doc 用它定位"),
                P("active", "bool", Verbs.Get, "是否为当前文档"),
                P("readOnly", "bool", Verbs.Add | Verbs.Get, "只读打开"),
                P("modified", "bool", Verbs.Get, "是否有未保存的修改"),
                P("current", "bool", Verbs.Set, "set \"/document[@name=...]\" --prop current=true 切换当前文档"),
                P("path", "string", Verbs.Add, "add /documents 时：打开该 DWG（已打开则切换过去）", "path=D:\\work\\plan.dwg"),
                P("template", "string", Verbs.Add, "add /documents 时：用该样板新建，默认 acadiso.dwt", "template=acadiso.dwt"),
                P("version", "string", Verbs.Get, "DWG 版本"),
                P("units", "units", Verbs.Get | Verbs.Set, "图形单位 INSUNITS：mm cm m in ft unitless。只影响插入图块 / 外部参照时的缩放和 measure convert 的缺省单位，不缩放已有几何", "units=mm"),
                P("measurement", "string", Verbs.Get | Verbs.Set, "MEASUREMENT：metric（公制）或 imperial（英制），决定填充图案与线型文件的缺省选择", "measurement=metric"),
                P("lunits", "number", Verbs.Get | Verbs.Set, "LUNITS 长度显示格式：1 科学 2 小数 3 工程 4 建筑 5 分数", "lunits=2"),
                P("luprec", "number", Verbs.Get | Verbs.Set, "LUPREC 长度显示的小数位 0-8", "luprec=0"),
                P("aunits", "number", Verbs.Get | Verbs.Set, "AUNITS 角度显示格式：0 十进制度 1 度分秒 2 百分度 3 弧度 4 勘测", "aunits=0"),
                P("auprec", "number", Verbs.Get | Verbs.Set, "AUPREC 角度显示的小数位 0-8", "auprec=0"),
                P("ltscale", "number", Verbs.Get | Verbs.Set, "LTSCALE 全局线型比例：大比例图上点划线看不出间隔时调大（1:100 常设 100）", "ltscale=100"),
                P("dimscale", "number", Verbs.Get | Verbs.Set, "DIMSCALE 当前标注样式的全局比例", "dimscale=100"),
                P("currentLayer", "string", Verbs.Get | Verbs.Set, "当前图层", "currentLayer=WALL"),
                P("currentLayout", "string", Verbs.Get, "当前布局（切换用 set \"/layout[@name=...]\" --prop current=true）"),
                P("layers", "number", Verbs.Get, "图层数"),
                P("entities", "number", Verbs.Get, "模型空间实体数"),
            }, "acadclr get /", "acadclr set / --prop units=mm --prop ltscale=100",
               "acadclr get /documents", "acadclr add /documents --type document --prop path=D:\\work\\plan.dwg",
               "acadclr set \"/document[@name=plan.dwg]\" --prop current=true", "acadclr remove \"/document[@name=plan.dwg]\" --force   # 丢弃修改并关闭"),

            new TypeDef("sysvar", "/sysvars", "系统变量（GETVAR / SETVAR）。get /sysvars 列出常用变量，任意变量用 /sysvar[@name=X]", false, new[]
            {
                P("name", "string", Verbs.Get, "变量名（大写）"),
                P("value", "string", Verbs.Get | Verbs.Set, "值：整数、实数、字符串或点 x,y[,z]，按变量当前类型转换", "value=100"),
                P("valueType", "string", Verbs.Get, "integer / real / string / point"),
            }, "acadclr get \"/sysvar[@name=PDMODE]\"", "acadclr set \"/sysvar[@name=PDMODE]\" --prop value=3",
               "acadclr query \"sysvar[name~=DIM]\"          # 只在常用变量里查"),
        };

        // ---------------- edit 动作 ----------------

        private static readonly string TargetsNote = "目标：路径、句柄或选择器；多个路径 / 句柄用 ; 分隔";

        public static readonly List<ActionDef> Actions = new List<ActionDef>
        {
            new ActionDef("offset", "偏移曲线（直线、多段线、圆、圆弧、椭圆、样条），生成新曲线", "一条曲线", false, new[]
            {
                P("distance", "number", Verbs.Set, "偏移距离", "distance=240", true),
                P("side", "point", Verbs.Set, "偏移到哪一侧：给该侧的任意一点；缺省按正方向"),
            }, "acadclr edit offset \"/entity[@handle=8A]\" --prop distance=240 --prop side=100,100"),

            new ActionDef("mirror", "沿轴线镜像", TargetsNote, false, new[]
            {
                P("axis", "points", Verbs.Set, "镜像轴上的两点 x1,y1;x2,y2", "axis=0,0;0,1000", true),
                P("keep", "bool", Verbs.Set, "保留源实体（默认 true：生成镜像副本；false：原地镜像）", "keep=false"),
            }, "acadclr edit mirror \"polyline[layer=WALL]\" --prop axis=\"6500,0;6500,1000\""),

            new ActionDef("explode", "分解多段线、块参照、标注、填充等，返回分解出的实体", TargetsNote, false, new PropDef[0],
                "acadclr edit explode \"/entity[@handle=8A]\""),

            new ActionDef("break", "打断曲线：一个点一分为二，两个点删除中间段", "一条曲线", false, new[]
            {
                P("at", "points", Verbs.Set, "打断点 x,y 或 x1,y1;x2,y2（自动投影到曲线上）", "at=1000,0;2000,0", true),
            }, "acadclr edit break \"/entity[@handle=8D]\" --prop at=\"1000,2000;3000,2000\""),

            new ActionDef("join", "把首尾相接或共线的实体合并进第一个", "至少两个实体，第一个为主体", false, new PropDef[0],
                "acadclr edit join \"8A;8B;8C\""),

            new ActionDef("array", "阵列复制（源实体保留）：给 center 为环形阵列，否则为矩形阵列", TargetsNote, false, new[]
            {
                P("rows", "number", Verbs.Set, "矩形：行数（含源），默认 1", "rows=3"),
                P("cols", "number", Verbs.Set, "矩形：列数（含源），默认 1", "cols=4"),
                P("rowSpacing", "number", Verbs.Set, "矩形：行距（沿 Y，可为负）", "rowSpacing=8400"),
                P("colSpacing", "number", Verbs.Set, "矩形：列距（沿 X，可为负）", "colSpacing=8400"),
                P("angle", "angle", Verbs.Set, "矩形：整个阵列的倾角（度）"),
                P("center", "point", Verbs.Set, "环形：阵列中心", "center=0,0"),
                P("count", "number", Verbs.Set, "环形：总份数（含源）", "count=8"),
                P("fillAngle", "angle", Verbs.Set, "环形：填充角度，默认 360"),
                P("rotateItems", "bool", Verbs.Set, "环形：每份是否随之旋转，默认 true"),
            }, "acadclr edit array \"/entity[@handle=8E]\" --prop rows=3 --prop cols=4 --prop rowSpacing=8400 --prop colSpacing=8400",
               "acadclr edit array \"/entity[@handle=8E]\" --prop center=0,0 --prop count=8"),

            new ActionDef("trim", "修剪（AutoCAD 命令）。要保留的部分由拾取点决定：目标写成 句柄@x,y，拾取点落在要剪掉的那段上",
                "句柄[@拾取点]，多个用 ; 分隔", true, new[]
            {
                P("edges", "paths", Verbs.Set, "剪切边：路径或句柄，多个用 ; 分隔", "edges=8A;8B", true),
            }, "acadclr edit trim \"8D@13500,2000\" --prop edges=8B"),

            new ActionDef("extend", "延伸到边界（AutoCAD 命令）。目标写成 句柄@x,y，拾取点靠近要延伸的那一端",
                "句柄[@拾取点]，多个用 ; 分隔", true, new[]
            {
                P("boundary", "paths", Verbs.Set, "边界：路径或句柄，多个用 ; 分隔", "boundary=8A", true),
            }, "acadclr edit extend \"95@0,900\" --prop boundary=8A"),

            new ActionDef("fillet", "两条曲线倒圆角（AutoCAD 命令，半径 0 为直角相接）", "第一条曲线（句柄[@拾取点]）", true, new[]
            {
                P("with", "path", Verbs.Set, "第二条曲线（句柄[@拾取点]）", "with=8B", true),
                P("radius", "number", Verbs.Set, "圆角半径，默认 0", "radius=500"),
            }, "acadclr edit fillet 8D --prop with=95 --prop radius=300"),

            new ActionDef("chamfer", "两条直线倒角（AutoCAD 命令）", "第一条直线（句柄[@拾取点]）", true, new[]
            {
                P("with", "path", Verbs.Set, "第二条直线（句柄[@拾取点]）", "with=8B", true),
                P("d1", "number", Verbs.Set, "第一条线上的倒角距离，默认 0", "d1=200"),
                P("d2", "number", Verbs.Set, "第二条线上的倒角距离，默认与 d1 相同", "d2=200"),
            }, "acadclr edit chamfer 8D --prop with=95 --prop d1=200"),
        };

        // ---------------- measure / check 动作 ----------------

        private static ActionDef Measure(string name, string desc, string target, bool needsTarget, PropDef[] props, params string[] examples) =>
            new ActionDef(name, desc, target, false, props, examples) { Verb = "measure", NeedsTarget = needsTarget };

        private static ActionDef Check(string name, string desc, string target, PropDef[] props, params string[] examples) =>
            new ActionDef(name, desc, target, false, props, examples) { Verb = "check" };

        public static readonly List<ActionDef> MeasureActions = new List<ActionDef>
        {
            Measure("distance", "两点间的距离、dx、dy 与方向角（度，逆时针为正）", "无", false, new[]
            {
                P("from", "point", Verbs.Set, "起点", "from=0,0", true),
                P("to", "point", Verbs.Set, "终点", "to=3000,4000", true),
            }, "acadclr measure distance --prop from=0,0 --prop to=3000,4000"),

            Measure("area", "闭合实体（多段线、圆、椭圆、面域、填充…）的面积与周长，多个实体给出合计", TargetsNote, true, new PropDef[0],
                "acadclr measure area \"polyline[layer=ROOM][closed=true]\""),

            Measure("length", "曲线（直线、多段线、圆弧、样条…）的长度，多个实体给出合计", TargetsNote, true, new PropDef[0],
                "acadclr measure length \"line[layer=WALL]\""),

            Measure("convert", "长度单位换算：from 缺省为图形单位（INSUNITS）", "无", false, new[]
            {
                P("value", "number", Verbs.Set, "要换算的长度", "value=3.6", true),
                P("to", "units", Verbs.Set, "目标单位：mm cm m km in ft yd mi", "to=mm", true),
                P("from", "units", Verbs.Set, "源单位，缺省为图形单位", "from=m"),
            }, "acadclr measure convert --prop value=3.6 --prop from=m --prop to=mm"),
        };

        public static readonly List<ActionDef> CheckActions = new List<ActionDef>
        {
            Check("overlap", "一组实体两两之间是否重叠（按包围盒，共边不算）", TargetsNote + "，至少两个", new[]
            {
                P("minArea", "number", Verbs.Set, "小于该面积的重叠忽略，默认 0"),
            }, "acadclr check overlap \"polyline[layer=ROOM]\""),

            Check("inside", "一组实体是否都落在边界实体内（按包围盒）", TargetsNote, new[]
            {
                P("boundary", "path", Verbs.Set, "边界实体：路径或句柄", "boundary=8A", true),
            }, "acadclr check inside \"polyline[layer=ROOM]\" --prop boundary=8A"),

            Check("adjacent", "两个实体是否相邻：一个方向有搭接，另一个方向的间距不超过 gap（按包围盒）", "一个实体", new[]
            {
                P("with", "path", Verbs.Set, "另一个实体：路径或句柄", "with=8B", true),
                P("gap", "number", Verbs.Set, "允许的间距（墙厚、走廊宽度），默认 0", "gap=240"),
            }, "acadclr check adjacent 8A --prop with=8B --prop gap=240"),
        };

        public static readonly List<ActionDef> ViewActions = new List<ActionDef>
        {
            new ActionDef("zoom", "调整当前视图：缩放到图形范围、窗口，或给定目标实体的范围（四周留 5% 边距）", "可选：路径、句柄或选择器", false, new[]
            {
                P("to", "string", Verbs.Set, "extents（默认）或窗口角点 x1,y1;x2,y2；给了目标实体时忽略", "to=0,0;20000,12000"),
            }, "acadclr view zoom", "acadclr view zoom \"polyline[layer=ROOM]\"") { Verb = "view", NeedsTarget = false },

            new ActionDef("capture", "截取 AutoCAD 窗口为 PNG，用来目视核对绘图结果。MCP 返回图片，命令行保存为文件", "可选：先缩放到这些实体", false, new[]
            {
                P("region", "string", Verbs.Set, "drawing（只截绘图区，默认，更省 token）或 window（整个窗口）", "region=window"),
                P("zoom", "string", Verbs.Set, "截图前先缩放：extents 或窗口 x1,y1;x2,y2", "zoom=extents"),
                P("maxWidth", "number", Verbs.Set, "输出图片最大宽度（像素），超过时等比缩小；建议 1200 左右", "maxWidth=1200"),
                P("output", "string", Verbs.Set, "保存为该 PNG 文件（绝对路径）；命令行缺省存到临时目录", "output=D:\\out\\view.png"),
            }, "acadclr view capture --prop zoom=extents --prop maxWidth=1200") { Verb = "view", NeedsTarget = false },
        };

        /// <summary>带动作的动词。</summary>
        public static readonly string[] ActionVerbs = { "edit", "measure", "check", "view" };

        public static List<ActionDef> ActionsOf(string verb) =>
            verb == "measure" ? MeasureActions : verb == "check" ? CheckActions : verb == "view" ? ViewActions : Actions;

        public static ActionDef? FindAction(string name) => FindAction("edit", name);

        public static ActionDef? FindAction(string verb, string name) =>
            ActionsOf(verb).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>查找动作，找不到时给出最接近的候选。</summary>
        public static ActionDef RequireAction(string verb, string? name)
        {
            var list = ActionsOf(verb);
            if (string.IsNullOrWhiteSpace(name))
                throw new CliError("invalid_request", $"{verb} 缺少 action。", "可用：" + string.Join("、", list.Select(a => a.Name)));
            return FindAction(verb, name!.Trim()) ?? throw new CliError("invalid_request", $"未知的 {verb} 动作 “{name}”。",
                (Suggest(name, list.Select(a => a.Name)) is string n ? $"是否想用 {n}？" : "") + "可用：" + string.Join("、", list.Select(a => a.Name)));
        }

        private static readonly Dictionary<string, string> VerbSummary = new Dictionary<string, string>
        {
            ["edit"] = "对已有实体做几何编辑，返回生成或修改后的实体",
            ["measure"] = "测量：距离、面积、长度、单位换算（只读）",
            ["check"] = "空间校验：重叠、越界、相邻（只读，按包围盒判定）",
            ["view"] = "视图：缩放与截图（仅实时模式）",
        };

        public static string HelpEdit() => HelpVerb("edit");

        public static string HelpVerb(string verb)
        {
            var list = ActionsOf(verb);
            var sb = new StringBuilder();
            sb.AppendLine($"{verb} —— {VerbSummary[verb]}");
            sb.AppendLine();
            sb.AppendLine($"用法：acadclr {verb} <动作> <目标> [--prop key=value ...]");
            var sample = list[0];
            sb.AppendLine($"batch：{{\"command\":\"{verb}\",\"action\":\"{sample.Name}\"" + (sample.NeedsTarget ? ",\"path\":\"$0\"" : "") + ",\"props\":{...}}");
            sb.AppendLine();
            foreach (var a in list)
                sb.AppendLine("  " + a.Name.PadRight(9) + (a.UsesCommand ? "[命令] " : "       ") + a.Description);
            sb.AppendLine();
            if (list.Any(a => a.UsesCommand))
                sb.AppendLine("[命令] 表示通过 AutoCAD 命令执行：不能放进 batch；实时模式走命令队列，离线模式由 accoreconsole 打开图纸执行并保存。");
            if (verb == "check")
                sb.AppendLine("结果 result=PASS / FAIL。包围盒是轴对齐矩形：矩形房间足够准，斜放或异形实体可能误报，需要时 get 实体复核。");
            sb.AppendLine($"详细：acadclr help {verb} <动作>，例如 acadclr help {verb} {list[Math.Min(1, list.Count - 1)].Name}");
            return sb.ToString();
        }

        public static string HelpAction(ActionDef a)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{a.Verb} {a.Name} —— {a.Description}");
            sb.AppendLine("目标：" + a.Target);
            if (a.UsesCommand) sb.AppendLine("方式：AutoCAD 命令（不能放进 batch）");
            if (a.Props.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("属性（* 为必填）：");
                int w = a.Props.Max(p => p.Name.Length) + 2;
                foreach (var p in a.Props)
                    sb.AppendLine("  " + (p.Required ? "*" : " ") + p.Name.PadRight(w) + p.Description + (p.Example != null ? "    例：" + p.Example : ""));
            }
            if (a.Examples.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("示例：");
                foreach (var e in a.Examples) sb.AppendLine("  " + e);
            }
            return sb.ToString();
        }

        public static TypeDef? FindType(string name) =>
            Types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>不在 Types 里的实体（hatch、dimension…）只支持公共属性。</summary>
        public static TypeDef GenericEntity(string dxfType) =>
            new TypeDef(dxfType, "/model", "其他实体（仅支持公共属性）", true, CommonEntityProps);

        public static IEnumerable<string> AddableTypes => Types.Where(t => t.Name != "device" && t.Name != "sysvar").Select(t => t.Name);

        // ---------------- plot ----------------

        public static readonly List<PropDef> PlotProps = new List<PropDef>
        {
            P("output", "string", Verbs.Set, "输出文件（绝对路径）；缺省为图纸同目录下 <图名>-<布局>-<时间>.pdf", "output=D:\\out\\A3.pdf"),
            P("device", "string", Verbs.Set, "打印设备，默认 DWG To PDF.pc3"),
            P("paper", "string", Verbs.Set, "纸张：完整名或 A3 这类简称；缺省沿用布局页面设置", "paper=A3"),
            P("landscape", "bool", Verbs.Set, "横向"),
            P("area", "string", Verbs.Set, "范围：layout（布局图纸，默认）或 extents（图形范围；模型空间默认）", "area=extents"),
            P("scale", "string", Verbs.Set, "比例：fit（布满，默认）或 1:N 的 N", "scale=100"),
            P("mono", "bool", Verbs.Set, "单色（monochrome.ctb）"),
        };

        /// <summary>校验打印属性名（不区分大小写），返回规范名 → 值。</summary>
        public static Dictionary<string, string> CheckPlotProps(IEnumerable<KeyValuePair<string, string>> props)
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in props)
            {
                var def = PlotProps.FirstOrDefault(p => p.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (def == null)
                {
                    var near = Suggest(kv.Key, PlotProps.Select(p => p.Name));
                    throw new CliError("unsupported_property", $"plot 没有属性 “{kv.Key}”。", (near != null ? $"是否想用 {near}？" : "") + "运行 acadclr help plot");
                }
                map[def.Name] = kv.Value;
            }
            return map;
        }

        /// <summary>打印目标 “/layout[@name=A3]”、“/model”、“A3”、“Model” → 布局名；空为 null。</summary>
        public static string? PlotLayoutName(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            var t = target!.Trim();
            if (!PathParser.IsPath(t)) return t;
            var segs = PathParser.Parse(t);
            var seg = segs.LastOrDefault(x => x.Name == "layout");
            if (seg?.AttrName == "name") return seg.AttrValue;
            if (segs.Count == 1 && segs[0].Name == "model") return "Model";
            throw new CliError("invalid_path", $"plot 的目标应为布局：{t}", "例：/layout[@name=A3] 或直接写布局名");
        }

        public static string HelpPlot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("plot —— 把布局（或模型空间）打印到 PDF");
            sb.AppendLine();
            sb.AppendLine("用法：acadclr plot [布局名或路径] [--prop key=value ...]");
            sb.AppendLine("      不给布局时，实时模式打印当前布局，离线模式打印 Model（范围 extents）");
            sb.AppendLine();
            sb.AppendLine("属性：");
            foreach (var p in PlotProps)
                sb.AppendLine("  " + p.Name.PadRight(11) + p.Description + (p.Example != null ? "    例：" + p.Example : ""));
            sb.AppendLine();
            sb.AppendLine("说明：");
            sb.AppendLine("  · 只出图纸的局部：建布局，用视口的 viewCenter / scale / width / height 框定范围，再打印该布局。");
            sb.AppendLine("  · area=window / display / limits 在 AutoCAD 2014 的打印引擎上出不了内容（AutoCADMCP 实测），不支持。");
            sb.AppendLine("  · 离线模式需要插件能在 accoreconsole 中加载（插件目录须在受信任位置）。");
            sb.AppendLine();
            sb.AppendLine("示例：");
            sb.AppendLine("  acadclr plot \"/layout[@name=A3]\" --prop output=D:\\out\\A3.pdf");
            sb.AppendLine("  acadclr plot plan.dwg Model --prop area=extents --prop paper=A3 --prop landscape=true --prop mono=true");
            return sb.ToString();
        }

        /// <summary>
        /// 校验属性名与动词是否匹配。未知属性给出最接近的候选；只读属性在 add/set 时报错。
        /// </summary>
        public static PropDef CheckProp(TypeDef type, string prop, Verbs verb)
        {
            var def = type.Find(prop);
            if (def == null)
            {
                var near = Suggest(prop, type.Props.Where(p => (p.Verbs & verb) != 0).Select(p => p.Name));
                throw new CliError("unsupported_property", $"{type.Name} 没有属性 “{prop}”。",
                    (near != null ? $"是否想用 {near}？" : "") + $"运行 acadclr help {type.Name} 查看全部属性。");
            }
            if ((def.Verbs & verb) == 0)
            {
                var what = verb == Verbs.Add ? "add" : verb == Verbs.Set ? "set" : "get";
                throw new CliError("unsupported_property", $"{type.Name}.{def.Name} 不能用于 {what}（只读或仅限特定操作）。",
                    $"运行 acadclr help {type.Name} 查看各属性支持的操作。");
            }
            return def;
        }

        public static string? Suggest(string input, IEnumerable<string> candidates)
        {
            string? best = null;
            int bestD = int.MaxValue;
            foreach (var c in candidates)
            {
                int d = Distance(input.ToLowerInvariant(), c.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = c; }
            }
            return best != null && bestD <= Math.Max(2, input.Length / 3) ? best : null;
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        // ---------------- help 输出 ----------------

        public static string HelpOverview()
        {
            var sb = new StringBuilder();
            sb.AppendLine("acadclr —— 面向 AI 智能体的 AutoCAD 命令行工具");
            sb.AppendLine();
            sb.AppendLine("用法：acadclr <命令> [参数] [--prop key=value ...] [--json]");
            sb.AppendLine();
            sb.AppendLine("命令：");
            sb.Append(Commands.HelpList());
            sb.AppendLine();
            sb.AppendLine("两种模式：");
            sb.AppendLine("  实时模式（默认）   操作已打开的 AutoCAD（需先 NETLOAD AcadClr.Plugin.dll）");
            sb.AppendLine("  离线模式 --dwg F   通过 accoreconsole 直接读写 DWG 文件，无需打开 AutoCAD 界面");
            sb.AppendLine();
            sb.AppendLine("全局选项：--json  --dwg <file>  --acad <accoreconsole 路径或年份>  --pid <进程号>  --timeout 秒");
            sb.AppendLine("各命令的选项见 acadclr help <命令>；同名参数也是 MCP 工具的参数（--best-effort → bestEffort）");
            sb.AppendLine();
            sb.AppendLine("路径：/  /model  /model/line[1]  /model/entity[@handle=2A3]  /entity[@handle=2A3]");
            sb.AppendLine("      /layers  /layer[@name=WALL]  /xrefs  /xref[@name=BASE]  /devices  /device[@name=...]");
            sb.AppendLine("      /layouts  /layout[@name=A3]  /layout[@name=A3]/viewport[1]  （图纸空间实体的父路径是布局）");
            sb.AppendLine("      /blocks  /block[@name=TREE]  /linetypes  /linetype[@name=CENTER]  /sysvars  /sysvar[@name=LTSCALE]");
            sb.AppendLine("      /documents  /document[@name=plan.dwg]  （打开的图形，仅实时模式；其他命令用 --doc 指定文档）");
            sb.AppendLine("      （索引从 1 开始，[last()] 取最后一个）");
            sb.AppendLine();
            sb.AppendLine("类型：" + string.Join("  ", Types.Select(t => t.Name)));
            sb.AppendLine("      其他实体（hatch、dimension、spline…）可查询，并可改公共属性。");
            sb.AppendLine();
            sb.AppendLine("详细：acadclr help <type>，例如 acadclr help line");
            return sb.ToString();
        }

        public static string HelpType(TypeDef t)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{t.Name} —— {t.Description}");
            if (t.Parent.Length > 0) sb.AppendLine($"父路径：{t.Parent}");
            sb.AppendLine();
            sb.AppendLine("属性（* 为 add 必填）：");
            int w = t.Props.Max(p => p.Name.Length) + 2;
            foreach (var p in t.Props)
            {
                var verbs = string.Join("/", new[] { (Verbs.Add, "add"), (Verbs.Set, "set"), (Verbs.Get, "get") }
                    .Where(v => (p.Verbs & v.Item1) != 0).Select(v => v.Item2));
                sb.Append("  ").Append((p.Required ? "*" : " ") + p.Name.PadRight(w))
                  .Append(("[" + verbs + "]").PadRight(16))
                  .Append(p.Description);
                if (p.Example != null) sb.Append("    例：" + p.Example);
                sb.AppendLine();
            }
            if (t.Examples.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("示例：");
                foreach (var e in t.Examples) sb.AppendLine("  " + e);
            }
            return sb.ToString();
        }

        public static JObject HelpJson(TypeDef t) => new JObject
        {
            ["type"] = t.Name,
            ["parent"] = t.Parent,
            ["description"] = t.Description,
            ["props"] = new JArray(t.Props.Select(p => new JObject
            {
                ["name"] = p.Name,
                ["kind"] = p.Kind,
                ["verbs"] = new JArray(new[] { (Verbs.Add, "add"), (Verbs.Set, "set"), (Verbs.Get, "get") }
                    .Where(v => (p.Verbs & v.Item1) != 0).Select(v => v.Item2)),
                ["required"] = p.Required,
                ["description"] = p.Description,
                ["example"] = p.Example,
            })),
            ["examples"] = new JArray(t.Examples),
        };
    }
}
