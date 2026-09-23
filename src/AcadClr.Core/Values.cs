using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadClr.Core
{
    /// <summary>颜色的解析结果，与 AutoCAD API 解耦，方便在 Core 里测试。</summary>
    public sealed class ColorSpec
    {
        public enum Kinds { ByLayer, ByBlock, Aci, Rgb }
        public Kinds Kind { get; set; }
        public short Aci { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    /// <summary>属性值的解析与格式化。所有数字一律使用 InvariantCulture（小数点）。</summary>
    public static class Values
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool TryDouble(string s, out double d) =>
            double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d);

        public static double Double(string prop, string s)
        {
            if (TryDouble(s, out double d)) return d;
            throw new CliError("invalid_value", $"{prop}：“{s}” 不是有效数字。", $"例如 {prop}=500");
        }

        public static double Positive(string prop, string s)
        {
            double d = Double(prop, s);
            if (d <= 0) throw new CliError("invalid_value", $"{prop} 必须大于 0，收到 {s}。");
            return d;
        }

        public static int Int(string prop, string s)
        {
            if (int.TryParse(s.Trim(), NumberStyles.Integer, Inv, out int i)) return i;
            throw new CliError("invalid_value", $"{prop}：“{s}” 不是整数。");
        }

        public static bool Bool(string prop, string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
            }
            throw new CliError("invalid_value", $"{prop}：“{s}” 不是布尔值。", "使用 true 或 false");
        }

        /// <summary>"x,y" 或 "x,y,z"。</summary>
        public static double[] Point(string prop, string s)
        {
            var parts = s.Split(',');
            if (parts.Length == 2 || parts.Length == 3)
            {
                var r = new double[3];
                bool ok = true;
                for (int i = 0; i < parts.Length; i++) ok &= TryDouble(parts[i], out r[i]);
                if (ok) return r;
            }
            throw new CliError("invalid_value", $"{prop}：“{s}” 不是有效坐标。", $"写法：{prop}=100,200 或 {prop}=100,200,0");
        }

        /// <summary>"x1,y1;x2,y2;..."，也接受空格分隔的点。</summary>
        public static List<double[]> Points(string prop, string s)
        {
            var chunks = s.Split(new[] { ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var pts = chunks.Select(c => Point(prop, c)).ToList();
            if (pts.Count < 2)
                throw new CliError("invalid_value", $"{prop} 至少需要 2 个点。", $"写法：{prop}=0,0;1000,0;1000,500");
            return pts;
        }

        /// <summary>"dx,dy" 或 "dx,dy,dz"。</summary>
        public static double[] Vector(string prop, string s) => Point(prop, s);

        private static readonly Dictionary<string, short> ColorNames = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = 1, ["yellow"] = 2, ["green"] = 3, ["cyan"] = 4, ["blue"] = 5, ["magenta"] = 6, ["white"] = 7, ["black"] = 7,
            ["gray"] = 8, ["grey"] = 8, ["lightgray"] = 9, ["lightgrey"] = 9,
        };

        /// <summary>
        /// 颜色：bylayer / byblock / ACI 索引 1-255 / 名称 red…white / "#RRGGBB" / "r,g,b"。
        /// </summary>
        public static ColorSpec Color(string prop, string s)
        {
            var t = s.Trim();
            if (t.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return new ColorSpec { Kind = ColorSpec.Kinds.ByLayer };
            if (t.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return new ColorSpec { Kind = ColorSpec.Kinds.ByBlock };
            if (ColorNames.TryGetValue(t, out short named)) return new ColorSpec { Kind = ColorSpec.Kinds.Aci, Aci = named };

            if (short.TryParse(t, NumberStyles.Integer, Inv, out short aci))
            {
                if (aci >= 1 && aci <= 255) return new ColorSpec { Kind = ColorSpec.Kinds.Aci, Aci = aci };
                throw new CliError("invalid_value", $"{prop}：ACI 颜色索引必须在 1-255 之间，收到 {t}。");
            }

            var hex = t.StartsWith("#", StringComparison.Ordinal) ? t.Substring(1) : null;
            if (hex != null && hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, Inv, out int rgb))
                return new ColorSpec { Kind = ColorSpec.Kinds.Rgb, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb };

            var parts = t.Split(',');
            if (parts.Length == 3 && parts.All(p => byte.TryParse(p.Trim(), NumberStyles.Integer, Inv, out _)))
                return new ColorSpec
                {
                    Kind = ColorSpec.Kinds.Rgb,
                    R = byte.Parse(parts[0].Trim(), Inv), G = byte.Parse(parts[1].Trim(), Inv), B = byte.Parse(parts[2].Trim(), Inv),
                };

            throw new CliError("invalid_value", $"{prop}：无法识别的颜色 “{s}”。",
                "可用：bylayer、byblock、1-255（ACI）、red/yellow/green/cyan/blue/magenta/white、#FF8800、255,128,0");
        }

        /// <summary>AutoCAD 标准线宽档（单位 0.01mm）。</summary>
        public static readonly int[] StandardLineWeights =
            { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

        /// <summary>
        /// 线宽：bylayer / byblock / default / 毫米数（就近取标准档，如 0.3 → 0.30、0.33 → 0.35）。
        /// 返回值：-1 ByLayer，-2 ByBlock，-3 Default，其余为 0.01mm 单位。
        /// </summary>
        public static int LineWeight(string prop, string s)
        {
            var t = s.Trim().ToLowerInvariant();
            if (t == "bylayer") return -1;
            if (t == "byblock") return -2;
            if (t == "default") return -3;
            if (t.EndsWith("mm", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 2);
            if (TryDouble(t, out double mm) && mm >= 0 && mm <= 2.11 + 1e-9)
            {
                int hundredths = (int)Math.Round(mm * 100);
                return StandardLineWeights.OrderBy(w => Math.Abs(w - hundredths)).First();
            }
            throw new CliError("invalid_value", $"{prop}：无法识别的线宽 “{s}”。", "使用毫米数（0-2.11，如 0.35）或 bylayer / byblock / default");
        }

        public static string FormatLineWeight(int v)
        {
            switch (v)
            {
                case -1: return "bylayer";
                case -2: return "byblock";
                case -3: return "default";
                default: return (v / 100.0).ToString("0.00", Inv);
            }
        }

        public static string Num(double d)
        {
            if (Math.Abs(d) < 1e-10) d = 0;
            return d.ToString("0.####", Inv);
        }

        /// <summary>z 在显示精度（4 位小数）内为 0 时只输出 x,y。</summary>
        public static string Pt(double x, double y, double z) =>
            Math.Abs(z) < 5e-5 ? Num(x) + "," + Num(y) : Num(x) + "," + Num(y) + "," + Num(z);

        public static double DegToRad(double deg) => deg * Math.PI / 180.0;

        public static double RadToDeg(double rad)
        {
            var d = rad * 180.0 / Math.PI;
            d %= 360;
            if (d < 0) d += 360;
            return d;
        }
    }
}
