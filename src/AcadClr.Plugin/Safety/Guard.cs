using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadClr.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Newtonsoft.Json;

namespace AcadClr.Plugin.Safety
{
    /// <summary>
    /// 实时模式的安全开关，管道入口统一把关（CLI 与 MCP 的请求都经过这里）：
    /// <list type="bullet">
    /// <item>只读模式：拒绝一切写请求（判定来自命令表 <see cref="Commands.IsWrite(Request)"/>）。</item>
    /// <item>LISP 开关：关闭时拒绝 lisp / script（任意代码执行）；trim 这类命令式编辑由插件自己生成代码，不受影响。</item>
    /// <item>写前备份：每个文档在本次会话第一次被写之前，把磁盘上最后保存的版本复制一份（移植自 AutoCADMCP）。</item>
    /// </list>
    /// 开关由 AutoCAD 命令 ACADCLR_READONLY / ACADCLR_LISP 切换，保存在 %LOCALAPPDATA%\AutoCADCLR\settings.json，
    /// 每个请求都重新读取（文件改动立即生效）。这些开关防的是误操作，不是恶意绕过：能改文件的程序也能改这个设置。
    /// </summary>
    internal static class Guard
    {
        private sealed class Settings
        {
            [JsonProperty("readOnly")] public bool ReadOnly { get; set; }
            [JsonProperty("allowLisp")] public bool AllowLisp { get; set; } = true;
        }

        private static readonly object Gate = new object();
        private static Settings _settings = new Settings();
        private static DateTime _loadedStamp = DateTime.MinValue;

        public static string SettingsFile => Path.Combine(Path.GetDirectoryName(Constants.InstancesDir)!, "settings.json");

        public static string BackupDir => Path.Combine(Path.GetDirectoryName(Constants.InstancesDir)!, "backups");

        public static bool ReadOnly => Current().ReadOnly;
        public static bool AllowLisp => Current().AllowLisp;

        private static Settings Current()
        {
            lock (Gate)
            {
                try
                {
                    var stamp = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.MinValue;
                    if (stamp != _loadedStamp)
                    {
                        _settings = stamp == DateTime.MinValue ? new Settings() : Json.Deserialize<Settings>(File.ReadAllText(SettingsFile));
                        _loadedStamp = stamp;
                    }
                }
                catch (Exception) { /* 设置文件损坏或正被写入：沿用上一次读到的 */ }
                return _settings;
            }
        }

        public static bool ToggleReadOnly() => Update(s => s.ReadOnly = !s.ReadOnly).ReadOnly;
        public static bool ToggleLisp() => Update(s => s.AllowLisp = !s.AllowLisp).AllowLisp;

        private static Settings Update(Action<Settings> change)
        {
            lock (Gate)
            {
                var s = Current();
                var copy = new Settings { ReadOnly = s.ReadOnly, AllowLisp = s.AllowLisp };
                change(copy);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, Json.Serialize(copy, true));
                _settings = copy;
                _loadedStamp = File.GetLastWriteTimeUtc(SettingsFile);
                return copy;
            }
        }

        /// <summary>按开关检查请求；放行返回 null。</summary>
        public static Response? Check(Request req)
        {
            if ((req.Kind == "lisp" || req.Kind == "script") && !AllowLisp)
                return Response.Fail("lisp_disabled", "AutoCAD 中已关闭 LISP / 脚本执行（任意代码执行）。",
                    "需要时在 AutoCAD 命令行执行 ACADCLR_LISP 打开；结构化命令（add / set / edit …）不受影响");
            if (ReadOnly && Commands.IsWrite(req))
                return Response.Fail("read_only", "AutoCAD 中已开启只读模式，拒绝修改图形的操作。",
                    "查询（get / query / measure / check / view / plot）照常可用；要修改请在 AutoCAD 命令行执行 ACADCLR_READONLY 关闭");
            return null;
        }

        // ------------------------------------------------------------------ 写前备份

        private static readonly Dictionary<string, string> Backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 文档在本次会话第一次被写之前调用：把磁盘上最后保存的版本复制到备份目录，返回备份路径；
        /// 已备份过、或是从未存盘的新图（没有磁盘文件）返回 null。复制失败不阻止写操作。
        /// </summary>
        public static string? BackupOnce(Document doc)
        {
            var path = doc.Name;
            if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;
            lock (Gate)
            {
                if (Backups.ContainsKey(path)) return null;
                var target = Path.Combine(BackupDir, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMdd-HHmmss}.dwg");
                try
                {
                    Directory.CreateDirectory(BackupDir);
                    // 允许共享读：文件正被 AutoCAD 打开时也能复制
                    using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        src.CopyTo(dst);
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                Backups[path] = target;
                return target;
            }
        }

        public static IEnumerable<string> BackupList()
        {
            lock (Gate) return Backups.Select(kv => kv.Key + " → " + kv.Value).ToList();
        }
    }
}
