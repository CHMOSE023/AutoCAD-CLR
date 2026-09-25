using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadClr.Plugin.Engine
{
    /// <summary>图块定义（/blocks）与线型（/linetypes）。</summary>
    internal static class Symbols
    {
        // ======================= 图块 =======================

        public static string BlockPath(string name) => $"/block[@name={name}]";

        /// <summary>可插入的图块：不含布局、匿名块、外部参照（外部参照在 /xrefs）。</summary>
        public static IEnumerable<BlockTableRecord> Blocks(Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return bt.Cast<ObjectId>()
                .Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))
                .Where(b => !b.IsLayout && !b.IsAnonymous && !b.IsFromExternalReference)
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 每个块定义被参照的次数（含嵌套在其他块里的参照，动态块按原始定义计）。
        /// 不用 GetBlockReferenceIds：它读的是已提交的状态，同一批次里刚插入的参照数不到，
        /// “先插入、再删除块定义”的保护就会失效。
        /// </summary>
        public static Dictionary<ObjectId, int> ReferenceCounts(Database db, Transaction tr)
        {
            var counts = new Dictionary<ObjectId, int>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId bid in bt)
            {
                var owner = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (owner.IsFromExternalReference) continue;
                foreach (ObjectId id in owner)
                {
                    if (id.ObjectClass.DxfName != "INSERT") continue;
                    var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    var def = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    counts[def] = counts.TryGetValue(def, out int n) ? n + 1 : 1;
                }
            }
            return counts;
        }

        public static Node Block(Transaction tr, BlockTableRecord b) =>
            Block(tr, b, ReferenceCounts(b.Database, tr));

        /// <summary>
        /// 块定义的节点。bboxFromBase 是相对基点的范围（移植自 AutoCADMCP 的 list_blocks）：
        /// 插入点给的是基点位置，块往基点哪个方向长只有看这个才知道，基点在底边的车位块尤其容易插反。
        /// </summary>
        public static Node Block(Transaction tr, BlockTableRecord b, Dictionary<ObjectId, int> refs)
        {
            var p = new Dictionary<string, string> { ["name"] = b.Name };
            p["base"] = Acad.Fmt(b.Origin);
            if (!string.IsNullOrEmpty(b.Comments)) p["description"] = b.Comments;

            int count = 0;
            Extents3d? ext = null;
            var tags = new List<string>();
            foreach (ObjectId id in b)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                count++;
                if (e is AttributeDefinition ad && !ad.Constant) tags.Add(ad.Tag);
                try
                {
                    var ge = e.GeometricExtents;
                    if (ext == null) ext = ge;
                    else { var x = ext.Value; x.AddExtents(ge); ext = x; }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { /* 没有几何范围的实体 */ }
            }
            p["count"] = count.ToString(CultureInfo.InvariantCulture);
            if (ext != null)
            {
                var o = b.Origin;
                var min = ext.Value.MinPoint; var max = ext.Value.MaxPoint;
                p["bboxFromBase"] = Values.Pt(min.X - o.X, min.Y - o.Y, 0) + ";" + Values.Pt(max.X - o.X, max.Y - o.Y, 0);
                p["size"] = Values.Num(max.X - min.X) + " x " + Values.Num(max.Y - min.Y);
            }
            p["references"] = (refs.TryGetValue(b.ObjectId, out int nref) ? nref : 0).ToString(CultureInfo.InvariantCulture);
            if (tags.Count > 0) p["attributes"] = string.Join(";", tags);
            return new Node { Path = BlockPath(b.Name), Type = "block", Props = p };
        }

        /// <summary>
        /// 用已有实体定义图块：实体复制进块定义（保持世界坐标，Origin 为基点，在基点处插入即与原图重合）。
        /// replace=true 时删除源实体，并在其所在空间的基点处插入该块。
        /// </summary>
        public static BlockTableRecord CreateBlock(Database db, Transaction tr, List<KeyValuePair<string, string>> props, Func<string, ObjectId> entityRef)
        {
            var type = Schema.FindType("block")!;
            var map = new Dictionary<string, string>();
            foreach (var kv in props) map[Schema.CheckProp(type, kv.Key, Verbs.Add).Name] = kv.Value;
            foreach (var req in type.Props.Where(x => x.Required && (x.Verbs & Verbs.Add) != 0))
                if (!map.ContainsKey(req.Name)) throw new CliError("missing_property", $"block 缺少属性 {req.Name}：{req.Description}。", "运行 acadclr help block");

            var name = map["name"].Trim();
            Acad.ValidateSymbolName(name, "块名");
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name)) throw new CliError("already_exists", $"图块 “{name}” 已存在。", "换个名字，或先 remove 旧的（未被引用时）");

            var src = Factory.SplitRefs(map["entities"]).Select(entityRef).Distinct().ToList();
            if (src.Count == 0) throw new CliError("invalid_value", "entities 至少要有一个实体。");
            var basePt = map.TryGetValue("base", out var bs) ? Acad.Pt("base", bs) : Point3d.Origin;

            var btr = new BlockTableRecord { Name = name, Origin = basePt };
            if (map.TryGetValue("description", out var desc)) btr.Comments = desc;
            bt.UpgradeOpen();
            var btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            var ids = new ObjectIdCollection(src.ToArray());
            db.DeepCloneObjects(ids, btrId, new IdMapping(), false);

            if (map.TryGetValue("replace", out var rep) && Values.Bool("replace", rep))
            {
                // 块参照放在第一个源实体所在的空间和图层上
                var first = (Entity)tr.GetObject(src[0], OpenMode.ForRead);
                var owner = first.OwnerId;
                var layer = first.LayerId;
                foreach (var id in src) ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
                var space = (BlockTableRecord)tr.GetObject(owner, OpenMode.ForWrite);
                var br = new BlockReference(basePt, btrId);
                br.SetDatabaseDefaults(db);
                br.LayerId = layer;
                space.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                SyncAttributes(tr, br, null, true);
            }
            return btr;
        }

        public static void ApplyBlock(Transaction tr, BlockTableRecord b, List<KeyValuePair<string, string>> props)
        {
            var type = Schema.FindType("block")!;
            b.UpgradeOpen();
            foreach (var kv in props)
            {
                var key = Schema.CheckProp(type, kv.Key, Verbs.Set).Name;
                if (key == "name")
                {
                    var name = kv.Value.Trim();
                    Acad.ValidateSymbolName(name, "块名");
                    var bt = (BlockTable)tr.GetObject(b.Database.BlockTableId, OpenMode.ForRead);
                    if (!name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && bt.Has(name))
                        throw new CliError("already_exists", $"图块 “{name}” 已存在。");
                    b.Name = name;
                }
                else if (key == "description") b.Comments = kv.Value;
            }
        }

        public static void RemoveBlock(Database db, Transaction tr, BlockTableRecord b)
        {
            int refs = ReferenceCounts(db, tr).TryGetValue(b.ObjectId, out int n) ? n : 0;
            if (refs > 0)
                throw new CliError("in_use", $"图块 “{b.Name}” 还有 {refs} 个参照，不能删除。", $"先删除参照：query \"insert[name={b.Name}]\"");
            var ids = new ObjectIdCollection { b.ObjectId };
            db.Purge(ids);
            if (ids.Count == 0) throw new CliError("in_use", $"图块 “{b.Name}” 被其他块定义引用，不能删除。");
            b.UpgradeOpen();
            b.Erase();
        }

        // ======================= 块参照的属性 =======================

        /// <summary>
        /// 块参照的属性值，写成 TAG=值;TAG=值。
        /// create=true（新插入）时按块定义补建属性引用（AutoCAD API 插入块不会自动带属性），
        /// values 里的值覆盖定义中的默认值；否则只改已有的属性。
        /// </summary>
        public static void SyncAttributes(Transaction tr, BlockReference br, string? values, bool create)
        {
            var want = ParseAttributes(values);
            if (create)
            {
                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (def.HasAttributeDefinitions)
                    foreach (ObjectId id in def)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad) || ad.Constant) continue;
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        ar.TextString = want.TryGetValue(ad.Tag, out var v) ? v : ad.TextString;
                        br.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                        want.Remove(ad.Tag);
                    }
            }
            else
            {
                foreach (ObjectId id in br.AttributeCollection)
                {
                    var ar = (AttributeReference)tr.GetObject(id, OpenMode.ForRead);
                    if (!want.TryGetValue(ar.Tag, out var v)) continue;
                    ar.UpgradeOpen();
                    ar.TextString = v;
                    want.Remove(ar.Tag);
                }
            }
            if (want.Count > 0)
                throw new CliError("invalid_value", $"块没有属性：{string.Join("、", want.Keys)}。",
                    $"get \"/block[@name={((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name}]\" 查看 attributes");
        }

        public static string AttributesOf(Transaction tr, BlockReference br) =>
            string.Join(";", br.AttributeCollection.Cast<ObjectId>()
                .Select(id => (AttributeReference)tr.GetObject(id, OpenMode.ForRead))
                .Select(a => a.Tag + "=" + a.TextString));

        private static Dictionary<string, string> ParseAttributes(string? s)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(s)) return map;
            foreach (var part in s!.Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) throw new CliError("invalid_value", $"attributes 应写成 TAG=值;TAG=值，收到 “{part}”。");
                map[part.Substring(0, eq).Trim()] = part.Substring(eq + 1);
            }
            return map;
        }

        // ======================= 线型 =======================

        public static string LinetypePath(string name) => $"/linetype[@name={name}]";

        public static IEnumerable<LinetypeTableRecord> Linetypes(Database db, Transaction tr)
        {
            var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            return lt.Cast<ObjectId>().Select(id => (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead));
        }

        public static Node Linetype(Database db, LinetypeTableRecord l)
        {
            var p = new Dictionary<string, string> { ["name"] = l.Name };
            if (!string.IsNullOrEmpty(l.AsciiDescription)) p["description"] = l.AsciiDescription;
            if (l.PatternLength > 0) p["patternLength"] = Values.Num(l.PatternLength);
            if (db.Celtype == l.ObjectId) p["current"] = "true";
            return new Node { Path = LinetypePath(l.Name), Type = "linetype", Props = p };
        }

        public static LinetypeTableRecord LoadLinetype(Database db, Transaction tr, List<KeyValuePair<string, string>> props)
        {
            var type = Schema.FindType("linetype")!;
            string? name = null;
            foreach (var kv in props)
                if (Schema.CheckProp(type, kv.Key, Verbs.Add).Name == "name") name = kv.Value.Trim();
            if (string.IsNullOrEmpty(name)) throw new CliError("missing_property", "linetype 缺少属性 name。", "例：--prop name=CENTER");
            return (LinetypeTableRecord)tr.GetObject(Acad.EnsureLinetype(db, tr, name!), OpenMode.ForRead);
        }

        public static void RemoveLinetype(Database db, LinetypeTableRecord l)
        {
            if (l.ObjectId == db.ContinuousLinetype || l.ObjectId == db.ByLayerLinetype || l.ObjectId == db.ByBlockLinetype)
                throw new CliError("invalid_value", $"线型 “{l.Name}” 不能删除。");
            if (db.Celtype == l.ObjectId) throw new CliError("invalid_value", $"“{l.Name}” 是当前线型，不能删除。");
            var ids = new ObjectIdCollection { l.ObjectId };
            db.Purge(ids);
            if (ids.Count == 0) throw new CliError("in_use", $"线型 “{l.Name}” 仍被图层或实体使用，不能删除。",
                $"查找使用者：query \"entity[linetype={l.Name}]\"、query \"layer[linetype={l.Name}]\"");
            l.UpgradeOpen();
            l.Erase();
        }
    }
}
