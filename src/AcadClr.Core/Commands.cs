using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AcadClr.Core
{
    public enum ArgKind
    {
        String,
        Integer,
        Boolean,
        /// <summary>键值对象；命令行写成可重复的 --prop key=value。</summary>
        Object,
        /// <summary>JSON 数组；命令行写成 JSON 字符串。</summary>
        Array,
        /// <summary>一个或多个字符串；命令行选项可重复。</summary>
        StringList,
    }

    /// <summary>命令的一个参数。JSON / MCP 按 <see cref="Name"/> 传；命令行按位置或 <see cref="Option"/> 传。</summary>
    public sealed class ArgDef
    {
        public string Name { get; }
        public ArgKind Kind { get; }
        public string Description { get; }
        public bool Required { get; set; }

        /// <summary>命令行位置参数序号（命令名之后，从 0 开始）；-1 表示不是位置参数。</summary>
        public int Position { get; set; } = -1;

        /// <summary>true：收下剩余的全部位置参数（help 的主题、script 的多个 DWG）。</summary>
        public bool Rest { get; set; }

        /// <summary>命令行选项名，如 --depth。<see cref="ArgKind.Boolean"/> 为开关。</summary>
        public string? Option { get; set; }

        /// <summary>命令行缺省值（JSON 调用同样适用）。</summary>
        public string? Default { get; set; }

        /// <summary>
        /// 命令行位置参数按内容决定落到哪个参数上：set / remove / edit 的目标可以是路径或选择器，
        /// 返回 “path” 或 “selector”。JSON 调用直接写 path 或 selector。
        /// </summary>
        public Func<string, string>? Route { get; set; }

        public ArgDef(string name, ArgKind kind, string description)
        {
            Name = name; Kind = kind; Description = description;
        }
    }

    [Flags]
    public enum CommandModes
    {
        /// <summary>实时模式：操作运行中的 AutoCAD。</summary>
        Live = 1,
        /// <summary>离线模式：通过 accoreconsole 读写 DWG 文件（dwg 参数）。</summary>
        Offline = 2,
        Both = Live | Offline,
        /// <summary>不连接 AutoCAD（help、instances）。</summary>
        None = 0,
    }

    public sealed class CommandDef
    {
        public string Name { get; }
        public string Synopsis { get; }
        public string Summary { get; }
        public CommandModes Modes { get; }

        /// <summary>是否修改图形。只读模式、MCP 工具注解据此判断。</summary>
        public bool Writes { get; }

        /// <summary>只属于命令行，不作为 MCP 工具。</summary>
        public bool CliOnly { get; set; }

        /// <summary>可以作为 batch 中的一条操作。</summary>
        public bool Batchable { get; set; }

        public List<ArgDef> Args { get; }

        public CommandDef(string name, string synopsis, string summary, CommandModes modes, bool writes, params ArgDef[] args)
        {
            Name = name; Synopsis = synopsis; Summary = summary; Modes = modes; Writes = writes;
            Args = args.ToList();
            // 公共参数统一排在命令自己的参数之后：dwg、pid、doc、timeout
            var doc = Args.FirstOrDefault(a => a.Name == "doc");
            if (doc != null) Args.Remove(doc);
            if ((modes & CommandModes.Offline) != 0 && !Args.Any(a => a.Name == "dwg")) Args.Add(Commands.DwgArg());
            if ((modes & CommandModes.Live) != 0) Args.Add(Commands.PidArg());
            if (doc != null) Args.Add(doc);
            if (modes != CommandModes.None) Args.Add(Commands.TimeoutArg());
        }

        public ArgDef? Find(string name) => Args.FirstOrDefault(a => a.Name == name);
    }

    /// <summary>
    /// 命令表：命令行参数解析、help、MCP 工具目录的唯一来源。
    /// 命令名即 MCP 工具名；参数名即 MCP arguments 的键，也是命令行 --选项 去掉前缀后的驼峰形式。
    /// </summary>
    public static class Commands
    {
        private static ArgDef A(string name, ArgKind kind, string desc, int pos = -1, string? option = null, bool required = false, string? def = null) =>
            new ArgDef(name, kind, desc) { Position = pos, Option = option, Required = required, Default = def };

        internal static ArgDef DwgArg() => A("dwg", ArgKind.String, "离线模式：要读写的 DWG 文件；不填则操作运行中的 AutoCAD", option: "--dwg");
        internal static ArgDef PidArg() => A("pid", ArgKind.Integer, "实时模式：AutoCAD 进程号；不填连接最近启动的实例", option: "--pid");
        private static ArgDef Doc() => A("doc", ArgKind.String, "实时模式：要操作的文档（文件名或序号，见 get /documents）；不填为当前文档", option: "--doc");
        internal static ArgDef TimeoutArg() => A("timeout", ArgKind.Integer, "超时（秒）", option: "--timeout");

        private static ArgDef Props(string desc = "属性，如 {\"layer\":\"WALL\",\"color\":1}") => A("props", ArgKind.Object, desc, option: "--prop");
        private static ArgDef Force(string desc) => A("force", ArgKind.Boolean, desc, option: "--force");
        private static ArgDef Limit() => A("limit", ArgKind.Integer, "最多返回多少个元素（默认 200）", option: "--limit");

        /// <summary>
        /// 目标是否为实体引用：路径（/ 开头）、$N、裸句柄（可带 @x,y 拾取点），多个用 ; 分隔。
        /// 否则按选择器处理（选择器条件里也可能有 ;，如 polyline[points=0,0;…]，所以要逐段判断）。
        /// </summary>
        public static bool IsRefList(string target)
        {
            var parts = target.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
            return parts.Count > 0 && parts.All(p =>
                p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("$", StringComparison.Ordinal) ||
                IsHandle(p.Split('@')[0]));
        }

        private static bool IsHandle(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

        /// <summary>命令行位置参数：实体引用落到 path，其余落到 selector。</summary>
        public static string RouteTarget(string target) => IsRefList(target) ? "path" : "selector";

        private static ArgDef Target(string desc, int pos, Func<string, string> route) =>
            new ArgDef("path", ArgKind.String, desc) { Position = pos, Route = route };

        private static ArgDef SelectorArg(string desc) => A("selector", ArgKind.String, desc);

        public static readonly List<CommandDef> All = new List<CommandDef>
        {
            new CommandDef("status", "status", "连接状态与当前图形信息", CommandModes.Both, false, Doc()),

            new CommandDef("get", "get <path> [--depth N]", "读取元素（及子元素）", CommandModes.Both, false,
                Doc(),
                A("path", ArgKind.String, "路径，如 /model、/layers、/entity[@handle=2A3]", pos: 0, def: "/"),
                A("depth", ArgKind.Integer, "展开子元素的层数", option: "--depth"),
                Limit()) { Batchable = true },

            new CommandDef("query", "query <selector>", "按选择器查找，如 line[layer=WALL][length>=3000]", CommandModes.Both, false,
                Doc(),
                A("selector", ArgKind.String, "选择器，如 line[layer=WALL][length>=3000]", pos: 0, option: "--selector", required: true),
                Limit()) { Batchable = true },

            new CommandDef("add", "add <parent> --type T", "添加元素；--from <path> 克隆已有实体", CommandModes.Both, true,
                Doc(),
                A("parent", ArgKind.String, "父路径：/model、/layers、/layout[@name=A3] 等", pos: 0, def: "/model"),
                A("type", ArgKind.String, "元素类型（help 查看全部类型）", option: "--type"),
                A("from", ArgKind.String, "克隆该路径的实体（与 type 二选一），可配合 props 的 move", option: "--from"),
                Props()) { Batchable = true },

            new CommandDef("set", "set <path|selector>", "修改属性（含 move / rotate / scale）", CommandModes.Both, true,
                Doc(),
                Target("目标：路径、句柄或 $N，多个用 ; 分隔", 0, RouteTarget),
                SelectorArg("目标选择器（必须带条件）"),
                Props(),
                Force("选择器不带条件时也执行")) { Batchable = true },

            new CommandDef("remove", "remove <path|selector>", "删除（一次超过 30 个需 --force）", CommandModes.Both, true,
                Doc(),
                Target("目标：路径、句柄或 $N，多个用 ; 分隔", 0, RouteTarget),
                SelectorArg("目标选择器（必须带条件）"),
                Force("允许一次删除超过 30 个元素")) { Batchable = true },

            new CommandDef("edit", "edit <动作> <目标>", "偏移 镜像 分解 打断 合并 阵列 修剪 延伸 倒圆角 倒角", CommandModes.Both, true,
                Doc(),
                A("action", ArgKind.String, "动作：offset mirror explode break join array trim extend fillet chamfer", pos: 0, required: true),
                Target("目标：路径、句柄、句柄@拾取点，多个用 ; 分隔", 1, RouteTarget),
                SelectorArg("目标选择器"),
                Props("动作参数（help edit <动作> 查看）"),
                Force("选择器命中过多时也执行")) { Batchable = true },

            new CommandDef("measure", "measure <动作> [目标]", "测量：距离 面积 长度 单位换算", CommandModes.Both, false,
                Doc(),
                A("action", ArgKind.String, "动作：distance area length convert", pos: 0, required: true),
                Target("目标：路径、句柄，多个用 ; 分隔（distance、convert 不需要）", 1, RouteTarget),
                SelectorArg("目标选择器"),
                Props("动作参数（help measure <动作> 查看）")) { Batchable = true },

            new CommandDef("check", "check <动作> <目标>", "空间校验：重叠 越界 相邻（按包围盒）", CommandModes.Both, false,
                Doc(),
                A("action", ArgKind.String, "动作：overlap inside adjacent", pos: 0, required: true),
                Target("目标：路径、句柄，多个用 ; 分隔", 1, RouteTarget),
                SelectorArg("目标选择器"),
                Props("动作参数（help check <动作> 查看）")) { Batchable = true },

            new CommandDef("view", "view <动作> [目标]", "视图：zoom 缩放、capture 截图（实时模式）", CommandModes.Live, false,
                A("action", ArgKind.String, "动作：zoom capture", pos: 0, required: true),
                Target("可选：缩放到这些实体（路径、句柄，多个用 ; 分隔）", 1, RouteTarget),
                SelectorArg("可选：缩放到选择器匹配的实体"),
                Props("动作参数（help view <动作> 查看）")),

            new CommandDef("plot", "plot [布局]", "打印到 PDF（acadclr help plot）", CommandModes.Both, false,
                A("layout", ArgKind.String, "布局名或路径 /layout[@name=A3]；不填时实时模式打印当前布局，离线模式打印 Model", pos: 0),
                Props("打印参数（help plot 查看）")),

            new CommandDef("batch", "batch", "批量执行 JSON（--input 文件 / --commands 字符串 / 标准输入）", CommandModes.Both, true,
                Doc(),
                A("items", ArgKind.Array, "操作列表：[{\"command\":\"add\",\"parent\":\"/model\",...}]，\"$N\" 引用第 N 条的结果路径", option: "--commands"),
                A("input", ArgKind.String, "从 JSON 文件读取操作列表", option: "--input"),
                A("bestEffort", ArgKind.Boolean, "保留成功的部分（默认任一失败整批回滚）", option: "--best-effort"),
                A("stopOnError", ArgKind.Boolean, "遇到第一个失败就停止", option: "--stop-on-error"),
                Force("对所有条目生效的 force")),

            new CommandDef("stats", "stats", "按类型、图层统计实体，给出图形范围", CommandModes.Both, false, Doc()) { Batchable = true },

            new CommandDef("lisp", "lisp \"<expr>\" | --file f.lsp", "执行 AutoLISP 并返回值（--cmd 走命令队列，离线加 --save 保存）", CommandModes.Both, true,
                A("code", ArgKind.String, "AutoLISP 代码，可以有多个表达式，返回最后一个的值", pos: 0),
                A("file", ArgKind.String, "从 .lsp 文件读取代码", option: "--file"),
                A("commandQueue", ArgKind.Boolean, "实时模式：走命令队列（代码含 (command ...) 时需要）", option: "--cmd"),
                A("save", ArgKind.Boolean, "离线模式：执行后保存", option: "--save")),

            new CommandDef("script", "script f.scr | --text \"...\"", "执行脚本；离线可对多个 --dwg（支持通配符）批量运行", CommandModes.Both, true,
                A("file", ArgKind.String, "脚本文件 .scr", pos: 0),
                A("code", ArgKind.String, "脚本文本", option: "--text"),
                new ArgDef("dwg", ArgKind.StringList, "离线模式：一个或多个 DWG，支持通配符") { Position = 1, Rest = true, Option = "--dwg" },
                A("save", ArgKind.Boolean, "离线模式：执行后保存", option: "--save")),

            new CommandDef("mark", "mark [标签]", "打 undo 标记：之后 rollback 回到这里（实时模式，当前文档）", CommandModes.Live, true,
                A("label", ArgKind.String, "标记名，便于辨认；缺省为 #序号", pos: 0)),

            new CommandDef("rollback", "rollback", "撤销回到最近一个 mark（没有 mark 时拒绝）", CommandModes.Live, true),

            new CommandDef("undo", "undo [N]", "撤销最近 N 步（AutoCAD 的 UNDO N，粒度由 AutoCAD 决定）", CommandModes.Live, true,
                A("steps", ArgKind.Integer, "步数 1-200", pos: 0, def: "1")),

            new CommandDef("save", "save [--as path]", "保存（实时模式）", CommandModes.Live, true,
                Doc(),
                A("saveAs", ArgKind.String, "另存为该路径", option: "--as")),

            new CommandDef("create", "create <file.dwg>", "新建空白 DWG（离线）", CommandModes.Offline, true,
                A("dwg", ArgKind.String, "新文件路径（已存在则报错）", pos: 0, required: true)),

            new CommandDef("log", "log [N]", "查看操作日志的最后 N 行（每次调用的时间、来源、命令、结果、耗时）", CommandModes.None, false,
                A("lines", ArgKind.Integer, "行数 1-2000", pos: 0, def: "50")),

            new CommandDef("instances", "instances", "列出加载了插件的 AutoCAD 实例", CommandModes.None, false),

            new CommandDef("help", "help [type|命令]", "查看类型、命令的参数与属性", CommandModes.None, false,
                new ArgDef("topic", ArgKind.String, "类型名（line、layer…）、edit、edit <动作>、plot；不填为总览") { Position = 0, Rest = true }),

            new CommandDef("mcp", "mcp [--http [--port 7140]]", "启动 MCP server：默认 stdio；--http 为 Streamable HTTP（127.0.0.1）", CommandModes.None, false,
                A("http", ArgKind.Boolean, "用 Streamable HTTP 代替 stdio", option: "--http"),
                A("port", ArgKind.Integer, "HTTP 端口（旧的 AutoCadMCP 插件占用 7130）", option: "--port", def: "7140"),
                A("token", ArgKind.String, "HTTP 要求 Authorization: Bearer <token>；也可用环境变量 ACADCLR_MCP_TOKEN", option: "--token"),
                A("readOnly", ArgKind.Boolean, "只读：修改图形的工具不出现在工具列表，调用也拒绝", option: "--read-only"),
                A("allowLisp", ArgKind.Boolean, "开放 lisp / script 工具（任意代码执行，默认不开放）", option: "--allow-lisp")) { CliOnly = true },

            new CommandDef("config", "config [acad <年份|auto>]", "查看或设置离线模式默认使用的 AutoCAD 版本", CommandModes.None, false,
                new ArgDef("args", ArgKind.StringList, "配置项与值") { Position = 0, Rest = true }) { CliOnly = true },
        };

        /// <summary>batch 中的一条操作是否修改图形（按命令表；未知动词按写处理）。</summary>
        public static bool IsWrite(BatchItem item)
        {
            // 切换当前文档不改图形（AutoCADMCP 的只读模式同样放行 activate_document）
            if (item.Verb == "set" && (item.Path ?? "").StartsWith("/document[", StringComparison.OrdinalIgnoreCase) &&
                item.GetProps().All(kv => kv.Key.Equals("current", StringComparison.OrdinalIgnoreCase)))
                return false;
            return Find(item.Verb)?.Writes ?? true;
        }

        /// <summary>
        /// 发给插件的请求是否修改图形。插件的只读模式、写前备份据此判断：
        /// plot、view、status 只读；lisp、script、命令式编辑、撤销、保存按写处理（无法预知 LISP 做什么）。
        /// </summary>
        public static bool IsWrite(Request req)
        {
            switch (req.Kind)
            {
                case "run": return req.Items.Any(IsWrite);
                case "status":
                case "plot":
                case "view":
                case "ping":
                    return false;
                default:
                    return true;
            }
        }

        public static CommandDef? Find(string name) =>
            All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        public static CommandDef Require(string name) =>
            Find(name) ?? throw new CliError("usage", $"未知命令 “{name}”。",
                (Schema.Suggest(name, All.Select(c => c.Name)) is string n ? $"是否想用 {n}？" : "") + "运行 acadclr help 查看全部命令");

        /// <summary>
        /// 校验 JSON 参数：未知参数、类型、必填。返回规范化后的副本（缺省值已填入）。
        /// 命令行解析得到的参数与 MCP 传入的参数都经过这里。
        /// </summary>
        public static JObject Normalize(CommandDef cmd, JObject? args)
        {
            var result = new JObject();
            foreach (var p in (args ?? new JObject()).Properties())
            {
                if (p.Value.Type == JTokenType.Null) continue;
                var def = cmd.Find(p.Name);
                if (def == null)
                {
                    var near = Schema.Suggest(p.Name, cmd.Args.Select(a => a.Name));
                    throw new CliError("usage", $"{cmd.Name} 没有参数 “{p.Name}”。",
                        (near != null ? $"是否想用 {near}？" : "") + "可用：" + string.Join("、", cmd.Args.Select(a => a.Name)));
                }
                result[def.Name] = Coerce(cmd, def, p.Value);
            }
            foreach (var def in cmd.Args)
            {
                if (result[def.Name] == null && def.Default != null) result[def.Name] = def.Default;
                if (def.Required && result[def.Name] == null)
                    throw new CliError("usage", $"{cmd.Name} 缺少参数 {def.Name}：{def.Description}。", "用法：acadclr " + cmd.Synopsis);
            }
            return result;
        }

        private static JToken Coerce(CommandDef cmd, ArgDef def, JToken v)
        {
            CliError Bad(string want) => new CliError("usage", $"{cmd.Name} 的参数 {def.Name} 应为{want}，收到 {v.ToString(Newtonsoft.Json.Formatting.None)}。");
            switch (def.Kind)
            {
                case ArgKind.String:
                    if (v.Type == JTokenType.String || v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return (string)v!;
                    throw Bad("字符串");
                case ArgKind.Integer:
                    if (v.Type == JTokenType.Integer) return v;
                    if (v.Type == JTokenType.String && int.TryParse((string)v!, out int i)) return i;
                    throw Bad("整数");
                case ArgKind.Boolean:
                    if (v.Type == JTokenType.Boolean) return v;
                    if (v.Type == JTokenType.String && bool.TryParse((string)v!, out bool b)) return b;
                    throw Bad("true 或 false");
                case ArgKind.Object:
                    if (v is JObject) return v;
                    throw Bad("对象");
                case ArgKind.Array:
                    if (v is JArray) return v;
                    if (v is JObject o && o["items"] is JArray inner) return inner; // 兼容 {"items":[...]} 写法
                    throw Bad("数组");
                case ArgKind.StringList:
                    if (v.Type == JTokenType.String) return new JArray(v);
                    if (v is JArray arr && arr.All(x => x.Type == JTokenType.String)) return v;
                    throw Bad("字符串或字符串数组");
                default:
                    return v;
            }
        }

        public static string? GetString(this JObject args, string name) => (string?)args[name];
        public static int? GetInt(this JObject args, string name) => (int?)args[name];
        public static bool GetBool(this JObject args, string name) => (bool?)args[name] == true;
        public static List<string> GetList(this JObject args, string name) =>
            args[name] is JArray a ? a.Select(x => (string)x!).ToList() : new List<string>();

        // ---------------- help ----------------

        public static string HelpList()
        {
            var sb = new StringBuilder();
            foreach (var c in All)
                sb.AppendLine("  " + c.Synopsis.PadRight(31 - (Width(c.Synopsis) - c.Synopsis.Length)) + c.Summary);
            return sb.ToString();
        }

        /// <summary>终端显示宽度：中文字符占两格。</summary>
        private static int Width(string s) => s.Sum(ch => ch >= 0x2E80 ? 2 : 1);
    }
}
