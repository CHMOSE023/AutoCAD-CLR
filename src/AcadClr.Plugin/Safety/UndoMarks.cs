using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using AcadClr.Core;
using AcadClr.Plugin.Host;
using Autodesk.AutoCAD.ApplicationServices;
using Newtonsoft.Json.Linq;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Safety
{
    /// <summary>
    /// mark / rollback / undo（仅实时模式，作用于当前文档）。移植自 AutoCADMCP，对应 AutoCAD 的 UNDO Mark / Back / N。
    /// 经命令队列执行 (command "_.UNDO" ...) 并等待完成，而不是像 AutoCADMCP 那样发出就返回。
    /// UNDO Back 在没有标记时会弹“撤销全部”的确认并卡在提示上，所以按文档记下打过的标记，没有标记时拒绝 rollback。
    /// 标记栈只记录经本插件打的标记；用户在 AutoCAD 里手工 UNDO 越过标记后，栈会与实际不符。
    /// </summary>
    internal static class UndoMarks
    {
        private static readonly Dictionary<Document, List<string>> Marks = new Dictionary<Document, List<string>>();
        private static readonly object Gate = new object();

        /// <summary>请求线程调用（内部等待命令队列，不能占用主线程）。</summary>
        public static Response Run(Request req, int timeoutMs, CancellationToken ct)
        {
            var item = req.Items.FirstOrDefault() ?? throw new CliError("bad_request", "undo 请求缺少内容。");
            var doc = MainThread.Invoke(() => CoreApp.DocumentManager.MdiActiveDocument
                ?? throw new CliError("no_document", "AutoCAD 当前没有打开的图形。"), 10_000, ct);
            var props = item.GetProps().ToDictionary(kv => kv.Key, kv => kv.Value);
            List<string> stack;
            lock (Gate)
            {
                if (!Marks.TryGetValue(doc, out stack)) Marks[doc] = stack = new List<string>();
            }

            string code, done;
            string? command = null;
            switch (item.Verb)
            {
                case "mark":
                    code = "(command \"_.UNDO\" \"_Mark\")";
                    done = "已打标记";
                    break;
                case "rollback":
                    lock (Gate)
                        if (stack.Count == 0)
                            throw new CliError("no_mark", "本插件没有在当前文档打过标记，拒绝 rollback（UNDO Back 会撤销全部并弹出确认）。",
                                "先 acadclr mark 再做要试的修改；只撤最近几步用 acadclr undo N");
                    code = "(command \"_.UNDO\" \"_Back\")";
                    done = "已回滚到标记";
                    break;
                default:
                    int steps = props.TryGetValue("steps", out var s) ? Values.Int("steps", s) : 1;
                    if (steps < 1 || steps > 200) throw new CliError("invalid_value", "steps 应在 1-200 之间。");
                    // UNDO N 必须作为独立命令、在主线程送进命令行（实测放在 LISP 的 (command ...) 里，
                    // 或从请求线程发送，都撤不到插件的修改）；随后的 LISP 只用来确认它已执行完
                    command = $"_.UNDO\n{steps}\n";
                    code = "(princ)";
                    done = $"已撤销 {steps} 步";
                    break;
            }

            if (command != null) MainThread.Invoke(() => { doc.SendStringToExecute(command, false, false, false); return true; }, 10_000, ct);
            var resp = LispRunner.ViaCommandQueue(code, timeoutMs, ct);
            if (!resp.Ok) return resp;

            string? label = null;
            lock (Gate)
            {
                if (item.Verb == "mark")
                {
                    label = props.TryGetValue("label", out var l) && l.Trim().Length > 0 ? l.Trim() : "#" + (stack.Count + 1).ToString(CultureInfo.InvariantCulture);
                    stack.Add(label);
                }
                else if (item.Verb == "rollback")
                {
                    label = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (stack.Count > 0)
                {
                    // UNDO N 可能越过标记，标记栈已无法确定：清空，避免之后的 rollback 回到意料之外的位置
                    stack.Clear();
                    done += "（标记已失效）";
                }
            }
            MainThread.Wake();
            return new Response
            {
                Document = doc.Name,
                Data = new JObject
                {
                    ["result"] = label != null ? $"{done} {label}" : done,
                    ["marks"] = new JArray(stack.ToArray()),
                },
            };
        }
    }
}
