using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AcadClr.Core
{
    /// <summary>
    /// 路径的一段：<c>name</c>、<c>name[3]</c>、<c>name[last()]</c>、<c>name[@attr=value]</c>。
    /// </summary>
    public sealed class PathSegment
    {
        public string Name { get; set; } = "";

        /// <summary>1-based 位置；-1 表示 last()。</summary>
        public int? Index { get; set; }

        public string? AttrName { get; set; }
        public string? AttrValue { get; set; }

        public override string ToString()
        {
            if (Index.HasValue) return Name + "[" + (Index == -1 ? "last()" : Index.Value.ToString(CultureInfo.InvariantCulture)) + "]";
            if (AttrName != null) return Name + "[@" + AttrName + "=" + AttrValue + "]";
            return Name;
        }
    }

    /// <summary>
    /// 路径语法（1-based，与 OfficeCLI 一致）：
    /// <code>
    /// /                                文档
    /// /model                           模型空间
    /// /model/line[3]                   模型空间第 3 条直线
    /// /model/entity[@handle=2A3]       按句柄定位（最稳定，增删不会漂移）
    /// /entity[@handle=2A3]             同上，简写
    /// /layers                          图层表
    /// /layer[@name=WALL]               按名称定位图层
    /// </code>
    /// </summary>
    public static class PathParser
    {
        public static bool IsPath(string s) => s.StartsWith("/", StringComparison.Ordinal);

        public static List<PathSegment> Parse(string path)
        {
            if (!IsPath(path))
                throw new CliError("invalid_path", $"路径必须以 / 开头：{path}", "例如 /model/line[1] 或 /entity[@handle=2A3]");

            var segs = new List<PathSegment>();
            var sb = new StringBuilder();
            int depth = 0;
            // 跳过开头的 /，按不在方括号内的 / 切分
            for (int i = 1; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '[') depth++;
                else if (c == ']') depth = Math.Max(0, depth - 1);

                if (c == '/' && depth == 0)
                {
                    if (sb.Length > 0) segs.Add(ParseSegment(sb.ToString(), path));
                    sb.Clear();
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) segs.Add(ParseSegment(sb.ToString(), path));
            return segs;
        }

        private static PathSegment ParseSegment(string text, string whole)
        {
            text = text.Trim();
            int lb = text.IndexOf('[');
            if (lb < 0) return new PathSegment { Name = text.ToLowerInvariant() };

            if (!text.EndsWith("]", StringComparison.Ordinal))
                throw new CliError("invalid_path", $"路径片段缺少 ]：{text}（完整路径 {whole}）");

            var seg = new PathSegment { Name = text.Substring(0, lb).Trim().ToLowerInvariant() };
            var inner = text.Substring(lb + 1, text.Length - lb - 2).Trim();

            if (inner.Equals("last()", StringComparison.OrdinalIgnoreCase))
            {
                seg.Index = -1;
            }
            else if (inner.StartsWith("@", StringComparison.Ordinal))
            {
                int eq = inner.IndexOf('=');
                if (eq < 0) throw new CliError("invalid_path", $"属性定位缺少 =：{text}", "写法：[@handle=2A3] 或 [@name=WALL]");
                seg.AttrName = inner.Substring(1, eq - 1).Trim().ToLowerInvariant();
                seg.AttrValue = Unquote(inner.Substring(eq + 1).Trim());
            }
            else if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) && idx >= 1)
            {
                seg.Index = idx;
            }
            else
            {
                throw new CliError("invalid_path", $"无法识别的定位 [{inner}]（路径 {whole}）",
                    "支持 [N]（从 1 开始）、[last()]、[@handle=...]、[@name=...]");
            }
            return seg;
        }

        internal static string Unquote(string s)
        {
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }

    public sealed class Condition
    {
        public string Attr { get; set; } = "";
        /// <summary>= != &gt; &lt; &gt;= &lt;= ~=</summary>
        public string Op { get; set; } = "=";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// CSS 风格选择器：<c>line[layer=WALL][length&gt;=3000]</c>、<c>entity[color=1]</c>、<c>layer[frozen=true]</c>。
    /// 多个 [] 之间是“与”；<c>~=</c> 为不区分大小写的包含匹配。数字比较在两边都能解析为数字时进行。
    /// </summary>
    public sealed class Selector
    {
        public string Type { get; set; } = "entity";
        public List<Condition> Conditions { get; } = new List<Condition>();

        private static readonly string[] Ops = { ">=", "<=", "!=", "~=", "=", ">", "<" };

        public static Selector Parse(string text)
        {
            var s = text.Trim();
            if (s.Length == 0) throw new CliError("invalid_selector", "选择器为空。");

            var sel = new Selector();
            int lb = s.IndexOf('[');
            var type = (lb < 0 ? s : s.Substring(0, lb)).Trim().ToLowerInvariant();
            sel.Type = type == "*" || type.Length == 0 ? "entity" : type;

            int i = lb < 0 ? s.Length : lb;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                if (s[i] != '[') throw new CliError("invalid_selector", $"选择器语法错误（位置 {i}）：{text}");
                int rb = s.IndexOf(']', i);
                if (rb < 0) throw new CliError("invalid_selector", $"选择器缺少 ]：{text}");
                sel.Conditions.Add(ParseCondition(s.Substring(i + 1, rb - i - 1), text));
                i = rb + 1;
            }
            return sel;
        }

        private static Condition ParseCondition(string body, string whole)
        {
            foreach (var op in Ops)
            {
                int p = body.IndexOf(op, StringComparison.Ordinal);
                if (p > 0)
                {
                    return new Condition
                    {
                        Attr = body.Substring(0, p).Trim().TrimStart('@').ToLowerInvariant(),
                        Op = op,
                        Value = PathParser.Unquote(body.Substring(p + op.Length).Trim()),
                    };
                }
            }
            throw new CliError("invalid_selector", $"条件缺少比较运算符：[{body}]（选择器 {whole}）",
                "支持 = != > < >= <= ~=，例如 [layer=WALL]、[radius>=500]");
        }

        public bool MatchesType(string nodeType) =>
            Type == "entity" || string.Equals(Type, nodeType, StringComparison.OrdinalIgnoreCase);

        /// <summary>getProp 返回 null 表示元素没有该属性，此时除 != 外均不匹配。</summary>
        public bool Matches(string nodeType, Func<string, string?> getProp)
        {
            if (!MatchesType(nodeType)) return false;
            foreach (var c in Conditions)
            {
                bool ok = c.Attr == "inside" || c.Attr == "crossing" ? TestWindow(c, getProp("bbox")) : Test(c, getProp(c.Attr));
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// 窗口条件（对应 AutoCADMCP 的 select 工具的 window / crossing）：按包围盒判断，
        /// [inside=x1,y1;x2,y2] 完全在窗口内，[crossing=x1,y1;x2,y2] 与窗口相交（含完全在内）。没有包围盒的元素不匹配。
        /// </summary>
        private static bool TestWindow(Condition c, string? bbox)
        {
            if (c.Op != "=") throw new CliError("invalid_selector", $"[{c.Attr}] 只支持 =，写成 [{c.Attr}=x1,y1;x2,y2]。");
            if (c.Value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Length != 2)
                throw new CliError("invalid_selector", $"[{c.Attr}] 需要窗口的两个角点 x1,y1;x2,y2，收到 “{c.Value}”。");
            var w = Values.Points(c.Attr, c.Value);
            if (string.IsNullOrEmpty(bbox)) return false;
            var b = Values.Points("bbox", bbox!);
            if (b.Count != 2) return false;
            double wx1 = Math.Min(w[0][0], w[1][0]), wx2 = Math.Max(w[0][0], w[1][0]);
            double wy1 = Math.Min(w[0][1], w[1][1]), wy2 = Math.Max(w[0][1], w[1][1]);
            double bx1 = b[0][0], by1 = b[0][1], bx2 = b[1][0], by2 = b[1][1];
            return c.Attr == "inside"
                ? bx1 >= wx1 && bx2 <= wx2 && by1 >= wy1 && by2 <= wy2
                : bx1 <= wx2 && bx2 >= wx1 && by1 <= wy2 && by2 >= wy1;
        }

        private static bool Test(Condition c, string? actual)
        {
            if (actual == null) return c.Op == "!=";

            if ((c.Op == ">" || c.Op == "<" || c.Op == ">=" || c.Op == "<=" || c.Op == "=" || c.Op == "!=")
                && Values.TryDouble(actual, out double a) && Values.TryDouble(c.Value, out double b))
            {
                const double eps = 1e-9;
                switch (c.Op)
                {
                    case "=": return Math.Abs(a - b) <= eps * Math.Max(1, Math.Abs(b));
                    case "!=": return Math.Abs(a - b) > eps * Math.Max(1, Math.Abs(b));
                    case ">": return a > b;
                    case "<": return a < b;
                    case ">=": return a >= b - eps;
                    case "<=": return a <= b + eps;
                }
            }

            switch (c.Op)
            {
                case "=": return string.Equals(actual, c.Value, StringComparison.OrdinalIgnoreCase);
                case "!=": return !string.Equals(actual, c.Value, StringComparison.OrdinalIgnoreCase);
                case "~=": return actual.IndexOf(c.Value, StringComparison.OrdinalIgnoreCase) >= 0;
                default:
                    // 非数字的大小比较：按字符串序
                    int cmp = string.Compare(actual, c.Value, StringComparison.OrdinalIgnoreCase);
                    return c.Op == ">" ? cmp > 0 : c.Op == "<" ? cmp < 0 : c.Op == ">=" ? cmp >= 0 : cmp <= 0;
            }
        }
    }

    /// <summary>面向调用方的错误：带机器可读的 code 和修正建议。</summary>
    public sealed class CliError : Exception
    {
        public string Code { get; }
        public string? Suggestion { get; }

        public CliError(string code, string message, string? suggestion = null) : base(message)
        {
            Code = code;
            Suggestion = suggestion;
        }

        public ErrorInfo ToInfo() => new ErrorInfo { Code = Code, Message = Message, Suggestion = Suggestion };
    }
}
