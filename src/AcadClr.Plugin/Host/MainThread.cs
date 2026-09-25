using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// 把工作项编组到 AutoCAD 主线程执行（AutoCAD API 只能在主线程调用）。
    /// 管道线程入队并阻塞等待；主线程在 Application.Idle 时出队执行。
    /// 入队后向主窗口投递 WM_NULL 唤醒消息泵，否则 AutoCAD 在后台时延迟不可预测（实测首次调用可达 4 秒）。
    /// 模态对话框期间不触发 Idle，只能靠超时兜底。
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static int _installed;
        private static IntPtr _mainWindow;

        /// <summary>_mainWindow 已是主线程从 Application.MainWindow 取到的准确句柄。</summary>
        private static bool _exact;

        /// <summary>必须在主线程调用（插件初始化时）。</summary>
        public static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            CoreApp.Idle += OnIdle;
            CacheMainWindow();
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            // 插件初始化时主窗口可能还没创建，这里补取；也替换掉 Wake 在管道线程上取的备用句柄
            if (!_exact) CacheMainWindow();
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
            if (_mainWindow == IntPtr.Zero)
            {
                // 主线程还没取到句柄：先用进程 API 顶上（任意线程可调，但可能取到启动画面等其他顶层窗口），Idle 时再换成准确的
                try { _mainWindow = Process.GetCurrentProcess().MainWindowHandle; }
                catch (InvalidOperationException) { /* 主窗口尚未创建 */ }
            }
            if (_mainWindow != IntPtr.Zero) PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// 只在主线程调用（插件初始化、Idle 回调）：Application.MainWindow 属于 AutoCAD .NET API，不能在管道线程访问。
        /// 不用 Process.MainWindowHandle：它返回“第一个可见的顶层无主窗口”，可能是启动画面或浮动面板。
        /// </summary>
        private static void CacheMainWindow()
        {
            try
            {
                var hwnd = AcApp.MainWindow?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return; // 主窗口尚未创建，下次 Idle 再取
                _mainWindow = hwnd;
                _exact = true;
            }
            catch (Exception) { /* 主窗口尚不可用，下次 Idle 再取 */ }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
