using System;
using System.Diagnostics;
using System.IO;
using AcadClr.Core;
using AcadClr.Plugin.Engine;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>实时模式：请求来自命名管道，作用于 AutoCAD 当前活动文档。</summary>
    internal static class LiveHost
    {
        private const int TimeoutMs = 120_000;

        public static Response Handle(Request req)
        {
            try { return HandleCore(req); }
            catch (CliError ex) { return new Response { Ok = false, Error = ex.ToInfo() }; }
            catch (TimeoutException ex) { return Response.Fail("timeout", ex.Message); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) { return new Response { Ok = false, Error = Engine.Acad.Translate(ex).ToInfo() }; }
        }

        private static Response HandleCore(Request req)
        {
            switch (req.Kind)
            {
                case "ping":
                    return new Response { Data = new JObject { ["pid"] = Process.GetCurrentProcess().Id } };
                case "status":
                    return MainThread.Invoke(Status, TimeoutMs);
                case "save":
                    return MainThread.Invoke(() => Save(req.SaveAs), TimeoutMs);
                case "run":
                    return MainThread.Invoke(() => Run(req), TimeoutMs);
                case "lisp":
                    if (string.IsNullOrWhiteSpace(req.Code)) return Response.Fail("bad_request", "lisp 缺少代码。");
                    return req.CommandQueue
                        ? LispRunner.ViaCommandQueue(req.Code!, TimeoutMs)
                        : MainThread.Invoke(() => LispRunner.Eval(req.Code!), TimeoutMs);
                case "script":
                    if (string.IsNullOrWhiteSpace(req.Code)) return Response.Fail("bad_request", "script 缺少内容。");
                    return MainThread.Invoke(() => LispRunner.QueueScript(req.Code!), TimeoutMs);
                default:
                    return Response.Fail("bad_request", $"未知请求类型 “{req.Kind}”。");
            }
        }

        private static Response Run(Request req)
        {
            var doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return Response.Fail("no_document", "AutoCAD 当前没有打开的图形。", "先在 AutoCAD 里新建或打开一个 DWG");

            Response resp;
            var exec = new Executor(doc.Database, doc.Name);
            using (doc.LockDocument())
                resp = exec.Run(req);

            if (exec.CommittedChanges)
            {
                try { doc.Editor.Regen(); } catch (Exception) { /* 刷新失败不影响结果 */ }
            }
            return resp;
        }

        private static Response Status()
        {
            var dm = CoreApp.DocumentManager;
            var doc = dm.MdiActiveDocument;
            var data = new JObject
            {
                ["mode"] = "live",
                ["pid"] = Process.GetCurrentProcess().Id,
                ["acadVersion"] = Convert.ToString(CoreApp.GetSystemVariable("ACADVER")),
                ["documents"] = dm.Count,
                ["activeDocument"] = doc?.Name,
            };
            if (doc != null)
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (var kv in Nodes.Document(doc.Database, tr, doc.Name).Props)
                        if (kv.Key != "file") data[kv.Key] = kv.Value;
                    tr.Commit();
                }
            }
            return new Response { Document = doc?.Name, Data = data };
        }

        private static Response Save(string? saveAs)
        {
            var doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return Response.Fail("no_document", "AutoCAD 当前没有打开的图形。");

            var target = saveAs ?? doc.Name;
            if (saveAs == null && !(Path.IsPathRooted(doc.Name) && File.Exists(doc.Name)))
                return Response.Fail("unnamed_document", $"图形 “{doc.Name}” 尚未保存过，没有文件路径。", "用 acadclr save --as D:\\path\\file.dwg");

            target = Path.GetFullPath(target);
            var db = doc.Database;
            using (doc.LockDocument())
                db.SaveAs(target, true, saveAs == null ? db.OriginalFileVersion : DwgVersion.Current, db.SecurityParameters);
            return new Response { Document = target, Saved = true, Data = new JObject { ["file"] = target } };
        }
    }

    /// <summary>
    /// 离线模式：由 accoreconsole 执行命令 ACADCLR_RUN 触发。
    /// 在后台 Database 中直接读写 DWG 文件，不依赖活动文档；结果写到请求指定的响应文件。
    /// </summary>
    internal static class OfflineHost
    {
        public static void RunFile(string requestPath)
        {
            Request? req = null;
            Response resp;
            try
            {
                req = Json.Deserialize<Request>(File.ReadAllText(requestPath));
                resp = Run(req);
            }
            catch (CliError ex) { resp = new Response { Ok = false, Error = ex.ToInfo() }; }
            catch (Exception ex) { resp = Response.Fail("offline_error", ex.GetType().Name + "：" + ex.Message); }

            var outPath = req?.ResponsePath ?? Path.ChangeExtension(requestPath, ".response.json");
            File.WriteAllText(outPath, Json.Serialize(resp));
        }

        private static Response Run(Request req)
        {
            if (string.IsNullOrEmpty(req.Dwg)) return Response.Fail("bad_request", "离线请求缺少 dwg 路径。");
            var path = Path.GetFullPath(req.Dwg);

            if (req.Create)
            {
                if (File.Exists(path)) return Response.Fail("already_exists", $"文件已存在：{path}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            else if (!File.Exists(path))
            {
                return Response.Fail("not_found", $"文件不存在：{path}", $"新建用 acadclr create \"{path}\"");
            }

            using (var db = new Database(req.Create, true))
            {
                if (req.Create)
                {
                    db.Insunits = UnitsValue.Millimeters;
                    db.Measurement = MeasurementValue.Metric;
                }
                else
                {
                    db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db.CloseInput(true);
                    // 后台读入的图形不会自动解析外部参照；涉及 xref 时先解析，状态和绑定才正确
                    if (Json.Serialize(req.Items).IndexOf("xref", StringComparison.OrdinalIgnoreCase) >= 0)
                        db.ResolveXrefs(false, false);
                }

                var previous = HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase = db;
                try
                {
                    var exec = new Executor(db, path);
                    var resp = exec.Run(req);
                    if (req.Create && resp.AtomicRolledBack != true || exec.CommittedChanges)
                    {
                        // 新建的图固定存为 2013 格式（AC1027）：AutoCAD 2013-2024 都能打开；已有文件保持原格式
                        db.SaveAs(path, !req.Create, req.Create ? DwgVersion.AC1027 : db.OriginalFileVersion, db.SecurityParameters);
                        resp.Saved = true;
                    }
                    resp.Document = path;
                    return resp;
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }
    }
}
