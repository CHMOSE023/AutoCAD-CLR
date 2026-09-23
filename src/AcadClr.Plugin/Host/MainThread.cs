using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// 把工作项编组到 AutoCAD 主线程执行（AutoCAD API 只能在主线程调用）。
    /// 管道线程入队并阻塞等待；主线程在 Application.Idle 时出队执行。
    /// 入队后向主窗口投递 WM_NULL 唤醒消息泵，否则 AutoCAD 在后台时延迟不可预测。
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static int _installed;
        private static IntPtr _mainWindow;

        /// <summary>必须在主线程调用（插件初始化时）。</summary>
        public static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            CoreApp.Idle += OnIdle;
            _mainWindow = Process.GetCurrentProcess().MainWindowHandle;
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* action 内部已捕获并回传 */ }
            }
        }

        public static T Invoke<T>(Func<T> func, int timeoutMs)
        {
            T result = default!;
            Exception? error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                Queue.Enqueue(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });

                Wake();

                if (!done.Wait(timeoutMs))
                    throw new TimeoutException($"AutoCAD 主线程 {timeoutMs / 1000} 秒内未处理请求（可能正在执行命令或弹出了对话框）。");
            }
            if (error != null) throw error;
            return result;
        }

        /// <summary>向主窗口投递 WM_NULL，让消息循环走一轮：处理 Idle 队列，也推动命令队列里刚排进去的字符串。</summary>
        public static void Wake()
        {
            if (_mainWindow == IntPtr.Zero) _mainWindow = Process.GetCurrentProcess().MainWindowHandle;
            if (_mainWindow != IntPtr.Zero) PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
