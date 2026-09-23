using System;
using System.Collections.Generic;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 布局（图纸空间）与浮动视口。
    ///
    /// 新建 / 删除 / 重命名 / 切换当前布局都经 LayoutManager，是数据库级操作，调用时不能有打开的事务
    /// （由 Executor 的逐条模式保证），各自按需开短事务。
    ///
    /// AutoCADMCP 曾踩过的坑：“当前空间”是隐式状态，切到布局后画图框，图元却进了模型空间且无任何提示。
    /// 这里实体落在哪个空间一律由父路径显式决定，切换当前布局不影响 add 的去向。
    /// </summary>
    internal static class Layouts
    {
        public const string ModelName = "Model";

        private static LayoutManager Lm => LayoutManager.Current;

        public static string LayoutPath(string name) => $"/layout[@name={name}]";

        public static List<Layout> All(Database db, Transaction tr)
        {
            var list = new List<Layout>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
                if (tr.GetObject(e.Value, OpenMode.ForRead) is Layout l) list.Add(l);
            return list.OrderBy(l => l.TabOrder).ToList();
        }

        public static ObjectId Find(Database db, Transaction tr, string name)
        {
            var n = name.Trim();
            if (n == "模型") n = ModelName;
            return All(db, tr).FirstOrDefault(l => l.LayoutName.Equals(n, StringComparison.OrdinalIgnoreCase))?.ObjectId ?? ObjectId.Null;
        }

        public static string CurrentName()
        {
            try { return Lm.CurrentLayout; } catch (AcRx.Exception) { return ""; }
        }

        /// <summary>
        /// 图纸空间里代表整张图纸的那个“总视口”（布局激活后第一个视口），它不是用户画的浮动视口，列举与计数时跳过。
        /// </summary>
        public static ObjectId OverallViewport(Transaction tr, Layout l)
        {
            if (l.ModelType) return ObjectId.Null;
            try
            {
                var vps = l.GetViewports();
                if (vps != null && vps.Count > 0) return vps[0];
            }
            catch (AcRx.Exception) { }
            // 布局从未激活过时 GetViewports 为空；此时若有 Number==1 的视口也视为总视口
            var btr = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Viewport v && v.Number == 1) return id;
            return ObjectId.Null;
        }

        /// <summary>某个空间（模型或布局）里的实体，布局会跳过总视口。</summary>
        public static IEnumerable<ObjectId> SpaceEntityIds(Database db, Transaction tr, ObjectId btrId)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            var skip = ObjectId.Null;
            if (btr.IsLayout && !btr.LayoutId.IsNull)
                skip = OverallViewport(tr, (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead));
            foreach (ObjectId id in btr)
                if (!id.IsErased && id != skip) yield return id;
        }

        public static Node Node(Database db, Transaction tr, Layout l)
        {
            var ids = SpaceEntityIds(db, tr, l.BlockTableRecordId).ToList();
            int vps = ids.Count(id => tr.GetObject(id, OpenMode.ForRead) is Viewport);
            var size = l.PlotPaperSize;
            bool rotated = l.PlotRotation == PlotRotation.Degrees090 || l.PlotRotation == PlotRotation.Degrees270;
            return new Node
            {
                Path = LayoutPath(l.LayoutName),
                Type = "layout",
                Props = new Dictionary<string, string>
                {
                    ["name"] = l.LayoutName,
                    ["current"] = l.LayoutName.Equals(CurrentName(), StringComparison.OrdinalIgnoreCase) ? "true" : "false",
                    ["device"] = l.PlotConfigurationName,
                    ["paper"] = l.CanonicalMediaName,
                    ["landscape"] = PageSetup.LayoutIsLandscape(l) ? "true" : "false",
                    ["plotStyle"] = l.CurrentStyleSheet,
                    ["paperSize"] = rotated ? $"{Values.Num(size.Y)} x {Values.Num(size.X)}" : $"{Values.Num(size.X)} x {Values.Num(size.Y)}",
                    ["tabOrder"] = l.TabOrder.ToString(),
                    ["viewports"] = vps.ToString(),
                    ["entities"] = (ids.Count - vps).ToString(),
                },
            };
        }

        public static Node NodeById(Database db, ObjectId id)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var n = Node(db, tr, (Layout)tr.GetObject(id, OpenMode.ForRead));
                tr.Commit();
                return n;
            }
        }

        private static Dictionary<string, string> Props(TypeDef type, List<KeyValuePair<string, string>> props, Verbs verb)
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in props) map[Schema.CheckProp(type, kv.Key, verb).Name] = kv.Value;
            return map;
        }

        // ---------------- 布局 ----------------

        public static Node Create(Database db, List<KeyValuePair<string, string>> props)
        {
            var map = Props(Schema.FindType("layout")!, props, Verbs.Add);
            if (!map.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                throw new CliError("missing_property", "添加 layout 缺少 name。", "例：--prop name=A3");
            name = name.Trim();
            if (name.Equals(ModelName, StringComparison.OrdinalIgnoreCase)) throw new CliError("invalid_value", "布局名不能是 Model（模型空间）。");
            Acad.ValidateSymbolName(name, "布局名");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Find(db, tr, name).IsNull) throw new CliError("already_exists", $"布局 “{name}” 已存在。");
                tr.Commit();
            }

            var id = Lm.CreateLayout(name);
            map.Remove("name");
            Apply(db, id, map);
            return NodeById(db, id);
        }

        public static Node Apply(Database db, ObjectId id, List<KeyValuePair<string, string>> props) =>
            Apply(db, id, Props(Schema.FindType("layout")!, props, Verbs.Set));

        private static Node Apply(Database db, ObjectId id, Dictionary<string, string> map)
        {
            string name;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var l = (Layout)tr.GetObject(id, OpenMode.ForRead);
                name = l.LayoutName;
                bool? land = map.TryGetValue("landscape", out var ls) ? Values.Bool("landscape", ls) : (bool?)null;
                map.TryGetValue("device", out var dev);
                map.TryGetValue("paper", out var paper);
                map.TryGetValue("plotStyle", out var style);
                PageSetup.Apply(l, dev, paper, land, style);
                tr.Commit();
            }

            if (map.TryGetValue("name", out var newName) && !newName.Equals(name, StringComparison.Ordinal))
            {
                if (name.Equals(ModelName, StringComparison.OrdinalIgnoreCase)) throw new CliError("invalid_value", "模型空间不能重命名。");
                Acad.ValidateSymbolName(newName, "布局名");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var other = Find(db, tr, newName);
                    if (!other.IsNull && other != id) throw new CliError("already_exists", $"布局 “{newName}” 已存在。");
                    tr.Commit();
                }
                Lm.RenameLayout(name, newName);
                name = newName;
            }

            if (map.TryGetValue("current", out var cur))
            {
                if (!Values.Bool("current", cur)) throw new CliError("invalid_value", "current 只能设为 true。", "切回模型空间：set \"/layout[@name=Model]\" --prop current=true");
                SetCurrent(name);
            }
            return NodeById(db, id);
        }

        public static void SetCurrent(string name)
        {
            try { Lm.CurrentLayout = name; }
            catch (AcRx.Exception ex)
            {
                throw new CliError("acad_error", $"切换到布局 “{name}” 失败（{ex.ErrorStatus}）。", "AutoCAD 正在执行命令时不能切换布局");
            }
        }

        public static void Delete(Database db, ObjectId id)
        {
            string name;
            int paperLayouts;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                name = ((Layout)tr.GetObject(id, OpenMode.ForRead)).LayoutName;
                paperLayouts = All(db, tr).Count(l => !l.ModelType);
                tr.Commit();
            }
            if (name.Equals(ModelName, StringComparison.OrdinalIgnoreCase)) throw new CliError("invalid_value", "不能删除模型空间。");
            if (paperLayouts <= 1) throw new CliError("invalid_value", "图形至少要保留一个布局，不能删除最后一个。");
            if (CurrentName().Equals(name, StringComparison.OrdinalIgnoreCase)) SetCurrent(ModelName);
            Lm.DeleteLayout(name);
        }

        // ---------------- 视口 ----------------

        /// <summary>
        /// 在布局上新建浮动视口。视口只有在所属布局为当前布局时才能可靠地打开（On），
        /// 所以临时切过去，建好后切回原布局。
        /// </summary>
        public static Node AddViewport(Database db, ObjectId layoutId, List<KeyValuePair<string, string>> props,
            Func<Transaction, Entity, List<KeyValuePair<string, string>>, Node> applyAndRead)
        {
            var type = Schema.FindType("viewport")!;
            foreach (var kv in props) Schema.CheckProp(type, kv.Key, Verbs.Add);
            string Need(string k) => props.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value
                ?? throw new CliError("missing_property", $"添加 viewport 缺少 {k}。", "运行 acadclr help viewport 查看示例");

            var center = Acad.Pt("center", Need("center"));
            var w = Values.Positive("width", Need("width"));
            var h = Values.Positive("height", Need("height"));

            string target, previous = CurrentName();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var l = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                target = l.LayoutName;
                if (l.ModelType) throw new CliError("invalid_path", "模型空间不能加浮动视口。", "父路径应为 /layout[@name=...]");
                tr.Commit();
            }

            bool switched = false;
            if (!previous.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                try { Lm.CurrentLayout = target; switched = true; } catch (AcRx.Exception) { /* 离线等场合切不了，仍尝试直接打开 */ }
            }

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var l = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForWrite);
                    var vp = new Viewport { CenterPoint = center, Width = w, Height = h };
                    vp.SetDatabaseDefaults(db);
                    btr.AppendEntity(vp);
                    tr.AddNewlyCreatedDBObject(vp, true);

                    var rest = props.Where(kv => !new[] { "center", "width", "height" }.Contains(type.Find(kv.Key)!.Name)).ToList();
                    if (!rest.Any(kv => kv.Key.Equals("on", StringComparison.OrdinalIgnoreCase)))
                        rest.Insert(0, new KeyValuePair<string, string>("on", "true"));
                    var node = applyAndRead(tr, vp, rest);
                    tr.Commit();
                    return node;
                }
            }
            finally
            {
                if (switched) try { Lm.CurrentLayout = previous; } catch (AcRx.Exception) { }
            }
        }

        /// <summary>视口属性。锁定的视口改比例 / 视图前先临时解锁。</summary>
        public static void SetViewport(Viewport vp, string key, string v)
        {
            bool relock = vp.Locked && (key == "scale" || key == "viewCenter");
            if (relock) vp.Locked = false;
            try
            {
                switch (key)
                {
                    case "center": vp.CenterPoint = Acad.Pt(key, v); break;
                    case "width": vp.Width = Values.Positive(key, v); break;
                    case "height": vp.Height = Values.Positive(key, v); break;
                    case "viewCenter":
                        var p = Acad.Pt(key, v);
                        vp.ViewCenter = new Point2d(p.X, p.Y);
                        break;
                    case "scale": vp.CustomScale = 1.0 / Values.Positive(key, v); break;
                    case "locked": vp.Locked = Values.Bool(key, v); relock = false; break;
                    case "on":
                        try { vp.On = Values.Bool(key, v); }
                        catch (AcRx.Exception ex)
                        {
                            throw new CliError("acad_error", $"打开 / 关闭视口失败（{ex.ErrorStatus}）。",
                                "先把视口所在布局设为当前布局再试（set \"/layout[@name=...]\" --prop current=true）；超过 MAXACTVP 上限时也会失败");
                        }
                        break;
                }
            }
            finally
            {
                if (relock) vp.Locked = true;
            }
        }
    }
}
