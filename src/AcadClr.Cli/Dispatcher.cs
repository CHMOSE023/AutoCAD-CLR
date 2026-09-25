using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AcadClr.Core;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli
{
    /// <summary>一次准备好的调用：发给插件的请求（便于测试比对）+ 执行方式。</summary>
    internal sealed class Call
    {
        public string Command { get; set; } = "";
        public bool Offline { get; set; }

        /// <summary>发给插件（或 accoreconsole）的请求；help、instances 为空。</summary>
        public Request? Request { get; set; }

        public Func<Response> Execute { get; set; } = () => new Response();
    }

    /// <summary>
    /// 命令执行入口：(命令名, JSON 参数) → Response。命令行与 MCP 都走这里，参数名、校验、错误完全一致。
    /// 根据 dwg 参数选择实时（命名管道）或离线（accoreconsole）传输。
    /// </summary>
    internal static class Dispatcher
    {
        private const int OfflineTimeoutSec = 300;

        /// <summary>离线模式使用的 AutoCAD：命令行 --acad，或 acadclr mcp 的启动参数。</summary>
        public static string? AcadHint { get; set; }

        /// <summary>准备并执行；参数错误也作为 Response 返回（MCP 用）。</summary>
        public static Response Dispatch(string command, JObject? args)
        {
            try { return Prepare(command, args).Execute(); }
            catch (CliError ex) { return new Response { Ok = false, Error = ex.ToInfo() }; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return Response.Fail("io_error", ex.GetType().Name + "：" + ex.Message);
            }
        }

        /// <summary>校验参数并构造调用，不连接 AutoCAD。参数有误时抛 <see cref="CliError"/>。</summary>
        public static Call Prepare(string command, JObject? rawArgs)
        {
            var cmd = Commands.Require(command);
            if (cmd.CliOnly) throw new CliError("usage", $"{cmd.Name} 只能在命令行使用。");
            var a = Commands.Normalize(cmd, rawArgs);

            var dwg = cmd.Name == "script" ? null : a.GetString("dwg");
            if (dwg != null && a.GetString("doc") != null)
                throw new CliError("usage", "dwg（离线文件）与 doc（实时模式的文档）不能同时给。");
            int? pid = a.GetInt("pid");
            int offlineTimeout = a.GetInt("timeout") ?? OfflineTimeoutSec;
            int? liveTimeoutMs = a.GetInt("timeout") * 1000;

            Call Live(Request req)
            {
                req.TimeoutMs = liveTimeoutMs;
                req.Doc ??= a.GetString("doc");
                return new Call { Command = cmd.Name, Request = req, Execute = () => LiveTransport.Send(req, pid) };
            }

            Call Offline(Request req)
            {
                req.Dwg = dwg;
                return new Call { Command = cmd.Name, Offline = true, Request = req, Execute = () => OfflineTransport.Send(req, AcadHint, offlineTimeout) };
            }

            Call Run(params BatchItem[] items)
            {
                var req = new Request { Items = items.ToList() };
                return dwg != null ? Offline(req) : Live(req);
            }

            switch (cmd.Name)
            {
                case "status":
                    // 离线没有“当前文档”的概念：返回文件的文档节点
                    return dwg != null ? Run(new BatchItem { Command = "get", Path = "/" }) : Live(new Request { Kind = "status" });

                case "get":
                    return Run(new BatchItem { Command = "get", Path = a.GetString("path"), Depth = a.GetInt("depth"), Limit = a.GetInt("limit") });

                case "query":
                    return Run(new BatchItem { Command = "query", Selector = a.GetString("selector"), Limit = a.GetInt("limit") });

                case "add":
                    return Run(new BatchItem
                    {
                        Command = "add", Parent = a.GetString("parent"), Type = a.GetString("type"), From = a.GetString("from"),
                        Props = (JObject?)a["props"],
                    });

                case "set":
                case "remove":
                {
                    var (path, selector) = Target(cmd, a);
                    return Run(new BatchItem
                    {
                        Command = cmd.Name, Path = path, Selector = selector,
                        Props = cmd.Name == "set" ? (JObject?)a["props"] : null,
                        Force = a.GetBool("force") ? true : (bool?)null,
                    });
                }

                case "stats":
                    return Run(new BatchItem { Command = "stats" });

                case "edit":
                    return Edit(cmd, a, dwg, pid, liveTimeoutMs, offlineTimeout);

                case "measure":
                case "check":
                {
                    var action = Schema.RequireAction(cmd.Name, a.GetString("action"));
                    string? path = a.GetString("path"), selector = a.GetString("selector");
                    if (action.NeedsTarget) (path, selector) = Target(cmd, a);
                    else if (path != null || selector != null)
                        throw new CliError("usage", $"{cmd.Name} {action.Name} 不需要目标。", $"运行 acadclr help {cmd.Name} {action.Name}");
                    action.CheckProps(new BatchItem { Props = (JObject?)a["props"] }.GetProps());
                    return Run(new BatchItem { Command = cmd.Name, Action = action.Name, Path = path, Selector = selector, Props = (JObject?)a["props"] });
                }

                case "view":
                {
                    var action = Schema.RequireAction("view", a.GetString("action"));
                    var props = (JObject?)a["props"];
                    var p = action.CheckProps(new BatchItem { Props = props }.GetProps());
                    var req = new Request
                    {
                        Kind = "view",
                        Items = { new BatchItem { Command = "view", Action = action.Name, Path = a.GetString("path"), Selector = a.GetString("selector"), Props = props } },
                    };
                    var call = Live(req);
                    if (p.TryGetValue("output", out var output))
                    {
                        if (!Path.IsPathRooted(output)) throw new CliError("usage", "output 必须是绝对路径。", "例：output=D:\\out\\view.png");
                        var send = call.Execute;
                        call.Execute = () => SaveImage(send(), output);
                    }
                    return call;
                }

                case "batch":
                {
                    var items = a["items"] as JArray
                        ?? (a.GetString("input") is string input ? ReadBatchFile(input) : null)
                        ?? throw new CliError("usage", "batch 需要 items（操作列表）或 input（JSON 文件）。",
                            "命令行：--input <文件>、--commands '<json>' 或从标准输入传入 JSON");
                    var req = new Request
                    {
                        Items = items.ToObject<List<BatchItem>>() ?? new List<BatchItem>(),
                        BestEffort = a.GetBool("bestEffort"),
                        StopOnError = a.GetBool("stopOnError"),
                    };
                    if (a.GetBool("force")) foreach (var it in req.Items) it.Force ??= true;
                    return dwg != null ? Offline(req) : Live(req);
                }

                case "plot":
                {
                    var props = (JObject?)a["props"];
                    Schema.CheckPlotProps(new BatchItem { Props = props }.GetProps());
                    var layout = Schema.PlotLayoutName(a.GetString("layout"));
                    var req = new Request
                    {
                        Kind = "plot",
                        Items = { new BatchItem { Command = "plot", Path = layout == null ? null : $"/layout[@name={layout}]", Props = props } },
                    };
                    if (dwg == null) return Live(req);
                    req.Dwg = dwg;
                    return new Call { Command = cmd.Name, Offline = true, Request = req, Execute = () => OfflineTransport.Plot(dwg, layout, props, AcadHint, offlineTimeout) };
                }

                case "lisp":
                {
                    var code = a.GetString("code") ?? (a.GetString("file") is string file ? OfflineTransport.ReadText(file) : null)
                        ?? throw new CliError("usage", "lisp 需要代码：acadclr lisp \"(expr)\" 或 acadclr lisp --file x.lsp",
                            "例：acadclr lisp \"(getvar \\\"DWGNAME\\\")\"");
                    bool save = a.GetBool("save");
                    if (dwg != null)
                    {
                        if (a.GetBool("commandQueue")) throw new CliError("usage", "--cmd 只用于实时模式；离线执行本身就在命令上下文中，(command ...) 可直接用。");
                        var req = new Request { Kind = "lisp", Code = code, Dwg = dwg };
                        return new Call { Command = cmd.Name, Offline = true, Request = req, Execute = () => OfflineTransport.Lisp(dwg, code, save, AcadHint, offlineTimeout) };
                    }
                    if (save) throw new CliError("usage", "--save 只用于离线模式；实时模式请在执行后运行 acadclr save。");
                    return Live(new Request { Kind = "lisp", Code = code, CommandQueue = a.GetBool("commandQueue") });
                }

                case "script":
                {
                    string? text = a.GetString("code");
                    if (text == null && a.GetString("file") is string file)
                    {
                        if (!File.Exists(file)) throw new CliError("usage", $"找不到脚本文件：{file}");
                        text = OfflineTransport.ReadText(file);
                    }
                    if (text == null)
                        throw new CliError("usage", "script 需要脚本：acadclr script <file.scr> [--dwg a.dwg ...] 或 acadclr script --text \"_.ZOOM _E\"");
                    var dwgs = a.GetList("dwg");
                    var req = new Request { Kind = "script", Code = text };
                    if (dwgs.Count == 0) return Live(req);
                    bool save = a.GetBool("save");
                    return new Call { Command = cmd.Name, Offline = true, Request = req, Execute = () => OfflineTransport.Script(dwgs, text, save, AcadHint, offlineTimeout) };
                }

                case "save":
                    return Live(new Request { Kind = "save", SaveAs = a.GetString("saveAs") });

                case "mark":
                case "rollback":
                case "undo":
                {
                    var props = new JObject();
                    if (a.GetString("label") is string label) props["label"] = label;
                    if (a.GetInt("steps") is int steps)
                    {
                        if (steps < 1 || steps > 200) throw new CliError("usage", "undo 的步数应在 1-200 之间。");
                        props["steps"] = steps;
                    }
                    return Live(new Request { Kind = "undo", Items = { new BatchItem { Command = cmd.Name, Props = props } } });
                }

                case "create":
                    return Offline(new Request { Create = true });

                case "instances":
                    return new Call
                    {
                        Command = cmd.Name,
                        Execute = () => new Response { Data = new JObject { ["instances"] = JArray.FromObject(LiveTransport.Instances()) } },
                    };

                case "help":
                {
                    var data = Help(a.GetString("topic"));
                    return new Call { Command = cmd.Name, Execute = () => new Response { Data = data } };
                }

                default:
                    throw new CliError("usage", $"命令 {cmd.Name} 尚未实现。");
            }
        }

        /// <summary>把响应里的第一张图写成文件，路径记入 data.file。</summary>
        public static Response SaveImage(Response resp, string file)
        {
            if (resp.Images == null || resp.Images.Count == 0) return resp;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(file, Convert.FromBase64String(resp.Images[0].Data));
            resp.Data ??= new JObject();
            resp.Data["file"] = file;
            return resp;
        }

        /// <summary>set / remove / edit 的目标：path 与 selector 必须给且只给一个。</summary>
        private static (string? path, string? selector) Target(CommandDef cmd, JObject a)
        {
            var path = a.GetString("path");
            var selector = a.GetString("selector");
            if (path == null && selector == null)
                throw new CliError("usage", $"{cmd.Name} 缺少目标：path（路径）或 selector（选择器）。", "用法：acadclr " + cmd.Synopsis);
            if (path != null && selector != null)
                throw new CliError("usage", $"{cmd.Name} 的 path 与 selector 只能给一个。");
            return (path, selector);
        }

        private static Call Edit(CommandDef cmd, JObject a, string? dwg, int? pid, int? liveTimeoutMs, int offlineTimeout)
        {
            var action = Schema.RequireAction("edit", a.GetString("action"));
            var (path, selector) = Target(cmd, a);
            var props = (JObject?)a["props"];

            if (!action.UsesCommand)
            {
                var req = new Request
                {
                    Items =
                    {
                        new BatchItem
                        {
                            Command = "edit", Action = action.Name, Path = path, Selector = selector, Props = props,
                            Force = a.GetBool("force") ? true : (bool?)null,
                        },
                    },
                };
                if (dwg != null)
                {
                    req.Dwg = dwg;
                    return new Call { Command = cmd.Name, Offline = true, Request = req, Execute = () => OfflineTransport.Send(req, AcadHint, offlineTimeout) };
                }
                req.TimeoutMs = liveTimeoutMs;
                return new Call { Command = cmd.Name, Request = req, Execute = () => LiveTransport.Send(req, pid) };
            }

            // 命令式动作（trim / extend / fillet / chamfer）：生成 (vl-cmdf ...)，实时走命令队列，离线由 accoreconsole 打开图纸执行并保存
            var p = action.CheckProps(new BatchItem { Props = props }.GetProps());

            if (a.GetString("doc") != null)
                throw new CliError("usage", $"edit {action.Name} 通过 AutoCAD 命令执行，只能作用于当前文档。",
                    "先切换：acadclr set \"/document[@name=...]\" --prop current=true");
            var code = CommandEdits.Build(action, path ?? selector!, p);
            if (dwg != null)
            {
                var req = new Request { Kind = "lisp", Code = code, Dwg = dwg };
                return new Call
                {
                    Command = cmd.Name, Offline = true, Request = req,
                    Execute = () => CommandEdits.Interpret(OfflineTransport.Lisp(dwg, code, true, AcadHint, offlineTimeout), action.Name),
                };
            }
            var live = new Request { Kind = "lisp", Code = code, CommandQueue = true, TimeoutMs = liveTimeoutMs };
            return new Call { Command = cmd.Name, Request = live, Execute = () => CommandEdits.Interpret(LiveTransport.Send(live, pid), action.Name) };
        }

        private static JArray ReadBatchFile(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            JToken token;
            try { token = JToken.Parse(text); }
            catch (Newtonsoft.Json.JsonException ex) { throw new CliError("usage", $"{path} 不是有效的 JSON：" + ex.Message); }
            return BatchArray(token);
        }

        /// <summary>batch 的操作列表：数组，或 {"items":[...]}。</summary>
        public static JArray BatchArray(JToken token)
        {
            if (token is JObject o && o["items"] is JArray inner) return inner;
            return token as JArray ?? throw new CliError("usage", "batch JSON 必须是数组：[{\"command\":\"add\",...}, ...]");
        }

        /// <summary>help 的内容：text 为终端文本；类型的结构化定义放在 schema。</summary>
        private static JObject Help(string? topic)
        {
            var words = (topic ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new JObject { ["text"] = Schema.HelpOverview() };
            var first = words[0];

            if (first.Equals("plot", StringComparison.OrdinalIgnoreCase)) return new JObject { ["text"] = Schema.HelpPlot() };
            if (Schema.ActionVerbs.FirstOrDefault(v => v.Equals(first, StringComparison.OrdinalIgnoreCase)) is string verb)
            {
                if (words.Length == 1) return new JObject { ["text"] = Schema.HelpVerb(verb) };
                return new JObject { ["text"] = Schema.HelpAction(Schema.RequireAction(verb, words[1])) };
            }
            if (Schema.FindAction(first) is ActionDef direct) return new JObject { ["text"] = Schema.HelpAction(direct) };
            if (Schema.FindType(first) is TypeDef t) return new JObject { ["text"] = Schema.HelpType(t), ["schema"] = Schema.HelpJson(t) };
            if (Commands.Find(first) is CommandDef c) return new JObject { ["text"] = HelpCommand(c) };

            var near = Schema.Suggest(first, Schema.Types.Select(x => x.Name).Concat(Commands.All.Select(x => x.Name)));
            throw new CliError("usage", $"没有类型或命令 “{first}”。",
                (near != null ? $"是否想看 {near}？" : "") + "类型：" + string.Join("、", Schema.Types.Select(x => x.Name)));
        }

        /// <summary>命令的参数表：命令行写法与 JSON / MCP 参数名对照。</summary>
        private static string HelpCommand(CommandDef c)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{c.Name} —— {c.Summary}");
            sb.AppendLine();
            sb.AppendLine("用法：acadclr " + c.Synopsis);
            if (c.Args.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("参数（命令行写法 → JSON / MCP 参数名，* 为必填）：");
                bool routed = c.Args.Any(x => x.Route != null);
                string Cli(ArgDef x)
                {
                    // set / remove / edit 的同一个位置参数按内容分给 path 或 selector
                    if (x.Route != null || routed && x.Name == "selector") return "<path|selector>";
                    if (x.Position < 0) return x.Option ?? "";
                    return $"<{(x.Rest ? x.Name + "…" : x.Name)}>" + (x.Option != null ? " | " + x.Option : "");
                }
                var rows = c.Args.Select(x => (cli: Cli(x), arg: x)).ToList();
                int w = rows.Max(r => r.cli.Length) + 2;
                foreach (var (cli, x) in rows)
                    sb.AppendLine("  " + (x.Required ? "*" : " ") + cli.PadRight(w) + x.Name.PadRight(14) + x.Description +
                                  (x.Default != null ? $"（默认 {x.Default}）" : ""));
            }
            if (c.Batchable)
            {
                sb.AppendLine();
                sb.AppendLine($"batch 中写成：{{\"command\":\"{c.Name}\", ...同名参数}}");
            }
            return sb.ToString();
        }
    }
}
