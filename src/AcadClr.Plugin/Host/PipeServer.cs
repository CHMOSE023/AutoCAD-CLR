using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using AcadClr.Core;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// 命名管道服务：每个连接收一行 JSON 请求、回一行 JSON 响应。
    /// <list type="bullet">
    /// <item>管道名按进程号区分，并在 %LOCALAPPDATA%\AutoCADCLR\instances 写发现文件，CLI 据此找到本实例。</item>
    /// <item>ACL 只允许当前用户连接。</item>
    /// <item>多个连接可同时在线（例如打印进行中仍能查 status），实际操作由 <see cref="MainThread"/> 串行执行。</item>
    /// <item>读完请求后在管道上挂一个读操作：客户端不会再发数据，读到 EOF 即说明对方已断开，此时取消请求。</item>
    /// </list>
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        private const int MaxConnections = 8;

        private readonly Func<Request, CancellationToken, Response> _handler;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread? _thread;
        private string? _discoveryFile;

        public string PipeName { get; }

        public PipeServer(Func<Request, CancellationToken, Response> handler)
        {
            _handler = handler;
            PipeName = Constants.PipePrefix + Process.GetCurrentProcess().Id;
        }

        public void Start(InstanceInfo info)
        {
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "AutoCADCLR pipe" };
            _thread.Start();

            Directory.CreateDirectory(Constants.InstancesDir);
            _discoveryFile = Path.Combine(Constants.InstancesDir, info.Pid + ".json");
            info.Pipe = PipeName;
            File.WriteAllText(_discoveryFile, Json.Serialize(info, true));
        }

        private void AcceptLoop()
        {
            while (!_stop.WaitOne(0))
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = CreatePipe();
                    var ar = server.BeginWaitForConnection(null, null);
                    if (WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, _stop }) == 1) break;
                    server.EndWaitForConnection(ar);

                    var conn = server;
                    server = null; // 交给连接线程负责释放
                    ThreadPool.QueueUserWorkItem(_ => Serve(conn));
                }
                catch (IOException)
                {
                    // 连接数已满或客户端刚连上就断开：稍等再接
                    Thread.Sleep(100);
                }
                catch (Exception)
                {
                    Thread.Sleep(200);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxConnections,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var gone = new CancellationTokenSource())
            {
                try
                {
                    var line = LineIo.ReadLine(pipe);
                    if (line == null) return;

                    WatchDisconnect(pipe, gone);

                    Response resp;
                    try { resp = _handler(Json.Deserialize<Request>(line), gone.Token); }
                    catch (OperationCanceledException) { return; } // 客户端已断开，没人收结果
                    catch (Exception ex) { resp = Response.Fail("bad_request", ex.Message); }

                    if (gone.IsCancellationRequested) return;
                    LineIo.WriteLine(pipe, Json.Serialize(resp));
                    pipe.WaitForPipeDrain();
                }
                catch (IOException)
                {
                    // 客户端中途断开：忽略
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>挂一个 1 字节的读：读到 EOF 或出错说明客户端断开，触发取消。</summary>
        private static void WatchDisconnect(NamedPipeServerStream pipe, CancellationTokenSource gone)
        {
            var buf = new byte[1];
            try
            {
                pipe.BeginRead(buf, 0, 1, ar =>
                {
                    try { if (pipe.EndRead(ar) > 0) return; } // 协议外的多余数据，忽略
                    catch (Exception) { /* 断开或管道已释放 */ }
                    try { gone.Cancel(); } catch (ObjectDisposedException) { }
                }, null);
            }
            catch (IOException) { gone.Cancel(); }
        }

        public void Dispose()
        {
            _stop.Set();
            _thread?.Join(2000);
            try { if (_discoveryFile != null) File.Delete(_discoveryFile); } catch (IOException) { }
        }
    }
}
