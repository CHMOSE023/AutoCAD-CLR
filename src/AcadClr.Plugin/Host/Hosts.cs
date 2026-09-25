using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AcadClr.Core;
using AcadClr.Plugin.Engine;
using AcadClr.Plugin.Safety;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>实时模式：请求来自命名管道，作用于 AutoCAD 当前活动文档。</summary>
    internal static class LiveHost
    {
        private const int DefaultTimeoutMs = 120_000;
        private const int PlotTimeoutMs = 180_000;

        /// <summary>
        /// 在管道线程上调用。<paramref name="ct"/> 在调用方断开时取消，
        /// 取消时抛出 <see cref="OperationCanceledException"/>，由调用方丢弃结果。
        /// </summary>
        public static Response Handle(Request req, CancellationToken ct)
        {
            try { return HandleCore(req, ct); }
            catch (CliError ex) { return new Response { Ok = false, Error = ex.ToInfo() }; }
            catch (TimeoutException ex) { return Response.Fail("timeout", ex.Message); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) { return new Response { Ok = false, Error = Engine.Acad.Translate(ex).ToInfo() }; }
        }

        private static Response HandleCore(Request req, CancellationToken ct)
        {
            int timeout = req.TimeoutMs is int t && t > 0 ? t : DefaultTimeoutMs;
            if (req.Kind != "ping" && Guard.Check(req) is Response denied) return denied;

            // 写操作前：该文档本次会话第一次被写时，先备份磁盘上的文件（打开 / 关闭文档本身不备份）
            string? backup = null;
            if (Commands.IsWrite(req) && !req.Items.Any(Documents.Targets))
                backup = MainThread.Invoke(() => Guard.BackupOnce(Documents.Resolve(req.Doc)), 10_000, ct);

            var resp = Dispatch(req, timeout, ct);
            if (backup != null) resp.Backup = backup;
            return resp;
        }

        private static Response Dispatch(Request req, int timeout, CancellationToken ct)
        {
            switch (req.Kind)
            {
                case "ping":
                    return new Response { Data = new JObject { ["pid"] = Process.GetCurrentProcess().Id } };
                case "status":
                    return MainThread.Invoke(() => Status(req.Doc), timeout, ct);
                case "save":
                    return MainThread.Invoke(() => Save(req.Doc, req.SaveAs), timeout, ct);
                case "run":
                    // 打开 / 关闭 / 切换文档要在应用程序上下文里做，不能持有文档锁或开着事务
                    if (req.Items.Any(Documents.Targets)) return MainThread.Invoke(() => Documents.Run(req), timeout, ct);
                    return MainThread.Invoke(() => Run(req), timeout, ct);
                case "lisp":
                    if (string.IsNullOrWhiteSpace(req.Code)) return Response.Fail("bad_request", "lisp 缺少代码。");
                    return req.CommandQueue
                        ? LispRunner.ViaCommandQueue(req.Code!, timeout, ct)
                        : MainThread.Invoke(() => LispRunner.Eval(req.Code!), timeout, ct);
                case "plot":
                    // 请求线程：内部轮询等待命令完成，不能占用主线程。
                    // 打印命令一旦送出就无法撤回，不响应取消，否则会在打印中途切回布局
                    return Plotting.Live(req, req.TimeoutMs is int pt && pt > 0 ? pt : PlotTimeoutMs);
                case "undo":
                    // 请求线程：内部等待命令队列执行完，不能占用主线程
                    return UndoMarks.Run(req, timeout, ct);
                case "cmdedit":
                {
                    // trim / extend / fillet / chamfer：插件按参数自己生成 (vl-cmdf ...)，不接受外部传来的代码，
                    // 所以关闭 LISP 开关时这类编辑照样可用
                    var item = req.Items.FirstOrDefault() ?? throw new CliError("bad_request", "cmdedit 请求缺少内容。");
                    var action = Schema.RequireAction("edit", item.Action);
                    if (!action.UsesCommand) throw new CliError("bad_request", $"edit {action.Name} 不是命令式动作。");
                    var target = item.Path ?? item.Selector ?? throw new CliError("invalid_request", $"edit {action.Name} 缺少目标。");
                    var code = CommandEdits.Build(action, target, action.CheckProps(item.GetProps()));
                    return CommandEdits.Interpret(LispRunner.ViaCommandQueue(code, timeout, ct), action.Name);
                }
                case "view":
                    return MainThread.Invoke(() =>
                    {
                        var doc = CoreApp.DocumentManager.MdiActiveDocument
                            ?? throw new CliError("no_document", "AutoCAD 当前没有打开的图形。");
                        return Views.Run(doc, req.Items.FirstOrDefault() ?? new BatchItem(), t => new Executor(doc.Database, doc.Name).ResolveEntities(t));
                    }, timeout, ct);
                case "script":
                    if (string.IsNullOrWhiteSpace(req.Code)) return Response.Fail("bad_request", "script 缺少内容。");
                    return MainThread.Invoke(() => LispRunner.QueueScript(req.Code!), timeout, ct);
                default:
                    return Response.Fail("bad_request", $"未知请求类型 “{req.Kind}”。");
            }
        }

        private static Response Run(Request req)
        {
            var doc = Documents.Resolve(req.Doc);

            Response resp;
            var exec = new Executor(doc.Database, doc.Name);
            // 有修改时带命令名加锁：这次请求的修改在 AutoCAD 里成为一个独立的撤销步（名为 ACADCLR），
            // acadclr undo 1 / 用户按 Ctrl+Z 正好撤销上一次操作；不带命令名时修改不单独成组，UNDO 1 撤不到它。
            // 只读请求不加锁（应用程序上下文里读数据库不需要锁）：实测任何文档锁都会留下一个撤销步，
            // 查询之后按一次 Ctrl+Z 就会撤了个空
            bool mutating = req.Items.Any(Commands.IsWrite);
            if (!mutating) resp = exec.Run(req);
            else using (doc.LockDocument(DocumentLockMode.Write, "ACADCLR", "ACADCLR", false)) resp = exec.Run(req);

            if (exec.CommittedChanges)
            {
                try { doc.Editor.Regen(); } catch (Exception) { /* 刷新失败不影响结果 */ }
            }
            return resp;
        }

        private static Response Status(string? docName)
        {
            var dm = CoreApp.DocumentManager;
            var doc = docName == null ? dm.MdiActiveDocument : Documents.Resolve(docName);
            var data = new JObject
            {
                ["mode"] = "live",
                ["pid"] = Process.GetCurrentProcess().Id,
                ["acadVersion"] = Convert.ToString(CoreApp.GetSystemVariable("ACADVER")),
                ["documents"] = dm.Count,
                ["activeDocument"] = doc?.Name,
                ["readOnly"] = Guard.ReadOnly,
                ["allowLisp"] = Guard.AllowLisp,
            };
            var backups = Guard.BackupList().ToList();
            if (backups.Count > 0) data["backups"] = new JArray(backups);
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

        private static Response Save(string? docName, string? saveAs)
        {
            var doc = Documents.Resolve(docName);

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
            if (req.UseDocument) return RunOnDocument(req, path);

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

        /// <summary>文档模式：在 accoreconsole 以 /i 打开的文档上执行；保存交给脚本（按原格式 SAVEAS）。</summary>
        private static Response RunOnDocument(Request req, string path)
        {
            var doc = CoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !string.Equals(Path.GetFullPath(doc.Name), path, StringComparison.OrdinalIgnoreCase))
                return Response.Fail("open_failed", $"accoreconsole 当前打开的不是 {path}（而是 {doc?.Name}）。");

            var exec = new Executor(doc.Database, path);
            var resp = exec.Run(req);
            resp.Document = path;
            if (exec.CommittedChanges && req.ResponsePath != null)
            {
                File.WriteAllText(req.ResponsePath + ".save", "1");
                resp.Saved = true; // 由脚本随后另存；CLI 会核对文件是否真的更新
            }
            return resp;
        }
    }
}
