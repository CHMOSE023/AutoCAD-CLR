using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 外部参照。xref 在宿主图中是一个 IsFromExternalReference 的块定义，每次插入是一个块参照。
    ///
    /// 附着 / 重载 / 卸载 / 绑定 / 拆离都是数据库级操作，不能可靠地放在事务里回滚，
    /// 因此这里的写方法要求调用时没有打开的事务，各自按需开短事务（由 Executor 的逐条模式保证）。
    /// </summary>
    internal static class Xrefs
    {
        public static string XrefPath(string name) => $"/xref[@name={name}]";

        public static IEnumerable<BlockTableRecord> All(Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (id.IsErased) continue;
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsFromExternalReference) yield return btr;
            }
        }

        public static ObjectId Find(Database db, Transaction tr, string name) =>
            All(db, tr).FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ObjectId ?? ObjectId.Null;

        public static Node Node(Database db, Transaction tr, BlockTableRecord btr)
        {
            var handles = new List<string>();
            foreach (ObjectId rid in btr.GetBlockReferenceIds(true, true))
                if (tr.GetObject(rid, OpenMode.ForRead) is BlockReference br) handles.Add(br.Handle.ToString());

            return new Node
            {
                Path = XrefPath(btr.Name),
                Type = "xref",
                Props = new Dictionary<string, string>
                {
                    ["name"] = btr.Name,
                    ["path"] = btr.PathName,
                    ["resolvedPath"] = ResolveFile(db, btr.PathName) ?? "",
                    ["overlay"] = btr.IsFromOverlayReference ? "true" : "false",
                    ["status"] = btr.XrefStatus.ToString().ToLowerInvariant(),
                    ["loaded"] = btr.IsUnloaded ? "false" : "true",
                    ["inserts"] = handles.Count.ToString(),
                    ["insertHandles"] = string.Join(",", handles),
                },
            };
        }

        public static Node NodeById(Database db, ObjectId id)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var n = Node(db, tr, (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead));
                tr.Commit();
                return n;
            }
        }

        /// <summary>附着（或覆盖）一个 DWG，并在模型空间插入一个块参照。</summary>
        public static Node Attach(Database db, List<KeyValuePair<string, string>> props)
        {
            var type = Schema.FindType("xref")!;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in props) map[Schema.CheckProp(type, kv.Key, Verbs.Add).Name] = kv.Value;

            if (!map.TryGetValue("path", out var raw) || string.IsNullOrWhiteSpace(raw))
                throw new CliError("missing_property", "添加 xref 缺少必填属性：path。", "例：--prop path=D:\\work\\base.dwg");

            var full = ResolveFile(db, raw) ?? throw new CliError("not_found", $"找不到参照文件：{raw}",
                "相对路径按当前图形所在目录解析；新建且未保存的图形请用绝对路径");
            if (!string.IsNullOrEmpty(db.Filename) &&
                string.Equals(Path.GetFullPath(full), Path.GetFullPath(db.Filename), StringComparison.OrdinalIgnoreCase))
                throw new CliError("invalid_value", "不能把图形自己作为外部参照。");

            var name = map.TryGetValue("name", out var n) && n.Trim().Length > 0 ? n.Trim() : Path.GetFileNameWithoutExtension(full);
            Acad.ValidateSymbolName(name, "参照名");
            bool overlay = map.TryGetValue("overlay", out var ov) && Values.Bool("overlay", ov);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (bt.Has(name)) throw new CliError("already_exists", $"图中已有名为 “{name}” 的块或参照。", "用 --prop name=... 换个名字");
                tr.Commit();
            }

            var btrId = overlay ? db.OverlayXref(full, name) : db.AttachXref(full, name);
            if (btrId.IsNull) throw new CliError("acad_error", $"附着失败：{full}（文件可能损坏或版本过高）。");

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var br = new BlockReference(map.TryGetValue("position", out var pos) ? Acad.Pt("position", pos) : Point3d.Origin, btrId);
                br.SetDatabaseDefaults(db);
                if (map.TryGetValue("scale", out var s)) br.ScaleFactors = new Scale3d(Values.Positive("scale", s));
                if (map.TryGetValue("rotation", out var r)) br.Rotation = Values.DegToRad(Values.Double("rotation", r));
                if (map.TryGetValue("layer", out var layer)) br.LayerId = Acad.EnsureLayer(db, tr, layer);

                var ms = (BlockTableRecord)tr.GetObject(Acad.ModelSpace(db), OpenMode.ForWrite);
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                tr.Commit();
            }
            // 提交后再读：GetBlockReferenceIds 看不到事务里尚未提交的块参照
            return NodeById(db, btrId);
        }

        /// <summary>
        /// 修改外部参照。执行顺序：name → path → loaded / reload → bind。
        /// 绑定后参照不复存在，返回的节点 status 为 bound。
        /// </summary>
        public static Node Apply(Database db, ObjectId id, List<KeyValuePair<string, string>> props)
        {
            var type = Schema.FindType("xref")!;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in props) map[Schema.CheckProp(type, kv.Key, Verbs.Set).Name] = kv.Value;
            var ids = new ObjectIdCollection { id };

            if (map.TryGetValue("name", out var newName) || map.ContainsKey("path"))
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                    if (newName != null && !newName.Equals(btr.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        Acad.ValidateSymbolName(newName, "参照名");
                        if (((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead)).Has(newName))
                            throw new CliError("already_exists", $"图中已有名为 “{newName}” 的块或参照。");
                        btr.Name = newName;
                    }
                    if (map.TryGetValue("path", out var raw))
                    {
                        var full = ResolveFile(db, raw) ?? throw new CliError("not_found", $"找不到参照文件：{raw}");
                        btr.PathName = full;
                    }
                    tr.Commit();
                }
                if (map.ContainsKey("path")) db.ReloadXrefs(ids);
            }

            if (map.TryGetValue("loaded", out var loaded))
            {
                if (Values.Bool("loaded", loaded)) db.ReloadXrefs(ids);
                else db.UnloadXrefs(ids);
            }
            if (map.TryGetValue("reload", out var reload) && Values.Bool("reload", reload))
                db.ReloadXrefs(ids);

            if (map.TryGetValue("bind", out var bind))
            {
                var mode = bind.Trim().ToLowerInvariant();
                if (mode != "bind" && mode != "insert")
                    throw new CliError("invalid_value", $"bind 只能是 bind 或 insert，收到 “{bind}”。");

                string name;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    name = btr.Name;
                    if (btr.XrefStatus != XrefStatus.Resolved)
                        throw new CliError("invalid_value", $"外部参照 “{name}” 状态为 {btr.XrefStatus}，只能绑定已解析的参照。",
                            "先 set --prop reload=true，或修正 path");
                    tr.Commit();
                }
                db.BindXrefs(ids, mode == "insert");
                return new Node
                {
                    Path = XrefPath(name), Type = "xref",
                    Props = new Dictionary<string, string> { ["name"] = name, ["status"] = "bound", ["bind"] = mode },
                };
            }

            return NodeById(db, id);
        }

        public static void Detach(Database db, ObjectId id) => db.DetachXref(id);

        /// <summary>相对路径按图形所在目录解析，缺扩展名补 .dwg；找不到返回 null。</summary>
        public static string? ResolveFile(Database db, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim().Trim('"');
            if (!Path.HasExtension(p)) p += ".dwg";

            if (Path.IsPathRooted(p)) return File.Exists(p) ? Path.GetFullPath(p) : null;

            if (!string.IsNullOrEmpty(db.Filename) && Path.IsPathRooted(db.Filename))
            {
                var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(db.Filename)!, p));
                if (File.Exists(candidate)) return candidate;
            }
            return File.Exists(p) ? Path.GetFullPath(p) : null;
        }
    }
}
