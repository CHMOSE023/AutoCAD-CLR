using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AcadClr.Core;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// ACADCLR_MCP：从 AutoCAD 里拉起 / 停止 `acadclr mcp --http` 子进程（与插件同目录的 acadclr.exe），
    /// 让只会用 HTTP 连接的客户端不必另开终端。AutoCAD 正常退出时随之停止。
    /// 端口与 token 沿用 acadclr mcp 的缺省（7140）与环境变量 ACADCLR_MCP_TOKEN；子进程的输出写到日志目录。
    /// </summary>
    internal static class McpProcess
    {
        private static Process? _proc;

        public static string LogFile => Path.Combine(Path.GetDirectoryName(Constants.InstancesDir)!, "logs", "mcp-http.log");

        public static bool Running => _proc != null && !_proc.HasExited;

        /// <summary>未运行则启动，运行中则停止。返回给命令行的提示。</summary>
        public static string Toggle()
        {
            if (Running) { Stop(); return "已停止 acadclr mcp（HTTP）。"; }

            var exe = Path.Combine(Path.GetDirectoryName(typeof(McpProcess).Assembly.Location)!, "acadclr.exe");
            if (!File.Exists(exe)) return "找不到 " + exe + "：acadclr.exe 应与 AcadClr.Plugin.dll 放在同一目录。";
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);

            var psi = new ProcessStartInfo(exe, "mcp --http")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.ErrorDataReceived += (s, e) => Append(e.Data);
            proc.OutputDataReceived += (s, e) => Append(e.Data);
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            // 端口被占用等启动错误会让子进程立刻退出：稍等片刻看一眼
            if (proc.WaitForExit(1500))
            {
                var tail = File.Exists(LogFile) ? string.Join(" ", File.ReadAllLines(LogFile).Reverse().Take(2).Reverse()) : "";
                return "acadclr mcp 启动失败：" + tail;
            }
            _proc = proc;
            return "已启动 acadclr mcp（HTTP）：http://127.0.0.1:7140/mcp。再次执行 ACADCLR_MCP 停止；输出见 " + LogFile;
        }

        public static void Stop()
        {
            try { if (Running) _proc!.Kill(); } catch (InvalidOperationException) { }
            _proc = null;
        }

        private static void Append(string? line)
        {
            if (line == null) return;
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + line + Environment.NewLine, new UTF8Encoding(false)); }
            catch (IOException) { }
        }
    }
}
