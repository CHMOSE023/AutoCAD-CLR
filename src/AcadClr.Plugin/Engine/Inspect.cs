using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// measure / check：只读的测量与空间校验。结果用 Node 表示：
    /// 一个汇总节点（result / 合计）+ 若干明细节点（每个实体的测量值、每处问题）。
    ///
    /// check 移植自 AutoCADMCP：判定基于轴对齐包围盒，对建筑平面里的矩形房间足够准；
    /// 斜放、异形实体偏保守（可能多报）。容差 1 个图形单位：小于它的搭接视为共边，不算重叠。
    /// 这组检查的起因是 AutoCADMCP 实测中两次“面积对、位置错”（车位撞上行道树、房间跑出外墙），
    /// 面积校核发现不了，只能看图；包围盒检查把“位置对不对”变成可判定的。
    /// </summary>
    internal static class Inspect
    {
        private const double Tol = 1.0;

        public sealed class Result
        {
            public Node Summary { get; } = new Node();
            public List<Node> Details { get; } = new List<Node>();
        }

        // ------------------------------------------------------------------ measure

        public static Result Distance(Dictionary<string, string> p)
        {
            var a = Values.Point("from", p["from"]);
            var b = Values.Point("to", p["to"]);
            double dx = b[0] - a[0], dy = b[1] - a[1];
            var r = new Result();
            r.Summary.Type = "distance";
            r.Summary.Props["distance"] = Values.Num(Math.Sqrt(dx * dx + dy * dy));
            r.Summary.Props["dx"] = Values.Num(dx);
            r.Summary.Props["dy"] = Values.Num(dy);
            r.Summary.Props["angle"] = Values.Num(Values.RadToDeg(Math.Atan2(dy, dx)));
            return r;
        }

        public static Result Convert(Database db, Dictionary<string, string> p)
        {
            double value = Values.Double("value", p["value"]);
            string from;
            if (p.TryGetValue("from", out var f)) from = f.Trim();
            else
            {
                from = Acad.Fmt(db.Insunits);
                if (db.Insunits == UnitsValue.Undefined)
                    throw new CliError("invalid_value", "图形单位是 unitless，无法推断源单位。", "用 --prop from=mm 指定");
            }
            var r = new Result();
            r.Summary.Type = "convert";
            r.Summary.Props["value"] = Values.Num(Values.ConvertLength(value, from, p["to"]));
            r.Summary.Props["unit"] = p["to"].Trim();
            r.Summary.Props["from"] = Values.Num(value) + " " + from;
            return r;
        }

        public static Result Area(Transaction tr, List<Entity> ents)
        {
            var r = new Result();
            double total = 0;
            foreach (var e in ents)
            {
                var n = Detail(tr, e);
                double? area = AreaOf(e);
                if (area == null)
                    throw new CliError("invalid_value", $"{n.Path} 是 {n.Type}，无法计算面积。", "面积只适用于闭合曲线、圆、椭圆、面域、填充");
                n.Props["area"] = Values.Num(area.Value);
                if (PerimeterOf(e) is double per) n.Props["perimeter"] = Values.Num(per);
                if (e is Polyline pl && !pl.Closed) n.Props["note"] = "未闭合，按首尾相连计算";
                total += area.Value;
                r.Details.Add(n);
            }
            r.Summary.Type = "area";
            r.Summary.Props["count"] = ents.Count.ToString(CultureInfo.InvariantCulture);
            r.Summary.Props["totalArea"] = Values.Num(total);
            return r;
        }

        public static Result Length(Transaction tr, List<Entity> ents)
        {
            var r = new Result();
            double total = 0;
            foreach (var e in ents)
            {
                var n = Detail(tr, e);
                double len = LengthOf(e) ?? throw new CliError("invalid_value", $"{n.Path} 是 {n.Type}，没有有限长度。", "长度只适用于直线、多段线、圆弧、圆、椭圆、样条");
                n.Props["length"] = Values.Num(len);
                total += len;
                r.Details.Add(n);
            }
            r.Summary.Type = "length";
            r.Summary.Props["count"] = ents.Count.ToString(CultureInfo.InvariantCulture);
            r.Summary.Props["totalLength"] = Values.Num(total);
            return r;
        }

        private static double? AreaOf(Entity e)
        {
            switch (e)
            {
                case Circle c: return Math.PI * c.Radius * c.Radius;
                case Ellipse el when Math.Abs(el.EndAngle - el.StartAngle - 2 * Math.PI) < 1e-9:
                    return Math.PI * el.MajorRadius * el.MinorRadius;
                case Region rg: return rg.Area;
                case Hatch h: return Try(() => h.Area);
                case Curve cv when !(cv is Line) && !(cv is Xline) && !(cv is Ray): return Try(() => cv.Area);
                default: return null;
            }
        }

        private static double? PerimeterOf(Entity e) =>
            e is Region rg ? rg.Perimeter : e is Curve ? LengthOf(e) : null;

        private static double? LengthOf(Entity e)
        {
            if (e is Xline || e is Ray || !(e is Curve c)) return null;
            return Try(() => c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam));
        }

        private static double? Try(Func<double> f)
        {
            try { return f(); }
            catch (AcRx.Exception) { return null; }
        }

        // ------------------------------------------------------------------ check

        private readonly struct Box
        {
            public readonly Node Node;
            public readonly double MinX, MinY, MaxX, MaxY;

            public Box(Node node, Extents3d e)
            {
                Node = node;
                MinX = e.MinPoint.X; MinY = e.MinPoint.Y; MaxX = e.MaxPoint.X; MaxY = e.MaxPoint.Y;
            }

            public string Range => $"{Values.Num(MinX)},{Values.Num(MinY)};{Values.Num(MaxX)},{Values.Num(MaxY)}";
        }

        private static Box BoxOf(Transaction tr, Entity e)
        {
            var n = Detail(tr, e);
            try { return new Box(n, e.GeometricExtents); }
            catch (AcRx.Exception) { throw new CliError("invalid_value", $"{n.Path} 没有几何范围（例如空文字），无法校验。"); }
        }

        public static Result Overlap(Transaction tr, List<Entity> ents, Dictionary<string, string> p)
        {
            if (ents.Count < 2) throw new CliError("invalid_request", $"check overlap 需要至少两个实体，目标只匹配到 {ents.Count} 个。");
            double minArea = p.TryGetValue("minArea", out var m) ? Values.Double("minArea", m) : 0;
            var boxes = ents.Select(e => BoxOf(tr, e)).ToList();

            var r = new Result();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i];
                    var b = boxes[j];
                    double ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    double oy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                    if (ox <= Tol || oy <= Tol || ox * oy < minArea) continue; // 不相交、只是共边，或小于阈值
                    r.Details.Add(Issue(a.Node, "overlap", ("with", b.Node.Path), ("size", Values.Num(ox) + " x " + Values.Num(oy)), ("area", Values.Num(ox * oy))));
                }
            Verdict(r, "overlap", boxes.Count, r.Details.Count == 0 ? "两两都不重叠（共边不算）" : $"{r.Details.Count} 处重叠");
            return r;
        }

        public static Result Inside(Transaction tr, List<Entity> ents, Entity boundary)
        {
            var bound = BoxOf(tr, boundary);
            var boxes = ents.Where(e => e.ObjectId != boundary.ObjectId).Select(e => BoxOf(tr, e)).ToList();

            var r = new Result();
            foreach (var b in boxes)
            {
                var dirs = new List<string>();
                void Out(string dir, double d) { if (d > Tol) dirs.Add(dir + " " + Values.Num(d)); }
                Out("左", bound.MinX - b.MinX);
                Out("右", b.MaxX - bound.MaxX);
                Out("下", bound.MinY - b.MinY);
                Out("上", b.MaxY - bound.MaxY);
                if (dirs.Count > 0) r.Details.Add(Issue(b.Node, "outside", ("outBy", string.Join("、", dirs)), ("bbox", b.Range)));
            }
            Verdict(r, "inside", boxes.Count, r.Details.Count == 0 ? "全部在边界内" : $"{r.Details.Count} 个越界，outBy 为各方向超出的距离");
            r.Summary.Props["boundary"] = bound.Node.Path;
            return r;
        }

        public static Result Adjacent(Transaction tr, Entity first, Entity second, Dictionary<string, string> p)
        {
            double gap = p.TryGetValue("gap", out var g) ? Values.Double("gap", g) : 0;
            var a = BoxOf(tr, first);
            var b = BoxOf(tr, second);

            // 两个方向各算一次：正数为搭接长度，负数为间距
            double ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            double oy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            double gapX = -ox, gapY = -oy;
            bool sideBySide = oy > Tol && gapX >= -Tol && gapX <= gap + Tol;
            bool stacked = ox > Tol && gapY >= -Tol && gapY <= gap + Tol;

            string result, detail;
            if (ox > Tol && oy > Tol)
            {
                result = "OVERLAP";
                detail = $"两者重叠 {Values.Num(ox)} x {Values.Num(oy)}，不是相邻而是压在一起";
            }
            else if (sideBySide)
            {
                result = "PASS";
                detail = $"左右相邻，水平间距 {Values.Num(Math.Max(gapX, 0))}，竖向搭接 {Values.Num(oy)}";
            }
            else if (stacked)
            {
                result = "PASS";
                detail = $"上下相邻，竖向间距 {Values.Num(Math.Max(gapY, 0))}，水平搭接 {Values.Num(ox)}";
            }
            else
            {
                result = "FAIL";
                double dx = Math.Max(gapX, 0), dy = Math.Max(gapY, 0);
                detail = ox <= Tol && oy <= Tol
                    ? $"两个方向都不搭接（错开 {Values.Num(dx)} x {Values.Num(dy)}），只是斜对角，不算相邻"
                    : $"间距 {Values.Num(Math.Max(dx, dy))} 超过允许的 {Values.Num(gap)}";
            }

            var r = new Result();
            r.Summary.Type = "adjacent";
            r.Summary.Props["result"] = result;
            r.Summary.Props["a"] = a.Node.Path;
            r.Summary.Props["b"] = b.Node.Path;
            r.Summary.Props["gap"] = Values.Num(gap);
            r.Summary.Props["detail"] = detail;
            return r;
        }

        private static void Verdict(Result r, string type, int checkedCount, string note)
        {
            r.Summary.Type = type;
            r.Summary.Props["result"] = r.Details.Count == 0 ? "PASS" : "FAIL";
            r.Summary.Props["checked"] = checkedCount.ToString(CultureInfo.InvariantCulture);
            r.Summary.Props["note"] = note;
        }

        /// <summary>明细节点：实体路径 + 类型 + 图层；文字、块参照带上内容 / 块名，便于看懂是哪一个。</summary>
        private static Node Detail(Transaction tr, Entity e)
        {
            var type = Nodes.TypeOf(e);
            var n = new Node { Path = Nodes.EntityPath(tr, type, e), Type = type };
            n.Props["layer"] = e.Layer;
            string? label = e switch
            {
                DBText t => t.TextString,
                MText m => m.Text,
                BlockReference br => br.Name,
                _ => null,
            };
            if (!string.IsNullOrEmpty(label)) n.Props["label"] = label!;
            return n;
        }

        private static Node Issue(Node subject, string type, params (string key, string value)[] props)
        {
            var n = new Node { Path = subject.Path, Type = type };
            foreach (var kv in subject.Props) n.Props[kv.Key] = kv.Value;
            foreach (var (k, v) in props) n.Props[k] = v;
            return n;
        }
    }
}
