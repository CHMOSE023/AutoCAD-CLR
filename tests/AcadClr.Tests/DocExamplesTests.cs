using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AcadClr.Cli;
using AcadClr.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AcadClr.Tests
{
    /// <summary>
    /// 迁移文档里的每条示例命令都要能通过命令行解析与参数校验，属性名也要存在：
    /// 改了命令表或 Schema 而文档没跟上时，这里会失败。
    /// </summary>
    public class DocExamplesTests
    {
        public static IEnumerable<object[]> Examples()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "migrate-from-autocad-mcp.md"))) dir = dir.Parent;
            if (dir == null) throw new FileNotFoundException("找不到 docs/migrate-from-autocad-mcp.md");
            var text = File.ReadAllText(Path.Combine(dir.FullName, "docs", "migrate-from-autocad-mcp.md"), Encoding.UTF8);
            text = text.Substring(text.IndexOf("## 对照表", StringComparison.Ordinal)); // 前面“读法”一节的第二列是 MCP 写法

            // 对照表的第二列里，以命令名开头的代码片段；含 … 的是示意写法，跳过
            foreach (var line in text.Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal)))
            {
                var cols = line.Split(new[] { " | " }, StringSplitOptions.None);
                if (cols.Length < 2) continue;
                foreach (Match m in Regex.Matches(cols[1], "`([^`]+)`"))
                {
                    var code = m.Groups[1].Value;
                    var first = code.Split(' ')[0];
                    if (Commands.Find(first) == null || code.Contains("…")) continue;
                    yield return new object[] { code };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Examples))]
        public void 示例命令有效(string code)
        {
            var parsed = CliParser.Parse(Split(code));
            var call = Dispatcher.Prepare(parsed.Command.Name, parsed.Args);
            foreach (var item in call.Request?.Items ?? new List<BatchItem>())
                CheckProps(item);
        }

        /// <summary>add 按 --type 核对属性；set 按路径推断类型，推断不出（实体句柄）时属性至少要在某个类型里可 set。</summary>
        private static void CheckProps(BatchItem item)
        {
            var props = item.GetProps();
            if (props.Count == 0) return;
            if (item.Verb == "add" && item.Type != null)
            {
                var type = Schema.FindType(item.Type) ?? throw new Exception($"没有类型 {item.Type}");
                foreach (var kv in props) Schema.CheckProp(type, kv.Key, Verbs.Add);
            }
            else if (item.Verb == "set")
            {
                var type = TypeOfTarget(item.Path ?? item.Selector ?? "");
                foreach (var kv in props)
                {
                    if (type != null) Schema.CheckProp(type, kv.Key, Verbs.Set);
                    else Assert.True(Schema.Types.Any(t => t.Find(kv.Key) is PropDef p && (p.Verbs & Verbs.Set) != 0), $"没有任何类型可 set 属性 {kv.Key}");
                }
            }
        }

        private static TypeDef? TypeOfTarget(string target)
        {
            if (target == "/") return Schema.FindType("document");
            var m = Regex.Match(target, @"^/?([a-z]+)\[");
            return m.Success && m.Groups[1].Value != "entity" ? Schema.FindType(m.Groups[1].Value) : null;
        }

        /// <summary>按 shell 规则切分：双引号内的空格不切，\" 是字面引号。</summary>
        private static string[] Split(string code)
        {
            var args = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '\\' && i + 1 < code.Length && code[i + 1] == '"') { sb.Append('"'); i++; any = true; }
                else if (c == '"') { quoted = !quoted; any = true; }
                else if (c == ' ' && !quoted) { if (any) args.Add(sb.ToString()); sb.Clear(); any = false; }
                else { sb.Append(c); any = true; }
            }
            if (any) args.Add(sb.ToString());
            return args.ToArray();
        }
    }
}
