using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>AutoCAD 类型与 Core 中字符串值之间的转换，以及符号表的常用操作。</summary>
    internal static class Acad
    {
        public static Point3d Pt(string prop, string s)
        {
            var p = Values.Point(prop, s);
            return new Point3d(p[0], p[1], p[2]);
        }

        public static string Fmt(Point3d p) => Values.Pt(p.X, p.Y, p.Z);
        public static string Fmt(Vector3d v) => Values.Pt(v.X, v.Y, v.Z);

        public static Vector3d Vec(string prop, string s)
        {
            var p = Values.Vector(prop, s);
            var v = new Vector3d(p[0], p[1], p[2]);
            if (v.Length < 1e-12) throw new CliError("invalid_value", $"{prop} 不能是零向量。");
            return v;
        }
        public static string Fmt(Point2d p) => Values.Pt(p.X, p.Y, 0);

        public static Color ToColor(ColorSpec c)
        {
            switch (c.Kind)
            {
                case ColorSpec.Kinds.ByLayer: return Color.FromColorIndex(ColorMethod.ByLayer, 256);
                case ColorSpec.Kinds.ByBlock: return Color.FromColorIndex(ColorMethod.ByBlock, 0);
                case ColorSpec.Kinds.Rgb: return Color.FromRgb(c.R, c.G, c.B);
                default: return Color.FromColorIndex(ColorMethod.ByAci, c.Aci);
            }
        }

        public static string Fmt(Color c)
        {
            if (c.IsByLayer) return "bylayer";
            if (c.IsByBlock) return "byblock";
            if (c.ColorMethod == ColorMethod.ByColor) return c.Red + "," + c.Green + "," + c.Blue;
            return c.ColorIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>取线型 id；表里没有就依次尝试从 acadiso.lin、acad.lin 加载。</summary>
        public static ObjectId EnsureLinetype(Database db, Transaction tr, string name)
        {
            var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];

            foreach (var file in new[] { "acadiso.lin", "acad.lin" })
            {
                try { db.LoadLineTypeFile(name, file); }
                catch (AcRx.Exception) { continue; }
                lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (lt.Has(name)) return lt[name];
            }
            throw new CliError("invalid_value", $"找不到线型 “{name}”（acadiso.lin / acad.lin 中都没有）。",
                "常用：Continuous、CENTER、DASHED、HIDDEN、PHANTOM、DASHDOT");
        }

        public static string LinetypeName(Transaction tr, ObjectId id)
        {
            if (id.IsNull) return "";
            return ((LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }

        public static ObjectId FindLayer(Database db, Transaction tr, string name)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            return lt.Has(name) ? lt[name] : ObjectId.Null;
        }

        /// <summary>取图层 id，不存在则以默认属性创建。</summary>
        public static ObjectId EnsureLayer(Database db, Transaction tr, string name)
        {
            var id = FindLayer(db, tr, name);
            if (!id.IsNull) return id;
            ValidateSymbolName(name, "图层名");
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
            // 显式设线型为 Continuous：API 新建的图层线型 id 默认为空，要到保存后才被补上
            var rec = new LayerTableRecord { Name = name, LinetypeObjectId = db.ContinuousLinetype };
            id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }

        public static void ValidateSymbolName(string name, string what)
        {
            try { SymbolUtilityServices.ValidateSymbolName(name, false); }
            catch (AcRx.Exception)
            {
                throw new CliError("invalid_value", $"{what} “{name}” 不合法。", "不能包含 < > / \\ \" : ; ? * | , = ` 等字符");
            }
        }

        public static ObjectId FindTextStyle(Database db, Transaction tr, string name)
        {
            var ts = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (ts.Has(name)) return ts[name];
            var names = new List<string>();
            foreach (ObjectId id in ts) names.Add(((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
            throw new CliError("not_found", $"文字样式 “{name}” 不存在。", "现有样式：" + string.Join("、", names.Where(n => n.Length > 0)));
        }

        public static string TextStyleName(Transaction tr, ObjectId id) =>
            id.IsNull ? "" : ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;

        public static ObjectId FindBlock(Database db, Transaction tr, string name)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name)) return bt[name];
            var names = new List<string>();
            foreach (ObjectId id in bt)
            {
                var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!b.IsLayout && !b.IsAnonymous && !b.IsFromExternalReference) names.Add(b.Name);
            }
            throw new CliError("not_found", $"图块 “{name}” 未定义。",
                names.Count == 0 ? "图中没有任何可插入的图块。" : "已定义的图块：" + string.Join("、", names.Take(30)));
        }

        public static ObjectId ModelSpace(Database db) => SymbolUtilityServices.GetBlockModelSpaceId(db);

        // -------- 对齐方式 --------

        private static readonly Dictionary<string, AttachmentPoint> JustifyMap =
            new Dictionary<string, AttachmentPoint>(StringComparer.OrdinalIgnoreCase)
            {
                ["left"] = AttachmentPoint.BaseLeft, ["center"] = AttachmentPoint.BaseCenter, ["right"] = AttachmentPoint.BaseRight,
                ["middle"] = AttachmentPoint.BaseMid,
                ["tl"] = AttachmentPoint.TopLeft, ["tc"] = AttachmentPoint.TopCenter, ["tr"] = AttachmentPoint.TopRight,
                ["ml"] = AttachmentPoint.MiddleLeft, ["mc"] = AttachmentPoint.MiddleCenter, ["mr"] = AttachmentPoint.MiddleRight,
                ["bl"] = AttachmentPoint.BottomLeft, ["bc"] = AttachmentPoint.BottomCenter, ["br"] = AttachmentPoint.BottomRight,
            };

        public static AttachmentPoint Justify(string prop, string s, bool mtext)
        {
            if (JustifyMap.TryGetValue(s.Trim(), out var ap))
            {
                bool isBase = ap == AttachmentPoint.BaseLeft || ap == AttachmentPoint.BaseCenter ||
                              ap == AttachmentPoint.BaseRight || ap == AttachmentPoint.BaseMid;
                if (!mtext || !isBase) return ap;
            }
            throw new CliError("invalid_value", $"{prop}：无法识别的对齐方式 “{s}”。",
                mtext ? "mtext 可用：tl tc tr ml mc mr bl bc br" : "text 可用：left center right middle tl tc tr ml mc mr bl bc br");
        }

        public static string Fmt(AttachmentPoint ap)
        {
            foreach (var kv in JustifyMap) if (kv.Value == ap) return kv.Key;
            return ap.ToString();
        }

        // -------- 单位 --------

        private static readonly Dictionary<string, UnitsValue> UnitsMap = new Dictionary<string, UnitsValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["unitless"] = UnitsValue.Undefined, ["mm"] = UnitsValue.Millimeters, ["cm"] = UnitsValue.Centimeters,
            ["m"] = UnitsValue.Meters, ["km"] = UnitsValue.Kilometers, ["in"] = UnitsValue.Inches, ["ft"] = UnitsValue.Feet,
        };

        public static UnitsValue Units(string prop, string s)
        {
            if (UnitsMap.TryGetValue(s.Trim(), out var u)) return u;
            throw new CliError("invalid_value", $"{prop}：无法识别的单位 “{s}”。", "可用：mm cm m km in ft unitless");
        }

        public static string Fmt(UnitsValue u)
        {
            foreach (var kv in UnitsMap) if (kv.Value == u) return kv.Key;
            return u.ToString();
        }

        /// <summary>把 AutoCAD 异常翻译成带 code 的 CliError。</summary>
        public static CliError Translate(AcRx.Exception ex)
        {
            switch (ex.ErrorStatus)
            {
                case AcRx.ErrorStatus.OnLockedLayer:
                    return new CliError("locked_layer", "实体所在图层已锁定，无法修改。", "先 set \"/layer[@name=...]\" --prop locked=false");
                case AcRx.ErrorStatus.DuplicateRecordName:
                    return new CliError("already_exists", "名称已存在。");
                case AcRx.ErrorStatus.InvalidInput:
                    return new CliError("invalid_value", "输入无效（AutoCAD：InvalidInput）。");
                default:
                    return new CliError("acad_error", "AutoCAD 错误：" + ex.ErrorStatus);
            }
        }
    }
}
