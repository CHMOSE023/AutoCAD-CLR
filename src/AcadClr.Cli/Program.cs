using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AcadClr.Core;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli
{
    /// <summary>
    /// acadclr 命令行入口。
    /// 退出码：0 成功；1 有操作失败；2 用法错误或无法连接。
    /// </summary>
    internal static class Program
    {
        private static readonly HashSet<string> ValueOptions = new HashSet<string>
        {
            "--prop", "--type", "--from", "--depth", "--limit", "--dwg", "--acad", "--pid",
            "--input", "--commands", "--as", "--timeout", "--selector", "--file", "--text",
        };

        private static readonly HashSet<string> FlagOptions = new HashSet<string>
        {
            "--json", "--best-effort", "--stop-on-error", "--force", "--help", "-h", "--cmd", "--save",
        };

        private static int Main(string[] argv)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                return Run(argv);
            }
            catch (CliError ex)
            {
                return Usage(ex.Message, ex.Suggestion);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return Usage(ex.GetType().Name + "：" + ex.Message, null);
            }
        }

        private static int Run(string[] argv)
        {
            var a = Args.Parse(argv, ValueOptions, FlagOptions);
            bool json = a.Has("--json");

            if (a.Positional.Count == 0 || a.Has("--help") || a.Has("-h"))
            {
                if (a.Positional.Count == 0) { Console.Write(Schema.HelpOverview()); return 0; }
                return Help(a.Positional[0] == "help" ? a.Positional.Skip(1).ToList() : a.Positional.Take(1).ToList(), json);
            }

            var verb = a.Positional[0].ToLowerInvariant();
            var pos = a.Positional.Skip(1).ToList();

            // 便捷写法：acadclr get plan.dwg /model  等价于  acadclr get /model --dwg plan.dwg
            var dwg = a.Get("--dwg");
            if (dwg == null && pos.Count > 0 && pos[0].EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) && verb != "create")
            {
                dwg = pos[0];
                pos.RemoveAt(0);
            }
            if (a.GetInt("--timeout") is int timeoutSec) LiveTransport.TimeoutMs = timeoutSec * 1000;

            var req = new Request
            {
                BestEffort = a.Has("--best-effort"),
                StopOnError = a.Has("--stop-on-error"),
            };

            switch (verb)
            {
                case "help":
                    return Help(pos, json);

                case "instances":
                    return Instances(json);

                case "config":
                    return ConfigCmd(pos);

                case "plot":
                {
                    var props = PropsOf(a);
                    foreach (var kv in props?.Properties() ?? Enumerable.Empty<JProperty>())
                        if (!Schema.PlotProps.Any(p => p.Name.Equals(kv.Name, StringComparison.OrdinalIgnoreCase)))
                            throw new CliError("usage", $"plot 没有属性 “{kv.Name}”。",
                                (Schema.Suggest(kv.Name, Schema.PlotProps.Select(p => p.Name)) is string n ? $"是否想用 {n}？" : "") + "运行 acadclr help plot");
                    var plotTarget = pos.FirstOrDefault();
                    // 布局可以写成名字或路径 /layout[@name=A3]
                    string? layoutName = plotTarget;
                    if (plotTarget != null && plotTarget.StartsWith("/", StringComparison.Ordinal))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(plotTarget, @"@name=([^\]]+)\]");
                        layoutName = m.Success ? m.Groups[1].Value
                            : plotTarget.Equals("/model", StringComparison.OrdinalIgnoreCase) ? "Model"
                            : throw new CliError("usage", $"plot 的目标应为布局：{plotTarget}", "例：/layout[@name=A3] 或直接写布局名");
                    }
                    var plotResp = dwg != null
                        ? OfflineTransport.Plot(dwg, layoutName, props, a.Get("--acad"), a.GetInt("--timeout") ?? 300)
                        : LiveTransport.Send(new Request
                          {
                              Kind = "plot",
                              Items = { new BatchItem { Command = "plot", Path = layoutName == null ? null : $"/layout[@name={layoutName}]", Props = props } },
                          }, a.GetInt("--pid"));
                    return Output.Render(plotResp, "plot", json, dwg != null);
                }

                case "lisp":
                {
                    var file = a.Get("--file");
                    var code = file != null ? OfflineTransport.ReadText(file) : pos.FirstOrDefault()
                        ?? throw new CliError("usage", "用法：acadclr lisp \"(expr)\" 或 acadclr lisp --file x.lsp",
                            "例：acadclr lisp \"(getvar \\\"DWGNAME\\\")\"");
                    Response lr;
                    if (dwg != null)
                    {
                        if (a.Has("--cmd")) throw new CliError("usage", "--cmd 只用于实时模式；离线执行本身就在命令上下文中，(command ...) 可直接用。");
                        lr = OfflineTransport.Lisp(dwg, code, a.Has("--save"), a.Get("--acad"), a.GetInt("--timeout") ?? 300);
                    }
                    else
                    {
                        if (a.Has("--save")) throw new CliError("usage", "--save 只用于离线模式；实时模式请在执行后运行 acadclr save。");
                        lr = LiveTransport.Send(new Request { Kind = "lisp", Code = code, CommandQueue = a.Has("--cmd") }, a.GetInt("--pid"));
                    }
                    return Output.Render(lr, "lisp", json, dwg != null);
                }

                case "script":
                {
                    var text = a.Get("--text") ?? (pos.Count > 0 && File.Exists(pos[0]) ? OfflineTransport.ReadText(pos[0]) : null)
                        ?? throw new CliError("usage", "用法：acadclr script <file.scr> [--dwg a.dwg ...] 或 acadclr script --text \"_.ZOOM _E\"",
                            pos.Count > 0 ? $"找不到脚本文件：{pos[0]}" : null);
                    // acadclr script fix.scr a.dwg b.dwg  等价于  --dwg a.dwg --dwg b.dwg
                    var dwgs = a.GetAll("--dwg").Concat(pos.Skip(1).Where(p => p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))).ToList();
                    if (dwg != null && !dwgs.Contains(dwg)) dwgs.Add(dwg);
                    Response sr = dwgs.Count > 0
                        ? OfflineTransport.Script(dwgs, text, a.Has("--save"), a.Get("--acad"), a.GetInt("--timeout") ?? 300)
                        : LiveTransport.Send(new Request { Kind = "script", Code = text }, a.GetInt("--pid"));
                    return Output.Render(sr, "script", json, dwgs.Count > 0);
                }

                case "status":
                    req.Kind = "status";
                    if (dwg != null)
                    {
                        // 离线没有“当前文档”的概念：返回文件的文档节点
                        req.Kind = "run";
                        req.Items.Add(new BatchItem { Command = "get", Path = "/" });
                    }
                    break;

                case "save":
                    if (dwg != null) throw new CliError("usage", "离线模式每次修改后已自动写回文件，无需 save。");
                    req.Kind = "save";
                    req.SaveAs = a.Get("--as");
                    break;

                case "create":
                    if (pos.Count < 1) throw new CliError("usage", "用法：acadclr create <file.dwg>");
                    dwg = pos[0];
                    req.Create = true;
                    break;

                case "get":
                    req.Items.Add(new BatchItem
                    {
                        Command = "get", Path = pos.FirstOrDefault() ?? "/",
                        Depth = a.GetInt("--depth"), Limit = a.GetInt("--limit"),
                    });
                    break;

                case "query":
                    req.Items.Add(new BatchItem
                    {
                        Command = "query",
                        Selector = a.Get("--selector") ?? pos.FirstOrDefault() ?? throw new CliError("usage", "用法：acadclr query <selector>", "例：acadclr query \"line[layer=WALL]\""),
                        Limit = a.GetInt("--limit"),
                    });
                    break;

                case "add":
                    req.Items.Add(new BatchItem
                    {
                        Command = "add", Parent = pos.FirstOrDefault() ?? "/model",
                        Type = a.Get("--type"), From = a.Get("--from"), Props = PropsOf(a),
                    });
                    break;

                case "set":
                case "remove":
                    if (pos.Count < 1) throw new CliError("usage", $"用法：acadclr {verb} <path|selector>");
                    var target = pos[0];
                    bool isPath = target.StartsWith("/", StringComparison.Ordinal) || target.StartsWith("$", StringComparison.Ordinal);
                    req.Items.Add(new BatchItem
                    {
                        Command = verb,
                        Path = isPath ? target : null,
                        Selector = isPath ? null : target,
                        Props = verb == "set" ? PropsOf(a) : null,
                        Force = a.Has("--force") ? true : (bool?)null,
                    });
                    break;

                case "stats":
                    req.Items.Add(new BatchItem { Command = "stats" });
                    break;

                case "edit":
                {
                    if (pos.Count < 2) throw new CliError("usage", "用法：acadclr edit <动作> <目标> [--prop k=v ...]", "运行 acadclr help edit 查看全部动作");
                    var action = Schema.FindAction(pos[0]) ?? throw new CliError("usage", $"未知的 edit 动作 “{pos[0]}”。",
                        (Schema.Suggest(pos[0], Schema.Actions.Select(x => x.Name)) is string n ? $"是否想用 {n}？" : "") +
                        "可用：" + string.Join("、", Schema.Actions.Select(x => x.Name)));
                    var editTarget = pos[1];

                    if (action.UsesCommand)
                    {
                        var props = new Dictionary<string, string>();
                        foreach (var kv in PropsOf(a)?.Properties() ?? Enumerable.Empty<JProperty>())
                            props[action.CheckProp(kv.Name).Name] = kv.Value.ToString();
                        var miss = action.Props.Where(p => p.Required && !props.ContainsKey(p.Name)).Select(p => p.Name).ToList();
                        if (miss.Count > 0) throw new CliError("usage", $"edit {action.Name} 缺少属性：{string.Join("、", miss)}。", $"运行 acadclr help edit {action.Name}");

                        var code = CommandEdits.Build(action, editTarget, props);
                        var cmdResp = dwg != null
                            ? OfflineTransport.Lisp(dwg, code, true, a.Get("--acad"), a.GetInt("--timeout") ?? 300)
                            : LiveTransport.Send(new Request { Kind = "lisp", Code = code, CommandQueue = true }, a.GetInt("--pid"));
                        return Output.Render(CommandEdits.Interpret(cmdResp, action.Name), "lisp", json, dwg != null);
                    }

                    bool isPathOrList = editTarget.StartsWith("/", StringComparison.Ordinal) || editTarget.StartsWith("$", StringComparison.Ordinal) ||
                                        editTarget.Contains(";") || editTarget.All(Uri.IsHexDigit);
                    req.Items.Add(new BatchItem
                    {
                        Command = "edit", Action = action.Name,
                        Path = isPathOrList ? editTarget : null,
                        Selector = isPathOrList ? null : editTarget,
                        Props = PropsOf(a),
                        Force = a.Has("--force") ? true : (bool?)null,
                    });
                    break;
                }

                case "batch":
                    req.Items = ReadBatch(a);
                    if (a.Has("--force")) foreach (var it in req.Items) it.Force ??= true;
                    break;

                default:
                    var near = Schema.Suggest(verb, new[] { "status", "get", "query", "add", "set", "remove", "batch", "stats", "save", "create", "instances", "lisp", "script", "config", "edit", "plot", "help" });
                    throw new CliError("usage", $"未知命令 “{verb}”。", (near != null ? $"是否想用 {near}？" : "") + "运行 acadclr help 查看全部命令");
            }

            Response resp;
            if (dwg != null)
            {
                req.Dwg = dwg;
                resp = OfflineTransport.Send(req, a.Get("--acad"), a.GetInt("--timeout") ?? 300);
            }
            else
            {
                resp = LiveTransport.Send(req, a.GetInt("--pid"));
            }

            return Output.Render(resp, verb, json, dwg != null);
        }

        private static JObject? PropsOf(Args a)
        {
            var list = a.GetAll("--prop");
            if (list.Count == 0) return null;
            var o = new JObject();
            foreach (var kv in list)
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) throw new CliError("usage", $"--prop 需要 key=value 形式，收到 “{kv}”。");
                o[kv.Substring(0, eq).Trim()] = kv.Substring(eq + 1);
            }
            return o;
        }

        private static List<BatchItem> ReadBatch(Args a)
        {
            string text;
            var input = a.Get("--input");
            var commands = a.Get("--commands");
            if (commands != null) text = commands;
            else if (input != null) text = File.ReadAllText(input, Encoding.UTF8);
            else if (Console.IsInputRedirected) text = Console.In.ReadToEnd();
            else throw new CliError("usage", "batch 需要 --input <文件>、--commands '<json>' 或从标准输入传入 JSON。");

            JToken token;
            try { token = JToken.Parse(text); }
            catch (Newtonsoft.Json.JsonException ex)
            {
                // Windows PowerShell 5.1 给外部程序传参时会吞掉参数里的双引号：{"op":"add"} 变成 {op:add}
                bool quotesStripped = commands != null && commands.IndexOf('"') < 0 && commands.IndexOf(':') >= 0;
                throw new CliError("usage", "batch JSON 解析失败：" + ex.Message,
                    quotesStripped
                        ? "参数里的双引号丢失了：Windows PowerShell 5.1 会吞掉传给外部程序的双引号。改用 --input <文件>、标准输入，或使用 PowerShell 7"
                        : null);
            }

            if (token is JObject o && o["items"] is JArray inner) token = inner;
            if (!(token is JArray arr)) throw new CliError("usage", "batch JSON 必须是数组：[{\"command\":\"add\",...}, ...]");
            return arr.ToObject<List<BatchItem>>() ?? new List<BatchItem>();
        }

        private static int Help(List<string> pos, bool json)
        {
            if (pos.Count == 0) { Console.Write(Schema.HelpOverview()); return 0; }
            if (pos[0].Equals("plot", StringComparison.OrdinalIgnoreCase)) { Console.Write(Schema.HelpPlot()); return 0; }
            if (pos[0].Equals("edit", StringComparison.OrdinalIgnoreCase))
            {
                if (pos.Count == 1) { Console.Write(Schema.HelpEdit()); return 0; }
                var act = Schema.FindAction(pos[1]) ?? throw new CliError("usage", $"没有 edit 动作 “{pos[1]}”。",
                    "可用：" + string.Join("、", Schema.Actions.Select(x => x.Name)));
                Console.Write(Schema.HelpAction(act));
                return 0;
            }
            if (Schema.FindAction(pos[0]) is ActionDef direct) { Console.Write(Schema.HelpAction(direct)); return 0; }
            var t = Schema.FindType(pos[0]);
            if (t == null)
            {
                var near = Schema.Suggest(pos[0], Schema.Types.Select(x => x.Name));
                throw new CliError("usage", $"没有类型 “{pos[0]}”。",
                    (near != null ? $"是否想看 {near}？" : "") + "可用：" + string.Join("、", Schema.Types.Select(x => x.Name)));
            }
            Console.Write(json ? Schema.HelpJson(t).ToString() + Environment.NewLine : Schema.HelpType(t));
            return 0;
        }

        private static int Instances(bool json)
        {
            var list = LiveTransport.Instances();
            if (json) { Console.WriteLine(Json.Serialize(list, true)); return 0; }
            if (list.Count == 0) { Console.WriteLine("没有加载了 AutoCADCLR 插件的 AutoCAD 实例。"); return 0; }
            foreach (var i in list)
                Console.WriteLine($"pid={i.Pid}  AutoCAD {i.AcadVersion}  启动于 {i.Started:yyyy-MM-dd HH:mm:ss}  管道 {i.Pipe}");
            if (list.Count > 1) Console.WriteLine("默认连接最近启动的实例；用 --pid 指定其他实例。");
            return 0;
        }

        /// <summary>
        /// acadclr config                 查看配置与已安装的 AutoCAD
        /// acadclr config acad 2014       离线模式默认使用 AutoCAD 2014（auto 恢复为自动选择）
        /// </summary>
        private static int ConfigCmd(List<string> pos)
        {
            if (pos.Count == 0)
            {
                Console.WriteLine("配置文件：" + Config.Location);
                foreach (var k in Config.Keys) Console.WriteLine($"{k} = {Config.Get(k) ?? "（未设置，自动选择）"}");
                var chosen = OfflineTransport.FindConsole(null, out var why);
                Console.WriteLine("离线模式当前使用：" + (chosen ?? "无 —— " + why));
                Console.WriteLine("已安装：" + string.Join("、", OfflineTransport.Candidates().Select(c => c.year.ToString())));
                return 0;
            }

            var key = pos[0];
            if (!Config.Keys.Contains(key))
                throw new CliError("usage", $"未知配置项 “{key}”。", "可用：" + string.Join("、", Config.Keys));
            if (pos.Count < 2) { Console.WriteLine(Config.Get(key) ?? "（未设置）"); return 0; }

            var value = pos[1];
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                Config.Set(key, null);
                Console.WriteLine("已清除，离线模式将自动选择 AutoCAD 版本。");
                return 0;
            }
            var exe = OfflineTransport.FindConsole(value, out var err) ?? throw new CliError("usage", err);
            Config.Set(key, value);
            Console.WriteLine($"已设置：离线模式默认使用 {exe}");
            return 0;
        }

        private static int Usage(string message, string? suggestion)
        {
            Console.Error.WriteLine("错误：" + message);
            if (suggestion != null) Console.Error.WriteLine("建议：" + suggestion);
            return 2;
        }
    }

    /// <summary>极简参数解析：位置参数 + 选项（值选项可重复，如多个 --prop）。</summary>
    internal sealed class Args
    {
        public List<string> Positional { get; } = new List<string>();
        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>();
        private readonly HashSet<string> _flags = new HashSet<string>();

        public static Args Parse(string[] argv, HashSet<string> valueOpts, HashSet<string> flagOpts)
        {
            var a = new Args();
            for (int i = 0; i < argv.Length; i++)
            {
                var s = argv[i];
                if (s.StartsWith("--", StringComparison.Ordinal) && s.Contains("="))
                {
                    // --prop=key=value 形式
                    int eq = s.IndexOf('=');
                    var name = s.Substring(0, eq);
                    if (valueOpts.Contains(name)) { a.Add(name, s.Substring(eq + 1)); continue; }
                }
                if (valueOpts.Contains(s))
                {
                    if (i + 1 >= argv.Length) throw new CliError("usage", $"{s} 缺少值。");
                    a.Add(s, argv[++i]);
                }
                else if (flagOpts.Contains(s)) a._flags.Add(s);
                else if (s.StartsWith("--", StringComparison.Ordinal))
                {
                    var near = Schema.Suggest(s, valueOpts.Concat(flagOpts));
                    throw new CliError("usage", $"未知选项 {s}。", near != null ? $"是否想用 {near}？" : "运行 acadclr help 查看全部选项");
                }
                else a.Positional.Add(s);
            }
            return a;
        }

        private void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out var list)) _values[name] = list = new List<string>();
            list.Add(value);
        }

        public bool Has(string flag) => _flags.Contains(flag);
        public string? Get(string name) => _values.TryGetValue(name, out var l) ? l.Last() : null;
        public List<string> GetAll(string name) => _values.TryGetValue(name, out var l) ? l : new List<string>();

        public int? GetInt(string name)
        {
            var v = Get(name);
            if (v == null) return null;
            if (int.TryParse(v, out int i)) return i;
            throw new CliError("usage", $"{name} 需要整数，收到 “{v}”。");
        }
    }
}
