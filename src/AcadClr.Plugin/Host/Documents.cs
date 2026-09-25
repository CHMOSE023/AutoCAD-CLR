using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AcadClr.Core;
using Autodesk.AutoCAD.ApplicationServices;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// /documents：AutoCAD 里打开的图形（仅实时模式）。移植自 AutoCADMCP 的 list / activate / open / new / close_document。
    /// 打开、新建、关闭、切换都要在应用程序上下文里做，不能持有文档锁或开着事务，所以不经过 Executor，也不能放进 batch。
    /// 其他命令默认作用于当前文档；带 doc 参数时作用于指定文档，不必先切换。
    /// </summary>
    internal static class Documents
    {
        /// <summary>请求里是否有针对 /documents 的操作。</summary>
        public static bool Targets(BatchItem item)
        {
            var text = (item.Path ?? item.Parent ?? item.Selector ?? "").Trim();
            return text.StartsWith("/document", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("document[", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("document", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Type?.Trim(), "document", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>主线程（应用程序上下文）调用。</summary>
        public static Response Run(Request req)
        {
            if (req.Items.Count != 1)
                throw new CliError("invalid_request", "文档操作（打开、关闭、切换）不能和其他操作放在同一个 batch 里。", "单独执行，例如 acadclr set \"/document[@name=a.dwg]\" --prop current=true");
            var item = req.Items[0];
            var r = new ItemResult { Index = 0, Op = item.Verb };
            switch (item.Verb)
            {
                case "get":
                {
                    var path = (item.Path ?? "/documents").Trim();
                    if (path.Equals("/documents", StringComparison.OrdinalIgnoreCase))
                    {
                        var all = All();
                        r.Node = new Node
                        {
                            Path = "/documents", Type = "documents",
                            Props = { ["count"] = all.Count.ToString(CultureInfo.InvariantCulture), ["active"] = FileName(CoreApp.DocumentManager.MdiActiveDocument) },
                            Children = (item.Depth ?? 1) >= 1 ? all.Select(Node).ToList() : null,
                        };
                    }
                    else r.Node = Node(Find(path));
                    r.Path = r.Node.Path;
                    break;
                }
                case "query":
                {
                    var sel = Selector.Parse(item.Selector ?? item.Path ?? "document");
                    var hits = All().Select(Node).Where(n => sel.Matches(n.Type, a => n.Props.TryGetValue(a, out var v) ? v : null)).ToList();
                    r.Matched = hits.Count;
                    r.Nodes = hits;
                    break;
                }
                case "add":
                    r.Node = Node(Add(item.GetProps()));
                    r.Path = r.Node.Path;
                    break;
                case "set":
                {
                    var doc = Find(item.Path ?? throw new CliError("invalid_request", "set 文档需要路径，例如 /document[@name=a.dwg]。"));
                    foreach (var kv in item.GetProps())
                    {
                        var key = Schema.CheckProp(Schema.FindType("document")!, kv.Key, Verbs.Set).Name;
                        if (key != "current") throw new CliError("unsupported_property", $"/document 只能 set current=true；{key} 请用 set / --doc ...", "例：acadclr set / --doc a.dwg --prop units=mm");
                        if (!Values.Bool(key, kv.Value)) throw new CliError("invalid_value", "current 只能设为 true（切换到该文档）。");
                        if (!ReferenceEquals(CoreApp.DocumentManager.MdiActiveDocument, doc)) CoreApp.DocumentManager.MdiActiveDocument = doc;
                    }
                    r.Node = Node(doc);
                    r.Path = r.Node.Path;
                    break;
                }
                case "remove":
                    r.Path = Close(Find(item.Path ?? throw new CliError("invalid_request", "remove 文档需要路径，例如 /document[@name=a.dwg]。")), item.Force == true);
                    break;
                default:
                    throw new CliError("invalid_request", $"/documents 不支持 {item.Verb}。", "可用：get query add set remove");
            }
            return new Response { Items = new List<ItemResult> { r }, Succeeded = 1, Failed = 0, Document = CoreApp.DocumentManager.MdiActiveDocument?.Name };
        }

        /// <summary>按 doc 参数找文档：文件名、完整路径或序号（从 1 开始）；空为当前文档。</summary>
        public static Document Resolve(string? doc)
        {
            if (string.IsNullOrWhiteSpace(doc))
                return CoreApp.DocumentManager.MdiActiveDocument ?? throw new CliError("no_document", "AutoCAD 当前没有打开的图形。", "先在 AutoCAD 里新建或打开一个 DWG");
            var key = doc!.Trim();
            var all = All();
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
            {
                if (idx < 1 || idx > all.Count) throw new CliError("not_found", $"没有第 {idx} 个文档（共 {all.Count} 个）。", "get /documents 查看");
                return all[idx - 1];
            }
            var hit = all.FirstOrDefault(d => FileName(d).Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                              d.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                              (Path.IsPathRooted(key) && Path.IsPathRooted(d.Name) &&
                                               Path.GetFullPath(d.Name).Equals(Path.GetFullPath(key), StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit;
            var near = Schema.Suggest(key, all.Select(FileName));
            throw new CliError("not_found", $"没有打开的文档 “{key}”。",
                (near != null ? $"是否想用 {near}？" : "") + "已打开：" + string.Join("、", all.Select(FileName)));
        }

        private static Document Find(string path)
        {
            var segs = PathParser.Parse(path);
            var seg = segs.Count == 1 && segs[0].Name == "document" ? segs[0]
                : segs.Count == 2 && segs[0].Name == "documents" && segs[1].Name == "document" ? segs[1]
                : throw new CliError("invalid_path", $"无法识别的文档路径：{path}", "例：/document[@name=a.dwg]、/document[2]");
            if (seg.AttrName == "name") return Resolve(seg.AttrValue);
            if (seg.Index != null) return Resolve((seg.Index == -1 ? All().Count : seg.Index.Value).ToString(CultureInfo.InvariantCulture));
            throw new CliError("invalid_path", $"文档需要用 [@name=...] 或 [N] 定位：{path}");
        }

        public static List<Document> All() => CoreApp.DocumentManager.Cast<Document>().ToList();

        public static string FileName(Document? d) => d == null ? "" : Path.GetFileName(d.Name);

        public static string PathOf(Document d) => $"/document[@name={FileName(d)}]";

        private static Node Node(Document d)
        {
            var active = ReferenceEquals(CoreApp.DocumentManager.MdiActiveDocument, d);
            var p = new Dictionary<string, string>
            {
                ["name"] = FileName(d),
                ["file"] = Path.IsPathRooted(d.Name) ? d.Name : "",
                ["active"] = active ? "true" : "false",
                ["readOnly"] = d.IsReadOnly ? "true" : "false",
            };
            if (Modified(d) is bool m) p["modified"] = m ? "true" : "false";
            return new Node { Path = PathOf(d), Type = "document", Props = p };
        }

        /// <summary>是否有未保存的修改：取 COM 文档对象的 Saved（对非活动文档也有效）；取不到时返回 null。</summary>
        private static bool? Modified(Document d)
        {
            try
            {
                var com = d.GetAcadDocument();
                var saved = com.GetType().InvokeMember("Saved", BindingFlags.GetProperty, null, com, null);
                return saved is bool s ? !s : (bool?)null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>path 打开已有文件（已打开则切换过去），template 或什么都不给则新建。新文档成为当前文档。</summary>
        private static Document Add(List<KeyValuePair<string, string>> props)
        {
            string? path = null, template = null;
            bool readOnly = false;
            foreach (var kv in props)
            {
                switch (kv.Key.ToLowerInvariant())
                {
                    case "path": path = kv.Value.Trim().Trim('"'); break;
                    case "template": template = kv.Value.Trim().Trim('"'); break;
                    case "readonly": readOnly = Values.Bool("readOnly", kv.Value); break;
                    default:
                        throw new CliError("unsupported_property", $"打开 / 新建文档没有属性 “{kv.Key}”。", "可用：path（打开）、template（新建）、readOnly");
                }
            }
            var dm = CoreApp.DocumentManager;
            if (path != null)
            {
                if (!Path.IsPathRooted(path)) throw new CliError("invalid_value", "path 必须是绝对路径。", "例：path=D:\\work\\plan.dwg");
                if (!File.Exists(path)) throw new CliError("not_found", $"文件不存在：{path}");
                var open = All().FirstOrDefault(d => Path.IsPathRooted(d.Name) && Path.GetFullPath(d.Name).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (open != null) { dm.MdiActiveDocument = open; return open; }
                var doc = Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension.Open(dm, path, readOnly);
                dm.MdiActiveDocument = doc;
                return doc;
            }

            var tpl = string.IsNullOrWhiteSpace(template) ? "acadiso.dwt" : template!;
            if (Path.IsPathRooted(tpl) && !File.Exists(tpl)) throw new CliError("not_found", $"样板文件不存在：{tpl}");
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension.Add(dm, tpl);
                dm.MdiActiveDocument = doc;
                return doc;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new CliError("invalid_value", $"新建图形失败（{ex.ErrorStatus}）：样板 “{tpl}” 可能不存在。", "试试 acad.dwt / acadiso.dwt，或给样板的绝对路径");
            }
        }

        /// <summary>关闭文档。有未保存的修改时要 force（丢弃）；要保留修改先 save --doc。不关闭最后一个文档。</summary>
        private static string Close(Document doc, bool force)
        {
            if (All().Count <= 1)
                throw new CliError("invalid_request", "这是最后一个打开的图形，拒绝关闭（AutoCAD 会失去操作目标）。");
            var path = PathOf(doc);
            if (Modified(doc) != false && !force)
                throw new CliError("unsaved_changes", $"{FileName(doc)} 有未保存的修改（或无法确认是否已保存）。",
                    $"先 acadclr save --doc {FileName(doc)} 保存；确认丢弃修改则加 --force");

            // 关的是当前文档时，先切到另一个文档再关，保证任何时刻都有当前文档。
            // 本方法在 Application.Idle 回调里执行；直接关掉当前文档后，到 AutoCAD 激活下一个文档之前没有当前文档，
            // 同一轮 Idle 里排在后面的 AutoCAD 自己的 CommandEditor.SyncUpAllCommandLineState 会抛 NullReferenceException，
            // 弹出“未经处理的异常”窗口（实测间歇出现，取决于 Idle 回调的先后顺序）。
            var dm = CoreApp.DocumentManager;
            if (ReferenceEquals(dm.MdiActiveDocument, doc))
                dm.MdiActiveDocument = All().First(d => !ReferenceEquals(d, doc));
            doc.CloseAndDiscard();
            return path;
        }
    }
}
