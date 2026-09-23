using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AcadClr.Core;

namespace AcadClr.Cli
{
    /// <summary>
    /// 用户配置：%LOCALAPPDATA%\AutoCADCLR\config.json。
    /// 目前只有 acad：离线模式默认使用的 AutoCAD（年份或 accoreconsole.exe 路径）。
    /// </summary>
    internal static class Config
    {
        public static readonly string[] Keys = { "acad" };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCADCLR", "config.json");

        public static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException) { }
            return new Dictionary<string, string>();
        }

        public static string? Get(string key) => Load().TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        public static void Set(string key, string? value)
        {
            var all = Load();
            if (string.IsNullOrEmpty(value)) all.Remove(key); else all[key] = value!;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, Json.Serialize(all, true), new UTF8Encoding(false));
        }

        public static string Location => FilePath;
    }
}
