using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AcadClr.Core;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli
{
    /// <summary>
    /// 操作日志：%LOCALAPPDATA%\AutoCADCLR\logs\acadclr-yyyyMMdd.log，一行一次调用。
    /// 记在 acadclr.exe 而不是插件里：命令行、MCP、离线三条路径都经过 <see cref="Dispatcher"/>，插件只看得到实时请求。
    /// 多个 acadclr 进程可能同时追加，写入允许共享、失败重试；日志写不进去绝不影响命令本身。
    /// </summary>
    internal static class OpLog
    {
        /// <summary>日志目录；环境变量 ACADCLR_LOG_DIR 可改（测试用它避免写进真实日志）。</summary>
        public static string Dir =>
            Environment.GetEnvironmentVariable("ACADCLR_LOG_DIR") is string d && d.Length > 0 ? d
                : Path.Combine(Path.GetDirectoryName(Constants.InstancesDir)!, "logs");

        public static string FileFor(DateTime day) => Path.Combine(Dir, "acadclr-" + day.ToString("yyyyMMdd") + ".log");

        /// <summary>
        /// 时间  来源  目标  命令  结果  耗时  参数摘要  [-> 错误]  [备份]
        /// 目标：live（实时，带 pid / doc）或离线文件名。
        /// </summary>
        public static void Write(string source, string command, JObject args, Call? call, Response resp, long ms)
        {
            try
            {
                string target = call?.Offline == true ? "offline:" + Path.GetFileName(call.Request?.Dwg ?? args.GetString("dwg") ?? "")
                    : "live" + (args.GetInt("pid") is int pid ? ":" + pid : "") + (args.GetString("doc") is string doc ? ":" + doc : "");
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("  ")
                  .Append(source.PadRight(9)).Append(' ')
                  .Append(target.PadRight(12)).Append(' ')
                  .Append(command.PadRight(8)).Append(' ')
                  .Append(Outcome(resp).PadRight(6)).Append(' ')
                  .Append((ms + "ms").PadLeft(7)).Append("  ")
                  .Append(Summary(command, args));
                if (resp.Error != null) sb.Append("  -> ").Append(resp.Error.Code).Append(": ").Append(Truncate(resp.Error.Message, 200));
                else if (resp.Failed > 0)
                {
                    var first = resp.Items?.FirstOrDefault(i => i.Error != null)?.Error;
                    if (first != null) sb.Append("  -> ").Append(first.Code).Append(": ").Append(Truncate(first.Message, 200));
                }
                if (resp.Backup != null) sb.Append("  backup=").Append(resp.Backup);
                Append(sb.ToString().Replace("\r", " ").Replace("\n", " "));
            }
            catch (Exception) { /* 日志失败不影响命令 */ }
        }

        private static string Outcome(Response resp)
        {
            if (resp.Error != null) return "ERR";
            if (resp.AtomicRolledBack == true) return "ROLLBK";
            if (resp.Failed > 0) return "PART";
            return "ok";
        }

        /// <summary>参数摘要：lisp / script 的代码记得多一些（审计任意代码执行），其余截断到 300 字。</summary>
        private static string Summary(string command, JObject args)
        {
            var copy = (JObject)args.DeepClone();
            foreach (var k in new[] { "pid", "doc", "dwg", "timeout" }) copy.Remove(k);
            if (command == "batch" && copy["items"] is JArray items) copy["items"] = $"[{items.Count} 条] " + Truncate(items.ToString(Newtonsoft.Json.Formatting.None), 240);
            var text = copy.ToString(Newtonsoft.Json.Formatting.None);
            return Truncate(text, command == "lisp" || command == "script" ? 1000 : 300);
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        private static void Append(string line)
        {
            Directory.CreateDirectory(Dir);
            var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(FileFor(DateTime.Now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { Thread.Sleep(20 * (attempt + 1)); }
            }
        }

        /// <summary>最近 n 行（今天的不够时往前补一天）。</summary>
        public static Response Tail(int n)
        {
            if (n < 1 || n > 2000) throw new CliError("usage", "log 的行数应在 1-2000 之间。");
            var lines = new List<string>();
            var files = new List<string>();
            for (int back = 0; back < 2 && lines.Count < n; back++)
            {
                var f = FileFor(DateTime.Now.AddDays(-back));
                if (!File.Exists(f)) continue;
                files.Add(f);
                string[] all;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    all = sr.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
                lines.InsertRange(0, all.Skip(Math.Max(0, all.Length - (n - lines.Count))));
            }
            return new Response
            {
                Data = new JObject
                {
                    ["dir"] = Dir,
                    ["files"] = new JArray(files),
                    ["lines"] = new JArray(lines),
                },
            };
        }
    }
}
