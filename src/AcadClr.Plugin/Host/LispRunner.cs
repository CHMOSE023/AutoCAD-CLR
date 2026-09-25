using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using AcadClr.Core;
using Autodesk.AutoCAD.ApplicationServices;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// 实时模式执行 AutoLISP（沿用 AutoCADMCP 中已验证的两种方式）：
    /// <list type="bullet">
    /// <item>直接求值：主线程调用 acedEvaluateLisp，同步取值。不能执行 (command ...)。</item>
    /// <item>命令队列：SendStringToExecute 送进命令行，在文档上下文执行，(command ...) 可用；管道线程轮询结果文件。</item>
    /// </list>
    /// 两种方式都用 vl-catch-all-apply 捕获错误，把结果写到临时文件回传。
    /// 注意：不用 (load 临时文件)，那会触发 SECURELOAD 模态框卡住 AutoCAD。
    /// </summary>
    internal static class LispRunner
    {
        // accore.dll（x64）导出：int acedEvaluateLisp(const wchar_t*, resbuf*&)，2014-2024 名字一致
        [SuppressUnmanagedCodeSecurity]
        [DllImport("accore.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "?acedEvaluateLisp@@YAHPEB_WAEAPEAUresbuf@@@Z")]
        private static extern int acedEvaluateLisp(string lispLine, out IntPtr result);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acutRelRb")]
        private static extern int acutRelRb(IntPtr rb);

        /// <summary>主线程调用。</summary>
        public static Response Eval(string code)
        {
            var doc = ActiveDocument();
            var outFile = NewOutFile();
            try
            {
                int rc;
                using (doc.LockDocument())
                {
                    rc = acedEvaluateLisp(Wrap(code, outFile), out IntPtr res);
                    if (res != IntPtr.Zero) try { acutRelRb(res); } catch (Exception) { }
                }
                if (!File.Exists(outFile))
                    return Response.Fail("lisp_error", $"LISP 没有产出结果（acedEvaluateLisp rc={rc}），多半是括号不匹配或语法错误。",
                        "若代码里有 (command ...)，改用 --cmd 走命令队列");
                return ReadResult(outFile);
            }
            finally { TryDelete(outFile); }
        }

        /// <summary>
        /// 请求线程调用（内部轮询等待，不能占用主线程）。
        /// 命令一旦送进命令行就无法撤回；<paramref name="ct"/> 取消只是停止等待，结果文件留给 <see cref="SweepStale"/> 清理。
        /// </summary>
        /// <param name="command">
        /// 先于 LISP 送进命令行的普通命令（例如 "_.UNDO 1"）。UNDO N 放在 LISP 里用 (command ...) 调用时，
        /// 撤掉的是这次 LISP 求值自己那一组；必须作为独立命令发送，LISP 只负责在它执行完后写结果文件。
        /// </param>
        public static Response ViaCommandQueue(string code, int timeoutMs, CancellationToken ct = default, string? command = null)
        {
            SweepStale();
            var outFile = NewOutFile();
            bool got = false;
            try
            {
                // 沿用 AutoCADMCP 验证过的方式：在请求线程调用、activate=false。
                // 不要改成在主线程（Idle 回调，属应用程序上下文）以 activate=true 调用：
                // 实测 AutoCAD 会就地同步执行，(command "._trim" ...) 在应用程序上下文里运行导致 0xC0000005 崩溃。
                // 必须是“一整行 + 一个回车”：中间有换行会让末尾回车变成重复上一条命令的空回车，导致崩溃
                var body = (command != null ? command.Trim() + "\n" : "") + LispWrap.Wrap(code, outFile, singleLine: true) + "\n";
                var doc = MainThread.Invoke(ActiveDocument, 10_000, ct);
                doc.SendStringToExecute(body, false, false, false);

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                // 只轮询结果文件，不要向主窗口投递消息：(command ...) 执行期间干扰消息循环实测会让 AutoCAD 崩溃
                while (DateTime.UtcNow < deadline && !File.Exists(outFile))
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(30);
                }
                if (!File.Exists(outFile))
                    return Response.Fail("timeout", $"命令队列 {timeoutMs / 1000} 秒内未返回结果。",
                        "AutoCAD 可能正在执行其他命令，或命令停在交互提示上；在 AutoCAD 里按 Esc 后重试");
                Thread.Sleep(30); // 等文件写完
                got = true;
                return ReadResult(outFile);
            }
            finally { if (got) TryDelete(outFile); }
        }

        /// <summary>把脚本文本送进命令行（异步，不等待结果）。</summary>
        public static Response QueueScript(string text)
        {
            var doc = ActiveDocument();
            var body = text.Replace("\r\n", "\n");
            if (!body.EndsWith("\n", StringComparison.Ordinal)) body += "\n";
            // activate=false：在 Idle 回调（应用程序上下文）里 activate=true 会就地执行命令，可能崩溃
            doc.SendStringToExecute(body, false, false, false);
            return new Response { Document = doc.Name, Data = new Newtonsoft.Json.Linq.JObject { ["queued"] = true } };
        }

        /// <summary>
        /// 预热命令队列：首次 SendStringToExecute 处理很慢，插件启动时先送一个空表达式。
        /// 主线程调用；没有活动文档时跳过。
        /// </summary>
        public static void WarmUp()
        {
            try { CoreApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("(princ)\n", false, false, false); }
            catch (Exception) { /* 预热失败不影响使用 */ }
        }

        /// <summary>清理超过 5 分钟的结果文件（超时或调用方断开后遗留的）。</summary>
        private static void SweepStale()
        {
            try
            {
                var cutoff = DateTime.Now.AddMinutes(-5);
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "acadclr-lisp-*.out"))
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { }
        }

        private static Document ActiveDocument() =>
            CoreApp.DocumentManager.MdiActiveDocument
            ?? throw new CliError("no_document", "AutoCAD 当前没有打开的图形。");

        private static Response ReadResult(string outFile) => LispWrap.Parse(File.ReadAllBytes(outFile), LispIsUtf8);

        /// <summary>ACADVER 主版本 24 起（AutoCAD 2021）AutoLISP 为 Unicode，结果文件是 UTF-8；之前为系统 ANSI。</summary>
        private static bool LispIsUtf8 =>
            int.TryParse(new string(Convert.ToString(CoreApp.GetSystemVariable("ACADVER")).TakeWhile(char.IsDigit).ToArray()), out int major) && major >= 24;

        private static string Wrap(string code, string outFile) => LispWrap.Wrap(code, outFile);

        private static string NewOutFile() =>
            Path.Combine(Path.GetTempPath(), "acadclr-lisp-" + Guid.NewGuid().ToString("N") + ".out");

        private static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
        }
    }
}
