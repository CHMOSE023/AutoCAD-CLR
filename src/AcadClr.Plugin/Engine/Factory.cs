using System;
using System.Collections.Generic;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 构造函数就需要完整几何数据的实体（椭圆、样条、构造线、射线、填充、标注、引线）。
    /// 这些类型无法“先建空实体再逐个设属性”，由这里一次建好并入库，
    /// 返回已消费的属性名，其余属性（图层、颜色、比例……）再交给 Mutate.ApplyEntity。
    /// 几何算法沿用 AutoCADMCP 中已验证的实现。
    /// </summary>
    internal static class Factory
    {
        private static readonly HashSet<string> Supported = new HashSet<string>
            { "ellipse", "spline", "xline", "ray", "hatch", "dimension", "leader" };

        public static bool Handles(string type) => Supported.Contains(type);

        /// <summary>本次 Create 的目标空间（模型空间或布局的块表记录）。只在主线程上使用。</summary>
        [ThreadStatic] private static ObjectId _space;

        public static Entity Create(Database db, Transaction tr, string type, Dictionary<string, string> p,
            Func<string, ObjectId> resolve, HashSet<string> consumed, ObjectId space)
        {
            _space = space;
            string? Get(string k) { consumed.Add(k); return p.TryGetValue(k, out var v) ? v : null; }
            string Need(string k) => Get(k) ?? throw new CliError("missing_property", $"添加 {type} 缺少属性：{k}。", $"运行 acadclr help {type} 查看示例。");

            Entity e;
            switch (type)
            {
                case "ellipse":
                {
                    var center = Acad.Pt("center", Need("center"));
                    var major = Acad.Vec("majorAxis", Need("majorAxis"));
                    var ratio = Ratio(Need("ratio"));
                    var s = Get("startAngle") is string sa ? Values.DegToRad(Values.Double("startAngle", sa)) : 0;
                    var en = Get("endAngle") is string ea ? Values.DegToRad(Values.Double("endAngle", ea)) : 2 * Math.PI;
                    e = new Ellipse(center, Vector3d.ZAxis, major, ratio, s, en);
                    break;
                }
                case "spline":
                    e = Spline(Values.Points("points", Need("points")), Get("method"), Get("closed"), Get("degree"),
                        Get("fitTolerance"), Get("startTangent"), Get("endTangent"));
                    break;
                case "xline":
                    e = new Xline { BasePoint = Acad.Pt("position", Need("position")), UnitDir = Acad.Vec("direction", Need("direction")).GetNormal() };
                    break;
                case "ray":
                    e = new Ray { BasePoint = Acad.Pt("position", Need("position")), UnitDir = Acad.Vec("direction", Need("direction")).GetNormal() };
                    break;
                case "hatch":
                    return Hatch(db, tr, Need("boundary"), Get("pattern"), Get("scale"), Get("angle"), Get("associative"), resolve);
                case "dimension":
                    e = Dimension(db, tr, Get, Need, resolve);
                    break;
                case "leader":
                    return Leader(db, tr, Values.Points("points", Need("points")), Need("text"), Get("height"));
                default:
                    throw new CliError("unsupported_type", $"Factory 不支持 {type}。");
            }

            // SetDatabaseDefaults 会把标注样式重置为当前样式，先记下再还原
            var dimStyle = (e as Dimension)?.DimensionStyle;
            e.SetDatabaseDefaults(db);
            if (e is Dimension d && dimStyle.HasValue) d.DimensionStyle = dimStyle.Value;
            Append(db, tr, e);
            return e;
        }

        private static void Append(Database db, Transaction tr, Entity e)
        {
            var ms = (BlockTableRecord)tr.GetObject(_space, OpenMode.ForWrite);
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        public static double Ratio(string s)
        {
            var r = Values.Double("ratio", s);
            if (r <= 0 || r > 1) throw new CliError("invalid_value", $"ratio 必须在 (0, 1] 之间，收到 {s}。", "长短轴互换时请调换 majorAxis 方向");
            return r;
        }

        // ---------------- 样条 ----------------

        private static Spline Spline(List<double[]> pts, string? method, string? closedS, string? degreeS, string? tolS, string? stS, string? etS)
        {
            bool cv = (method ?? "fit").Trim().ToLowerInvariant() switch
            {
                "fit" => false,
                "cv" => true,
                _ => throw new CliError("invalid_value", $"method 只能是 fit 或 cv，收到 “{method}”。"),
            };
            bool closed = closedS != null && Values.Bool("closed", closedS);
            int degree = degreeS != null ? Values.Int("degree", degreeS) : 3;
            if (degree < 1 || degree > 11) throw new CliError("invalid_value", "degree 必须在 1-11 之间，常用 3。");
            double tol = tolS != null ? Values.Double("fitTolerance", tolS) : 0;
            if (tol < 0) throw new CliError("invalid_value", "fitTolerance 不能为负。");
            if ((stS == null) != (etS == null)) throw new CliError("invalid_value", "startTangent 与 endTangent 必须成对给出。");

            var p3 = new Point3dCollection();
            foreach (var p in pts) p3.Add(new Point3d(p[0], p[1], p[2]));

            if (!cv)
            {
                if (stS != null)
                {
                    if (closed) throw new CliError("invalid_value", "闭合样条不能同时指定起终点切向，二选一。");
                    return new Spline(p3, Acad.Vec("startTangent", stS), Acad.Vec("endTangent", etS!), KnotParameterizationEnum.Chord, degree, tol);
                }
                if (closed && p3.Count < 3) throw new CliError("invalid_value", "闭合样条至少需要 3 个点。");
                // 第二个参数是 isPeriodic：闭合时不要把起点重复写在末尾
                return new Spline(p3, closed, KnotParameterizationEnum.Chord, degree, tol);
            }

            if (stS != null) throw new CliError("invalid_value", "切向只对 method=fit 有效。");
            int n = p3.Count, least = closed ? degree : degree + 1;
            if (n < least) throw new CliError("invalid_value", $"控制点方式至少需要 {least} 个点，当前 {n} 个。降低 degree 或多给几个点。");

            var cps = new Point3dCollection();
            for (int i = 0; i < n; i++) cps.Add(p3[i]);
            var knots = new DoubleCollection();
            if (closed)
            {
                // 闭合：把首 degree 个控制点接到末尾，配均匀节点。
                // 构造函数的 closed / periodic 参数实测会被忽略（AutoCAD 2020），AutoCAD 只认节点矢量。
                for (int i = 0; i < degree; i++) cps.Add(p3[i]);
                for (int i = 0; i <= cps.Count + degree; i++) knots.Add(i);
            }
            else
            {
                // 夹紧节点矢量：两端各重复 degree 次
                for (int i = 0; i < degree; i++) knots.Add(0.0);
                for (int i = 0; i <= n - degree; i++) knots.Add(i);
                for (int i = 0; i < degree; i++) knots.Add(n - degree);
            }
            var weights = new DoubleCollection();
            for (int i = 0; i < cps.Count; i++) weights.Add(1.0);
            try { return new Spline(degree, false, false, false, cps, knots, weights, 1e-9, 1e-10); }
            catch (AcRx.Exception ex)
            {
                throw new CliError("acad_error", $"按控制点创建样条失败（{ex.ErrorStatus}）。", "可改用 method=fit");
            }
        }

        // ---------------- 填充 ----------------

        private static Entity Hatch(Database db, Transaction tr, string boundary, string? pattern, string? scaleS, string? angleS,
            string? assocS, Func<string, ObjectId> resolve)
        {
            var ids = SplitRefs(boundary).Select(resolve).ToList();
            if (ids.Count == 0) throw new CliError("missing_property", "boundary 为空。");
            foreach (var id in ids)
                if (!(tr.GetObject(id, OpenMode.ForRead) is Curve))
                    throw new CliError("invalid_value", "填充边界必须是曲线（多段线、圆、椭圆、样条、直线、圆弧……）。");

            var name = string.IsNullOrWhiteSpace(pattern) ? "ANSI31" : pattern!.Trim().ToUpperInvariant();
            var hatch = new Hatch();
            Append(db, tr, hatch);
            hatch.SetDatabaseDefaults(db);
            SetPattern(hatch, name, scaleS != null ? Values.Positive("scale", scaleS) : 1.0,
                angleS != null ? Values.DegToRad(Values.Double("angle", angleS)) : 0.0);
            hatch.Associative = assocS == null || Values.Bool("associative", assocS);

            try
            {
                // 每个边界都各自闭合时，一个实体一个环（外框 + 内部孤岛）；否则所有实体共同围成一个环
                bool eachClosed = ids.All(id => ((Curve)tr.GetObject(id, OpenMode.ForRead)).Closed);
                if (eachClosed)
                    foreach (var id in ids) hatch.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { id });
                else
                    hatch.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection(ids.ToArray()));
                hatch.EvaluateHatch(true);
            }
            catch (AcRx.Exception ex)
            {
                throw new CliError("invalid_value", $"填充失败：边界可能不闭合或不首尾相连（{ex.ErrorStatus}）。");
            }
            return hatch;
        }

        /// <summary>图案、比例、角度必须一起设置，并再次 SetHatchPattern 才会生效。</summary>
        public static void SetPattern(Hatch h, string name, double scale, double angle)
        {
            try
            {
                h.SetHatchPattern(HatchPatternType.PreDefined, name);
                h.PatternScale = scale;
                h.PatternAngle = angle;
                h.SetHatchPattern(HatchPatternType.PreDefined, name);
            }
            catch (AcRx.Exception)
            {
                throw new CliError("invalid_value", $"找不到填充图案 “{name}”。", "常用：SOLID、ANSI31、ANSI37、AR-CONC、AR-SAND、NET、GRAVEL、EARTH");
            }
        }

        // ---------------- 标注 ----------------

        private static Entity Dimension(Database db, Transaction tr,
            Func<string, string?> Get, Func<string, string> Need, Func<string, ObjectId> resolve)
        {
            var kind = Need("kind").Trim().ToLowerInvariant();
            var text = Get("text") ?? "";
            var style = Get("style") is string sn ? FindDimStyle(db, tr, sn) : db.Dimstyle;
            var dimLine = Acad.Pt("dimLine", Need("dimLine"));

            switch (kind)
            {
                case "linear":
                    var rot = Get("rotation") is string r ? Values.DegToRad(Values.Double("rotation", r)) : 0;
                    return new RotatedDimension(rot, Acad.Pt("p1", Need("p1")), Acad.Pt("p2", Need("p2")), dimLine, text, style);
                case "aligned":
                    return new AlignedDimension(Acad.Pt("p1", Need("p1")), Acad.Pt("p2", Need("p2")), dimLine, text, style);
                case "angular":
                    return new Point3AngularDimension(Acad.Pt("vertex", Need("vertex")), Acad.Pt("p1", Need("p1")), Acad.Pt("p2", Need("p2")),
                        dimLine, text, style);
                case "radius":
                case "diameter":
                {
                    var target = tr.GetObject(resolve(Need("target")), OpenMode.ForRead);
                    Point3d c; double rad;
                    switch (target)
                    {
                        case Circle ci: c = ci.Center; rad = ci.Radius; break;
                        case Arc ar: c = ar.Center; rad = ar.Radius; break;
                        default: throw new CliError("invalid_value", "半径 / 直径标注的 target 必须是圆或圆弧。");
                    }
                    var dir = dimLine - c;
                    var u = dir.Length < 1e-9 ? Vector3d.XAxis : dir.GetNormal();
                    double leader = Math.Max(0, dir.Length - rad);
                    return kind == "radius"
                        ? (Entity)new RadialDimension(c, c + u * rad, leader, text, style)
                        : new DiametricDimension(c + u * rad, c - u * rad, leader, text, style);
                }
                default:
                    throw new CliError("invalid_value", $"未知的标注类型 kind=“{kind}”。", "可用：linear aligned angular radius diameter");
            }
        }

        public static ObjectId FindDimStyle(Database db, Transaction tr, string name)
        {
            var t = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (t.Has(name)) return t[name];
            var names = new List<string>();
            foreach (ObjectId id in t) names.Add(((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
            throw new CliError("not_found", $"标注样式 “{name}” 不存在。", "现有：" + string.Join("、", names));
        }

        // ---------------- 引线 ----------------

        private static Entity Leader(Database db, Transaction tr, List<double[]> pts, string text, string? heightS)
        {
            var last = pts[pts.Count - 1];
            var mt = new MText
            {
                Location = new Point3d(last[0], last[1], last[2]),
                Contents = text,
                Attachment = AttachmentPoint.MiddleLeft,
            };
            mt.SetDatabaseDefaults(db);
            mt.TextHeight = heightS != null ? Values.Positive("height", heightS) : db.Textsize;
            mt.TextStyleId = TextStyleFor(db, tr, db.Textstyle, text);
            Append(db, tr, mt);

            var ld = new Leader();
            ld.SetDatabaseDefaults(db);
            foreach (var p in pts) ld.AppendVertex(new Point3d(p[0], p[1], p[2]));
            ld.HasArrowHead = true;
            Append(db, tr, ld);
            ld.Annotation = mt.ObjectId;
            ld.EvaluateLeader();
            return ld;
        }

        // ---------------- 中文文字样式 ----------------

        private const string CjkStyleName = "SimSun_CJK";

        /// <summary>
        /// 文字含中文且样式显示不了中文（SHX 字体又没配大字体）时，改用宋体样式，
        /// 否则 AutoCAD 2014 默认的 Standard（txt.shx）会把中文显示成问号。沿用 AutoCADMCP 的做法。
        /// </summary>
        public static ObjectId TextStyleFor(Database db, Transaction tr, ObjectId current, string text)
        {
            if (!text.Any(c => c > 0x7F)) return current;
            var rec = (TextStyleTableRecord)tr.GetObject(current, OpenMode.ForRead);
            // SHX 字体的文件名可能不带扩展名（Standard 样式就是 "txt"），所以按“不是 TrueType”判断
            var ext = System.IO.Path.GetExtension(rec.FileName ?? "").ToLowerInvariant();
            bool trueType = ext == ".ttf" || ext == ".ttc" || ext == ".otf" ||
                            (string.IsNullOrEmpty(rec.FileName) && !string.IsNullOrEmpty(rec.Font.TypeFace));
            bool canShowCjk = trueType || !string.IsNullOrEmpty(rec.BigFontFileName);
            return canShowCjk ? current : EnsureCjkStyle(db, tr);
        }

        private static ObjectId EnsureCjkStyle(Database db, Transaction tr)
        {
            var t = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (t.Has(CjkStyleName)) return t[CjkStyleName];
            t.UpgradeOpen();
            var rec = new TextStyleTableRecord
            {
                Name = CjkStyleName,
                FileName = "simsun.ttc",
                Font = new FontDescriptor("SimSun", false, false, 0, 0),
                TextSize = 0, // 0：字高由各文字自己决定
            };
            var id = t.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }

        /// <summary>“8A;8B” / “/entity[@handle=8A];/model/line[2]” 拆成单个引用。</summary>
        public static List<string> SplitRefs(string s) =>
            s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }
}
