using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AcadClr.Core;

namespace AcadClr.Cli
{
    /// <summary>
    /// trim / extend / fillet / chamfer：交互式命令，需要文档上下文。沿用 AutoCADMCP 的做法，
    /// 生成 (vl-cmdf ...) 调用，实时模式经命令队列、离线模式由 accoreconsole 打开图纸执行。
    ///
    /// 实体引用写成 句柄 或 句柄@x,y：带拾取点时传 (list 图元名 点)，等价于在该点点选，
    /// 修剪、延伸、倒角都靠这个点判断处理哪一段。也接受 /entity[@handle=..] 形式的路径。
    /// 执行前后会临时修改 TRIMMODE 等系统变量，结束后恢复原值。
    /// </summary>
    internal static class CommandEdits
    {
        private static readonly Regex HandleInPath = new Regex(@"@handle=([0-9A-Fa-f]+)", RegexOptions.Compiled);
        private static readonly Regex BareRef = new Regex(@"^([0-9A-Fa-f]+)(?:@(-?[\d.]+),(-?[\d.]+))?$", RegexOptions.Compiled);

        private sealed class Ref
        {
            public string Handle = "";
            public double[]? Pick;
        }

        public static string Build(ActionDef action, string target, Dictionary<string, string> p)
        {
            var handles = new List<string>();
            Ref One(string s) { var r = Parse(s); handles.Add(r.Handle); return r; }
            List<Ref> Many(string s) => s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => One(x)).ToList();

            string body;
            switch (action.Name)
            {
                case "trim":
                case "extend":
                {
                    var edges = Many(p[action.Name == "trim" ? "edges" : "boundary"]);
                    var targets = Many(target);
                    // AutoCAD 2021 起修剪 / 延伸默认“快速模式”，提示顺序不同，切回标准模式
                    body = Save("TRIMEXTENDMODE", "0", optional: true) +
                           $"(setq #ok (vl-cmdf \"._{action.Name}\" {string.Join(" ", edges.Select(Sel))} \"\" {string.Join(" ", targets.Select(Pick))} \"\"))";
                    break;
                }
                case "fillet":
                {
                    var a = One(target);
                    var b = One(p["with"]);
                    var r = p.TryGetValue("radius", out var rs) ? Values.Double("radius", rs) : 0;
                    if (r < 0) throw new CliError("invalid_value", "radius 不能为负。");
                    body = Save("FILLETRAD", F(r)) + Save("TRIMMODE", "1") +
                           $"(setq #ok (vl-cmdf \"._fillet\" {Pick(a)} {Pick(b)}))";
                    break;
                }
                case "chamfer":
                {
                    var a = One(target);
                    var b = One(p["with"]);
                    var d1 = p.TryGetValue("d1", out var s1) ? Values.Double("d1", s1) : 0;
                    var d2 = p.TryGetValue("d2", out var s2) ? Values.Double("d2", s2) : d1;
                    if (d1 < 0 || d2 < 0) throw new CliError("invalid_value", "倒角距离不能为负。");
                    body = Save("CHAMMODE", "0") + Save("CHAMFERA", F(d1)) + Save("CHAMFERB", F(d2)) + Save("TRIMMODE", "1") +
                           $"(setq #ok (vl-cmdf \"._chamfer\" {Pick(a)} {Pick(b)}))";
                    break;
                }
                default:
                    throw new CliError("invalid_request", $"edit {action.Name} 不是命令式动作。");
            }

            // 先检查句柄都存在；执行命令；恢复系统变量；返回 ok 与新生成的最后一个实体句柄
            var list = string.Join(" ", handles.Distinct().Select(h => "\"" + h + "\""));
            return "(setq #acsv nil #ok nil)" +
                   $"(if (setq #miss (vl-remove-if (function handent) (list {list})))" +
                   " (strcat \"missing:\" (car #miss))" +
                   " (progn (setq #last (entlast)) " + body +
                   " (foreach v #acsv (setvar (car v) (cdr v)))" +
                   " (if #ok (list \"ok\" (if (not (eq #last (entlast))) (cdr (assoc 5 (entget (entlast)))) \"\")) \"failed\")))";
        }

        /// <summary>记下原值后设置系统变量；optional=true 时变量不存在（旧版本）就跳过。</summary>
        private static string Save(string name, string value, bool optional = false)
        {
            var set = $"(progn (setq #acsv (cons (cons \"{name}\" (getvar \"{name}\")) #acsv)) (setvar \"{name}\" {value}))";
            return optional ? $"(if (getvar \"{name}\") {set})" : set;
        }

        /// <summary>对象选择：带拾取点时点选，否则直接给图元名。</summary>
        private static string Sel(Ref r) => r.Pick != null ? Pick(r) : $"(handent \"{r.Handle}\")";

        /// <summary>对象 + 拾取点；没给点时取曲线参数中点，避开两实体的公共端点造成歧义。</summary>
        private static string Pick(Ref r)
        {
            var e = $"(handent \"{r.Handle}\")";
            if (r.Pick != null) return $"(list {e} (list {F(r.Pick[0])} {F(r.Pick[1])} 0.0))";
            return $"(list {e} (vlax-curve-getPointAtParam {e} (/ (+ (vlax-curve-getStartParam {e}) (vlax-curve-getEndParam {e})) 2.0)))";
        }

        private static Ref Parse(string s)
        {
            s = s.Trim();
            var m = HandleInPath.Match(s);
            if (s.StartsWith("/", StringComparison.Ordinal))
            {
                if (!m.Success) throw new CliError("invalid_path", $"“{s}” 需要按句柄定位的路径（…[@handle=...]）或裸句柄。");
                return new Ref { Handle = m.Groups[1].Value.ToUpperInvariant() };
            }
            var b = BareRef.Match(s);
            if (!b.Success) throw new CliError("invalid_path", $"无法识别的实体引用 “{s}”。", "写成 句柄 或 句柄@x,y，例如 8D@13500,2000");
            var r = new Ref { Handle = b.Groups[1].Value.ToUpperInvariant() };
            if (b.Groups[2].Success)
                r.Pick = new[] { double.Parse(b.Groups[2].Value, CultureInfo.InvariantCulture), double.Parse(b.Groups[3].Value, CultureInfo.InvariantCulture) };
            return r;
        }

        private static string F(double d) => d.ToString("0.############", CultureInfo.InvariantCulture);

        /// <summary>把 LISP 的返回值翻译成 Response。</summary>
        public static Response Interpret(Response lisp, string action)
        {
            if (lisp.Error != null) return lisp;
            var v = lisp.Data?["value"]?.ToString() ?? "";
            if (v.StartsWith("\"missing:", StringComparison.Ordinal))
                return Response.Fail("not_found", $"句柄 {v.Substring(9).Trim('"')} 不存在或已删除。", "用 query 重新查找目标");
            if (!v.StartsWith("(\"ok\"", StringComparison.Ordinal))
                return Response.Fail("command_failed", $"{action} 未生效：AutoCAD 拒绝了该操作。",
                    action == "trim" || action == "extend"
                        ? "检查剪切边 / 边界与目标是否相交，拾取点是否落在要处理的那一段上"
                        : "检查两条曲线是否能相交、半径 / 距离是否过大");
            var last = v.Split('"').Skip(3).FirstOrDefault() ?? "";
            lisp.Data!["value"] = last.Length > 0 ? $"完成，新生成的实体之一：{last}" : "完成";
            return lisp;
        }
    }
}
