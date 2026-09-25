using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AcadClr.Core;
using AcadClr.Plugin.Engine;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.PlottingServices;
using Newtonsoft.Json.Linq;
using AcRx = Autodesk.AutoCAD.Runtime;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// 打印到 PDF。移植自 AutoCADMCP 的 plot_pdf，那里实测过的约束全部保留：
    /// <list type="bullet">
    /// <item>PlotEngine 必须在文档上下文运行 → 实际打印放在命令 ACADCLR_PLOT 里。</item>
    /// <item>命令执行期间不能切换布局（eDocumentSwitchDisabled）→ 实时模式在进命令前切好、打完切回；
    ///       离线模式由脚本在调用命令前 (setvar "CTAB" ...)。</item>
    /// <item>PlotEngine 用完必须 Destroy()，只 Dispose 会让之后每次打印都失败。</item>
    /// <item>后台打印会让引擎立刻返回而文件未写完，打印期间关闭 BACKGROUNDPLOT。</item>
    /// <item>图纸单位要先于比例 / 范围设置，否则窗口按 25.4 换算，打出白纸。</item>
    /// <item>window / display / limits 范围在 2014 的 PlotEngine 上打不出内容，直接拒绝。</item>
    /// </list>
    /// </summary>
    internal static class Plotting
    {
        private sealed class Job
        {
            public string Layout = "";
            public Dictionary<string, string> Props = new Dictionary<string, string>();
            public Response? Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static Job? _pending;
        private static readonly object Gate = new object();

        // ---------------- 实时模式（管道线程调用） ----------------

        public static Response Live(Request req, int timeoutMs)
        {
            var item = req.Items.FirstOrDefault() ?? new BatchItem();
            var props = Schema.CheckPlotProps(item.GetProps());
            var doc = MainThread.Invoke(() => CoreApp.DocumentManager.MdiActiveDocument
                ?? throw new CliError("no_document", "AutoCAD 当前没有打开的图形。"), 10_000);

            // 目标布局与原布局都在主线程（应用上下文）里读
            string target = MainThread.Invoke(() =>
            {
                var name = Schema.PlotLayoutName(item.Path) ?? Layouts.CurrentName();
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var id = Layouts.Find(doc.Database, tr, name);
                    if (id.IsNull) throw new CliError("not_found", $"布局 “{name}” 不存在。", "运行 acadclr get /layouts");
                    var n = ((Layout)tr.GetObject(id, OpenMode.ForRead)).LayoutName;
                    tr.Commit();
                    return n;
                }
            }, 10_000);
            string previous = MainThread.Invoke(Layouts.CurrentName, 10_000);

            var job = new Job { Layout = target, Props = props };
            lock (Gate)
            {
                if (_pending != null) return Response.Fail("busy", "已有一个打印任务在进行中，稍后再试。");
                _pending = job;
            }

            bool switched = false;
            try
            {
                // 打印引擎只可靠地打印当前布局；切换必须在命令外（应用上下文）进行
                if (!previous.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    MainThread.Invoke(() => { using (doc.LockDocument()) Layouts.SetCurrent(target); return true; }, 30_000);
                    switched = true;
                }
                // 与 AutoCADMCP 的 MCPPLOT 相同：从管道线程送命令名，activate=false
                doc.SendStringToExecute("ACADCLR_PLOT\n", false, false, false);
                if (!job.Done.Wait(timeoutMs))
                    return Response.Fail("timeout", $"打印 {timeoutMs / 1000} 秒内没有完成。", "AutoCAD 可能正忙、弹出了对话框，或打印驱动弹了窗，切到 AutoCAD 看一眼");
                return job.Result ?? Response.Fail("error", "打印结束但没有返回结果。");
            }
            finally
            {
                if (switched)
                    try { MainThread.Invoke(() => { using (doc.LockDocument()) Layouts.SetCurrent(previous); return true; }, 30_000); }
                    catch (Exception) { /* 切回失败不掩盖打印结果 */ }
                lock (Gate) _pending = null;
            }
        }

        // ---------------- 命令 ACADCLR_PLOT ----------------

        public static void Command()
        {
            var job = _pending;
            if (job != null)
            {
                try { job.Result = Execute(CoreApp.DocumentManager.MdiActiveDocument, job.Layout, job.Props); }
                catch (CliError ex) { job.Result = new Response { Ok = false, Error = ex.ToInfo() }; }
                catch (Exception ex) { job.Result = Response.Fail("plot_failed", ex.Message); }
                finally { job.Done.Set(); }
                return;
            }

            // 离线模式：参数来自请求文件；目标布局已由脚本设为当前（CTAB）
            var ed = CoreApp.DocumentManager.MdiActiveDocument.Editor;
            var pr = ed.GetString(new PromptStringOptions("\n请求文件：") { AllowSpaces = true });
            if (pr.Status != PromptStatus.OK) return;
            var path = pr.StringResult.Trim().Trim('"');
            Request? req = null;
            Response resp;
            try
            {
                req = Json.Deserialize<Request>(File.ReadAllText(path));
                var item = req.Items.FirstOrDefault() ?? new BatchItem();
                var want = Schema.PlotLayoutName(item.Path) ?? Layouts.ModelName;
                var cur = Layouts.CurrentName();
                if (!cur.Equals(want, StringComparison.OrdinalIgnoreCase))
                    throw new CliError("not_found", $"没能切换到布局 “{want}”（当前是 “{cur}”），可能不存在。", "运行 acadclr get <dwg> /layouts 查看");
                resp = Execute(CoreApp.DocumentManager.MdiActiveDocument, cur, Schema.CheckPlotProps(item.GetProps()));
            }
            catch (CliError ex) { resp = new Response { Ok = false, Error = ex.ToInfo() }; }
            catch (Exception ex) { resp = Response.Fail("plot_failed", ex.GetType().Name + "：" + ex.Message); }
            File.WriteAllText(req?.ResponsePath ?? Path.ChangeExtension(path, ".response.json"), Json.Serialize(resp));
        }

        // ---------------- 打印本身（文档上下文） ----------------

        private static Response Execute(Document doc, string layoutName, Dictionary<string, string> p)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new CliError("busy", "AutoCAD 正在打印（或上一次打印未结束），稍后重试。");

            var db = doc.Database;
            string area = (p.TryGetValue("area", out var a) ? a : "layout").Trim().ToLowerInvariant();
            if (area == "window" || area == "display" || area == "limits")
                throw new CliError("unsupported_value", $"area={area} 在 AutoCAD 2014 的打印引擎上打不出内容（AutoCADMCP 已实测排除多种写法）。",
                    "整张图用 area=extents；只出局部：建布局，用视口的 viewCenter / scale / width / height 框定范围，再打印该布局");
            if (area != "layout" && area != "extents") throw new CliError("invalid_value", $"area 只能是 layout 或 extents，收到 “{area}”。");

            string device = PageSetup.EnsureDevice(p.TryGetValue("device", out var dv) ? dv : PageSetup.PdfDevice);
            bool? landscape = p.TryGetValue("landscape", out var ls) ? Values.Bool("landscape", ls) : (bool?)null;
            double? scale = null;
            if (p.TryGetValue("scale", out var sc) && !sc.Trim().Equals("fit", StringComparison.OrdinalIgnoreCase))
                scale = Values.Positive("scale", sc.Trim().StartsWith("1:", StringComparison.Ordinal) ? sc.Trim().Substring(2) : sc);
            bool mono = p.TryGetValue("mono", out var mo) && Values.Bool("mono", mo);
            string output = ResolveOutput(doc, p.TryGetValue("output", out var o) ? o : null, layoutName);

            short bgPlot = Convert.ToInt16(CoreApp.GetSystemVariable("BACKGROUNDPLOT"));
            var notes = new List<string>();
            string usedMedia;
            using (doc.LockDocument())
            {
                if (bgPlot != 0) CoreApp.SetSystemVariable("BACKGROUNDPLOT", (short)0);
                try
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var layoutId = Layouts.Find(db, tr, layoutName);
                        if (layoutId.IsNull) throw new CliError("not_found", $"布局 “{layoutName}” 不存在。");
                        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                        if (layout.ModelType && area == "layout") area = "extents"; // 模型空间没有“图纸”范围
                        if (area == "extents") db.UpdateExt(false);                 // EXTMIN/EXTMAX 可能是陈旧的
                        string layoutMedia = layout.CanonicalMediaName ?? "";       // 换设备后 ps 上的纸张可能被清空，先记下

                        var psv = PlotSettingsValidator.Current;
                        using (var ps = new PlotSettings(layout.ModelType))
                        {
                            ps.CopyFrom(layout);
                            Stage("设置打印设备", () => { psv.SetPlotConfigurationName(ps, device, null); psv.RefreshLists(ps); });

                            bool rotate;
                            if (p.TryGetValue("paper", out var paper))
                            {
                                var picked = PageSetup.MatchMedia(psv, ps, paper, landscape);
                                usedMedia = picked.Name;
                                rotate = picked.Exact && landscape == true;
                            }
                            else
                            {
                                usedMedia = layoutMedia.Length > 0 && PageSetup.MediaAvailable(psv, ps, layoutMedia)
                                    ? layoutMedia : PageSetup.MatchMedia(psv, ps, "A4", landscape).Name;
                                bool isLand = PageSetup.IsLandscape(usedMedia);
                                rotate = landscape.HasValue ? landscape.Value != isLand : PageSetup.LayoutIsLandscape(layout) != isLand;
                            }
                            var media = usedMedia;
                            Stage("设置纸张", () => psv.SetCanonicalMediaName(ps, media));
                            // 图纸单位必须先于范围、比例设置
                            Soft("图纸单位", () => psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters), notes);
                            var type = area == "layout" ? DbPlotType.Layout : DbPlotType.Extents;
                            Stage("设置打印范围", () => psv.SetPlotType(ps, type));
                            if (area != "layout") Soft("居中", () => psv.SetPlotCentered(ps, true), notes);
                            if (scale.HasValue)
                            {
                                var s = scale.Value;
                                Stage("设置打印比例", () => { psv.SetUseStandardScale(ps, false); psv.SetCustomPrintScale(ps, new CustomScale(1.0, s)); });
                            }
                            else Stage("设置打印比例", () => { psv.SetUseStandardScale(ps, true); psv.SetStdScaleType(ps, StdScaleType.ScaleToFit); });
                            Soft("纸张方向", () => psv.SetPlotRotation(ps, rotate ? PlotRotation.Degrees090 : PlotRotation.Degrees000), notes);
                            if (mono)
                            {
                                var sheet = psv.GetPlotStyleSheetList().Cast<string>().FirstOrDefault(s => s.StartsWith("monochrome", StringComparison.OrdinalIgnoreCase));
                                if (sheet != null) psv.SetCurrentStyleSheet(ps, sheet); else notes.Add("没有 monochrome 打印样式表");
                            }

                            var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
                            // 优先 MatchDisabled：纸张已自行选定，不让校验器重新“匹配”（AutoCADMCP 在界面版实测）。
                            // accoreconsole 里校验器会对同一张纸报 NoMatchingMedia，此时退回 MatchEnabled；
                            // AutoCADMCP 担心的是它改掉窗口范围，而 window 范围本来就不支持，layout / extents 不受影响。
                            try { new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchDisabled }.Validate(pi); }
                            catch (AcRx.Exception ex) when (ex.ErrorStatus == AcRx.ErrorStatus.NoMatchingMedia)
                            {
                                Stage("校验打印配置", () => new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(pi));
                                notes.Add("纸张由校验器重新匹配(NoMatchingMedia)");
                            }

                            var pe = PlotFactory.CreatePublishEngine();
                            try
                            {
                                Stage("BeginPlot", () => pe.BeginPlot(null, null));
                                Stage("BeginDocument", () => pe.BeginDocument(pi, doc.Name, null, 1, true, output));
                                Stage("BeginPage", () => pe.BeginPage(new PlotPageInfo(), pi, true, null));
                                Stage("生成图形", () => { pe.BeginGenerateGraphics(null); pe.EndGenerateGraphics(null); });
                                Stage("EndPage", () => pe.EndPage(null));
                                Stage("EndDocument", () => pe.EndDocument(null));
                                Stage("EndPlot", () => pe.EndPlot(null));
                            }
                            finally
                            {
                                // 必须 Destroy：只 Dispose 会把引擎留在占用状态，之后每次打印都失败
                                try { pe.Destroy(); } catch (Exception) { }
                                try { pe.Dispose(); } catch (Exception) { }
                            }
                        }
                        tr.Commit();
                    }
                }
                finally
                {
                    if (bgPlot != 0) try { CoreApp.SetSystemVariable("BACKGROUNDPLOT", bgPlot); } catch (Exception) { }
                }
            }

            var fi = new FileInfo(output);
            if (!fi.Exists) throw new CliError("plot_failed", $"打印结束但没有生成文件：{output}", "检查目录是否可写，或该布局是否为空");
            var data = new JObject
            {
                ["file"] = output,
                ["sizeKB"] = Math.Round(fi.Length / 1024.0, 1),
                ["layout"] = layoutName,
                ["device"] = device,
                ["paper"] = usedMedia,
                ["area"] = area,
                ["scale"] = scale.HasValue ? "1:" + Values.Num(scale.Value) : "fit",
                ["mono"] = mono,
            };
            if (notes.Count > 0) data["skipped"] = string.Join("、", notes);
            return new Response { Document = doc.Name, Data = data };
        }

        /// <summary>打印是十几步链式调用，AutoCAD 的错误码不说是哪一步，给每步加上阶段名。</summary>
        private static void Stage(string name, Action action)
        {
            try { action(); }
            catch (AcRx.Exception ex) { throw new CliError("plot_failed", $"打印阶段【{name}】失败：{ex.ErrorStatus}"); }
        }

        /// <summary>可选设置：个别范围 × 设备组合不接受，跳过也能出图，只在结果里注明。</summary>
        private static void Soft(string name, Action action, List<string> notes)
        {
            try { action(); }
            catch (AcRx.Exception ex) { notes.Add($"{name}({ex.ErrorStatus})"); }
        }

        private static string ResolveOutput(Document doc, string? output, string layoutName)
        {
            string path;
            if (!string.IsNullOrWhiteSpace(output))
            {
                path = output!.Trim().Trim('"');
                if (!Path.HasExtension(path)) path += ".pdf";
                if (!Path.IsPathRooted(path)) throw new CliError("invalid_value", "output 必须是绝对路径。", "例：output=D:\\out\\A3.pdf");
            }
            else
            {
                var baseName = Path.IsPathRooted(doc.Name)
                    ? Path.Combine(Path.GetDirectoryName(doc.Name)!, Path.GetFileNameWithoutExtension(doc.Name))
                    : Path.Combine(Path.GetTempPath(), "acadclr-plot");
                var safe = new string(layoutName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                path = $"{baseName}-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return path;
        }
    }
}
