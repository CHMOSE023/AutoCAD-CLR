using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 在一个 <see cref="Database"/> 上执行一批操作。实时模式与离线模式共用。
    ///
    /// 事务模型：整批一个外层事务，每条操作一个嵌套事务。
    /// 单条失败只回滚它自己；整批结束后，原子模式（默认）下只要有失败就放弃外层事务，
    /// 图形回到执行前的状态；--best-effort 则提交成功的部分。
    /// </summary>
    internal sealed class Executor
    {
        private const int DefaultListLimit = 200;
        private const int RemoveGuard = 30;

        private static readonly HashSet<string> MutatingVerbs =
            new HashSet<string> { "add", "set", "remove", "edit" };

        private readonly Database _db;
        private readonly string? _file;

        /// <summary>不受事务管理的修改（系统变量、数据库头变量）的恢复动作；原子批处理整批放弃时倒序执行。</summary>
        private readonly List<Action> _compensate = new List<Action>();
        private Transaction _tr = null!;

        /// <summary>本次执行是否提交了修改（离线模式据此决定是否写回文件）。</summary>
        public bool CommittedChanges { get; private set; }

        public Executor(Database db, string? file)
        {
            _db = db;
            _file = file;
        }

        /// <summary>解析实体目标（路径、句柄列表或选择器），供 view 等不走批处理的动作使用。</summary>
        public List<ObjectId> ResolveEntities(string text)
        {
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                _tr = tr;
                var ids = TargetEntities(text, new List<ItemResult>(), true, new ItemResult());
                tr.Commit();
                if (ids.Count == 0) throw new CliError("not_found", $"目标 “{text}” 没有匹配任何实体。");
                return ids;
            }
        }

        public Response Run(Request req) =>
            req.Items.Any(i => IsDirect(i, req.Items, 0)) ? RunSequential(req) : RunAtomic(req);

        private static readonly HashSet<string> DirectAddTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xref", "layout", "viewport" };

        /// <summary>“/layout[@name=X]” 或 “/layouts/layout[...]” 本身（不含其下的实体路径）。</summary>
        private static readonly System.Text.RegularExpressions.Regex LayoutItself =
            new System.Text.RegularExpressions.Regex(@"^/(layouts/)?layout\[[^\]]*\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// 数据库级操作不能放进事务：外部参照（附着 / 重载 / 卸载 / 绑定 / 拆离）、
        /// 布局（新建 / 删除 / 重命名 / 切换）、新建视口（要临时切换当前布局）。
        /// 含这类操作的批次改为逐条执行：普通操作各自一个事务立即提交，失败不回滚已成功的部分。
        /// </summary>
        private static bool IsDirect(BatchItem item, List<BatchItem> all, int depth)
        {
            if (item.Verb == "add") return item.From == null && DirectAddTypes.Contains(item.Type?.Trim() ?? "");
            if (item.Verb != "set" && item.Verb != "remove") return false;

            var target = (item.Path ?? item.Selector ?? "").Trim();
            if (target.StartsWith("$", StringComparison.Ordinal) && depth < 8 &&
                int.TryParse(target.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) &&
                k >= 0 && k < all.Count)
                return all[k].Verb == "add" && (all[k].Type ?? "").Trim().ToLowerInvariant() != "viewport" && IsDirect(all[k], all, depth + 1);
            return target.StartsWith("/xref[", StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith("xref", StringComparison.OrdinalIgnoreCase) ||
                   LayoutItself.IsMatch(target) ||
                   target.StartsWith("layout[", StringComparison.OrdinalIgnoreCase);
        }

        private Response RunSequential(Request req)
        {
            var results = new List<ItemResult>();
            var tm = _db.TransactionManager;
            bool stop = false;

            for (int i = 0; i < req.Items.Count; i++)
            {
                var item = req.Items[i];
                if (stop)
                {
                    results.Add(new ItemResult { Index = i, Op = item.Verb, Status = "skipped" });
                    continue;
                }

                var r = new ItemResult { Index = i, Op = item.Verb };
                try
                {
                    if (IsDirect(item, req.Items, 0)) ExecuteDirect(item, r, results);
                    else
                    {
                        using (var tr = tm.StartTransaction())
                        {
                            _tr = tr;
                            Execute(item, r, results);
                            tr.Commit();
                        }
                    }
                }
                catch (CliError ex) { Fail(r, ex.ToInfo()); }
                catch (AcRx.Exception ex) { Fail(r, Acad.Translate(ex).ToInfo()); }
                catch (Exception ex) { Fail(r, new ErrorInfo { Code = "error", Message = ex.Message }); }

                results.Add(r);
                if (r.Ok && MutatingVerbs.Contains(r.Op)) CommittedChanges = true;
                if (!r.Ok && req.StopOnError) stop = true;
            }

            int failed = results.Count(r => r.Status == "failed");
            return new Response
            {
                Items = results,
                Succeeded = results.Count(r => r.Ok),
                Failed = failed,
                Skipped = results.Count(r => r.Status == "skipped"),
                Ok = failed == 0,
                Document = _file,
                Atomic = false,
            };
        }

        /// <summary>在没有打开事务的情况下执行外部参照、布局、新建视口等数据库级操作。</summary>
        private void ExecuteDirect(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            if (item.Verb == "add")
            {
                var type = (item.Type ?? "").Trim().ToLowerInvariant();
                var parentPath = Ref(item.Parent ?? item.Path ?? (type == "xref" ? "/xrefs" : "/layouts"), prior, "parent");
                switch (type)
                {
                    case "xref":
                        if (!parentPath.Equals("/xrefs", StringComparison.OrdinalIgnoreCase) && parentPath != "/")
                            throw new CliError("invalid_path", "外部参照的父路径应为 /xrefs。");
                        Done(r, Xrefs.Attach(_db, item.GetProps()));
                        return;
                    case "layout":
                        if (!parentPath.Equals("/layouts", StringComparison.OrdinalIgnoreCase) && parentPath != "/")
                            throw new CliError("invalid_path", "布局的父路径应为 /layouts。");
                        Done(r, Layouts.Create(_db, item.GetProps()));
                        return;
                    default: // viewport
                        Target parent;
                        using (var tr = _db.TransactionManager.StartTransaction())
                        {
                            _tr = tr;
                            parent = Resolve(parentPath);
                            tr.Commit();
                        }
                        if (parent.Kind != TargetKind.Layout)
                            throw new CliError("invalid_path", "视口的父路径应为布局，例如 /layout[@name=A3]。");
                        var vtype = Schema.FindType("viewport")!;
                        Done(r, Layouts.AddViewport(_db, parent.Id, item.GetProps(), (tr, e, rest) =>
                        {
                            Mutate.ApplyEntity(_db, tr, e, vtype, rest, Verbs.Add);
                            return Nodes.Entity(tr, e);
                        }));
                        return;
                }
            }

            // 先在只读事务里解析目标，事务关闭后再做数据库级操作
            List<Target> targets;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                _tr = tr;
                targets = Targets(item, prior, item.Verb, r);
                tr.Commit();
            }
            if (targets.Any(t => t.Kind != TargetKind.Xref && t.Kind != TargetKind.Layout))
                throw new CliError("invalid_path", "目标不是外部参照或布局。");

            if (item.Verb == "set")
            {
                var props = item.GetProps();
                if (props.Count == 0) throw new CliError("invalid_request", "set 没有要修改的属性。");
                foreach (var t in targets)
                    Collect(r, t.Kind == TargetKind.Xref ? Xrefs.Apply(_db, t.Id, props) : Layouts.Apply(_db, t.Id, props));
                return;
            }

            if (targets.Count > RemoveGuard && item.Force != true)
                throw new CliError("too_many", $"将删除 {targets.Count} 个外部参照 / 布局，超过保护阈值 {RemoveGuard}。", "确认无误后加 --force");
            var removed = new List<Node>();
            foreach (var t in targets)
            {
                if (t.Kind == TargetKind.Xref)
                {
                    var n = Xrefs.NodeById(_db, t.Id);
                    Xrefs.Detach(_db, t.Id);
                    removed.Add(new Node { Path = n.Path, Type = "xref" });
                }
                else
                {
                    var n = Layouts.NodeById(_db, t.Id);
                    Layouts.Delete(_db, t.Id);
                    removed.Add(new Node { Path = n.Path, Type = "layout" });
                }
            }
            if (r.Matched == null && removed.Count == 1) r.Path = removed[0].Path;
            else r.Nodes = removed;
        }

        private Response RunAtomic(Request req)
        {
            var results = new List<ItemResult>();
            var tm = _db.TransactionManager;
            bool mutating = req.Items.Any(i => MutatingVerbs.Contains(i.Verb));
            bool stop = false;

            using (var outer = tm.StartTransaction())
            {
                for (int i = 0; i < req.Items.Count; i++)
                {
                    var item = req.Items[i];
                    if (stop)
                    {
                        results.Add(new ItemResult { Index = i, Op = item.Verb, Status = "skipped" });
                        continue;
                    }

                    var r = new ItemResult { Index = i, Op = item.Verb };
                    try
                    {
                        using (var inner = tm.StartTransaction())
                        {
                            _tr = inner;
                            Execute(item, r, results);
                            inner.Commit();
                        }
                    }
                    catch (CliError ex) { Fail(r, ex.ToInfo()); }
                    catch (AcRx.Exception ex) { Fail(r, Acad.Translate(ex).ToInfo()); }
                    catch (Exception ex) { Fail(r, new ErrorInfo { Code = "error", Message = ex.Message }); }

                    results.Add(r);
                    if (!r.Ok && req.StopOnError) stop = true;
                }

                int failed = results.Count(r => r.Status == "failed");
                var resp = new Response
                {
                    Items = results,
                    Succeeded = results.Count(r => r.Ok),
                    Failed = failed,
                    Skipped = results.Count(r => r.Status == "skipped"),
                    Ok = failed == 0,
                    Document = _file,
                };

                if (failed > 0 && !req.BestEffort && mutating)
                {
                    outer.Abort();
                    for (int k = _compensate.Count - 1; k >= 0; k--) _compensate[k]();
                    resp.AtomicRolledBack = true;
                }
                else
                {
                    outer.Commit();
                    CommittedChanges = mutating && results.Any(r => r.Ok && MutatingVerbs.Contains(r.Op));
                }
                return resp;
            }
        }

        private static void Fail(ItemResult r, ErrorInfo e)
        {
            r.Status = "failed";
            r.Error = e;
            r.Node = null;
            r.Nodes = null;
        }

        private void Execute(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            switch (item.Verb)
            {
                case "get": DoGet(item, r, prior); break;
                case "query": DoQuery(item, r); break;
                case "add": DoAdd(item, r, prior); break;
                case "set": DoSet(item, r, prior); break;
                case "remove": DoRemove(item, r, prior); break;
                case "stats": DoStats(r); break;
                case "edit": DoEdit(item, r, prior); break;
                case "measure": DoMeasure(item, r, prior); break;
                case "check": DoCheck(item, r, prior); break;
                case "":
                    throw new CliError("invalid_request", "缺少 command（或 op）字段。", "可用：" + BatchVerbs);
                default:
                    throw new CliError("invalid_request", $"未知操作 “{item.Verb}”。", "可用：" + BatchVerbs);
            }
        }

        // ------------------------------------------------------------------ get

        private void DoGet(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var path = Ref(item.Path ?? "/", prior, "path");
            var t = Resolve(path);
            int limit = item.Limit ?? DefaultListLimit;
            int depth;

            Node node;
            switch (t.Kind)
            {
                case TargetKind.Document:
                    node = Nodes.Document(_db, _tr, _file);
                    depth = item.Depth ?? 0;
                    if (depth >= 1)
                        node.Children = new List<Node> { ModelNode(depth - 1, limit), LayersNode(depth - 1, limit) };
                    break;
                case TargetKind.Model:
                    node = ModelNode(item.Depth ?? 1, limit);
                    break;
                case TargetKind.Layers:
                    node = LayersNode(item.Depth ?? 1, limit);
                    break;
                case TargetKind.Layer:
                    node = Nodes.Layer(_db, _tr, (LayerTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead));
                    break;
                case TargetKind.Xrefs:
                    var xs = Xrefs.All(_db, _tr).ToList();
                    node = new Node { Path = "/xrefs", Type = "xrefs", Props = { ["count"] = xs.Count.ToString() } };
                    if ((item.Depth ?? 1) >= 1) node.Children = xs.Select(x => Xrefs.Node(_db, _tr, x)).ToList();
                    break;
                case TargetKind.Xref:
                    node = Xrefs.Node(_db, _tr, (BlockTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead));
                    break;
                case TargetKind.Sysvars:
                    Sysvars.RequireActive(_db);
                    node = new Node { Path = "/sysvars", Type = "sysvars", Props = { ["count"] = Sysvars.Common.Length.ToString(), ["note"] = "常用变量；任意变量用 /sysvar[@name=X]" } };
                    if ((item.Depth ?? 1) >= 1) node.Children = CommonSysvars().ToList();
                    break;
                case TargetKind.Sysvar:
                    node = Sysvars.Node(t.Name!);
                    break;
                case TargetKind.Blocks:
                    var bs = Symbols.Blocks(_db, _tr).ToList();
                    node = new Node { Path = "/blocks", Type = "blocks", Props = { ["count"] = bs.Count.ToString() } };
                    if ((item.Depth ?? 1) >= 1)
                    {
                        var refs = Symbols.ReferenceCounts(_db, _tr);
                        node.Children = bs.Take(limit).Select(b => Symbols.Block(_tr, b, refs)).ToList();
                        if (bs.Count > limit) node.ChildCount = bs.Count;
                    }
                    break;
                case TargetKind.Block:
                    node = Symbols.Block(_tr, (BlockTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead));
                    break;
                case TargetKind.Linetypes:
                    var lts = Symbols.Linetypes(_db, _tr).ToList();
                    node = new Node { Path = "/linetypes", Type = "linetypes", Props = { ["count"] = lts.Count.ToString() } };
                    if ((item.Depth ?? 1) >= 1) node.Children = lts.Select(l => Symbols.Linetype(_db, l)).ToList();
                    break;
                case TargetKind.Linetype:
                    node = Symbols.Linetype(_db, (LinetypeTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead));
                    break;
                case TargetKind.Layouts:
                    var ls = Layouts.All(_db, _tr);
                    node = new Node { Path = "/layouts", Type = "layouts", Props = { ["count"] = ls.Count.ToString(), ["current"] = Layouts.CurrentName() } };
                    if ((item.Depth ?? 1) >= 1) node.Children = ls.Select(l => Layouts.Node(_db, _tr, l)).ToList();
                    break;
                case TargetKind.Layout:
                    var lay = (Layout)_tr.GetObject(t.Id, OpenMode.ForRead);
                    node = Layouts.Node(_db, _tr, lay);
                    if ((item.Depth ?? 1) >= 1)
                    {
                        var ids = Layouts.SpaceEntityIds(_db, _tr, lay.BlockTableRecordId).ToList();
                        node.Children = ids.Take(limit).Select(id => Nodes.Entity(_tr, (Entity)_tr.GetObject(id, OpenMode.ForRead))).ToList();
                        if (ids.Count > limit) node.ChildCount = ids.Count;
                    }
                    break;
                case TargetKind.Devices:
                    var devs = PageSetup.Devices();
                    node = new Node { Path = "/devices", Type = "devices", Props = { ["count"] = devs.Count.ToString() } };
                    node.Children = devs.Select(d => PageSetup.DeviceNode(d, false)).ToList();
                    break;
                case TargetKind.Device:
                    node = PageSetup.DeviceNode(t.Name!, true);
                    break;
                default:
                    node = Nodes.Entity(_tr, (Entity)_tr.GetObject(t.Id, OpenMode.ForRead));
                    break;
            }
            r.Path = node.Path;
            r.Node = node;
        }

        /// <summary>常用系统变量；个别版本没有的变量跳过。</summary>
        private static IEnumerable<Node> CommonSysvars()
        {
            foreach (var name in Sysvars.Common)
            {
                Node? n = null;
                try { n = Sysvars.Node(name); }
                catch (CliError) { /* 该版本没有这个变量 */ }
                if (n != null) yield return n;
            }
        }

        private Node ModelNode(int depth, int limit)
        {
            var ids = Nodes.ModelEntityIds(_db, _tr).ToList();
            var node = new Node { Path = "/model", Type = "model", Props = { ["entities"] = ids.Count.ToString() } };
            if (depth >= 1)
            {
                node.Children = ids.Take(limit).Select(id => Nodes.Entity(_tr, (Entity)_tr.GetObject(id, OpenMode.ForRead))).ToList();
                if (ids.Count > limit) node.ChildCount = ids.Count;
            }
            return node;
        }

        private Node LayersNode(int depth, int limit)
        {
            var layers = Nodes.Layers(_db, _tr).ToList();
            var node = new Node { Path = "/layers", Type = "layers", Props = { ["count"] = layers.Count.ToString() } };
            if (depth >= 1)
            {
                node.Children = layers.Take(limit).Select(l => Nodes.Layer(_db, _tr, l)).ToList();
                if (layers.Count > limit) node.ChildCount = layers.Count;
            }
            return node;
        }

        // ------------------------------------------------------------------ query

        private void DoQuery(BatchItem item, ItemResult r)
        {
            var text = item.Selector ?? item.Path ?? throw new CliError("invalid_request", "query 缺少 selector。", "例：line[layer=WALL]");
            var sel = Selector.Parse(text);
            int limit = item.Limit ?? DefaultListLimit;
            var matches = Match(sel);
            r.Matched = matches.Count;
            r.Nodes = matches.Take(limit).ToList();
            if (matches.Count > limit) r.Truncated = true;
        }

        /// <summary>按选择器匹配，返回读出的 Node（路径里带稳定的句柄 / 名称）。</summary>
        private List<Node> Match(Selector sel)
        {
            var list = new List<Node>();
            if (sel.Type == "xref")
            {
                foreach (var x in Xrefs.All(_db, _tr))
                {
                    var n = Xrefs.Node(_db, _tr, x);
                    if (sel.Matches(n.Type, a => Prop(n, a))) list.Add(n);
                }
                return list;
            }
            if (sel.Type == "layer")
            {
                foreach (var l in Nodes.Layers(_db, _tr))
                {
                    var n = Nodes.Layer(_db, _tr, l);
                    if (sel.Matches(n.Type, a => Prop(n, a))) list.Add(n);
                }
                return list;
            }

            if (sel.Type == "sysvar")
            {
                Sysvars.RequireActive(_db);
                foreach (var n in CommonSysvars())
                    if (sel.Matches(n.Type, a => Prop(n, a))) list.Add(n);
                return list;
            }

            if (sel.Type == "block" || sel.Type == "linetype")
            {
                var refs = sel.Type == "block" ? Symbols.ReferenceCounts(_db, _tr) : null;
                var nodes = sel.Type == "block"
                    ? Symbols.Blocks(_db, _tr).Select(b => Symbols.Block(_tr, b, refs!))
                    : Symbols.Linetypes(_db, _tr).Select(l => Symbols.Linetype(_db, l));
                foreach (var n in nodes)
                    if (sel.Matches(n.Type, a => Prop(n, a))) list.Add(n);
                return list;
            }

            if (sel.Type == "layout")
            {
                foreach (var l in Layouts.All(_db, _tr))
                {
                    var n = Layouts.Node(_db, _tr, l);
                    if (sel.Matches(n.Type, a => Prop(n, a))) list.Add(n);
                }
                return list;
            }

            // 默认只查模型空间；条件里提到 space，或查的是视口时，扫描所有布局
            bool allSpaces = sel.Type == "viewport" || sel.Conditions.Any(c => c.Attr == "space");
            var spaces = allSpaces
                ? Layouts.All(_db, _tr).Select(l => l.BlockTableRecordId).ToList()
                : new List<ObjectId> { Acad.ModelSpace(_db) };
            foreach (var space in spaces)
                foreach (var id in Layouts.SpaceEntityIds(_db, _tr, space))
                {
                    var e = (Entity)_tr.GetObject(id, OpenMode.ForRead);
                    if (!sel.MatchesType(Nodes.TypeOf(e))) continue;
                    var n = Nodes.Entity(_tr, e);
                    if (sel.Matches(n.Type, a => Prop(n, a))) list.Add(n);
                }
            return list;
        }

        private static string? Prop(Node n, string attr)
        {
            if (attr == "type") return n.Type;
            if (attr == "space" && !n.Props.ContainsKey("space") && n.Path.StartsWith("/model/", StringComparison.Ordinal)) return Layouts.ModelName;
            foreach (var kv in n.Props)
                if (string.Equals(kv.Key, attr, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        // ------------------------------------------------------------------ add

        private void DoAdd(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var parentPath = Ref(item.Parent ?? item.Path ?? "/model", prior, "parent");
            var parent = Resolve(parentPath);
            var props = item.GetProps();

            if (item.From != null)
            {
                var src = Resolve(Ref(item.From, prior, "from"));
                if (src.Kind != TargetKind.Entity)
                    throw new CliError("invalid_request", "--from 只能克隆实体。", "例：--from /model/entity[@handle=2A3]");
                var clone = Mutate.Clone(_db, _tr, src.Id);
                Mutate.ApplyEntity(_db, _tr, clone, Nodes.SchemaOf(Nodes.TypeOf(clone)), props, Verbs.Add);
                Done(r, Nodes.Entity(_tr, clone));
                return;
            }

            var typeName = (item.Type ?? "").Trim().ToLowerInvariant();
            if (typeName.Length == 0)
                throw new CliError("invalid_request", "add 缺少 --type。", "可用：" + string.Join("、", Schema.AddableTypes));
            var type = Schema.FindType(typeName);
            if (type == null || type.Name == "document")
            {
                var near = Schema.Suggest(typeName, Schema.AddableTypes);
                throw new CliError("unsupported_type", $"不支持的类型 “{typeName}”。",
                    (near != null ? $"是否想用 {near}？" : "") + "可用：" + string.Join("、", Schema.AddableTypes));
            }

            if (type.Name == "layer")
            {
                if (parent.Kind != TargetKind.Layers && parent.Kind != TargetKind.Document)
                    throw new CliError("invalid_path", "图层的父路径应为 /layers。");
                Done(r, Nodes.Layer(_db, _tr, Mutate.CreateLayer(_db, _tr, props)));
                return;
            }

            if (type.Name == "block")
            {
                if (parent.Kind != TargetKind.Blocks && parent.Kind != TargetKind.Document)
                    throw new CliError("invalid_path", "图块的父路径应为 /blocks。");
                Done(r, Symbols.Block(_tr, Symbols.CreateBlock(_db, _tr, props, s => EntityRef(s, prior))));
                return;
            }
            if (type.Name == "linetype")
            {
                if (parent.Kind != TargetKind.Linetypes && parent.Kind != TargetKind.Document)
                    throw new CliError("invalid_path", "线型的父路径应为 /linetypes。");
                Done(r, Symbols.Linetype(_db, Symbols.LoadLinetype(_db, _tr, props)));
                return;
            }

            if (!type.IsEntity || type.Name == "viewport")
                throw new CliError("invalid_request", $"{type.Name} 不能在这里添加。", $"父路径应为 {type.Parent}");

            // 实体落在哪个空间完全由父路径决定：/model 为模型空间，/layout[@name=X] 为该布局的图纸空间
            ObjectId space;
            if (parent.Kind == TargetKind.Model || parent.Kind == TargetKind.Document) space = Acad.ModelSpace(_db);
            else if (parent.Kind == TargetKind.Layout) space = ((Layout)_tr.GetObject(parent.Id, OpenMode.ForRead)).BlockTableRecordId;
            else throw new CliError("invalid_path", $"实体的父路径应为 /model 或 /layout[@name=...]，收到 {parentPath}。");
            Done(r, Nodes.Entity(_tr, Mutate.CreateEntity(_db, _tr, type, props, s => EntityRef(s, prior), space)));
        }

        // ------------------------------------------------------------------ edit

        private void DoEdit(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var action = Schema.RequireAction("edit", item.Action);
            var name = action.Name;
            if (action.UsesCommand)
                throw new CliError("invalid_request", $"edit {name} 通过 AutoCAD 命令执行，不能放进 batch。",
                    $"单独运行：acadclr edit {name} ...");
            var props = action.CheckProps(item.GetProps());

            var text = item.Path ?? item.Selector ?? throw new CliError("invalid_request", $"edit {name} 缺少目标。");
            var targets = TargetEntities(text, prior, item.Force == true, r);
            if (targets.Count == 0) throw new CliError("not_found", $"目标 “{text}” 没有匹配任何实体。");

            var results = Edits.Run(_db, _tr, action, targets, props, item.Force == true);
            r.Nodes = results.Take(DefaultListLimit).Select(e => Nodes.Entity(_tr, e)).ToList();
            if (results.Count > DefaultListLimit) r.Truncated = true;
            r.Path = r.Nodes.FirstOrDefault()?.Path;
            r.Matched = results.Count;
        }

        // ------------------------------------------------------------------ measure / check

        private const string BatchVerbs = "get query add set remove edit measure check stats";

        private void DoMeasure(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var action = Schema.RequireAction("measure", item.Action);
            var props = action.CheckProps(item.GetProps());
            switch (action.Name)
            {
                case "distance": Report(r, Inspect.Distance(props)); break;
                case "convert": Report(r, Inspect.Convert(_db, props)); break;
                case "area": Report(r, Inspect.Area(_tr, Entities(item, action, prior, r))); break;
                default: Report(r, Inspect.Length(_tr, Entities(item, action, prior, r))); break;
            }
        }

        private void DoCheck(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var action = Schema.RequireAction("check", item.Action);
            var props = action.CheckProps(item.GetProps());
            var ents = Entities(item, action, prior, r);
            switch (action.Name)
            {
                case "overlap":
                    Report(r, Inspect.Overlap(_tr, ents, props));
                    break;
                case "inside":
                    Report(r, Inspect.Inside(_tr, ents, Open(EntityRef(props["boundary"], prior))));
                    break;
                default:
                    if (ents.Count != 1) throw new CliError("invalid_request", $"check adjacent 的目标应为一个实体，匹配到 {ents.Count} 个。", "另一个用 --prop with=...");
                    Report(r, Inspect.Adjacent(_tr, ents[0], Open(EntityRef(props["with"], prior)), props));
                    break;
            }
        }

        /// <summary>measure / check 的目标实体；选择器不需要 --force（只读）。</summary>
        private List<Entity> Entities(BatchItem item, ActionDef action, List<ItemResult> prior, ItemResult r)
        {
            var text = item.Path ?? item.Selector ?? throw new CliError("invalid_request", $"{action.Verb} {action.Name} 缺少目标。", "目标：" + action.Target);
            var ids = TargetEntities(text, prior, true, r);
            if (ids.Count == 0) throw new CliError("not_found", $"目标 “{text}” 没有匹配任何实体。");
            return ids.Select(Open).ToList();
        }

        private Entity Open(ObjectId id) => (Entity)_tr.GetObject(id, OpenMode.ForRead);

        private static void Report(ItemResult r, Inspect.Result res)
        {
            r.Node = res.Summary;
            if (res.Details.Count > 0)
            {
                r.Nodes = res.Details.Take(DefaultListLimit).ToList();
                if (res.Details.Count > DefaultListLimit) r.Truncated = true;
            }
        }

        /// <summary>
        /// 解析 edit 的目标：多个用 ; 分隔的路径 / 句柄 / $N；单个路径或句柄；否则按选择器匹配。
        /// </summary>
        private List<ObjectId> TargetEntities(string text, List<ItemResult> prior, bool force, ItemResult r)
        {
            text = text.Trim();
            if (text.Contains(";")) return Factory.SplitRefs(text).Select(s => EntityRef(s, prior)).ToList();
            if (text.StartsWith("$", StringComparison.Ordinal) || PathParser.IsPath(text) || IsHandle(text))
                return new List<ObjectId> { EntityRef(text, prior) };

            var sel = Selector.Parse(text);
            if (sel.Conditions.Count == 0 && !force)
                throw new CliError("unscoped_selector", $"选择器 “{text}” 没有任何条件，会作用于所有 {sel.Type}。", "加上条件，或确认后加 --force");
            return Match(sel).Select(n => Resolve(n.Path).Id).ToList();
        }

        private static bool IsHandle(string s) =>
            s.Length > 0 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

        /// <summary>单个实体引用：$N、路径或裸句柄；句柄后的 @x,y 拾取点在原生动作里忽略。</summary>
        private ObjectId EntityRef(string s, List<ItemResult> prior)
        {
            s = s.Trim();
            int at = s.IndexOf('@');
            if (at > 0 && !s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(0, at);
            if (IsHandle(s)) s = $"/entity[@handle={s}]";
            var t = Resolve(Ref(s, prior, "path"));
            if (t.Kind != TargetKind.Entity) throw new CliError("invalid_path", $"“{s}” 不是图形实体。");
            return t.Id;
        }

        private static void Done(ItemResult r, Node n)
        {
            r.Path = n.Path;
            r.Node = n;
        }

        // ------------------------------------------------------------------ set

        private void DoSet(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var props = item.GetProps();
            if (props.Count == 0) throw new CliError("invalid_request", "set 没有要修改的属性。", "例：--prop color=1");

            foreach (var t in Targets(item, prior, "set", r))
            {
                switch (t.Kind)
                {
                    case TargetKind.Document:
                        _compensate.AddRange(Mutate.ApplyDocument(_db, _tr, props));
                        Done(r, Nodes.Document(_db, _tr, _file));
                        break;
                    case TargetKind.Layer:
                        var l = (LayerTableRecord)_tr.GetObject(t.Id, OpenMode.ForWrite);
                        Mutate.ApplyLayer(_db, _tr, l, props, Verbs.Set);
                        Collect(r, Nodes.Layer(_db, _tr, l));
                        break;
                    case TargetKind.Entity:
                        var e = (Entity)_tr.GetObject(t.Id, OpenMode.ForWrite);
                        Mutate.ApplyEntity(_db, _tr, e, Nodes.SchemaOf(Nodes.TypeOf(e)), props, Verbs.Set);
                        Collect(r, Nodes.Entity(_tr, e));
                        break;
                    case TargetKind.Sysvar:
                        Sysvars.RequireActive(_db);
                        _compensate.Add(Sysvars.Set(t.Name!, props));
                        Collect(r, Sysvars.Node(t.Name!));
                        break;
                    case TargetKind.Block:
                        var blk = (BlockTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead);
                        Symbols.ApplyBlock(_tr, blk, props);
                        Collect(r, Symbols.Block(_tr, blk));
                        break;
                    default:
                        throw new CliError("invalid_path", $"{t.Kind} 不能直接 set。", "可 set 的目标：/、实体、图层、图块");
                }
            }
        }

        /// <summary>单目标时填 Path/Node；选择器多目标时累积到 Nodes（超过上限只计数）。</summary>
        private static void Collect(ItemResult r, Node n)
        {
            if (r.Matched == null) { Done(r, n); return; }
            r.Nodes ??= new List<Node>();
            if (r.Nodes.Count < DefaultListLimit) r.Nodes.Add(n);
            else r.Truncated = true;
        }

        // ------------------------------------------------------------------ remove

        private void DoRemove(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var targets = Targets(item, prior, "remove", r);
            if (targets.Count > RemoveGuard && item.Force != true)
                throw new CliError("too_many", $"将删除 {targets.Count} 个元素，超过保护阈值 {RemoveGuard}。", "确认无误后加 --force");

            var removed = new List<Node>();
            foreach (var t in targets)
            {
                switch (t.Kind)
                {
                    case TargetKind.Entity:
                        var e = (Entity)_tr.GetObject(t.Id, OpenMode.ForWrite);
                        removed.Add(new Node { Path = Nodes.EntityPath(_tr, Nodes.TypeOf(e), e), Type = Nodes.TypeOf(e) });
                        e.Erase();
                        break;
                    case TargetKind.Layer:
                        var l = (LayerTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead);
                        removed.Add(new Node { Path = Nodes.LayerPath(l.Name), Type = "layer" });
                        Mutate.RemoveLayer(_db, _tr, l);
                        break;
                    case TargetKind.Block:
                        var blk = (BlockTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead);
                        removed.Add(new Node { Path = Symbols.BlockPath(blk.Name), Type = "block" });
                        Symbols.RemoveBlock(_db, _tr, blk);
                        break;
                    case TargetKind.Linetype:
                        var ltr = (LinetypeTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead);
                        removed.Add(new Node { Path = Symbols.LinetypePath(ltr.Name), Type = "linetype" });
                        Symbols.RemoveLinetype(_db, ltr);
                        break;
                    default:
                        throw new CliError("invalid_path", $"{t.Kind} 不能删除。");
                }
            }

            if (r.Matched == null && removed.Count == 1) r.Path = removed[0].Path;
            else r.Nodes = removed.Take(DefaultListLimit).ToList();
        }

        /// <summary>
        /// set / remove 的目标：以 / 或 $ 开头按路径解析为单个目标，否则按选择器匹配多个。
        /// 选择器必须带条件（防止误改整张图），除非显式 --force。
        /// </summary>
        private List<Target> Targets(BatchItem item, List<ItemResult> prior, string verb, ItemResult r)
        {
            var text = item.Path ?? item.Selector ?? throw new CliError("invalid_request", $"{verb} 缺少目标路径或选择器。");
            if (text.StartsWith("$", StringComparison.Ordinal) || PathParser.IsPath(text))
                return new List<Target> { Resolve(Ref(text, prior, "path")) };

            var sel = Selector.Parse(text);
            if (sel.Conditions.Count == 0 && item.Force != true)
                throw new CliError("unscoped_selector", $"选择器 “{text}” 没有任何条件，会作用于所有 {sel.Type}。",
                    "加上条件（如 line[layer=TEMP]），或确认后加 --force");

            var matches = Match(sel);
            r.Matched = matches.Count;
            return matches.Select(n => Resolve(n.Path)).ToList();
        }

        // ------------------------------------------------------------------ stats

        private void DoStats(ItemResult r)
        {
            var byType = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byLayer = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Extents3d? ext = null;
            int total = 0;

            foreach (var id in Nodes.ModelEntityIds(_db, _tr))
            {
                var e = (Entity)_tr.GetObject(id, OpenMode.ForRead);
                total++;
                var t = Nodes.TypeOf(e);
                byType[t] = byType.TryGetValue(t, out int a) ? a + 1 : 1;
                byLayer[e.Layer] = byLayer.TryGetValue(e.Layer, out int b) ? b + 1 : 1;
                try
                {
                    var ge = e.GeometricExtents;
                    if (ext == null) ext = ge;
                    else { var x = ext.Value; x.AddExtents(ge); ext = x; }
                }
                catch (AcRx.Exception) { /* 空文字等没有范围的实体 */ }
            }

            var node = new Node { Path = "/model", Type = "stats" };
            node.Props["entities"] = total.ToString(CultureInfo.InvariantCulture);
            if (ext != null)
            {
                node.Props["extentsMin"] = Acad.Fmt(ext.Value.MinPoint);
                node.Props["extentsMax"] = Acad.Fmt(ext.Value.MaxPoint);
                var size = ext.Value.MaxPoint - ext.Value.MinPoint;
                node.Props["size"] = Values.Num(size.X) + " x " + Values.Num(size.Y);
            }
            node.Children = new List<Node>
            {
                new Node { Path = "/model", Type = "byType", Props = byType.ToDictionary(k => k.Key, k => k.Value.ToString(CultureInfo.InvariantCulture)) },
                new Node { Path = "/layers", Type = "byLayer", Props = byLayer.ToDictionary(k => k.Key, k => k.Value.ToString(CultureInfo.InvariantCulture)) },
            };
            r.Path = "/model";
            r.Node = node;
        }

        // ------------------------------------------------------------------ 路径解析

        private enum TargetKind { Document, Model, Layers, Layer, Entity, Xrefs, Xref, Layouts, Layout, Devices, Device, Blocks, Block, Linetypes, Linetype, Sysvars, Sysvar }

        private readonly struct Target
        {
            public readonly TargetKind Kind;
            public readonly ObjectId Id;
            public readonly string? Name;
            public Target(TargetKind kind, ObjectId id, string? name = null) { Kind = kind; Id = id; Name = name; }
        }

        /// <summary>把 "$3" 替换为第 3 条操作（0 起）返回的路径。</summary>
        private static string Ref(string s, List<ItemResult> prior, string field)
        {
            if (!s.StartsWith("$", StringComparison.Ordinal)) return s;
            if (!int.TryParse(s.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || k < 0 || k >= prior.Count)
                throw new CliError("invalid_reference", $"{field} 引用 “{s}” 无效：只能引用本条之前的操作（从 $0 开始）。");
            var p = prior[k];
            if (!p.Ok || p.Path == null)
                throw new CliError("invalid_reference", $"{field} 引用 {s} 失败：第 {k} 条操作没有成功或没有返回路径。");
            return p.Path;
        }

        private Target Resolve(string path)
        {
            var segs = PathParser.Parse(path);
            if (segs.Count == 0) return new Target(TargetKind.Document, ObjectId.Null);

            var head = segs[0];
            switch (head.Name)
            {
                case "model":
                    if (head.Index != null || head.AttrName != null) break;
                    if (segs.Count == 1) return new Target(TargetKind.Model, ObjectId.Null);
                    if (segs.Count == 2) return new Target(TargetKind.Entity, ResolveEntity(segs[1], path));
                    break;
                case "entity":
                    if (segs.Count == 1) return new Target(TargetKind.Entity, ResolveEntity(head, path));
                    break;
                case "layers":
                    if (segs.Count == 1) return new Target(TargetKind.Layers, ObjectId.Null);
                    if (segs.Count == 2 && segs[1].Name == "layer") return new Target(TargetKind.Layer, ResolveLayer(segs[1], path));
                    break;
                case "layer":
                    if (segs.Count == 1) return new Target(TargetKind.Layer, ResolveLayer(head, path));
                    break;
                case "xrefs":
                    if (segs.Count == 1) return new Target(TargetKind.Xrefs, ObjectId.Null);
                    if (segs.Count == 2 && segs[1].Name == "xref") return new Target(TargetKind.Xref, ResolveXref(segs[1], path));
                    break;
                case "xref":
                    if (segs.Count == 1) return new Target(TargetKind.Xref, ResolveXref(head, path));
                    break;
                case "layouts":
                    if (segs.Count == 1) return new Target(TargetKind.Layouts, ObjectId.Null);
                    if (segs[1].Name != "layout") break;
                    return ResolveInLayout(segs.Skip(1).ToList(), path);
                case "layout":
                    return ResolveInLayout(segs, path);
                case "blocks":
                    if (segs.Count == 1) return new Target(TargetKind.Blocks, ObjectId.Null);
                    if (segs.Count == 2 && segs[1].Name == "block") return new Target(TargetKind.Block, ResolveBlock(segs[1], path));
                    break;
                case "block":
                    if (segs.Count == 1) return new Target(TargetKind.Block, ResolveBlock(head, path));
                    break;
                case "linetypes":
                    if (segs.Count == 1) return new Target(TargetKind.Linetypes, ObjectId.Null);
                    if (segs.Count == 2 && segs[1].Name == "linetype") return new Target(TargetKind.Linetype, ResolveLinetype(segs[1], path));
                    break;
                case "linetype":
                    if (segs.Count == 1) return new Target(TargetKind.Linetype, ResolveLinetype(head, path));
                    break;
                case "documents":
                case "document":
                    throw new CliError("live_only", "/documents 只用于实时模式，且不能和其他操作放在同一个 batch 里。",
                        "离线模式一次只处理一个文件（dwg 参数）；实时模式单独执行文档操作");
                case "sysvars":
                    if (segs.Count == 1) return new Target(TargetKind.Sysvars, ObjectId.Null);
                    if (segs.Count == 2 && segs[1].Name == "sysvar") return ResolveSysvar(segs[1], path);
                    break;
                case "sysvar":
                    if (segs.Count == 1) return ResolveSysvar(head, path);
                    break;
                case "devices":
                    if (segs.Count == 1) return new Target(TargetKind.Devices, ObjectId.Null);
                    if (segs.Count == 2 && segs[1].Name == "device") return ResolveDevice(segs[1], path);
                    break;
                case "device":
                    if (segs.Count == 1) return ResolveDevice(head, path);
                    break;
            }
            throw new CliError("invalid_path", $"无法识别的路径：{path}",
                "可用：/  /model  /model/<type>[N]  /entity[@handle=H]  /layers  /layer[@name=N]  /xrefs  /xref[@name=N]  " +
                "/layouts  /layout[@name=N]  /layout[@name=N]/<type>[N]  /blocks  /block[@name=N]  /linetypes  /linetype[@name=N]  /sysvars  /sysvar[@name=N]  /devices  /device[@name=N]");
        }

        /// <summary>/layout[@name=X] 或 /layout[@name=X]/&lt;type&gt;[N|@handle=H]。</summary>
        private Target ResolveInLayout(List<PathSegment> segs, string whole)
        {
            var seg = segs[0];
            var all = Layouts.All(_db, _tr);
            Layout? lay = null;
            if (seg.AttrName == "name")
            {
                var id = Layouts.Find(_db, _tr, seg.AttrValue ?? "");
                if (!id.IsNull) lay = (Layout)_tr.GetObject(id, OpenMode.ForRead);
                else
                {
                    var near = Schema.Suggest(seg.AttrValue ?? "", all.Select(l => l.LayoutName));
                    throw new CliError("not_found", $"布局 “{seg.AttrValue}” 不存在。",
                        (near != null ? $"是否想用 {near}？" : "") + "现有：" + string.Join("、", all.Select(l => l.LayoutName)));
                }
            }
            else if (seg.Index != null)
            {
                int idx = seg.Index == -1 ? all.Count : seg.Index.Value;
                if (idx < 1 || idx > all.Count) throw new CliError("not_found", $"{whole}：只有 {all.Count} 个布局（含 Model）。");
                lay = all[idx - 1];
            }
            else throw new CliError("invalid_path", $"布局需要用 [@name=...] 或 [N] 定位：{whole}");

            if (segs.Count == 1) return new Target(TargetKind.Layout, lay.ObjectId);
            if (segs.Count == 2)
            {
                var id = ResolveEntity(segs[1], whole, lay.BlockTableRecordId);
                // 按句柄定位时核对实体确实在这个布局里，免得路径与实际空间不符
                var e = (Entity)_tr.GetObject(id, OpenMode.ForRead);
                if (e.OwnerId != lay.BlockTableRecordId)
                    throw new CliError("not_found", $"{whole}：该实体不在布局 “{lay.LayoutName}” 中。", "它的实际路径是 " + Nodes.EntityPath(_tr, Nodes.TypeOf(e), e));
                return new Target(TargetKind.Entity, id);
            }
            throw new CliError("invalid_path", $"无法识别的路径：{whole}");
        }

        private static Target ResolveDevice(PathSegment seg, string whole)
        {
            if (seg.AttrName != "name") throw new CliError("invalid_path", $"设备需要用 [@name=...] 定位：{whole}");
            return new Target(TargetKind.Device, ObjectId.Null, PageSetup.EnsureDevice(seg.AttrValue ?? ""));
        }

        private ObjectId ResolveXref(PathSegment seg, string whole)
        {
            var all = Xrefs.All(_db, _tr).ToList();
            if (seg.AttrName == "name")
            {
                var hit = all.FirstOrDefault(b => b.Name.Equals(seg.AttrValue ?? "", StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.ObjectId;
                throw new CliError("not_found", $"外部参照 “{seg.AttrValue}” 不存在。",
                    all.Count == 0 ? "当前图形没有外部参照。" : "现有：" + string.Join("、", all.Select(b => b.Name)));
            }
            if (seg.Index != null)
            {
                int idx = seg.Index == -1 ? all.Count : seg.Index.Value;
                if (idx < 1 || idx > all.Count) throw new CliError("not_found", $"{whole}：只有 {all.Count} 个外部参照。");
                return all[idx - 1].ObjectId;
            }
            throw new CliError("invalid_path", $"外部参照需要用 [N] 或 [@name=...] 定位：{whole}");
        }

        /// <param name="space">按序号定位时在哪个空间里数（null 为模型空间）；按句柄定位时不限空间。</param>
        private ObjectId ResolveEntity(PathSegment seg, string whole, ObjectId? space = null)
        {
            string type = seg.Name;

            if (seg.AttrName == "handle")
            {
                var id = ByHandle(seg.AttrValue ?? "");
                if (id.IsNull || id.IsErased)
                    throw new CliError("not_found", $"句柄 {seg.AttrValue} 不存在或已删除：{whole}", "用 query 重新查找目标");
                var obj = _tr.GetObject(id, OpenMode.ForRead);
                if (!(obj is Entity e))
                    throw new CliError("not_found", $"句柄 {seg.AttrValue} 不是图形实体（而是 {obj.GetType().Name}）。");
                var actual = Nodes.TypeOf(e);
                if (type != "entity" && type != actual)
                    throw new CliError("not_found", $"句柄 {seg.AttrValue} 是 {actual}，不是 {type}。",
                        $"改用 /model/{actual}[@handle={seg.AttrValue}] 或 /entity[@handle={seg.AttrValue}]");
                return id;
            }

            if (seg.Index != null)
            {
                var ids = Layouts.SpaceEntityIds(_db, _tr, space ?? Acad.ModelSpace(_db))
                    .Where(id => type == "entity" || Nodes.TypeOf((Entity)_tr.GetObject(id, OpenMode.ForRead)) == type)
                    .ToList();
                int idx = seg.Index == -1 ? ids.Count : seg.Index.Value;
                if (idx < 1 || idx > ids.Count)
                    throw new CliError("not_found", $"{whole}：{(space == null ? "模型空间" : "该布局")}只有 {ids.Count} 个 {type}。");
                return ids[idx - 1];
            }

            throw new CliError("invalid_path", $"实体需要用 [N] 或 [@handle=...] 定位：{whole}");
        }

        private ObjectId ByHandle(string hex)
        {
            if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v))
                throw new CliError("invalid_path", $"句柄 “{hex}” 不是十六进制数。");
            try { return _db.GetObjectId(false, new Handle(v), 0); }
            catch (AcRx.Exception) { return ObjectId.Null; }
        }

        private Target ResolveSysvar(PathSegment seg, string whole)
        {
            if (seg.AttrName != "name" || string.IsNullOrWhiteSpace(seg.AttrValue))
                throw new CliError("invalid_path", $"系统变量需要用 [@name=...] 定位：{whole}");
            Sysvars.RequireActive(_db);
            var name = seg.AttrValue!.Trim().ToUpperInvariant();
            Sysvars.Read(name); // 不存在时报错
            return new Target(TargetKind.Sysvar, ObjectId.Null, name);
        }

        private ObjectId ResolveBlock(PathSegment seg, string whole) =>
            ResolveNamed(seg, whole, "图块", Symbols.Blocks(_db, _tr).Select(b => (b.Name, b.ObjectId)));

        private ObjectId ResolveLinetype(PathSegment seg, string whole) =>
            ResolveNamed(seg, whole, "线型", Symbols.Linetypes(_db, _tr).Select(l => (l.Name, l.ObjectId)));

        /// <summary>按 [@name=...] 或 [N] 定位图块、线型这类命名对象。</summary>
        private static ObjectId ResolveNamed(PathSegment seg, string whole, string what, IEnumerable<(string name, ObjectId id)> items)
        {
            var all = items.ToList();
            if (seg.AttrName == "name")
            {
                var hit = all.FirstOrDefault(x => x.name.Equals(seg.AttrValue ?? "", StringComparison.OrdinalIgnoreCase));
                if (!hit.id.IsNull) return hit.id;
                var near = Schema.Suggest(seg.AttrValue ?? "", all.Select(x => x.name));
                throw new CliError("not_found", $"{what} “{seg.AttrValue}” 不存在。",
                    near != null ? $"是否想用 {near}？" : all.Count == 0 ? $"当前图形没有{what}。" : "现有：" + string.Join("、", all.Select(x => x.name).Take(30)));
            }
            if (seg.Index != null)
            {
                int idx = seg.Index == -1 ? all.Count : seg.Index.Value;
                if (idx < 1 || idx > all.Count) throw new CliError("not_found", $"{whole}：只有 {all.Count} 个{what}。");
                return all[idx - 1].id;
            }
            throw new CliError("invalid_path", $"{what}需要用 [N] 或 [@name=...] 定位：{whole}");
        }

        private ObjectId ResolveLayer(PathSegment seg, string whole)
        {
            if (seg.AttrName == "name")
            {
                var id = Acad.FindLayer(_db, _tr, seg.AttrValue ?? "");
                if (id.IsNull)
                {
                    var names = Nodes.Layers(_db, _tr).Select(l => l.Name).ToList();
                    var near = Schema.Suggest(seg.AttrValue ?? "", names);
                    throw new CliError("not_found", $"图层 “{seg.AttrValue}” 不存在。",
                        near != null ? $"是否想用 {near}？" : "现有图层：" + string.Join("、", names.Take(30)));
                }
                return id;
            }
            if (seg.Index != null)
            {
                var layers = Nodes.Layers(_db, _tr).ToList();
                int idx = seg.Index == -1 ? layers.Count : seg.Index.Value;
                if (idx < 1 || idx > layers.Count) throw new CliError("not_found", $"{whole}：只有 {layers.Count} 个图层。");
                return layers[idx - 1].ObjectId;
            }
            throw new CliError("invalid_path", $"图层需要用 [N] 或 [@name=...] 定位：{whole}");
        }
    }
}
