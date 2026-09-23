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
    /// edit 的原生动作（offset / mirror / explode / break / join / array），都在调用方的事务里完成，可参与 batch 回滚。
    /// 几何算法沿用 AutoCADMCP 中已验证的实现。返回生成或修改后的实体。
    /// </summary>
    internal static class Edits
    {
        /// <summary>一次阵列最多生成的实体数，防止写错参数生成几万个图元。</summary>
        private const int MaxArrayItems = 2000;

        public static List<Entity> Run(Database db, Transaction tr, ActionDef action, List<ObjectId> targets,
            Dictionary<string, string> p, bool force)
        {
            switch (action.Name)
            {
                case "offset": return Offset(db, tr, Single(targets, "offset"), p);
                case "mirror": return Mirror(db, tr, targets, p);
                case "explode": return Explode(db, tr, targets);
                case "break": return Break(db, tr, Single(targets, "break"), p);
                case "join": return Join(tr, targets);
                case "array": return Array(db, tr, targets, p, force);
                default: throw new CliError("invalid_request", $"edit {action.Name} 不能在这里执行。");
            }
        }

        private static ObjectId Single(List<ObjectId> ids, string action)
        {
            if (ids.Count != 1) throw new CliError("invalid_request", $"edit {action} 只能作用于一个实体，当前匹配到 {ids.Count} 个。");
            return ids[0];
        }

        /// <summary>新实体放进指定的块表记录（源实体所在的模型空间或布局）。</summary>
        private static void Append(Transaction tr, ObjectId owner, Entity e)
        {
            var ms = (BlockTableRecord)tr.GetObject(owner, OpenMode.ForWrite);
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        // ---------------- offset ----------------

        private static List<Entity> Offset(Database db, Transaction tr, ObjectId id, Dictionary<string, string> p)
        {
            if (!(tr.GetObject(id, OpenMode.ForRead) is Curve curve))
                throw new CliError("invalid_value", "offset 只支持曲线（直线、多段线、圆、圆弧、椭圆、样条）。");
            double d = Math.Abs(Values.Double("distance", p["distance"]));
            if (d < 1e-12) throw new CliError("invalid_value", "distance 不能为 0。");

            // 给了 side：按侧点在曲线的左侧还是右侧决定正负
            if (p.TryGetValue("side", out var sideS))
            {
                var side = Acad.Pt("side", sideS);
                var closest = curve.GetClosestPointTo(side, false);
                var cross = curve.GetFirstDerivative(closest).CrossProduct(side - closest);
                // 闭合曲线的正偏移方向与走向有关：用偏移后是否更靠近侧点来判定更可靠
                d = cross.Z < 0 ? -d : d;
                if (curve.Closed) d = CloserSide(curve, side, d);
            }

            DBObjectCollection res;
            try { res = curve.GetOffsetCurves(d); }
            catch (AcRx.Exception ex) { throw new CliError("invalid_value", $"偏移失败：距离过大或几何不允许（{ex.ErrorStatus}）。"); }

            var list = new List<Entity>();
            foreach (DBObject o in res)
                if (o is Entity e) { Append(tr, curve.OwnerId, e); list.Add(e); }
            if (list.Count == 0) throw new CliError("invalid_value", "偏移没有产生结果（距离过大或几何不允许）。");
            return list;
        }

        private static double CloserSide(Curve curve, Point3d side, double d)
        {
            double Dist(double dd)
            {
                try
                {
                    var c = curve.GetOffsetCurves(dd);
                    var best = c.Cast<Curve>().Min(x => x.GetClosestPointTo(side, false).DistanceTo(side));
                    foreach (DBObject o in c) o.Dispose();
                    return best;
                }
                catch (AcRx.Exception) { return double.MaxValue; }
            }
            return Dist(d) <= Dist(-d) ? d : -d;
        }

        // ---------------- mirror ----------------

        private static List<Entity> Mirror(Database db, Transaction tr, List<ObjectId> ids, Dictionary<string, string> p)
        {
            var axis = Values.Points("axis", p["axis"]);
            var a = new Point3d(axis[0][0], axis[0][1], 0);
            var b = new Point3d(axis[1][0], axis[1][1], 0);
            if (a.DistanceTo(b) < 1e-9) throw new CliError("invalid_value", "镜像轴的两点不能重合。");
            var m = Matrix3d.Mirroring(new Line3d(a, b));
            bool keep = !p.TryGetValue("keep", out var k) || Values.Bool("keep", k);

            var list = new List<Entity>();
            foreach (var id in ids)
            {
                var src = (Entity)tr.GetObject(id, keep ? OpenMode.ForRead : OpenMode.ForWrite);
                if (keep)
                {
                    var c = (Entity)src.Clone();
                    c.TransformBy(m);
                    Append(tr, src.OwnerId, c);
                    list.Add(c);
                }
                else
                {
                    src.TransformBy(m);
                    list.Add(src);
                }
            }
            return list;
        }

        // ---------------- explode ----------------

        private static List<Entity> Explode(Database db, Transaction tr, List<ObjectId> ids)
        {
            var list = new List<Entity>();
            foreach (var id in ids)
            {
                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                var frags = new DBObjectCollection();
                try { ent.Explode(frags); }
                catch (AcRx.Exception) { throw new CliError("invalid_value", $"{Nodes.TypeOf(ent)}（{ent.Handle}）不能分解。"); }
                foreach (DBObject o in frags)
                    if (o is Entity fe) { Append(tr, ent.OwnerId, fe); list.Add(fe); }
                ent.Erase();
            }
            return list;
        }

        // ---------------- break ----------------

        private static List<Entity> Break(Database db, Transaction tr, ObjectId id, Dictionary<string, string> p)
        {
            if (!(tr.GetObject(id, OpenMode.ForWrite) is Curve curve))
                throw new CliError("invalid_value", "break 只支持曲线。");
            var at = Values.Points("at", p["at"].Contains(";") ? p["at"] : p["at"] + ";" + p["at"]);
            var p1 = curve.GetClosestPointTo(new Point3d(at[0][0], at[0][1], 0), false);
            var p2 = curve.GetClosestPointTo(new Point3d(at[1][0], at[1][1], 0), false);
            bool onePoint = p1.DistanceTo(p2) < 1e-9;

            // 分割点必须按曲线参数升序
            var pts = new Point3dCollection();
            if (onePoint) pts.Add(p1);
            else if (curve.GetParameterAtPoint(p1) <= curve.GetParameterAtPoint(p2)) { pts.Add(p1); pts.Add(p2); }
            else { pts.Add(p2); pts.Add(p1); }

            DBObjectCollection pieces;
            try { pieces = curve.GetSplitCurves(pts); }
            catch (AcRx.Exception ex) { throw new CliError("invalid_value", $"打断失败：点不在曲线上或几何不允许（{ex.ErrorStatus}）。"); }
            if (pieces.Count == 0) throw new CliError("invalid_value", "打断没有产生结果。");

            var list = new List<Entity>();
            for (int i = 0; i < pieces.Count; i++)
            {
                // 两点打断：开放曲线丢中间一段；闭合曲线分成两段，丢第一段（两点之间）
                bool drop = !onePoint && (curve.Closed ? i == 0 : i == 1);
                if (drop) { pieces[i].Dispose(); continue; }
                if (pieces[i] is Entity e)
                {
                    e.SetPropertiesFrom(curve);
                    Append(tr, curve.OwnerId, e);
                    list.Add(e);
                }
            }
            curve.Erase();
            return list;
        }

        // ---------------- join ----------------

        private static List<Entity> Join(Transaction tr, List<ObjectId> ids)
        {
            if (ids.Count < 2) throw new CliError("invalid_request", "join 至少需要两个实体。");
            var first = (Entity)tr.GetObject(ids[0], OpenMode.ForWrite);
            var rest = ids.Skip(1).Select(id => (Entity)tr.GetObject(id, OpenMode.ForWrite)).ToArray();

            int joined;
            try { joined = first.JoinEntities(rest).Count; }
            catch (AcRx.Exception ex) { throw new CliError("invalid_value", $"合并失败：类型或几何不兼容（{ex.ErrorStatus}）。", "直线 / 圆弧 / 多段线须首尾相接，且合并到多段线时应在同一平面"); }
            if (joined == 0) throw new CliError("invalid_value", "没有合并任何实体：它们不相接或不共线。");

            foreach (var e in rest)
                if (!e.IsErased && e.ObjectId != first.ObjectId) e.Erase();
            return new List<Entity> { first };
        }

        // ---------------- array ----------------

        private static List<Entity> Array(Database db, Transaction tr, List<ObjectId> ids, Dictionary<string, string> p, bool force)
        {
            int Int(string k, int def) => p.TryGetValue(k, out var s) ? Values.Int(k, s) : def;
            double Dbl(string k, double def) => p.TryGetValue(k, out var s) ? Values.Double(k, s) : def;
            var list = new List<Entity>();

            if (p.TryGetValue("center", out var cs))
            {
                var center = Acad.Pt("center", cs);
                int count = Int("count", 0);
                if (count < 2) throw new CliError("invalid_value", "环形阵列需要 count >= 2（含源实体）。");
                Guard((long)count * ids.Count, force);
                double fill = Dbl("fillAngle", 360);
                bool rotate = !p.TryGetValue("rotateItems", out var ri) || Values.Bool("rotateItems", ri);
                bool full = Math.Abs(Math.Abs(fill) - 360) < 1e-9;
                // 整圈时最后一份与第一份重合，除以 count；不足整圈首尾都占位，除以 count-1
                double step = Values.DegToRad(fill) / (full ? count : count - 1);

                foreach (var id in ids)
                {
                    var src = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    var refPt = SafeCenter(src);
                    for (int i = 1; i < count; i++)
                    {
                        var rot = Matrix3d.Rotation(step * i, Vector3d.ZAxis, center);
                        var c = (Entity)src.Clone();
                        // rotateItems=false：只把位置绕中心转，实体本身保持朝向（树、路灯）
                        c.TransformBy(rotate ? rot : Matrix3d.Displacement(refPt.TransformBy(rot) - refPt));
                        Append(tr, src.OwnerId, c);
                        list.Add(c);
                    }
                }
                return list;
            }

            int rows = Int("rows", 1), cols = Int("cols", 1);
            if (rows < 1 || cols < 1) throw new CliError("invalid_value", "rows、cols 必须 >= 1。");
            if (rows == 1 && cols == 1) throw new CliError("invalid_value", "rows 和 cols 都是 1，没有要生成的副本。", "矩形阵列给 rows / cols，环形阵列给 center + count");
            if (rows > 1 && !p.ContainsKey("rowSpacing")) throw new CliError("missing_property", "rows > 1 时需要 rowSpacing。");
            if (cols > 1 && !p.ContainsKey("colSpacing")) throw new CliError("missing_property", "cols > 1 时需要 colSpacing。");
            Guard((long)rows * cols * ids.Count, force);
            double rs = Dbl("rowSpacing", 0), csp = Dbl("colSpacing", 0);
            double ang = Values.DegToRad(Dbl("angle", 0)), cos = Math.Cos(ang), sin = Math.Sin(ang);

            foreach (var id in ids)
            {
                var src = (Entity)tr.GetObject(id, OpenMode.ForRead);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        if (r == 0 && c == 0) continue; // 源实体本身
                        double dx = c * csp, dy = r * rs;
                        var e = (Entity)src.Clone();
                        e.TransformBy(Matrix3d.Displacement(new Vector3d(dx * cos - dy * sin, dx * sin + dy * cos, 0)));
                        Append(tr, src.OwnerId, e);
                        list.Add(e);
                    }
            }
            return list;
        }

        private static void Guard(long total, bool force)
        {
            if (total > MaxArrayItems && !force)
                throw new CliError("too_many", $"这次阵列会生成约 {total} 个实体，超过上限 {MaxArrayItems}。", "减少数量，或确认无误后加 --force");
        }

        private static Point3d SafeCenter(Entity e)
        {
            try
            {
                var x = e.GeometricExtents;
                return x.MinPoint + (x.MaxPoint - x.MinPoint) / 2;
            }
            catch (AcRx.Exception) { return Point3d.Origin; }
        }
    }
}
