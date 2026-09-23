using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AcadClr.Plugin.Host;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

[assembly: ExtensionApplication(typeof(AcadClr.Plugin.PluginEntry))]
[assembly: CommandClass(typeof(AcadClr.Plugin.PluginEntry))]

namespace AcadClr.Plugin
{
    /// <summary>
    /// 插件入口。
    /// 在 AutoCAD 界面中 NETLOAD：自动启动命名管道服务（实时模式）。
    /// 在 accoreconsole 中：不启动服务，只提供 ACADCLR_RUN 命令给离线模式调用。
    /// </summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        private static PipeServer? _server;

        private static bool IsCoreConsole =>
            Process.GetCurrentProcess().ProcessName.Equals("accoreconsole", StringComparison.OrdinalIgnoreCase);

        public void Initialize()
        {
            if (IsCoreConsole) return;
            try { StartServer(); }
            catch (Exception ex) { Print("启动失败：" + ex.Message + "。可执行 ACADCLR_START 重试。"); }
        }

        public void Terminate() => StopServer();

        [CommandMethod("ACADCLR_START")]
        public void Start()
        {
            if (_server != null) { Print("已在运行，管道：" + _server.PipeName); return; }
            try { StartServer(); }
            catch (Exception ex) { Print("启动失败：" + ex.Message); }
        }

        [CommandMethod("ACADCLR_STOP")]
        public void Stop()
        {
            StopServer();
            Print("已停止。");
        }

        [CommandMethod("ACADCLR_STATUS")]
        public void Status() =>
            Print(_server != null ? "运行中，管道：" + _server.PipeName : "未运行（ACADCLR_START 启动）。");

        /// <summary>离线模式入口：accoreconsole 脚本调用，参数为请求文件路径。</summary>
        [CommandMethod("ACADCLR_RUN")]
        public void Run()
        {
            var ed = CoreApp.DocumentManager.MdiActiveDocument.Editor;
            var pr = ed.GetString(new PromptStringOptions("\n请求文件：") { AllowSpaces = true });
            if (pr.Status != PromptStatus.OK) return;
            OfflineHost.RunFile(pr.StringResult.Trim().Trim('"'));
        }

        /// <summary>
        /// 打印命令：实时模式由插件从命令队列触发（参数在内存里）；离线模式由脚本调用，参数为请求文件路径。
        /// PlotEngine 必须在文档上下文（命令）中运行。
        /// </summary>
        [CommandMethod("ACADCLR_PLOT")]
        public void Plot() => Plotting.Command();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartServer()
        {
            MainThread.EnsureInstalled();
            var server = new PipeServer(LiveHost.Handle);
            server.Start(Convert.ToString(CoreApp.GetSystemVariable("ACADVER")) ?? "");
            _server = server;
            Print("已启动，管道：" + server.PipeName);
            Print("命令行工具：acadclr status");
        }

        private static void StopServer()
        {
            _server?.Dispose();
            _server = null;
        }

        private static void Print(string message)
        {
            try { CoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[AutoCADCLR] " + message); }
            catch (Exception) { /* 没有活动文档时不打印 */ }
        }
    }
}
