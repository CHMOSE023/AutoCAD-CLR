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
    /// acadclr 命令行入口：命令行 → (命令, JSON 参数) → <see cref="Dispatcher"/> → 输出。
    /// 退出码：0 成功；1 有操作失败；2 用法错误或无法连接。
    /// </summary>
    internal static class Program
    {
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
            var parsed = CliParser.Parse(argv);
            Dispatcher.AcadHint = parsed.Acad;
            var name = parsed.Command.Name;

            if (name == "config") return ConfigCmd(parsed.Args.GetList("args"));
            if (name == "mcp") return Mcp(parsed.Args);

            // batch 没给 --input / --commands 时从标准输入读
            if (name == "batch" && parsed.Args["items"] == null && parsed.Args["input"] == null && Console.IsInputRedirected)
                parsed.Args["items"] = Dispatcher.BatchArray(CliParser.ParseJson(Console.In.ReadToEnd(), null));

            var call = Dispatcher.Prepare(name, parsed.Args);
            return Output.Render(call.Execute(), name, parsed.Json, call.Offline);
        }

        /// <summary>
        /// acadclr mcp：MCP server。工具与命令一一对应，调用都交给 Dispatcher，与命令行共用参数、校验和日志。
        /// MCP 默认不开放 lisp / script（--allow-lisp 开放）；--read-only 隐藏并拒绝写操作。
        /// </summary>
        private static int Mcp(JObject a)
        {
            var args = Commands.Normalize(Commands.Require("mcp"), a);
            Dispatcher.ReadOnly = args.GetBool("readOnly");
            Dispatcher.AllowLisp = args.GetBool("allowLisp");
            if (!args.GetBool("http"))
            {
                Dispatcher.Source = "mcp-stdio";
                return AcadClr.Cli.Mcp.StdioHost.Run();
            }
            Dispatcher.Source = "mcp-http";
            var token = args.GetString("token") ?? Environment.GetEnvironmentVariable("ACADCLR_MCP_TOKEN");
            return new AcadClr.Cli.Mcp.HttpHost(args.GetInt("port") ?? 7140, token).Run();
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

    /// <summary>
    /// 命令行 → (命令, JSON 参数)。按命令表把位置参数与 --选项 映射成参数名，
    /// 得到的参数与 MCP 调用传入的完全同构，随后都交给 <see cref="Dispatcher"/>。
    /// </summary>
    internal static class CliParser
    {
        internal sealed class Parsed
        {
            public CommandDef Command { get; set; } = null!;
            public JObject Args { get; } = new JObject();
            public bool Json { get; set; }
            public string? Acad { get; set; }
        }

        /// <summary>命令表之外、所有命令都接受的选项。</summary>
        private static readonly string[] GlobalValueOptions = { "--acad" };
        private static readonly string[] GlobalFlagOptions = { "--json", "--help", "-h" };

        /// <summary>命令表里出现过的全部选项。同一选项在各命令里“开关 / 取值”一致，才能在确定命令之前先切分参数。</summary>
        private static readonly Dictionary<string, bool> IsFlag = BuildOptions();

        private static Dictionary<string, bool> BuildOptions()
        {
            var map = new Dictionary<string, bool>();
            foreach (var arg in Commands.All.SelectMany(c => c.Args).Where(x => x.Option != null))
            {
                bool flag = arg.Kind == ArgKind.Boolean;
                if (map.TryGetValue(arg.Option!, out var f) && f != flag)
                    throw new InvalidOperationException($"命令表错误：选项 {arg.Option} 在不同命令中既是开关又取值。");
                map[arg.Option!] = flag;
            }
            foreach (var o in GlobalValueOptions) map[o] = false;
            foreach (var o in GlobalFlagOptions) map[o] = true;
            return map;
        }

        public static Parsed Parse(string[] argv)
        {
            var a = Args.Parse(argv,
                new HashSet<string>(IsFlag.Where(kv => !kv.Value).Select(kv => kv.Key)),
                new HashSet<string>(IsFlag.Where(kv => kv.Value).Select(kv => kv.Key)));
            var result = new Parsed { Json = a.Has("--json"), Acad = a.Get("--acad") };

            // acadclr、acadclr --help、acadclr X --help  →  help
            if (a.Positional.Count == 0 || a.Has("--help") || a.Has("-h"))
            {
                result.Command = Commands.Require("help");
                var topic = a.Positional.Count > 0 && a.Positional[0].Equals("help", StringComparison.OrdinalIgnoreCase)
                    ? a.Positional.Skip(1).ToList() : a.Positional.Take(1).ToList();
                if (topic.Count > 0) result.Args["topic"] = string.Join(" ", topic);
                return result;
            }

            var cmd = Commands.Require(a.Positional[0]);
            result.Command = cmd;
            var pos = a.Positional.Skip(1).ToList();
            CheckOptions(cmd, a);

            var dwgArg = cmd.Find("dwg");
            var listValues = new List<string>();

            // 便捷写法：acadclr get plan.dwg /model  等价于  acadclr get /model --dwg plan.dwg
            if (dwgArg != null && dwgArg.Position < 0 && pos.Count > 0 && IsDwg(pos[0]) && a.Get("--dwg") == null)
            {
                result.Args["dwg"] = pos[0];
                pos.RemoveAt(0);
            }
            else if (dwgArg?.Kind == ArgKind.StringList)
            {
                // script：位置参数里的 .dwg 都是要处理的图纸，其余的才是脚本文件（acadclr script a.dwg b.dwg --text "..."）
                listValues.AddRange(pos.Where(IsDwg));
                pos.RemoveAll(IsDwg);
            }

            // 位置参数
            foreach (var def in cmd.Args.Where(x => x.Position >= 0 && x.Position < pos.Count))
            {
                if (!def.Rest)
                {
                    var value = pos[def.Position];
                    if (def.Kind == ArgKind.Integer)
                        result.Args[def.Name] = int.TryParse(value, out int n) ? n
                            : throw new CliError("usage", $"{cmd.Name} 的 {def.Name} 需要整数，收到 “{value}”。", "用法：acadclr " + cmd.Synopsis);
                    else result.Args[def.Route?.Invoke(value) ?? def.Name] = value;
                }
                else if (def.Kind == ArgKind.StringList) listValues.AddRange(pos.Skip(def.Position));
                else result.Args[def.Name] = string.Join(" ", pos.Skip(def.Position));
            }
            int maxPos = cmd.Args.Where(x => x.Position >= 0).Select(x => x.Rest ? int.MaxValue : x.Position + 1).DefaultIfEmpty(0).Max();
            if (pos.Count > maxPos)
                throw new CliError("usage", $"多余的参数：{string.Join(" ", pos.Skip(maxPos))}。", "用法：acadclr " + cmd.Synopsis + "（参数含空格时加引号）");

            // 选项
            foreach (var def in cmd.Args.Where(x => x.Option != null))
            {
                var opt = def.Option!;
                switch (def.Kind)
                {
                    case ArgKind.Boolean:
                        if (a.Has(opt)) result.Args[def.Name] = true;
                        break;
                    case ArgKind.Integer:
                        if (a.GetInt(opt) is int i) result.Args[def.Name] = i;
                        break;
                    case ArgKind.Object:
                        if (PropsOf(a.GetAll(opt)) is JObject o) result.Args[def.Name] = o;
                        break;
                    case ArgKind.Array:
                        if (a.Get(opt) is string json) result.Args[def.Name] = ParseJson(json, opt);
                        break;
                    case ArgKind.StringList:
                        listValues.InsertRange(0, a.GetAll(opt));
                        break;
                    default:
                        if (a.Get(opt) is string v) result.Args[def.Name] = v;
                        break;
                }
            }
            var listArg = cmd.Args.FirstOrDefault(x => x.Kind == ArgKind.StringList);
            if (listArg != null && listValues.Count > 0) result.Args[listArg.Name] = new JArray(listValues);
            return result;
        }

        private static bool IsDwg(string s) => s.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase);

        /// <summary>用到的选项必须属于该命令（或是全局选项）。</summary>
        private static void CheckOptions(CommandDef cmd, Args a)
        {
            foreach (var opt in a.UsedOptions)
            {
                if (GlobalValueOptions.Contains(opt) || GlobalFlagOptions.Contains(opt) || cmd.Args.Any(x => x.Option == opt)) continue;
                string? hint = null;
                if (opt == "--dwg") hint = $"{cmd.Name} 只用于实时模式" + (cmd.Name == "save" ? "；离线模式每次修改后已自动写回文件，无需 save。" : "。");
                else if (opt == "--pid") hint = $"{cmd.Name} 只用于离线模式。";
                var owners = Commands.All.Where(c => c.Args.Any(x => x.Option == opt)).Select(c => c.Name).ToList();
                throw new CliError("usage", $"{cmd.Name} 不支持 {opt}。",
                    (hint ?? (owners.Count > 0 ? $"{opt} 用于：{string.Join("、", owners)}。" : "")) + $"运行 acadclr help {cmd.Name}");
            }
        }

        private static JObject? PropsOf(List<string> list)
        {
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

        /// <summary>解析命令行传入的 JSON；<paramref name="option"/> 非空时提示 Windows PowerShell 5.1 吞引号的问题。</summary>
        public static JToken ParseJson(string text, string? option)
        {
            try { return JToken.Parse(text); }
            catch (Newtonsoft.Json.JsonException ex)
            {
                // Windows PowerShell 5.1 给外部程序传参时会吞掉参数里的双引号：{"op":"add"} 变成 {op:add}
                bool quotesStripped = option != null && text.IndexOf('"') < 0 && text.IndexOf(':') >= 0;
                throw new CliError("usage", "JSON 解析失败：" + ex.Message,
                    quotesStripped
                        ? "参数里的双引号丢失了：Windows PowerShell 5.1 会吞掉传给外部程序的双引号。改用 --input <文件>、标准输入，或使用 PowerShell 7"
                        : null);
            }
        }
    }

    /// <summary>极简参数解析：位置参数 + 选项（值选项可重复，如多个 --prop）。</summary>
    internal sealed class Args
    {
        public List<string> Positional { get; } = new List<string>();
        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>();
        private readonly HashSet<string> _flags = new HashSet<string>();

        public IEnumerable<string> UsedOptions => _values.Keys.Concat(_flags);

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
