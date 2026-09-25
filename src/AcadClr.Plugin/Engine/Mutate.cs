using System;
using System.Collections.Generic;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 实体 / 图层 / 文档的创建与属性修改。
    /// add 的做法是“先建一个空实体、入库，再走与 set 完全相同的属性写入流程”，两条路径共用一套代码。
    /// </summary>
    internal static class Mutate
    {
        private static readonly HashSet<string> TransformProps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "move", "rotate", "scale", "base" };

        /// <summary>
        /// 属性执行顺序：必填的几何属性 → 其他属性 → 变换（move/rotate/scale）。
        /// 同一优先级内保持书写顺序（例如先 points 后 width，多段线才有顶点可设宽度）。
        /// </summary>
        private static List<KeyValuePair<string, string>> Order(TypeDef type, List<KeyValuePair<string, string>> props) =>
            props.Select((kv, i) => new
                 {
                     kv, i,
                     pr = TransformProps.Contains(kv.Key) && Schema.CommonEntityProps.Contains(type.Find(kv.Key)!) ? 2
                        : type.Find(kv.Key)?.Required == true ? 0 : 1,
                 })
                 .OrderBy(x => x.pr).ThenBy(x => x.i).Select(x => x.kv).ToList();

        // ======================= 实体 =======================

        /// <param name="resolve">把路径或句柄解析成 ObjectId（填充边界、标注目标用）。</param>
        public static Entity CreateEntity(Database db, Transaction tr, TypeDef type, List<KeyValuePair<string, string>> props,
            Func<string, ObjectId> resolve, ObjectId space)
        {
            foreach (var kv in props) Schema.CheckProp(type, kv.Key, Verbs.Add);
            var missing = type.Props.Where(p => p.Required && !props.Any(kv => kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                                    .Select(p => p.Name).ToList();
            if (missing.Count > 0)
                throw new CliError("missing_property", $"添加 {type.Name} 缺少必填属性：{string.Join("、", missing)}。",
                    $"运行 acadclr help {type.Name} 查看示例。");

            if (Factory.Handles(type.Name))
            {
                var map = props.ToDictionary(kv => type.Find(kv.Key)!.Name, kv => kv.Value);
                var consumed = new HashSet<string>();
                var made = Factory.Create(db, tr, type.Name, map, resolve, consumed, space);
                ApplyEntity(db, tr, made, type, props.Where(kv => !consumed.Contains(type.Find(kv.Key)!.Name)).ToList(), Verbs.Add);
                SyncLeaderAnnotation(tr, made);
                return made;
            }

            Entity e;
            switch (type.Name)
            {
                case "line": e = new Line(); break;
                case "circle": e = new Circle { Normal = Vector3d.ZAxis, Radius = 1 }; break;
                case "arc": e = new Arc { Normal = Vector3d.ZAxis, Radius = 1 }; break;
                case "polyline": e = new Polyline(); break;
                case "text": e = new DBText { Height = db.Textsize, TextStyleId = db.Textstyle }; break;
                case "mtext": e = new MText { TextHeight = db.Textsize, TextStyleId = db.Textstyle }; break;
                case "point": e = new DBPoint(); break;
                case "insert":
                    var name = props.First(kv => kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase)).Value;
                    e = new BlockReference(Point3d.Origin, Acad.FindBlock(db, tr, name));
                    break;
                default:
                    throw new CliError("unsupported_type", $"不支持添加类型 “{type.Name}”。",
                        "可添加：" + string.Join("、", Schema.AddableTypes));
            }

            e.SetDatabaseDefaults(db);
            if (e is Polyline pl0)
            {
                // 空多段线入库会失败：先放两个占位顶点，随后由 points 属性覆盖
                pl0.AddVertexAt(0, Point2d.Origin, 0, 0, 0);
                pl0.AddVertexAt(1, new Point2d(1, 0), 0, 0, 0);
            }
            if (e is DBText t0) t0.TextString = "_";
            if (e is MText m0) m0.Contents = "_";

            var ms = (BlockTableRecord)tr.GetObject(space, OpenMode.ForWrite);
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);

            ApplyEntity(db, tr, e, type, props, Verbs.Add);

            // 含中文又没指定样式：当前样式显示不了中文时改用宋体样式
            bool styled = props.Any(kv => kv.Key.Equals("style", StringComparison.OrdinalIgnoreCase));
            if (!styled && e is DBText dt) { dt.TextStyleId = Factory.TextStyleFor(db, tr, dt.TextStyleId, dt.TextString); if (dt.Justify != AttachmentPoint.BaseLeft) dt.AdjustAlignment(db); }
            if (!styled && e is MText mt) mt.TextStyleId = Factory.TextStyleFor(db, tr, mt.TextStyleId, mt.Contents);
            return e;
        }

        /// <summary>引线的注释文字跟随引线的图层与颜色。</summary>
        private static void SyncLeaderAnnotation(Transaction tr, Entity e)
        {
            if (!(e is Leader ld) || ld.Annotation.IsNull) return;
            var mt = (Entity)tr.GetObject(ld.Annotation, OpenMode.ForWrite);
            mt.LayerId = ld.LayerId;
            mt.Color = ld.Color;
        }

        public static void ApplyEntity(Database db, Transaction tr, Entity e, TypeDef type, List<KeyValuePair<string, string>> props, Verbs verb)
        {
            foreach (var kv in props) Schema.CheckProp(type, kv.Key, verb);

            Point3d? basePt = null;
            var b = props.FirstOrDefault(kv => kv.Key.Equals("base", StringComparison.OrdinalIgnoreCase));
            if (b.Key != null) basePt = Acad.Pt("base", b.Value);

            bool textTouched = false;
            string? attributes = null;
            foreach (var kv in Order(type, props))
            {
                string key = type.Find(kv.Key)!.Name, v = kv.Value;
                switch (key)
                {
                    case "layer": e.LayerId = Acad.EnsureLayer(db, tr, v); continue;
                    case "color": e.Color = Acad.ToColor(Values.Color(key, v)); continue;
                    case "linetype":
                        if (v.Equals("bylayer", StringComparison.OrdinalIgnoreCase) || v.Equals("byblock", StringComparison.OrdinalIgnoreCase))
                            e.Linetype = v.Equals("bylayer", StringComparison.OrdinalIgnoreCase) ? "ByLayer" : "ByBlock";
                        else e.LinetypeId = Acad.EnsureLinetype(db, tr, v);
                        continue;
                    case "lineWeight": e.LineWeight = (LineWeight)Values.LineWeight(key, v); continue;
                    case "base": continue;
                    case "move":
                        var d = Values.Vector(key, v);
                        e.TransformBy(Matrix3d.Displacement(new Vector3d(d[0], d[1], d[2])));
                        continue;
                    case "rotate":
                        e.TransformBy(Matrix3d.Rotation(Values.DegToRad(Values.Double(key, v)), Vector3d.ZAxis, basePt ?? Center(e)));
                        continue;
                    // insert、hatch、dimension、leader 有自己的 scale（比例属性），只有公共定义的 scale 才是缩放变换
                    case "scale" when Schema.CommonEntityProps.Contains(type.Find(key)!):
                        e.TransformBy(Matrix3d.Scaling(Values.Positive(key, v), basePt ?? Center(e)));
                        continue;
                }

                switch (e)
                {
                    case Line l:
                        if (key == "start") l.StartPoint = Acad.Pt(key, v);
                        else if (key == "end") l.EndPoint = Acad.Pt(key, v);
                        break;
                    case Arc a:
                        if (key == "center") a.Center = Acad.Pt(key, v);
                        else if (key == "radius") a.Radius = Values.Positive(key, v);
                        else if (key == "startAngle") a.StartAngle = Values.DegToRad(Values.Double(key, v));
                        else if (key == "endAngle") a.EndAngle = Values.DegToRad(Values.Double(key, v));
                        break;
                    case Circle c:
                        if (key == "center") c.Center = Acad.Pt(key, v);
                        else if (key == "radius") c.Radius = Values.Positive(key, v);
                        break;
                    case Polyline pl:
                        if (key == "points") SetPoints(pl, Values.Points(key, v));
                        else if (key == "closed") pl.Closed = Values.Bool(key, v);
                        else if (key == "width") pl.ConstantWidth = Values.Double(key, v);
                        break;
                    case DBText t:
                        textTouched = true;
                        SetText(t, key, v, db, tr);
                        break;
                    case MText m:
                        if (key == "text") m.Contents = v;
                        else if (key == "position") m.Location = Acad.Pt(key, v);
                        else if (key == "height") m.TextHeight = Values.Positive(key, v);
                        else if (key == "width") m.Width = Values.Double(key, v);
                        else if (key == "rotation") m.Rotation = Values.DegToRad(Values.Double(key, v));
                        else if (key == "style") m.TextStyleId = Acad.FindTextStyle(db, tr, v);
                        else if (key == "justify") m.Attachment = Acad.Justify(key, v, true);
                        break;
                    case DBPoint p:
                        if (key == "position") p.Position = Acad.Pt(key, v);
                        break;
                    case Viewport vp:
                        Layouts.SetViewport(vp, key, v);
                        break;
                    case Ellipse el:
                        SetEllipse(el, key, v);
                        break;
                    case Xline xl:
                        if (key == "position") xl.BasePoint = Acad.Pt(key, v);
                        else if (key == "direction") xl.UnitDir = Acad.Vec(key, v).GetNormal();
                        break;
                    case Ray ray:
                        if (key == "position") ray.BasePoint = Acad.Pt(key, v);
                        else if (key == "direction") ray.UnitDir = Acad.Vec(key, v).GetNormal();
                        break;
                    case Hatch h:
                        var pat = key == "pattern" ? v.Trim().ToUpperInvariant() : h.PatternName;
                        var sc = key == "scale" ? Values.Positive(key, v) : h.PatternScale;
                        var an = key == "angle" ? Values.DegToRad(Values.Double(key, v)) : h.PatternAngle;
                        Factory.SetPattern(h, pat, sc, an);
                        h.EvaluateHatch(true);
                        break;
                    case Leader ld:
                        if (key == "scale") { ld.Dimscale = Values.Positive(key, v); ld.EvaluateLeader(); }
                        break;
                    case Dimension dim:
                        SetDimension(db, tr, dim, key, v);
                        break;
                    case BlockReference br:
                        if (key == "name") br.BlockTableRecord = Acad.FindBlock(db, tr, v);
                        else if (key == "position") br.Position = Acad.Pt(key, v);
                        else if (key == "scale") br.ScaleFactors = new Scale3d(Values.Positive(key, v));
                        else if (key == "rotation") br.Rotation = Values.DegToRad(Values.Double(key, v));
                        else if (key == "attributes") attributes = v;
                        break;
                }
            }

            // 块属性在位置、比例、旋转都设好之后再建 / 改：属性引用按块参照的变换定位
            if (e is BlockReference bref && (verb == Verbs.Add || attributes != null))
                Symbols.SyncAttributes(tr, bref, attributes, verb == Verbs.Add);

            // 非左对齐的单行文字要按对齐点重新计算插入点，否则显示位置不对
            if (textTouched && e is DBText dt && dt.Justify != AttachmentPoint.BaseLeft)
                dt.AdjustAlignment(db);
            // 标注的图形块要重新生成，否则后台数据库里的新标注 / 改过的标注显示不出来
            if (e is Dimension d0) d0.RecomputeDimensionBlock(true);
        }

        private static void SetEllipse(Ellipse el, string key, string v)
        {
            var center = el.Center;
            var major = el.MajorAxis;
            var ratio = el.RadiusRatio;
            var s = el.StartAngle;
            var en = el.EndAngle;
            switch (key)
            {
                case "center": center = Acad.Pt(key, v); break;
                case "majorAxis": major = Acad.Vec(key, v); break;
                case "ratio": ratio = Factory.Ratio(v); break;
                case "startAngle": s = Values.DegToRad(Values.Double(key, v)); break;
                case "endAngle": en = Values.DegToRad(Values.Double(key, v)); break;
                default: return;
            }
            el.Set(center, el.Normal, major, ratio, s, en);
        }

        private static void SetDimension(Database db, Transaction tr, Dimension d, string key, string v)
        {
            switch (key)
            {
                case "text": d.DimensionText = v; return;
                case "style": d.DimensionStyle = Factory.FindDimStyle(db, tr, v); return;
                case "scale": d.Dimscale = Values.Positive(key, v); return;
            }
            switch (d)
            {
                case RotatedDimension rd:
                    if (key == "p1") rd.XLine1Point = Acad.Pt(key, v);
                    else if (key == "p2") rd.XLine2Point = Acad.Pt(key, v);
                    else if (key == "dimLine") rd.DimLinePoint = Acad.Pt(key, v);
                    else if (key == "rotation") rd.Rotation = Values.DegToRad(Values.Double(key, v));
                    return;
                case AlignedDimension ad:
                    if (key == "p1") ad.XLine1Point = Acad.Pt(key, v);
                    else if (key == "p2") ad.XLine2Point = Acad.Pt(key, v);
                    else if (key == "dimLine") ad.DimLinePoint = Acad.Pt(key, v);
                    else break;
                    return;
                case Point3AngularDimension an:
                    if (key == "p1") an.XLine1Point = Acad.Pt(key, v);
                    else if (key == "p2") an.XLine2Point = Acad.Pt(key, v);
                    else if (key == "dimLine") an.ArcPoint = Acad.Pt(key, v);
                    else break;
                    return;
            }
            throw new CliError("unsupported_property", $"{Nodes.DimensionKind(d)} 标注不支持修改 {key}。",
                "可修改：text、style、scale；线性 / 对齐 / 角度标注还可改 p1、p2、dimLine（线性另有 rotation）");
        }

        private static void SetText(DBText t, string key, string v, Database db, Transaction tr)
        {
            switch (key)
            {
                case "text": t.TextString = v; break;
                case "height": t.Height = Values.Positive(key, v); break;
                case "rotation": t.Rotation = Values.DegToRad(Values.Double(key, v)); break;
                case "style": t.TextStyleId = Acad.FindTextStyle(db, tr, v); break;
                case "position":
                    var p = Acad.Pt(key, v);
                    if (t.Justify == AttachmentPoint.BaseLeft) t.Position = p; else t.AlignmentPoint = p;
                    break;
                case "justify":
                    // 保持用户看到的定位点不变，只改对齐方式
                    var anchor = t.Justify == AttachmentPoint.BaseLeft ? t.Position : t.AlignmentPoint;
                    t.Justify = Acad.Justify(key, v, false);
                    if (t.Justify == AttachmentPoint.BaseLeft) t.Position = anchor; else t.AlignmentPoint = anchor;
                    break;
            }
        }

        private static void SetPoints(Polyline pl, List<double[]> pts)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p = new Point2d(pts[i][0], pts[i][1]);
                if (i < pl.NumberOfVertices)
                {
                    pl.SetPointAt(i, p);
                    pl.SetBulgeAt(i, 0);
                }
                else pl.AddVertexAt(i, p, 0, 0, 0);
            }
            while (pl.NumberOfVertices > pts.Count) pl.RemoveVertexAt(pl.NumberOfVertices - 1);
            pl.Elevation = pts[0][2];
        }

        private static Point3d Center(Entity e)
        {
            var ext = e.GeometricExtents;
            return ext.MinPoint + (ext.MaxPoint - ext.MinPoint) / 2;
        }

        /// <summary>深度克隆一个实体到模型空间（块参照的属性、扩展数据一并复制）。</summary>
        public static Entity Clone(Database db, Transaction tr, ObjectId source)
        {
            var ids = new ObjectIdCollection { source };
            var map = new IdMapping();
            // 副本放在源实体所在的空间（模型空间或某个布局）
            db.DeepCloneObjects(ids, tr.GetObject(source, OpenMode.ForRead).OwnerId, map, false);
            return (Entity)tr.GetObject(map[source].Value, OpenMode.ForWrite);
        }

        // ======================= 图层 =======================

        public static LayerTableRecord CreateLayer(Database db, Transaction tr, List<KeyValuePair<string, string>> props)
        {
            var type = Schema.FindType("layer")!;
            foreach (var kv in props) Schema.CheckProp(type, kv.Key, Verbs.Add);
            var name = props.FirstOrDefault(kv => kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(name))
                throw new CliError("missing_property", "添加 layer 缺少必填属性：name。", "例：--prop name=WALL");
            if (!Acad.FindLayer(db, tr, name).IsNull)
                throw new CliError("already_exists", $"图层 “{name}” 已存在。", $"修改请用 set \"/layer[@name={name}]\"");

            var id = Acad.EnsureLayer(db, tr, name);
            var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            ApplyLayer(db, tr, rec, props.Where(kv => !kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase)).ToList(), Verbs.Add);
            return rec;
        }

        public static void ApplyLayer(Database db, Transaction tr, LayerTableRecord l, List<KeyValuePair<string, string>> props, Verbs verb)
        {
            var type = Schema.FindType("layer")!;
            foreach (var kv in props) Schema.CheckProp(type, kv.Key, verb);

            foreach (var kv in props)
            {
                string key = type.Find(kv.Key)!.Name, v = kv.Value;
                switch (key)
                {
                    case "name":
                        if (l.Name == "0" || l.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
                            throw new CliError("invalid_value", $"图层 “{l.Name}” 不能重命名。");
                        Acad.ValidateSymbolName(v, "图层名");
                        if (!Acad.FindLayer(db, tr, v).IsNull && !v.Equals(l.Name, StringComparison.OrdinalIgnoreCase))
                            throw new CliError("already_exists", $"图层 “{v}” 已存在。");
                        l.Name = v;
                        break;
                    case "color":
                        var c = Values.Color(key, v);
                        if (c.Kind == ColorSpec.Kinds.ByLayer || c.Kind == ColorSpec.Kinds.ByBlock)
                            throw new CliError("invalid_value", "图层颜色不能是 bylayer / byblock。");
                        l.Color = Acad.ToColor(c);
                        break;
                    case "linetype": l.LinetypeObjectId = Acad.EnsureLinetype(db, tr, v); break;
                    case "lineWeight":
                        var w = Values.LineWeight(key, v);
                        if (w == -1 || w == -2) throw new CliError("invalid_value", "图层线宽不能是 bylayer / byblock。");
                        l.LineWeight = (LineWeight)w;
                        break;
                    case "on": l.IsOff = !Values.Bool(key, v); break;
                    case "frozen":
                        bool fz = Values.Bool(key, v);
                        if (fz && db.Clayer == l.ObjectId)
                            throw new CliError("invalid_value", $"不能冻结当前图层 “{l.Name}”。", "先把其他图层设为当前：set / --prop currentLayer=0");
                        l.IsFrozen = fz;
                        break;
                    case "locked": l.IsLocked = Values.Bool(key, v); break;
                    case "plot": l.IsPlottable = Values.Bool(key, v); break;
                    case "current":
                        if (!Values.Bool(key, v)) throw new CliError("invalid_value", "current 只能设为 true（把本图层设为当前）。");
                        if (l.IsFrozen) throw new CliError("invalid_value", $"图层 “{l.Name}” 已冻结，不能设为当前。");
                        db.Clayer = l.ObjectId;
                        break;
                }
            }
        }

        public static void RemoveLayer(Database db, Transaction tr, LayerTableRecord l)
        {
            if (l.Name == "0" || l.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
                throw new CliError("invalid_value", $"图层 “{l.Name}” 不能删除。");
            if (db.Clayer == l.ObjectId)
                throw new CliError("invalid_value", $"“{l.Name}” 是当前图层，不能删除。", "先 set / --prop currentLayer=0");
            var ids = new ObjectIdCollection { l.ObjectId };
            db.Purge(ids);
            if (ids.Count == 0)
                throw new CliError("in_use", $"图层 “{l.Name}” 上仍有对象（或被块定义引用），不能删除。",
                    $"先删除或移走其上的实体：query \"entity[layer={l.Name}]\"");
            l.UpgradeOpen();
            l.Erase();
        }

        // ======================= 文档 =======================

        /// <summary>
        /// 修改文档级设置。返回恢复旧值的动作：数据库头变量是否随事务回滚没有文档保证，
        /// 原子批处理整批放弃时由 Executor 显式恢复（已恢复的再写一次同样的值没有副作用）。
        /// </summary>
        public static List<Action> ApplyDocument(Database db, Transaction tr, List<KeyValuePair<string, string>> props)
        {
            var type = Schema.FindType("document")!;
            var undo = new List<Action>();
            int Range(string key, string v, int min, int max)
            {
                int n = Values.Int(key, v);
                if (n < min || n > max) throw new CliError("invalid_value", $"{key} 应在 {min}-{max} 之间，收到 {n}。", "运行 acadclr help document");
                return n;
            }
            foreach (var kv in props)
            {
                var key = Schema.CheckProp(type, kv.Key, Verbs.Set).Name;
                var v = kv.Value;
                switch (key)
                {
                    case "units": { var old = db.Insunits; undo.Add(() => db.Insunits = old); db.Insunits = Acad.Units(key, v); continue; }
                    case "measurement":
                    {
                        var old = db.Measurement;
                        undo.Add(() => db.Measurement = old);
                        var m = v.Trim().ToLowerInvariant();
                        db.Measurement = m == "metric" ? MeasurementValue.Metric : m == "imperial" ? MeasurementValue.English
                            : throw new CliError("invalid_value", $"measurement 只能是 metric 或 imperial，收到 “{v}”。");
                        continue;
                    }
                    case "lunits": { var old = db.Lunits; undo.Add(() => db.Lunits = old); db.Lunits = Range(key, v, 1, 5); continue; }
                    case "luprec": { var old = db.Luprec; undo.Add(() => db.Luprec = old); db.Luprec = Range(key, v, 0, 8); continue; }
                    case "aunits": { var old = db.Aunits; undo.Add(() => db.Aunits = old); db.Aunits = Range(key, v, 0, 4); continue; }
                    case "auprec": { var old = db.Auprec; undo.Add(() => db.Auprec = old); db.Auprec = Range(key, v, 0, 8); continue; }
                    case "ltscale": { var old = db.Ltscale; undo.Add(() => db.Ltscale = old); db.Ltscale = Values.Positive(key, v); continue; }
                    case "dimscale": { var old = db.Dimscale; undo.Add(() => db.Dimscale = old); db.Dimscale = Values.Positive(key, v); continue; }
                }
                if (key == "currentLayer")
                {
                    var id = Acad.FindLayer(db, tr, kv.Value);
                    if (id.IsNull) throw new CliError("not_found", $"图层 “{kv.Value}” 不存在。", "先 add /layers --type layer --prop name=...");
                    var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (rec.IsFrozen) throw new CliError("invalid_value", $"图层 “{kv.Value}” 已冻结，不能设为当前。");
                    var old = db.Clayer;
                    undo.Add(() => db.Clayer = old);
                    db.Clayer = id;
                }
                else throw new CliError("unsupported_property", $"document.{key} 不能 set。");
            }
            return undo;
        }
    }
}
