using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AcadClr.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// view 动词（仅实时模式，主线程调用）：zoom 调整当前视图，capture 截取 AutoCAD 窗口为 PNG。
    /// 截图移植自 AutoCADMCP 的 capture_view：PrintWindow(PW_RENDERFULLCONTENT) 即使窗口被遮挡也能让它自行绘制；
    /// region=drawing 只截绘图区（去掉功能区、命令行、状态栏），图更干净、token 更省。
    /// </summary>
    internal static class Views
    {
        public static Response Run(Document doc, BatchItem item, Func<string, List<ObjectId>> entities)
        {
            var action = Schema.RequireAction("view", item.Action);
            var p = action.CheckProps(item.GetProps());
            var target = item.Path ?? item.Selector;

            using (doc.LockDocument())
            {
                if (action.Name == "zoom")
                {
                    var data = Zoom(doc, p, target, entities);
                    return new Response { Document = doc.Name, Data = data };
                }

                // capture：可先缩放（zoom=extents / window / 目标实体），再截图
                JObject? zoomed = null;
                if (p.TryGetValue("zoom", out var z) || target != null)
                    zoomed = Zoom(doc, z != null ? new Dictionary<string, string> { ["to"] = z } : new Dictionary<string, string>(), target, entities);
                var shot = Capture(p.TryGetValue("maxWidth", out var mw) ? Values.Int("maxWidth", mw) : (int?)null,
                                   p.TryGetValue("region", out var rg) ? rg : "drawing");
                shot.Data["zoomed"] = zoomed;
                return new Response { Document = doc.Name, Data = shot.Data, Images = new List<ImageData> { shot.Image } };
            }
        }

        // ------------------------------------------------------------------ zoom

        /// <summary>
        /// to=extents（默认）缩放到图形范围；to=x1,y1;x2,y2 缩放到窗口；给了目标实体时缩放到它们的范围。
        /// 四周留 5% 边距。
        /// </summary>
        private static JObject Zoom(Document doc, Dictionary<string, string> p, string? target, Func<string, List<ObjectId>> entities)
        {
            var db = doc.Database;
            Point3d min, max;
            string what;
            if (target != null)
            {
                Extents3d? ext = null;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var id in entities(target))
                    {
                        try
                        {
                            var ge = ((Entity)tr.GetObject(id, OpenMode.ForRead)).GeometricExtents;
                            if (ext == null) ext = ge; else { var x = ext.Value; x.AddExtents(ge); ext = x; }
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    }
                    tr.Commit();
                }
                if (ext == null) throw new CliError("invalid_value", $"目标 “{target}” 没有几何范围，无法缩放。");
                min = ext.Value.MinPoint; max = ext.Value.MaxPoint;
                what = "目标实体";
            }
            else if (p.TryGetValue("to", out var to) && !to.Trim().Equals("extents", StringComparison.OrdinalIgnoreCase))
            {
                var pts = Values.Points("to", to);
                if (pts.Count != 2) throw new CliError("invalid_value", "to 应为 extents 或窗口的两个角点 x1,y1;x2,y2。");
                min = new Point3d(Math.Min(pts[0][0], pts[1][0]), Math.Min(pts[0][1], pts[1][1]), 0);
                max = new Point3d(Math.Max(pts[0][0], pts[1][0]), Math.Max(pts[0][1], pts[1][1]), 0);
                what = "窗口";
            }
            else
            {
                db.UpdateExt(true);
                min = db.Extmin; max = db.Extmax;
                what = "图形范围";
            }

            double w = max.X - min.X, h = max.Y - min.Y;
            if (w <= 1e-9 && h <= 1e-9) throw new CliError("invalid_value", "范围为空（图形里没有实体？），未缩放。");
            // 单点或一条水平 / 竖直线：给个最小尺寸，免得视图退化
            double size = Math.Max(w, h);
            if (w < size * 1e-3) w = size * 1e-3;
            if (h < size * 1e-3) h = size * 1e-3;

            var ed = doc.Editor;
            using (var view = ed.GetCurrentView())
            {
                view.CenterPoint = new Point2d((min.X + max.X) / 2.0, (min.Y + max.Y) / 2.0);
                view.Width = w * 1.05;
                view.Height = h * 1.05;
                ed.SetCurrentView(view);
            }
            return new JObject
            {
                ["zoom"] = what,
                ["min"] = Values.Pt(min.X, min.Y, 0),
                ["max"] = Values.Pt(max.X, max.Y, 0),
                ["space"] = Engine.Layouts.CurrentName(),
            };
        }

        // ------------------------------------------------------------------ capture

        private sealed class Shot
        {
            public ImageData Image = new ImageData();
            public JObject Data = new JObject();
        }

        private static Shot Capture(int? maxWidth, string region)
        {
            IntPtr main = AcApp.MainWindow?.Handle ?? IntPtr.Zero;
            if (main == IntPtr.Zero || !IsWindow(main)) throw new CliError("capture_failed", "拿不到 AutoCAD 主窗口句柄。");
            if (IsIconic(main)) throw new CliError("capture_failed", "AutoCAD 窗口已最小化，无法截图。", "先还原 AutoCAD 窗口");

            region = region.Trim().ToLowerInvariant();
            if (region != "drawing" && region != "window") throw new CliError("invalid_value", $"region 只能是 drawing 或 window，收到 “{region}”。");
            IntPtr hwnd = main;
            string label = "window";
            if (region == "drawing")
            {
                var view = FindDrawingArea(main);
                if (view != IntPtr.Zero) { hwnd = view; label = "drawing"; }
                else label = "window（没找到绘图区子窗口，已退回整个窗口）";
            }

            if (!GetClientRect(hwnd, out RECT rc) || rc.Width <= 0 || rc.Height <= 0)
                throw new CliError("capture_failed", "窗口尺寸无效（可能已最小化或正在调整布局）。");
            int w = rc.Width, h = rc.Height;

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // 2 = PW_RENDERFULLCONTENT（Win8.1+，能抓到 DirectX 绘制的视口）
                        if (!PrintWindow(hwnd, hdc, 2)) PrintWindow(hwnd, hdc, 0);
                    }
                    finally { g.ReleaseHdc(hdc); }
                }

                int outW = w, outH = h;
                byte[] png;
                if (maxWidth is int mw && mw > 0 && w > mw)
                {
                    outW = mw;
                    outH = (int)Math.Round(h * (mw / (double)w));
                    using (var scaled = new Bitmap(outW, outH, PixelFormat.Format32bppArgb))
                    {
                        using (var g2 = Graphics.FromImage(scaled))
                        {
                            // 线图缩小时默认插值会把细线糊掉，用高质量双三次
                            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g2.SmoothingMode = SmoothingMode.HighQuality;
                            g2.DrawImage(bmp, new Rectangle(0, 0, outW, outH));
                        }
                        png = ToPng(scaled);
                    }
                }
                else png = ToPng(bmp);

                var shot = new Shot();
                shot.Image = new ImageData { MimeType = "image/png", Data = Convert.ToBase64String(png), Width = outW, Height = outH };
                shot.Data["region"] = label;
                shot.Data["size"] = $"{outW}x{outH}";
                if (outW != w) shot.Data["sourceSize"] = $"{w}x{h}";
                shot.Data["sizeKB"] = Math.Round(png.Length / 1024.0, 1);
                int dpi = WindowDpi(hwnd);
                if (dpi > 96 && !ProcessIsDpiAware())
                    shot.Data["note"] = $"窗口 DPI {dpi}，AutoCAD 未声明 DPI 感知，系统会拉伸位图，细节受限";
                return shot;
            }
        }

        private static byte[] ToPng(System.Drawing.Image img)
        {
            using (var ms = new MemoryStream())
            {
                img.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 找绘图区窗口。图形窗口的类名各版本不同（2014 是动态的 Afx:&lt;基址&gt;:28:…，新版是 AfxFrameOrView*），
        /// 不按类名匹配，而是取 MDIClient 里面积最大的可见窗口，即当前图形的 MDI 子窗口；
        /// 它顶部还有一条工具栏，再下探一层取几乎同样大的子窗口，才是真正的图形视图。
        /// </summary>
        private static IntPtr FindDrawingArea(IntPtr parent)
        {
            var buf = new StringBuilder(256);
            IntPtr mdi = IntPtr.Zero;
            EnumWindowsProc findMdi = (child, _) =>
            {
                buf.Length = 0;
                GetClassName(child, buf, buf.Capacity);
                if (!buf.ToString().Equals("MDIClient", StringComparison.OrdinalIgnoreCase)) return true;
                mdi = child;
                return false;
            };
            EnumChildWindows(parent, findMdi, IntPtr.Zero);
            GC.KeepAlive(findMdi);
            if (mdi == IntPtr.Zero) return IntPtr.Zero;

            var best = Largest(mdi, 0);
            if (best == IntPtr.Zero || !GetClientRect(best, out RECT outer)) return best;
            var inner = Largest(best, (long)outer.Width * outer.Height * 7 / 10);
            return inner != IntPtr.Zero ? inner : best;
        }

        /// <summary>面积最大（且不小于 minArea）的可见子窗口。</summary>
        private static IntPtr Largest(IntPtr parent, long minArea)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindowsProc pick = (child, _) =>
            {
                if (IsWindowVisible(child) && GetClientRect(child, out RECT r) && r.Width > 200 && r.Height > 200)
                {
                    long area = (long)r.Width * r.Height;
                    if (area > bestArea && area >= minArea) { bestArea = area; best = child; }
                }
                return true;
            };
            EnumChildWindows(parent, pick, IntPtr.Zero);
            GC.KeepAlive(pick);
            return best;
        }

        private static int WindowDpi(IntPtr hwnd)
        {
            try { return (int)GetDpiForWindow(hwnd); }
            catch (EntryPointNotFoundException) { return 0; } // Win10 1607 之前没有这个 API
        }

        private static bool ProcessIsDpiAware()
        {
            try { return IsProcessDPIAware(); }
            catch (EntryPointNotFoundException) { return true; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsProcessDPIAware();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }
    }
}
