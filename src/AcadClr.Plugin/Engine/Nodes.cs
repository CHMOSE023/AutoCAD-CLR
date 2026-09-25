using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>把 AutoCAD 对象读成 <see cref="Node"/>：类型名、规范路径、属性字典。</summary>
    internal static class Nodes
    {
        /// <summary>实体的类型名。已建模的类型用 schema 名，其余用小写 DXF 名（hatch、dimension、spline…）。</summary>
        public static string TypeOf(Entity e)
        {
            switch (e)
            {
                case Line _: return "line";
                case Arc _: return "arc";
                case Circle _: return "circle";
                case Polyline _: return "polyline";
                case Polyline2d _: return "polyline2d";
                case Polyline3d _: return "polyline3d";
                case AttributeDefinition _: return "attdef";
                case DBText _: return "text";
                case MText _: return "mtext";
                case DBPoint _: return "point";
                case Viewport _: return "viewport";
                case Ellipse _: return "ellipse";
                case Spline _: return "spline";
                case Xline _: return "xline";
                case Ray _: return "ray";
                case Hatch _: return "hatch";
                case Leader _: return "leader";
                case Table _: return "table";
                case Dimension _: return "dimension";
                case BlockReference _: return "insert";
            }
            var dxf = e.GetRXClass().DxfName;
            return string.IsNullOrEmpty(dxf) ? e.GetType().Name.ToLowerInvariant() : dxf.ToLowerInvariant();
        }

        public static TypeDef SchemaOf(string type) => Schema.FindType(type) ?? Schema.GenericEntity(type);

        /// <summary>
        /// 实体的规范路径跟随它实际所在的空间：模型空间 /model/…，图纸空间 /layout[@name=X]/…，
        /// 其他（块定义内部等）用 /entity[@handle=…]。空间写在路径里，不会出现“以为画在布局上其实进了模型空间”的情况。
        /// </summary>
        public static string EntityPath(Transaction tr, string type, Entity e)
        {
            var layout = SpaceName(tr, e);
            if (layout == null) return $"/entity[@handle={e.Handle}]";
            return layout == Layouts.ModelName
                ? $"/model/{type}[@handle={e.Handle}]"
                : $"{Layouts.LayoutPath(layout)}/{type}[@handle={e.Handle}]";
        }

        /// <summary>实体所在空间的布局名（模型空间为 Model）；不在任何布局里时返回 null。</summary>
        public static string? SpaceName(Transaction tr, Entity e)
        {
            if (e.OwnerId == Acad.ModelSpace(e.Database)) return Layouts.ModelName;
            if (tr.GetObject(e.OwnerId, OpenMode.ForRead) is BlockTableRecord btr && btr.IsLayout && !btr.LayoutId.IsNull)
                return ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
            return null;
        }

        public static string LayerPath(string name) => $"/layer[@name={name}]";

        public static Node Entity(Transaction tr, Entity e)
        {
            var type = TypeOf(e);
            var p = new Dictionary<string, string>
            {
                ["handle"] = e.Handle.ToString(),
                ["layer"] = e.Layer,
                ["color"] = Acad.Fmt(e.Color),
                ["linetype"] = e.Linetype,
                ["lineWeight"] = Values.FormatLineWeight((int)e.LineWeight),
            };

            switch (e)
            {
                case Line l:
                    p["start"] = Acad.Fmt(l.StartPoint);
                    p["end"] = Acad.Fmt(l.EndPoint);
                    p["length"] = Values.Num(l.Length);
                    p["angle"] = Values.Num(Values.RadToDeg(l.Angle));
                    break;
                case Arc a:
                    p["center"] = Acad.Fmt(a.Center);
                    p["radius"] = Values.Num(a.Radius);
                    p["startAngle"] = Values.Num(Values.RadToDeg(a.StartAngle));
                    p["endAngle"] = Values.Num(Values.RadToDeg(a.EndAngle));
                    p["length"] = Values.Num(a.Length);
                    break;
                case Circle c:
                    p["center"] = Acad.Fmt(c.Center);
                    p["radius"] = Values.Num(c.Radius);
                    p["diameter"] = Values.Num(c.Diameter);
                    p["area"] = Values.Num(Math.PI * c.Radius * c.Radius);
                    break;
                case Polyline pl:
                    var pts = Enumerable.Range(0, pl.NumberOfVertices).Select(i => Acad.Fmt(pl.GetPoint2dAt(i)));
                    p["points"] = string.Join(";", pts);
                    p["closed"] = pl.Closed ? "true" : "false";
                    p["width"] = TryGet(() => Values.Num(pl.ConstantWidth), "varies");
                    p["count"] = pl.NumberOfVertices.ToString();
                    p["length"] = Values.Num(pl.Length);
                    if (pl.Closed) p["area"] = Values.Num(pl.Area);
                    break;
                case AttributeDefinition _:
                    break;
                case DBText t:
                    p["text"] = t.TextString;
                    p["position"] = Acad.Fmt(t.Justify == AttachmentPoint.BaseLeft ? t.Position : t.AlignmentPoint);
                    p["height"] = Values.Num(t.Height);
                    p["rotation"] = Values.Num(Values.RadToDeg(t.Rotation));
                    p["style"] = Acad.TextStyleName(tr, t.TextStyleId);
                    p["justify"] = Acad.Fmt(t.Justify);
                    break;
                case MText m:
                    p["text"] = m.Contents;
                    p["position"] = Acad.Fmt(m.Location);
                    p["height"] = Values.Num(m.TextHeight);
                    p["width"] = Values.Num(m.Width);
                    p["rotation"] = Values.Num(Values.RadToDeg(m.Rotation));
                    p["style"] = Acad.TextStyleName(tr, m.TextStyleId);
                    p["justify"] = Acad.Fmt(m.Attachment);
                    break;
                case DBPoint pt:
                    p["position"] = Acad.Fmt(pt.Position);
                    break;
                case Viewport vp:
                    p["center"] = Acad.Fmt(vp.CenterPoint);
                    p["width"] = Values.Num(vp.Width);
                    p["height"] = Values.Num(vp.Height);
                    p["viewCenter"] = Acad.Fmt(vp.ViewCenter);
                    p["scale"] = vp.CustomScale > 0 ? Values.Num(1.0 / vp.CustomScale) : "";
                    p["on"] = vp.On ? "true" : "false";
                    p["locked"] = vp.Locked ? "true" : "false";
                    p["viewHeight"] = Values.Num(vp.ViewHeight);
                    break;
                case Ellipse el:
                    p["center"] = Acad.Fmt(el.Center);
                    p["majorAxis"] = Acad.Fmt(el.MajorAxis);
                    p["ratio"] = Values.Num(el.RadiusRatio);
                    p["startAngle"] = Values.Num(Values.RadToDeg(el.StartAngle));
                    p["endAngle"] = Values.Num(Values.RadToDeg(el.EndAngle));
                    p["length"] = TryGet(() => Values.Num(el.GetDistanceAtParameter(el.EndParam)), "");
                    if (Math.Abs(el.EndParam - el.StartParam - 2 * Math.PI) < 1e-9)
                        p["area"] = Values.Num(Math.PI * el.MajorAxis.Length * el.MinorAxis.Length);
                    break;
                case Spline sp:
                    bool fit = sp.HasFitData && sp.NumFitPoints > 0;
                    var spts = fit
                        ? Enumerable.Range(0, sp.NumFitPoints).Select(i => Acad.Fmt(sp.GetFitPointAt(i)))
                        : Enumerable.Range(0, sp.NumControlPoints).Select(i => Acad.Fmt(sp.GetControlPointAt(i)));
                    p["points"] = string.Join(";", spts);
                    p["method"] = fit ? "fit" : "cv";
                    p["closed"] = sp.Closed ? "true" : "false";
                    p["degree"] = sp.Degree.ToString();
                    p["length"] = TryGet(() => Values.Num(sp.GetDistanceAtParameter(sp.EndParam)), "");
                    break;
                case Xline xl:
                    p["position"] = Acad.Fmt(xl.BasePoint);
                    p["direction"] = Acad.Fmt(xl.UnitDir);
                    break;
                case Ray ray:
                    p["position"] = Acad.Fmt(ray.BasePoint);
                    p["direction"] = Acad.Fmt(ray.UnitDir);
                    break;
                case Hatch h:
                    p["pattern"] = h.PatternName;
                    p["scale"] = Values.Num(h.PatternScale);
                    p["angle"] = Values.Num(Values.RadToDeg(h.PatternAngle));
                    p["associative"] = h.Associative ? "true" : "false";
                    var harea = TryGet(() => Values.Num(h.Area), "");
                    if (harea.Length > 0) p["area"] = harea;
                    p["loops"] = h.NumberOfLoops.ToString();
                    break;
                case Leader ld:
                    p["points"] = string.Join(";", Enumerable.Range(0, ld.NumVertices).Select(i => Acad.Fmt(ld.VertexAt(i))));
                    p["scale"] = Values.Num(ld.Dimscale);
                    if (!ld.Annotation.IsNull) p["annotation"] = ld.Annotation.Handle.ToString();
                    break;
                case Dimension dim:
                    DimensionProps(dim, p);
                    break;
                case Table _:
                    break;
                case BlockReference br:
                    p["name"] = BlockName(tr, br);
                    p["position"] = Acad.Fmt(br.Position);
                    var s = br.ScaleFactors;
                    p["scale"] = Math.Abs(s.X - s.Y) < 1e-9 && Math.Abs(s.X - s.Z) < 1e-9
                        ? Values.Num(s.X)
                        : Values.Pt(s.X, s.Y, 0) + "," + Values.Num(s.Z);
                    p["rotation"] = Values.Num(Values.RadToDeg(br.Rotation));
                    if (br.AttributeCollection.Count > 0) p["attributes"] = Symbols.AttributesOf(tr, br);
                    break;
            }

            p["bbox"] = TryGet(() =>
            {
                var ext = e.GeometricExtents;
                return Acad.Fmt(ext.MinPoint) + ";" + Acad.Fmt(ext.MaxPoint);
            }, "");
            if (p["bbox"].Length == 0) p.Remove("bbox");

            // 只有不在模型空间时才标出所在空间（查询时 [space=A3] 可用；模型空间按 Model 匹配）
            var space = SpaceName(tr, e);
            if (space != null && space != Layouts.ModelName) p["space"] = space;

            return new Node { Path = EntityPath(tr, type, e), Type = type, Props = p };
        }

        public static string DimensionKind(Dimension d)
        {
            switch (d)
            {
                case RotatedDimension _: return "linear";
                case AlignedDimension _: return "aligned";
                case Point3AngularDimension _:
                case LineAngularDimension2 _: return "angular";
                case RadialDimension _: return "radius";
                case DiametricDimension _: return "diameter";
                case ArcDimension _: return "arc";
                case OrdinateDimension _: return "ordinate";
                default: return "other";
            }
        }

        private static void DimensionProps(Dimension d, Dictionary<string, string> p)
        {
            var kind = DimensionKind(d);
            p["kind"] = kind;
            switch (d)
            {
                case RotatedDimension rd:
                    p["p1"] = Acad.Fmt(rd.XLine1Point);
                    p["p2"] = Acad.Fmt(rd.XLine2Point);
                    p["dimLine"] = Acad.Fmt(rd.DimLinePoint);
                    p["rotation"] = Values.Num(Values.RadToDeg(rd.Rotation));
                    break;
                case AlignedDimension ad:
                    p["p1"] = Acad.Fmt(ad.XLine1Point);
                    p["p2"] = Acad.Fmt(ad.XLine2Point);
                    p["dimLine"] = Acad.Fmt(ad.DimLinePoint);
                    break;
                case Point3AngularDimension an:
                    p["vertex"] = Acad.Fmt(an.CenterPoint);
                    p["p1"] = Acad.Fmt(an.XLine1Point);
                    p["p2"] = Acad.Fmt(an.XLine2Point);
                    p["dimLine"] = Acad.Fmt(an.ArcPoint);
                    break;
                case RadialDimension ra:
                    p["p1"] = Acad.Fmt(ra.Center);
                    p["p2"] = Acad.Fmt(ra.ChordPoint);
                    p["dimLine"] = Acad.Fmt(ra.TextPosition);
                    break;
                case DiametricDimension di:
                    p["p1"] = Acad.Fmt(di.ChordPoint);
                    p["p2"] = Acad.Fmt(di.FarChordPoint);
                    p["dimLine"] = Acad.Fmt(di.TextPosition);
                    break;
            }
            p["text"] = d.DimensionText;
            p["style"] = d.DimensionStyleName;
            p["scale"] = Values.Num(d.Dimscale);
            p["measurement"] = TryGet(() =>
                Values.Num(kind == "angular" ? d.Measurement * 180.0 / Math.PI : d.Measurement), "");
        }

        /// <summary>动态块的参照指向匿名块，这里取回用户看到的原始块名。</summary>
        private static string BlockName(Transaction tr, BlockReference br)
        {
            var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }

        public static Node Layer(Database db, Transaction tr, LayerTableRecord l) => new Node
        {
            Path = LayerPath(l.Name),
            Type = "layer",
            Props = new Dictionary<string, string>
            {
                ["name"] = l.Name,
                ["color"] = Acad.Fmt(l.Color),
                ["linetype"] = Acad.LinetypeName(tr, l.LinetypeObjectId),
                ["lineWeight"] = Values.FormatLineWeight((int)l.LineWeight),
                ["on"] = Bool(!l.IsOff),
                ["frozen"] = Bool(l.IsFrozen),
                ["locked"] = Bool(l.IsLocked),
                ["plot"] = Bool(l.IsPlottable),
                ["current"] = Bool(db.Clayer == l.ObjectId),
            },
        };

        public static Node Document(Database db, Transaction tr, string? file)
        {
            var clayer = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
            return new Node
            {
                Path = "/",
                Type = "document",
                Props = new Dictionary<string, string>
                {
                    ["file"] = file ?? db.Filename,
                    ["version"] = FormatVersion(db.OriginalFileVersion),
                    ["units"] = Acad.Fmt(db.Insunits),
                    ["measurement"] = db.Measurement == MeasurementValue.Metric ? "metric" : "imperial",
                    ["lunits"] = db.Lunits.ToString(CultureInfo.InvariantCulture),
                    ["luprec"] = db.Luprec.ToString(CultureInfo.InvariantCulture),
                    ["aunits"] = db.Aunits.ToString(CultureInfo.InvariantCulture),
                    ["auprec"] = db.Auprec.ToString(CultureInfo.InvariantCulture),
                    ["ltscale"] = Values.Num(db.Ltscale),
                    ["dimscale"] = Values.Num(db.Dimscale),
                    ["currentLayer"] = clayer.Name,
                    ["currentLayout"] = Layouts.CurrentName(),
                    ["layers"] = CountLayers(db, tr).ToString(),
                    ["entities"] = ModelEntityIds(db, tr).Count().ToString(),
                },
            };
        }

        /// <summary>
        /// 按值比较，而不是 ToString()：运行时的枚举来自所在 AutoCAD 版本，
        /// 2014 里 AC1027 与别名 Newest 同值，ToString() 会得到 "Newest"。
        /// </summary>
        public static string FormatVersion(DwgVersion v)
        {
            if (v == DwgVersion.AC1027) return "AC1027";
            if (v == DwgVersion.AC1024) return "AC1024";
            if (v == DwgVersion.AC1021) return "AC1021";
            if (v == DwgVersion.AC1800) return "AC1018";
            if (v == DwgVersion.AC1015) return "AC1015";
            return (int)v > (int)DwgVersion.AC1027 ? "AC1032" : v.ToString();
        }

        public static int CountLayers(Database db, Transaction tr)
        {
            int n = 0;
            foreach (ObjectId _ in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)) n++;
            return n;
        }

        public static IEnumerable<ObjectId> ModelEntityIds(Database db, Transaction tr)
        {
            var ms = (BlockTableRecord)tr.GetObject(Acad.ModelSpace(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
                if (!id.IsErased) yield return id;
        }

        public static IEnumerable<LayerTableRecord> Layers(Database db, Transaction tr)
        {
            foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
                if (!id.IsErased) yield return (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
        }

        private static string Bool(bool b) => b ? "true" : "false";

        private static string TryGet(Func<string> f, string fallback)
        {
            try { return f(); }
            catch (AcRx.Exception) { return fallback; }
            catch (InvalidOperationException) { return fallback; }
        }
    }
}
