using System;
using System.Linq;
using System.Text;
using AcadClr.Core;

namespace AcadClr.Cli
{
    /// <summary>
    /// 把 Response 渲染成终端文本。默认文本便于人读、也便于 grep：
    /// <c>path (type) key=value key=value ...</c>；加 --json 则原样输出结构化结果。
    /// </summary>
    internal static class Output
    {
        public static int Render(Response resp, string verb, bool json, bool offline)
        {
            if (verb == "help" && resp.Data != null)
            {
                // help --json 输出类型的结构化定义；总览、命令、edit、plot 只有文本
                if (json && resp.Data["schema"] is Newtonsoft.Json.Linq.JObject schema) Console.WriteLine(schema.ToString());
                else Console.Write(resp.Data["text"]?.ToString());
                return 0;
            }

            if (json)
            {
                Console.WriteLine(Json.Serialize(resp, true));
                return resp.Error != null && resp.Error.Code != "lisp_error" ? 2 : resp.Ok ? 0 : 1;
            }

            if (resp.Error != null)
            {
                Console.Error.WriteLine($"错误 [{resp.Error.Code}]：{resp.Error.Message}");
                if (resp.Error.Suggestion != null) Console.Error.WriteLine("建议：" + resp.Error.Suggestion);
                return resp.Error.Code == "lisp_error" ? 1 : 2;
            }

            if (verb == "instances" && resp.Data?["instances"] is Newtonsoft.Json.Linq.JArray list)
            {
                if (list.Count == 0) { Console.WriteLine("没有加载了 AutoCADCLR 插件的 AutoCAD 实例。"); return 0; }
                foreach (var i in list.ToObject<System.Collections.Generic.List<InstanceInfo>>()!)
                    Console.WriteLine($"pid={i.Pid}  AutoCAD {i.AcadVersion}  启动于 {i.Started:yyyy-MM-dd HH:mm:ss}  管道 {i.Pipe}");
                if (list.Count > 1) Console.WriteLine("默认连接最近启动的实例；用 --pid 指定其他实例。");
                return 0;
            }

            // 命令式 edit（trim / extend / fillet / chamfer）经 LISP 执行，返回值与 lisp 相同
            if (verb == "lisp" || verb == "edit" && resp.Items == null)
            {
                Console.WriteLine(resp.Data?["value"]?.ToString() ?? "nil");
                if (resp.Saved == true) Console.WriteLine("已保存：" + resp.Document);
                else if (resp.Saved == false) Console.Error.WriteLine("注意：文件未发生变化（代码没有修改图形，或保存失败）。");
                return 0;
            }

            if (verb == "plot" && resp.Data != null)
            {
                var d = resp.Data;
                Console.WriteLine($"已打印：{d["file"]}（{d["sizeKB"]} KB）");
                Console.WriteLine($"布局={d["layout"]}  设备={d["device"]}  纸张={d["paper"]}  范围={d["area"]}  比例={d["scale"]}" +
                                  ((bool?)d["mono"] == true ? "  单色" : ""));
                if (d["skipped"] != null) Console.WriteLine("（该设备 / 范围组合不支持、已跳过的设置：" + d["skipped"] + "）");
                return 0;
            }

            if (verb == "script")
            {
                if (resp.Data?["queued"] != null) { Console.WriteLine("已送入 AutoCAD 命令行（异步执行，不等待结果）。"); return 0; }
                foreach (var f in resp.Data?["results"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    Console.WriteLine($"=== {f["file"]}" + ((bool?)f["saved"] == true ? "（已保存）" : "") + (f["error"] != null ? "  错误：" + f["error"] : ""));
                    if (f["log"] != null) Console.WriteLine(f["log"]);
                }
                return resp.Ok ? 0 : 1;
            }

            if (resp.Data != null && (verb == "status" || verb == "save"))
            {
                if (verb == "save") Console.WriteLine("已保存：" + resp.Data["file"]);
                else foreach (var p in resp.Data.Properties()) Console.WriteLine($"{p.Name}: {p.Value}");
                return 0;
            }

            var items = resp.Items ?? new System.Collections.Generic.List<ItemResult>();
            bool batch = verb == "batch";
            foreach (var r in items) RenderItem(r, batch);

            if (batch)
            {
                Console.WriteLine();
                Console.WriteLine($"批处理完成：{resp.Succeeded} 成功，{resp.Failed} 失败" +
                                  (resp.Skipped > 0 ? $"，{resp.Skipped} 跳过" : "") + $"，共 {items.Count} 条");
            }
            if (batch && resp.AtomicRolledBack == true)
                Console.Error.WriteLine("有操作失败，整批已回滚，图形未被修改（加 --best-effort 可保留成功的部分）。");
            if (resp.Atomic == false && resp.Failed > 0)
                Console.Error.WriteLine("本批含外部参照操作，按逐条方式执行、不支持回滚：成功的操作已生效。");
            if (verb == "create" && resp.Saved == true) Console.WriteLine("已创建：" + resp.Document);
            else if (offline && resp.Saved == true) Console.WriteLine("已写回：" + resp.Document);

            return resp.Ok ? 0 : 1;
        }

        private static void RenderItem(ItemResult r, bool batch)
        {
            var prefix = batch ? $"[{r.Index}] " : "";
            if (r.Status == "skipped") { Console.WriteLine(prefix + "跳过"); return; }
            if (!r.Ok)
            {
                var w = batch ? Console.Out : Console.Error;
                w.WriteLine($"{prefix}错误 [{r.Error?.Code}]：{r.Error?.Message}");
                if (r.Error?.Suggestion != null) w.WriteLine((batch ? "    " : "") + "建议：" + r.Error.Suggestion);
                return;
            }

            switch (r.Op)
            {
                case "add":
                    Console.WriteLine(prefix + "已添加：" + Line(r.Node!));
                    break;
                case "set":
                    if (r.Matched != null)
                    {
                        Console.WriteLine($"{prefix}已修改 {r.Matched} 个元素");
                        foreach (var n in r.Nodes ?? Enumerable.Empty<Node>()) Console.WriteLine("  " + Line(n));
                    }
                    else Console.WriteLine(prefix + "已修改：" + Line(r.Node!));
                    break;
                case "remove":
                    if (r.Path != null) Console.WriteLine(prefix + "已删除：" + r.Path);
                    else
                    {
                        Console.WriteLine($"{prefix}已删除 {r.Nodes?.Count ?? 0} 个元素");
                        foreach (var n in r.Nodes ?? Enumerable.Empty<Node>()) Console.WriteLine("  " + n.Path);
                    }
                    break;
                case "edit":
                    Console.WriteLine($"{prefix}完成，得到 {r.Matched ?? r.Nodes?.Count ?? 0} 个实体" +
                                      (r.Truncated == true ? $"（只显示前 {r.Nodes?.Count} 个）" : ""));
                    foreach (var n in r.Nodes ?? Enumerable.Empty<Node>()) Console.WriteLine(prefix + "  " + Line(n));
                    break;
                case "query":
                    foreach (var n in r.Nodes ?? Enumerable.Empty<Node>()) Console.WriteLine(prefix + Line(n));
                    Console.WriteLine($"{prefix}共 {r.Matched} 个匹配" + (r.Truncated == true ? $"（只显示前 {r.Nodes?.Count} 个，--limit 调整）" : ""));
                    break;
                default:
                    if (r.Node != null) Tree(r.Node, prefix, 0);
                    break;
            }
        }

        private static void Tree(Node n, string prefix, int level)
        {
            Console.WriteLine(prefix + new string(' ', level * 2) + Line(n));
            if (n.Children == null) return;
            foreach (var c in n.Children) Tree(c, prefix, level + 1);
            if (n.ChildCount != null)
                Console.WriteLine(prefix + new string(' ', (level + 1) * 2) + $"……共 {n.ChildCount} 个，只显示前 {n.Children.Count} 个（--limit 调整）");
        }

        public static string Line(Node n)
        {
            var sb = new StringBuilder();
            sb.Append(n.Path).Append(" (").Append(n.Type).Append(')');
            foreach (var kv in n.Props)
            {
                var v = kv.Value;
                if (v.Length == 0 || v.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0) v = "\"" + v.Replace("\"", "\\\"") + "\"";
                sb.Append(' ').Append(kv.Key).Append('=').Append(v);
            }
            return sb.ToString();
        }
    }
}
