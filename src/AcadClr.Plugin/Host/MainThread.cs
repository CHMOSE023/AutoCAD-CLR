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
    /// 管道 / HTTP 线程入队并阻塞等待；主线程在 Application.Idle 时出队执行。
    /// 入队后向主窗口投递 WM_NULL 唤醒消息泵，否则 AutoCAD 在后台时延迟不可预测（实测首次调用可达 4 秒）。
    /// 模态对话框期间不触发 Idle，只能靠超时兜底。
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
            CacheMainWindow();
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            // 插件初始化时主窗口可能还没创建，这里补取
            if (_mainWindow == IntPtr.Zero) CacheMainWindow();
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* action 内部已捕获并回传 */ }
            }
        }

        /// <summary>
        /// 在主线程执行并等待结果。
        /// <paramref name="ct"/> 取消（调用方已断开）时：尚未开始的工作项直接跳过，不再执行；已开始的无法中断，只是不再等待。
        /// </summary>
        public static T Invoke<T>(Func<T> func, int timeoutMs, CancellationToken ct = default)
        {
            T result = default!;
            Exception? error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                Queue.Enqueue(() =>
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        result = func();
                    }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });

                Wake();

                if (!done.Wait(timeoutMs, ct))
                    throw new TimeoutException($"AutoCAD 主线程 {timeoutMs / 1000} 秒内未处理请求（可能正在执行命令或弹出了对话框）。");
            }
            if (error != null) throw error;
            return result;
        }

        /// <summary>向主窗口投递 WM_NULL，让消息循环走一轮：处理 Idle 队列，也推动命令队列里刚排进去的字符串。</summary>
        public static void Wake()
        {
            if (_mainWindow == IntPtr.Zero) CacheMainWindow();
            if (_mainWindow != IntPtr.Zero) PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
        }

        // 用进程 API 取句柄，任意线程都可调用（AutoCAD 的 Application.MainWindow 只能在主线程访问）
        private static void CacheMainWindow()
        {
            try { _mainWindow = Process.GetCurrentProcess().MainWindowHandle; }
            catch (InvalidOperationException) { /* 主窗口尚未创建，下次再取 */ }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
