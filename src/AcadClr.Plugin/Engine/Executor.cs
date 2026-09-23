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
        private Transaction _tr = null!;

        /// <summary>本次执行是否提交了修改（离线模式据此决定是否写回文件）。</summary>
        public bool CommittedChanges { get; private set; }

        public Executor(Database db, string? file)
        {
            _db = db;
            _file = file;
        }

        public Response Run(Request req) =>
            req.Items.Any(i => IsDirect(i, req.Items, 0)) ? RunSequential(req) : RunAtomic(req);

        /// <summary>
        /// 外部参照的写操作（附着 / 重载 / 卸载 / 绑定 / 拆离）是数据库级操作，不能放进事务。
        /// 含这类操作的批次改为逐条执行：普通操作各自一个事务立即提交，失败不回滚已成功的部分。
        /// </summary>
        private static bool IsDirect(BatchItem item, List<BatchItem> all, int depth)
        {
            if (item.Verb == "add") return string.Equals(item.Type?.Trim(), "xref", StringComparison.OrdinalIgnoreCase);
            if (item.Verb != "set" && item.Verb != "remove") return false;

            var target = (item.Path ?? item.Selector ?? "").Trim();
            if (target.StartsWith("$", StringComparison.Ordinal) && depth < 8 &&
                int.TryParse(target.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) &&
                k >= 0 && k < all.Count)
                return all[k].Verb == "add" && IsDirect(all[k], all, depth + 1);
            return target.StartsWith("/xref[", StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith("xref", StringComparison.OrdinalIgnoreCase);
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
                    if (IsDirect(item, req.Items, 0)) ExecuteXref(item, r, results);
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

        private void ExecuteXref(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            if (item.Verb == "add")
            {
                var parent = Ref(item.Parent ?? item.Path ?? "/xrefs", prior, "parent");
                if (!parent.Equals("/xrefs", StringComparison.OrdinalIgnoreCase) && parent != "/")
                    throw new CliError("invalid_path", "外部参照的父路径应为 /xrefs。");
                Done(r, Xrefs.Attach(_db, item.GetProps()));
                return;
            }

            // 先在只读事务里解析目标，事务关闭后再做数据库级操作
            List<Target> targets;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                _tr = tr;
                targets = Targets(item, prior, item.Verb, r);
                tr.Commit();
            }
            if (targets.Any(t => t.Kind != TargetKind.Xref))
                throw new CliError("invalid_path", "目标不是外部参照。");

            if (item.Verb == "set")
            {
                var props = item.GetProps();
                if (props.Count == 0) throw new CliError("invalid_request", "set 没有要修改的属性。", "例：--prop reload=true");
                foreach (var t in targets) Collect(r, Xrefs.Apply(_db, t.Id, props));
                return;
            }

            if (targets.Count > RemoveGuard && item.Force != true)
                throw new CliError("too_many", $"将拆离 {targets.Count} 个外部参照，超过保护阈值 {RemoveGuard}。", "确认无误后加 --force");
            var removed = new List<Node>();
            foreach (var t in targets)
            {
                var n = Xrefs.NodeById(_db, t.Id);
                Xrefs.Detach(_db, t.Id);
                removed.Add(new Node { Path = n.Path, Type = "xref" });
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
                case "":
                    throw new CliError("invalid_request", "缺少 command（或 op）字段。", "可用：get query add set remove edit stats");
                default:
                    throw new CliError("invalid_request", $"未知操作 “{item.Verb}”。", "可用：get query add set remove edit stats");
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
                default:
                    node = Nodes.Entity(_tr, (Entity)_tr.GetObject(t.Id, OpenMode.ForRead));
                    break;
            }
            r.Path = node.Path;
            r.Node = node;
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

            foreach (var id in Nodes.ModelEntityIds(_db, _tr))
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

            if (parent.Kind != TargetKind.Model && parent.Kind != TargetKind.Document)
                throw new CliError("invalid_path", $"实体的父路径应为 /model，收到 {parentPath}。");
            Done(r, Nodes.Entity(_tr, Mutate.CreateEntity(_db, _tr, type, props, s => EntityRef(s, prior))));
        }

        // ------------------------------------------------------------------ edit

        private void DoEdit(BatchItem item, ItemResult r, List<ItemResult> prior)
        {
            var name = (item.Action ?? "").Trim().ToLowerInvariant();
            var action = Schema.FindAction(name) ?? throw new CliError("invalid_request",
                name.Length == 0 ? "edit 缺少 action。" : $"未知的 edit 动作 “{name}”。",
                "可用：" + string.Join("、", Schema.Actions.Select(a => a.Name)));
            if (action.UsesCommand)
                throw new CliError("invalid_request", $"edit {name} 通过 AutoCAD 命令执行，不能放进 batch。",
                    $"单独运行：acadclr edit {name} ...");

            var props = new Dictionary<string, string>();
            foreach (var kv in item.GetProps()) props[action.CheckProp(kv.Key).Name] = kv.Value;
            var missing = action.Props.Where(p => p.Required && !props.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            if (missing.Count > 0)
                throw new CliError("missing_property", $"edit {name} 缺少属性：{string.Join("、", missing)}。", $"运行 acadclr help edit {name}");

            var text = item.Path ?? item.Selector ?? throw new CliError("invalid_request", $"edit {name} 缺少目标。");
            var targets = TargetEntities(text, prior, item.Force == true, r);
            if (targets.Count == 0) throw new CliError("not_found", $"目标 “{text}” 没有匹配任何实体。");

            var results = Edits.Run(_db, _tr, action, targets, props, item.Force == true);
            r.Nodes = results.Take(DefaultListLimit).Select(e => Nodes.Entity(_tr, e)).ToList();
            if (results.Count > DefaultListLimit) r.Truncated = true;
            r.Path = r.Nodes.FirstOrDefault()?.Path;
            r.Matched = results.Count;
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
                        Mutate.ApplyDocument(_db, _tr, props);
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
                    default:
                        throw new CliError("invalid_path", $"{t.Kind} 不能直接 set。", "可 set 的目标：/、实体、图层");
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
                        removed.Add(new Node { Path = Nodes.EntityPath(Nodes.TypeOf(e), e), Type = Nodes.TypeOf(e) });
                        e.Erase();
                        break;
                    case TargetKind.Layer:
                        var l = (LayerTableRecord)_tr.GetObject(t.Id, OpenMode.ForRead);
                        removed.Add(new Node { Path = Nodes.LayerPath(l.Name), Type = "layer" });
                        Mutate.RemoveLayer(_db, _tr, l);
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

        private enum TargetKind { Document, Model, Layers, Layer, Entity, Xrefs, Xref }

        private readonly struct Target
        {
            public readonly TargetKind Kind;
            public readonly ObjectId Id;
            public Target(TargetKind kind, ObjectId id) { Kind = kind; Id = id; }
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
            }
            throw new CliError("invalid_path", $"无法识别的路径：{path}",
                "可用：/  /model  /model/<type>[N]  /model/entity[@handle=H]  /entity[@handle=H]  /layers  /layer[@name=N]  /xrefs  /xref[@name=N]");
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

        private ObjectId ResolveEntity(PathSegment seg, string whole)
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
                var ids = Nodes.ModelEntityIds(_db, _tr)
                    .Where(id => type == "entity" || Nodes.TypeOf((Entity)_tr.GetObject(id, OpenMode.ForRead)) == type)
                    .ToList();
                int idx = seg.Index == -1 ? ids.Count : seg.Index.Value;
                if (idx < 1 || idx > ids.Count)
                    throw new CliError("not_found", $"{whole}：模型空间只有 {ids.Count} 个 {type}。");
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
