using System;
using System.Collections.Generic;
using System.Globalization;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 系统变量（GETVAR / SETVAR）。移植自 AutoCADMCP 的 get_sysvars / set_sysvar。
    /// Application.Get/SetSystemVariable 作用于活动文档，所以只能在活动文档的数据库上用：
    /// 实时模式天然如此；离线模式由 CLI 改走“文档模式”（accoreconsole /i 打开图纸）。
    /// 值的类型按变量当前值推断：整数、实数、字符串、点。
    /// </summary>
    internal static class Sysvars
    {
        /// <summary>get /sysvars 与 query sysvar[...] 的范围：常用变量（绘图状态、单位、标注、显示）。</summary>
        public static readonly string[] Common =
        {
            "DWGNAME", "DWGPREFIX", "CTAB", "TILEMODE",
            "CLAYER", "CECOLOR", "CELTYPE", "CELTSCALE", "CELWEIGHT",
            "INSUNITS", "MEASUREMENT", "LUNITS", "LUPREC", "AUNITS", "AUPREC", "ANGBASE", "ANGDIR",
            "LTSCALE", "PSLTSCALE", "DIMSCALE", "DIMSTYLE", "CANNOSCALE",
            "TEXTSIZE", "TEXTSTYLE", "LWDISPLAY",
            "OSMODE", "ORTHOMODE", "SNAPMODE", "PDMODE", "PDSIZE", "FILLETRAD",
            "VIEWCTR", "VIEWSIZE", "EXTMIN", "EXTMAX",
            "CMDECHO", "FILEDIA", "BACKGROUNDPLOT", "SECURELOAD",
        };

        public static string PathOf(string name) => $"/sysvar[@name={name.ToUpperInvariant()}]";

        /// <summary>确认 <paramref name="db"/> 是活动文档的数据库，否则系统变量读写会落到别的图上。</summary>
        public static void RequireActive(Database db)
        {
            if (CoreApp.DocumentManager.MdiActiveDocument?.Database != db)
                throw new CliError("needs_document", "系统变量只能在打开的文档上读写。",
                    "离线模式会自动改用文档模式；如果看到这条错误，请报告问题");
        }

        public static object Read(string name)
        {
            try { return CoreApp.GetSystemVariable(name) ?? throw new CliError("not_found", $"没有系统变量 {name}。"); }
            catch (AcRx.Exception)
            {
                var near = Schema.Suggest(name, Common);
                throw new CliError("not_found", $"没有系统变量 {name}（或当前上下文不可读）。", near != null ? $"是否想用 {near}？" : "get /sysvars 查看常用变量");
            }
        }

        public static Node Node(string name)
        {
            name = name.Trim().ToUpperInvariant();
            var v = Read(name);
            return new Node
            {
                Path = PathOf(name),
                Type = "sysvar",
                Props = new Dictionary<string, string> { ["name"] = name, ["value"] = Format(v), ["valueType"] = TypeName(v) },
            };
        }

        /// <summary>设置变量，返回恢复旧值的动作（原子批处理回滚时调用）。</summary>
        public static Action Set(string name, List<KeyValuePair<string, string>> props)
        {
            name = name.Trim().ToUpperInvariant();
            var type = Schema.FindType("sysvar")!;
            string? value = null;
            foreach (var kv in props)
                if (Schema.CheckProp(type, kv.Key, Verbs.Set).Name == "value") value = kv.Value;
            if (value == null) throw new CliError("missing_property", "set 系统变量需要 value。", $"例：set \"{PathOf(name)}\" --prop value=100");

            var old = Read(name);
            var converted = Coerce(name, old, value);
            try { CoreApp.SetSystemVariable(name, converted); }
            catch (AcRx.Exception ex)
            {
                throw new CliError("invalid_value", $"设置 {name} 失败（{ex.ErrorStatus}）：可能是只读变量，或值超出允许范围。当前值 {Format(old)}。");
            }
            return () => { try { CoreApp.SetSystemVariable(name, old); } catch (AcRx.Exception) { } };
        }

        private static object Coerce(string name, object current, string s)
        {
            s = s.Trim();
            try
            {
                switch (current)
                {
                    case short _: return short.Parse(s, CultureInfo.InvariantCulture);
                    case int _: return int.Parse(s, CultureInfo.InvariantCulture);
                    case double _: return double.Parse(s, CultureInfo.InvariantCulture);
                    case Point3d _: { var p = Values.Point(name, s); return new Point3d(p[0], p[1], p[2]); }
                    case Point2d _: { var p = Values.Point(name, s); return new Point2d(p[0], p[1]); }
                    default: return s;
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException)
            {
                throw new CliError("invalid_value", $"{name} 需要{TypeLabel(current)}，收到 “{s}”。当前值 {Format(current)}。");
            }
        }

        public static string Format(object v)
        {
            switch (v)
            {
                case double d: return Values.Num(d);
                case Point3d p: return Values.Pt(p.X, p.Y, p.Z);
                case Point2d p2: return Values.Pt(p2.X, p2.Y, 0);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }
        }

        private static string TypeName(object v) => v switch
        {
            short _ => "integer",
            int _ => "integer",
            double _ => "real",
            Point3d _ => "point",
            Point2d _ => "point",
            _ => "string",
        };

        private static string TypeLabel(object v) => v switch
        {
            short _ => "整数",
            int _ => "整数",
            double _ => "实数",
            Point3d _ => "点 x,y,z",
            Point2d _ => "点 x,y",
            _ => "字符串",
        };
    }
}
