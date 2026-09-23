using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AcadClr.Core;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadClr.Plugin.Engine
{
    /// <summary>
    /// 打印设备、纸张与页面设置。布局的页面设置与打印共用这里的纸张匹配规则（移植自 AutoCADMCP，已实测）。
    /// </summary>
    internal static class PageSetup
    {
        public const string PdfDevice = "DWG To PDF.pc3";

        public static List<string> Devices() =>
            PlotSettingsValidator.Current.GetPlotDeviceList().Cast<string>().ToList();

        public static string EnsureDevice(string device)
        {
            var devices = Devices();
            var hit = devices.FirstOrDefault(d => d.Equals(device, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var near = Schema.Suggest(device, devices);
            throw new CliError("not_found", $"打印设备 “{device}” 不可用。",
                (near != null ? $"是否想用 {near}？" : "") + "运行 acadclr get /devices 查看全部设备");
        }

        public static Node DeviceNode(string device, bool withMedia)
        {
            var n = new Node { Path = $"/device[@name={device}]", Type = "device", Props = { ["name"] = device } };
            if (!withMedia) return n;
            var psv = PlotSettingsValidator.Current;
            using (var ps = new PlotSettings(false))
            {
                try
                {
                    psv.SetPlotConfigurationName(ps, device, null);
                    psv.RefreshLists(ps);
                    n.Props["media"] = string.Join(";", psv.GetCanonicalMediaNameList(ps).Cast<string>());
                }
                catch (AcRx.Exception ex) { n.Props["media"] = "（读取失败：" + ex.ErrorStatus + "）"; }
            }
            n.Props["styleSheets"] = string.Join(";", psv.GetPlotStyleSheetList().Cast<string>());
            return n;
        }

        /// <summary>纸张匹配结果：名字，以及是否为用户给出的完整纸张名。</summary>
        public readonly struct Media
        {
            public readonly string Name;
            public readonly bool Exact;
            public Media(string name, bool exact) { Name = name; Exact = exact; }
        }

        /// <summary>
        /// 纸张匹配：完整名优先；否则按 A4 / A3 这类简称模糊找，优先非 full_bleed 的公制纸张。
        /// 方向在这里就参与选择：纸张名自带方向（ISO_A3_(420.00_x_297.00_MM) 是横向），
        /// 横向应当靠选横向纸张实现，而不是再叠一次旋转 —— 两者叠加会转回纵向。
        /// </summary>
        public static Media MatchMedia(PlotSettingsValidator psv, PlotSettings ps, string wanted, bool? landscape)
        {
            var media = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
            if (media.Count == 0) throw new CliError("invalid_value", "当前打印设备没有可用纸张。");

            var exact = media.FirstOrDefault(m => m.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return new Media(exact, true);

            string key = wanted.Trim().ToUpperInvariant().Replace(" ", "_");
            var hits = media.Where(m => m.ToUpperInvariant().Contains(key)).ToList();
            if (hits.Count == 0)
                throw new CliError("not_found", $"找不到匹配 “{wanted}” 的纸张。", "运行 acadclr get \"/device[@name=...]\" 查看该设备的纸张名");

            var ordered = hits.OrderBy(m => m.ToUpperInvariant().Contains("FULL_BLEED") ? 1 : 0)
                              .ThenBy(m => m.ToUpperInvariant().Contains("MM") ? 0 : 1)
                              .ThenBy(m => m.Length).ToList();
            if (landscape.HasValue)
            {
                var want = ordered.FirstOrDefault(m => IsLandscape(m) == landscape.Value);
                if (want != null) return new Media(want, false);
            }
            return new Media(ordered[0], false);
        }

        public static bool MediaAvailable(PlotSettingsValidator psv, PlotSettings ps, string name) =>
            psv.GetCanonicalMediaNameList(ps).Cast<string>().Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>从纸张名里的 “(420.00_x_297.00_MM)” 判断方向；解析不出来当作纵向。</summary>
        public static bool IsLandscape(string mediaName)
        {
            var m = Regex.Match(mediaName ?? "", @"\((\d+(?:\.\d+)?)_x_(\d+(?:\.\d+)?)_");
            return m.Success
                && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
                && w > h;
        }

        /// <summary>布局实际是不是横向：纸张方向与打印旋转合起来看。</summary>
        public static bool LayoutIsLandscape(Layout l)
        {
            bool rotated = l.PlotRotation == PlotRotation.Degrees090 || l.PlotRotation == PlotRotation.Degrees270;
            return IsLandscape(l.CanonicalMediaName) != rotated;
        }

        /// <summary>
        /// 把设备 / 纸张 / 方向 / 打印样式写进布局的页面设置（随 DWG 保存）。只改传入的项。
        /// </summary>
        public static void Apply(Layout layout, string? device, string? paper, bool? landscape, string? plotStyle)
        {
            if (device == null && paper == null && landscape == null && plotStyle == null) return;
            var psv = PlotSettingsValidator.Current;
            using (var ps = new PlotSettings(layout.ModelType))
            {
                ps.CopyFrom(layout);
                var current = layout.PlotConfigurationName;
                var dev = device != null ? EnsureDevice(device)
                        : string.IsNullOrWhiteSpace(current) || current == "None" ? EnsureDevice(PdfDevice) : current;
                string keepMedia = layout.CanonicalMediaName ?? "";
                psv.SetPlotConfigurationName(ps, dev, null);
                psv.RefreshLists(ps);

                bool rotate;
                string media;
                if (paper != null)
                {
                    var picked = MatchMedia(psv, ps, paper, landscape);
                    media = picked.Name;
                    // 给了完整纸张名：方向只能靠旋转；简称已经按方向选了纸
                    rotate = picked.Exact && landscape == true;
                }
                else
                {
                    media = keepMedia.Length > 0 && MediaAvailable(psv, ps, keepMedia) ? keepMedia : MatchMedia(psv, ps, "A4", landscape).Name;
                    bool isLand = IsLandscape(media);
                    rotate = landscape.HasValue ? landscape.Value != isLand : LayoutIsLandscape(layout) != isLand;
                }
                psv.SetCanonicalMediaName(ps, media);
                psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
                psv.SetPlotRotation(ps, rotate ? PlotRotation.Degrees090 : PlotRotation.Degrees000);

                if (plotStyle != null)
                {
                    var sheets = psv.GetPlotStyleSheetList().Cast<string>().ToList();
                    var sheet = sheets.FirstOrDefault(s => s.Equals(plotStyle, StringComparison.OrdinalIgnoreCase))
                        ?? throw new CliError("not_found", $"打印样式表 “{plotStyle}” 不存在。", "可用：" + string.Join("、", sheets.Take(20)));
                    psv.SetCurrentStyleSheet(ps, sheet);
                }
                layout.UpgradeOpen();
                layout.CopyFrom(ps);
            }
        }
    }
}
