using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using AcadClr.Core;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli
{
    /// <summary>实时模式：通过命名管道连接已加载插件的 AutoCAD。</summary>
    internal static class LiveTransport
    {
        public static List<InstanceInfo> Instances()
        {
            var list = new List<InstanceInfo>();
            if (!Directory.Exists(Constants.InstancesDir)) return list;
            foreach (var f in Directory.GetFiles(Constants.InstancesDir, "*.json"))
            {
                try
                {
                    var info = Json.Deserialize<InstanceInfo>(File.ReadAllText(f));
                    if (IsAlive(info.Pid)) list.Add(info);
                    else File.Delete(f); // AutoCAD 已退出：清理残留的发现文件
                }
                catch (Exception) { /* 损坏或正被写入的文件，跳过 */ }
            }
            return list.OrderByDescending(i => i.Started).ToList();
        }

        private static bool IsAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return false; }
        }

        private static readonly System.Threading.AsyncLocal<System.Threading.CancellationToken> CancelSlot =
            new System.Threading.AsyncLocal<System.Threading.CancellationToken>();

        /// <summary>
        /// 当前调用的取消信号（acadclr mcp：HTTP 客户端断开、stdio 收到 notifications/cancelled）。
        /// 取消时关闭管道，插件感知到断开后跳过尚未开始的工作、不再回写结果。
        /// </summary>
        public static System.Threading.CancellationToken Cancel
        {
            get => CancelSlot.Value;
            set => CancelSlot.Value = value;
        }

        public static Response Send(Request req, int? pid)
        {
            var all = Instances();
            var inst = pid.HasValue ? all.FirstOrDefault(i => i.Pid == pid.Value) : all.FirstOrDefault();
            if (inst == null)
            {
                return Response.Fail("not_connected",
                    pid.HasValue ? $"没有找到进程号为 {pid} 的 AutoCAD 实例。" : "没有找到加载了 AutoCADCLR 插件的 AutoCAD。",
                    "在 AutoCAD 命令行执行 NETLOAD 加载 " + Path.Combine(AppDir, "AcadClr.Plugin.dll") +
                    "；或给 dwg 参数（命令行 --dwg <文件>）离线读写 DWG");
            }

            try
            {
                using (var client = new NamedPipeClientStream(".", inst.Pipe, PipeDirection.InOut))
                using (Cancel.Register(() => { try { client.Dispose(); } catch (Exception) { } }))
                {
                    client.Connect(5000);
                    LineIo.WriteLine(client, Json.Serialize(req));
                    var line = LineIo.ReadLine(client);
                    if (line == null) return Response.Fail("connection_lost", "AutoCAD 未返回结果就断开了连接。");
                    return Json.Deserialize<Response>(line);
                }
            }
            catch (TimeoutException)
            {
                return Response.Fail("not_connected", $"连接 AutoCAD（进程 {inst.Pid}）超时。", "AutoCAD 可能正忙，稍后重试");
            }
            catch (Exception ex) when (Cancel.IsCancellationRequested && (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException))
            {
                throw new OperationCanceledException(Cancel);
            }
            catch (IOException ex)
            {
                return Response.Fail("connection_lost", "与 AutoCAD 的连接中断：" + ex.Message);
            }
        }

        public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>
    /// 离线模式：启动 accoreconsole（AutoCAD 自带的无界面控制台），
    /// 由脚本 NETLOAD 插件并执行 ACADCLR_RUN，请求与响应通过临时 JSON 文件交换。
    /// </summary>
    internal static class OfflineTransport
    {
        public static Response Send(Request req, string? acadHint, int timeoutSec)
        {
            var console = FindConsole(acadHint, out var why);
            if (console == null) return Response.Fail("no_accoreconsole", why, "用 --acad <年份或 accoreconsole.exe 路径> 指定，或 acadclr config acad 2014");

            var plugin = Path.Combine(LiveTransport.AppDir, "AcadClr.Plugin.dll");
            if (!File.Exists(plugin)) return Response.Fail("plugin_missing", "找不到插件：" + plugin);

            req.Dwg = Path.GetFullPath(req.Dwg!);
            if (!req.Create && CheckOpenable(console, req.Dwg) is Response bad) return bad;

            // 新建视口必须在真正的文档上执行（后台数据库里操作视口会让 accoreconsole 崩溃）；
            // 系统变量只能读写活动文档，后台数据库没有对应的文档
            bool needDocument = req.Items.Any(i =>
                i.Verb == "add" && string.Equals(i.Type?.Trim(), "viewport", StringComparison.OrdinalIgnoreCase) ||
                (i.Path ?? i.Selector ?? "").IndexOf("sysvar", StringComparison.OrdinalIgnoreCase) >= 0);
            if (needDocument)
            {
                if (req.Create)
                {
                    var created = Send(new Request { Create = true, Dwg = req.Dwg }, acadHint, timeoutSec);
                    if (created.Error != null) return created;
                    req.Create = false;
                }
                return SendOnDocument(req, console, plugin, timeoutSec);
            }

            var tmp = NewTempDir();
            var reqPath = Path.Combine(tmp, "request.json");
            req.ResponsePath = Path.Combine(tmp, "response.json");
            File.WriteAllText(reqPath, Json.Serialize(req), new UTF8Encoding(false));

            // 插件在后台 Database 里读写 DWG，不依赖 /i 打开的文档；
            // 脚本里只出现插件与临时目录路径，DWG 路径（可能含中文）放在 UTF-8 的 request.json 中
            var script = Path.Combine(tmp, "run.scr");
            File.WriteAllText(script, string.Join("\r\n", "_.NETLOAD", "\"" + plugin + "\"", "ACADCLR_RUN", reqPath, "_.QUIT", "_Y", ""), Encoding.Default);

            try
            {
                var log = RunConsole(console, "/s \"" + script + "\"", tmp, timeoutSec, out bool finished);
                if (!finished) return Response.Fail("timeout", $"accoreconsole {timeoutSec} 秒内未完成。", "大图可用 --timeout 加长");
                if (!File.Exists(req.ResponsePath))
                    return Response.Fail("offline_failed", "accoreconsole 没有返回结果，插件多半未能加载（SECURELOAD 拒绝了不受信任位置的 DLL）。" +
                        (log.Length > 0 ? "控制台输出末尾：\n" + Tail(log, 12) : ""),
                        $"在 AutoCAD {ConsoleYear(console)}「选项 → 文件 → 受信任的位置」中添加 {LiveTransport.AppDir.TrimEnd('\\')}");
                return Json.Deserialize<Response>(File.ReadAllText(req.ResponsePath, Encoding.UTF8));
            }
            finally { Cleanup(tmp); }
        }

        /// <summary>
        /// 文档模式：accoreconsole /i 打开 DWG，NETLOAD 插件后在该文档上执行 ACADCLR_RUN；
        /// 插件确认有修改时写标记文件，脚本见到标记才按原格式另存。
        /// </summary>
        private static Response SendOnDocument(Request req, string console, string plugin, int timeoutSec)
        {
            if (req.Dwg != null && IsLocked(req.Dwg))
                return Response.Fail("file_locked", $"文件正被占用（可能已在 AutoCAD 中打开）：{req.Dwg}", "关闭后重试，或改用实时模式");

            var tmp = NewTempDir();
            var reqPath = Path.Combine(tmp, "request.json");
            req.UseDocument = true;
            req.ResponsePath = Path.Combine(tmp, "response.json");
            File.WriteAllText(reqPath, Json.Serialize(req), new UTF8Encoding(false));
            var before = File.GetLastWriteTimeUtc(req.Dwg!);
            try
            {
                var body = string.Join("\r\n", "_.NETLOAD", "\"" + plugin + "\"", "ACADCLR_RUN", reqPath);
                var log = RunScript(console, req.Dwg!, body, true, tmp, timeoutSec, out bool finished, out string? opened, req.ResponsePath + ".save");
                if (!finished) return Response.Fail("timeout", $"accoreconsole {timeoutSec} 秒内未完成。", "大图可用 --timeout 加长");
                if (WrongDocument(req.Dwg!, opened) is Response wrong) return wrong;
                if (!File.Exists(req.ResponsePath))
                    return Response.Fail("offline_failed", "accoreconsole 没有返回结果（文档模式）。" + (log.Length > 0 ? "控制台输出末尾：\n" + Tail(log, 12) : ""));

                var resp = Json.Deserialize<Response>(File.ReadAllText(req.ResponsePath, Encoding.UTF8));
                if (resp.Saved == true && File.GetLastWriteTimeUtc(req.Dwg!) == before)
                {
                    resp.Saved = false;
                    resp.Ok = false;
                    resp.Error = new ErrorInfo { Code = "save_failed", Message = "修改已完成，但另存回原文件失败，文件未改变。" };
                }
                return resp;
            }
            finally { Cleanup(tmp); }
        }

        /// <summary>
        /// 离线打印：/i 打开 DWG，脚本先 (setvar "CTAB" 布局) 切到目标布局（命令里不能切），
        /// 再 NETLOAD 插件并执行 ACADCLR_PLOT。打印不修改图纸，退出时放弃修改。
        /// </summary>
        public static Response Plot(string dwg, string? layout, JObject? props, string? acadHint, int timeoutSec)
        {
            var console = FindConsole(acadHint, out var why);
            if (console == null) return Response.Fail("no_accoreconsole", why, "用 --acad <年份或 accoreconsole.exe 路径> 指定，或 acadclr config acad 2014");
            var plugin = Path.Combine(LiveTransport.AppDir, "AcadClr.Plugin.dll");
            var full = Path.GetFullPath(dwg);
            if (CheckOpenable(console, full) is Response bad) return bad;

            var name = string.IsNullOrWhiteSpace(layout) ? "Model" : layout!;
            var tmp = NewTempDir();
            var reqPath = Path.Combine(tmp, "request.json");
            var req = new Request
            {
                Kind = "plot",
                Dwg = full,
                ResponsePath = Path.Combine(tmp, "response.json"),
                Items = { new BatchItem { Command = "plot", Path = $"/layout[@name={name}]", Props = props } },
            };
            File.WriteAllText(reqPath, Json.Serialize(req), new UTF8Encoding(false));
            try
            {
                var body = string.Join("\r\n", "(setvar \"CTAB\" " + LispString(name) + ")", "_.NETLOAD", "\"" + plugin + "\"", "ACADCLR_PLOT", reqPath);
                var log = RunScript(console, full, body, false, tmp, timeoutSec, out bool finished, out string? opened);
                if (!finished) return Response.Fail("timeout", $"accoreconsole {timeoutSec} 秒内未完成。", "大图可用 --timeout 加长");
                if (WrongDocument(full, opened) is Response wrong) return wrong;
                if (!File.Exists(req.ResponsePath))
                    return Response.Fail("offline_failed", "accoreconsole 没有返回打印结果。" + (log.Length > 0 ? "控制台输出末尾：\n" + Tail(log, 12) : ""),
                        "布局名是否正确（acadclr get <dwg> /layouts）；插件目录是否在受信任位置");
                return Json.Deserialize<Response>(File.ReadAllText(req.ResponsePath, Encoding.UTF8));
            }
            finally { Cleanup(tmp); }
        }

        // ---------------- lisp / script：直接让 accoreconsole 打开 DWG 执行，不需要插件 ----------------

        /// <summary>离线执行 AutoLISP，取回返回值。save=true 时执行后 QSAVE。</summary>
        public static Response Lisp(string dwg, string code, bool save, string? acadHint, int timeoutSec)
        {
            var console = FindConsole(acadHint, out var why);
            if (console == null) return Response.Fail("no_accoreconsole", why, "用 --acad <年份或 accoreconsole.exe 路径> 指定，或 acadclr config acad 2014");
            var full = Path.GetFullPath(dwg);
            if (CheckOpenable(console, full) is Response bad) return bad;
            if (save && IsLocked(full)) return Response.Fail("file_locked", $"文件正被占用（可能已在 AutoCAD 中打开）：{full}", "关闭后重试，或去掉 --save 只读执行");

            var tmp = NewTempDir();
            var outFile = Path.Combine(tmp, "result.out");
            var before = File.GetLastWriteTimeUtc(full);
            try
            {
                var log = RunScript(console, full, LispWrap.Wrap(code, outFile), save, tmp, timeoutSec, out bool finished, out string? opened);
                if (!finished) return Response.Fail("timeout", $"accoreconsole {timeoutSec} 秒内未完成。", "代码可能在等待交互输入，或用 --timeout 加长");
                if (WrongDocument(full, opened) is Response wrong) return wrong;
                if (!File.Exists(outFile))
                    return Response.Fail("lisp_error", "LISP 没有产出结果，多半是括号不匹配或语法错误。" + (log.Length > 0 ? "控制台输出末尾：\n" + Tail(log, 12) : ""));

                var resp = LispWrap.Parse(File.ReadAllBytes(outFile), LispWrap.IsUtf8Year(ConsoleYear(console)));
                resp.Document = full;
                if (save) resp.Saved = File.GetLastWriteTimeUtc(full) != before;
                return resp;
            }
            finally { Cleanup(tmp); }
        }

        /// <summary>对一个或多个 DWG（支持通配符）逐个执行脚本，返回每个文件的控制台输出。</summary>
        public static Response Script(List<string> patterns, string text, bool save, string? acadHint, int timeoutSec)
        {
            var console = FindConsole(acadHint, out var why);
            if (console == null) return Response.Fail("no_accoreconsole", why, "用 --acad <年份或 accoreconsole.exe 路径> 指定，或 acadclr config acad 2014");

            var files = Expand(patterns);
            if (files.Count == 0) return Response.Fail("not_found", "没有匹配的 DWG 文件：" + string.Join("，", patterns));

            var results = new JArray();
            bool allOk = true;
            foreach (var f in files)
            {
                var entry = new JObject { ["file"] = f };
                if (CheckOpenable(console, f) is Response bad)
                {
                    entry["ok"] = false;
                    entry["error"] = bad.Error!.Message;
                    allOk = false;
                    results.Add(entry);
                    continue;
                }
                if (save && IsLocked(f))
                {
                    entry["ok"] = false;
                    entry["error"] = "文件正被占用（可能已在 AutoCAD 中打开），已跳过";
                    allOk = false;
                    results.Add(entry);
                    continue;
                }

                var tmp = NewTempDir();
                var before = File.GetLastWriteTimeUtc(f);
                try
                {
                    var log = RunScript(console, f, text, save, tmp, timeoutSec, out bool finished, out string? opened);
                    entry["ok"] = finished;
                    if (!finished) { entry["error"] = $"{timeoutSec} 秒内未完成，已终止"; allOk = false; }
                    else if (WrongDocument(f, opened) is Response wrong) { entry["ok"] = false; entry["error"] = wrong.Error!.Message; allOk = false; }
                    entry["saved"] = File.GetLastWriteTimeUtc(f) != before;
                    entry["log"] = log;
                }
                finally { Cleanup(tmp); }
                results.Add(entry);
            }
            return new Response { Ok = allOk, Data = new JObject { ["results"] = results } };
        }

        /// <summary>
        /// accoreconsole /i dwg /s 脚本，由三部分组成：
        /// <list type="number">
        /// <item>记录实际打开的图纸。/i 打不开文件时（版本过高、损坏），控制台会静默退回空白的 Drawing1 继续执行，
        ///       必须事后核对，否则改动会落在错误的图上。</item>
        /// <item>用户的代码 / 脚本。</item>
        /// <item>save 时：确认当前图纸就是目标文件，再按原格式 SAVEAS 覆盖（QSAVE 会把旧格式升级成当前版本格式）。
        ///       最后 QUIT；图形改过又没保存会问“是否放弃修改”，答 Y。</item>
        /// </list>
        /// </summary>
        /// <param name="saveFlag">不为 null 时，只有该文件存在（插件确认有修改）才另存。</param>
        private static string RunScript(string console, string dwg, string body, bool save, string tmp, int timeoutSec,
            out bool finished, out string? opened, string? saveFlag = null)
        {
            var marker = Path.Combine(tmp, "opened.txt");
            var parts = new List<string>
            {
                "(progn (setq #acm (open \"" + marker.Replace("\\", "/") + "\" \"w\"))" +
                " (write-line (strcat (getvar \"DWGPREFIX\") (getvar \"DWGNAME\")) #acm) (close #acm) (setq #acm nil) (princ))",
                body,
            };
            if (save)
            {
                var lispPath = LispString(dwg);
                var cond = "(= (strcase (strcat (getvar \"DWGPREFIX\") (getvar \"DWGNAME\"))) (strcase " + lispPath + "))";
                if (saveFlag != null) cond = "(and " + cond + " (findfile " + LispString(saveFlag.Replace("\\", "/")) + "))";
                parts.Add("(if " + cond + " (command \"_.SAVEAS\" \"" + SaveFormat(dwg) + "\" " + lispPath + " \"_Y\"))");
            }
            parts.Add("_.QUIT");
            parts.Add("_Y");

            // 脚本里空行 = 回车，会重复上一条命令。各段只补齐缺少的行尾，绝不额外插入空行，
            // 否则用户脚本末尾用来结束命令的空行之后再多一个回车，就会把 LAYER 之类的命令重新调起来卡住。
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                var t = part.Replace("\r\n", "\n").Replace("\n", "\r\n");
                sb.Append(t);
                if (!t.EndsWith("\r\n", StringComparison.Ordinal)) sb.Append("\r\n");
            }
            var script = Path.Combine(tmp, "run.scr");
            File.WriteAllText(script, sb.ToString(), ScriptEncoding(console));
            var log = RunConsole(console, $"/i \"{dwg}\" /s \"{script}\"", tmp, timeoutSec, out finished);

            opened = File.Exists(marker) ? LispWrap.Decode(File.ReadAllBytes(marker), LispWrap.IsUtf8Year(ConsoleYear(console))).Trim() : null;
            return log;
        }

        private static Response? WrongDocument(string expected, string? opened)
        {
            if (opened != null && string.Equals(Path.GetFullPath(opened), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                return null;
            return Response.Fail("open_failed",
                $"accoreconsole 没能打开 {expected}" + (opened != null ? $"，实际打开的是 {opened}" : "") + "。未做任何保存。",
                "文件可能损坏、版本过高或正被占用");
        }

        /// <summary>写成 LISP 字符串字面量（反斜杠与引号转义）。</summary>
        private static string LispString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---------------- DWG 格式与 AutoCAD 版本 ----------------

        /// <summary>DWG 文件头版本码 → 格式年份。</summary>
        private static readonly Dictionary<string, int> FormatYears = new Dictionary<string, int>
        {
            ["AC1014"] = 1997, ["AC1015"] = 2000, ["AC1018"] = 2004, ["AC1021"] = 2007,
            ["AC1024"] = 2010, ["AC1027"] = 2013, ["AC1032"] = 2018,
        };

        private static string? HeaderOf(string file)
        {
            var buf = new byte[6];
            // 允许共享读：文件正在 AutoCAD 中打开时也能读文件头
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                if (fs.Read(buf, 0, 6) < 6) return null;
            var s = Encoding.ASCII.GetString(buf);
            return s.StartsWith("AC", StringComparison.Ordinal) ? s : null;
        }

        /// <summary>AutoCAD 能打开的最高格式：2018 起 2018 格式，2013-2017 为 2013 格式，2010-2012 为 2010 格式。</summary>
        private static int MaxFormatFor(int acadYear) =>
            acadYear >= 2018 ? 2018 : acadYear >= 2013 ? 2013 : acadYear >= 2010 ? 2010 : 2007;

        /// <summary>打开前检查：文件存在、是 DWG、格式不高于所用 AutoCAD 能打开的版本。</summary>
        private static Response? CheckOpenable(string console, string full)
        {
            if (!File.Exists(full)) return Response.Fail("not_found", "文件不存在：" + full);
            string? header;
            try { header = HeaderOf(full); }
            catch (IOException ex) { return Response.Fail("file_locked", $"无法读取 {full}：{ex.Message}"); }
            if (header == null) return Response.Fail("invalid_dwg", $"{full} 不是有效的 DWG 文件。");

            int acadYear = ConsoleYear(console);
            if (acadYear > 0 && FormatYears.TryGetValue(header, out int fmt) && fmt > MaxFormatFor(acadYear))
                return Response.Fail("unsupported_version",
                    $"{Path.GetFileName(full)} 是 AutoCAD {fmt} 格式（{header}），AutoCAD {acadYear} 只能打开 {MaxFormatFor(acadYear)} 及更早的格式。",
                    "换用更高版本（--acad 2020），或先在高版本中另存为 2013 格式");
            return null;
        }

        /// <summary>SAVEAS 的格式关键字：按文件原来的格式保存，避免把 2013 格式的图升级成当前版本格式。</summary>
        private static string SaveFormat(string file)
        {
            var header = HeaderOf(file);
            return header != null && FormatYears.TryGetValue(header, out int y) && y >= 2000 ? y.ToString() : "2013";
        }

        public static int ConsoleYear(string console)
        {
            var dir = Path.GetFileName(Path.GetDirectoryName(console) ?? "");
            return dir.Length >= 12 && int.TryParse(dir.Substring(8, 4), out int y) ? y : 0;
        }

        private static string RunConsole(string console, string args, string workDir, int timeoutSec, out bool finished)
        {
            var psi = new ProcessStartInfo(console, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode,
                WorkingDirectory = workDir,
            };
            var output = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.StandardInput.Close();
                finished = p.WaitForExit(timeoutSec * 1000);
                if (!finished) { try { p.Kill(); } catch (InvalidOperationException) { } }
                p.WaitForExit();
            }
            var clean = output.ToString().Replace("\0", "").Replace("\r", "");
            return string.Join("\n", clean.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
        }

        /// <summary>AutoCAD 2021 起 AutoLISP 支持 Unicode，脚本用带 BOM 的 UTF-8；更早版本用系统 ANSI 代码页。</summary>
        private static Encoding ScriptEncoding(string console) =>
            ConsoleYear(console) >= 2021 ? new UTF8Encoding(true) : Encoding.Default;

        /// <summary>读用户的 .lsp / .scr：有 BOM 按 BOM；否则先严格按 UTF-8 解码，失败再按系统 ANSI（中文 Windows 为 GBK）。</summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
                return File.ReadAllText(path, Encoding.Unicode); // 按 BOM 自动区分 UTF-16 LE / BE
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }

        private static List<string> Expand(IEnumerable<string> patterns)
        {
            var list = new List<string>();
            foreach (var p in patterns)
            {
                // .NET Framework 的 Path.GetFullPath 不接受 * ?，先拆出目录再展开
                var name = Path.GetFileName(p);
                if (name.IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    var rawDir = Path.GetDirectoryName(p);
                    var dir = Path.GetFullPath(string.IsNullOrEmpty(rawDir) ? "." : rawDir);
                    if (Directory.Exists(dir))
                        list.AddRange(Directory.GetFiles(dir, name).Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f));
                }
                else if (File.Exists(p)) list.Add(Path.GetFullPath(p));
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsLocked(string file)
        {
            try { using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static string NewTempDir()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "AutoCADCLR", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return tmp;
        }

        private static void Cleanup(string tmp)
        {
            if (Environment.GetEnvironmentVariable("ACADCLR_KEEP_TEMP") != null) return;
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }

        private static string Tail(string log, int n) =>
            string.Join("\n", log.Split('\n').Reverse().Take(n).Reverse());

        /// <summary>
        /// 查找 accoreconsole.exe，优先级：--acad（年份或完整路径）→ 环境变量 ACADCLR_ACCORE
        /// → acadclr config acad 的设置 → Program Files 中最新的 2014-2024 版本。
        /// 2025 起 AutoCAD 改用 .NET 8，无法加载本插件（.NET Framework 4.7.2），自动查找时跳过。
        /// </summary>
        public static string? FindConsole(string? hint, out string why)
        {
            why = "";
            string source = "--acad";
            if (string.IsNullOrEmpty(hint)) { hint = Environment.GetEnvironmentVariable("ACADCLR_ACCORE"); source = "ACADCLR_ACCORE"; }
            if (string.IsNullOrEmpty(hint)) { hint = Config.Get("acad"); source = "acadclr config acad"; }
            if (!string.IsNullOrEmpty(hint))
            {
                if (File.Exists(hint)) return hint;
                var byYear = Candidates().FirstOrDefault(c => c.year.ToString() == hint);
                if (byYear.path != null) return byYear.path;
                why = $"{source} 指定的 “{hint}” 既不是存在的文件，也没有对应年份的 AutoCAD。已安装：" +
                      string.Join("、", Candidates().Select(c => c.year.ToString()));
                return null;
            }

            var usable = Candidates().Where(c => c.year >= 2014 && c.year <= 2024).OrderByDescending(c => c.year).ToList();
            if (usable.Count > 0) return usable[0].path;
            why = "没有找到 AutoCAD 2014-2024 的 accoreconsole.exe。";
            return null;
        }

        public static IEnumerable<(int year, string path)> Candidates()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");
            if (!Directory.Exists(root)) yield break;
            foreach (var dir in Directory.GetDirectories(root, "AutoCAD 20*"))
            {
                var exe = Path.Combine(dir, "accoreconsole.exe");
                if (File.Exists(exe) && int.TryParse(Path.GetFileName(dir).Substring(8, 4), out int year))
                    yield return (year, exe);
            }
        }
    }
}
