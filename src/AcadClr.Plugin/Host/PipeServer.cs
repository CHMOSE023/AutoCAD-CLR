using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using AcadClr.Core;

namespace AcadClr.Plugin.Host
{
    /// <summary>
    /// 命名管道服务：每个连接收一行 JSON 请求、回一行 JSON 响应。
    /// 同时在 %LOCALAPPDATA%\AutoCADCLR\instances 写发现文件，CLI 据此找到本实例。
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        private readonly Func<Request, Response> _handler;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread? _thread;
        private string? _discoveryFile;

        public string PipeName { get; }

        public PipeServer(Func<Request, Response> handler)
        {
            _handler = handler;
            PipeName = Constants.PipePrefix + Process.GetCurrentProcess().Id;
        }

        public void Start(string acadVersion)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "AutoCADCLR pipe" };
            _thread.Start();

            Directory.CreateDirectory(Constants.InstancesDir);
            _discoveryFile = Path.Combine(Constants.InstancesDir, Process.GetCurrentProcess().Id + ".json");
            File.WriteAllText(_discoveryFile, Json.Serialize(new InstanceInfo
            {
                Pid = Process.GetCurrentProcess().Id,
                Pipe = PipeName,
                AcadVersion = acadVersion,
                Started = DateTime.Now,
            }, true));
        }

        private void Loop()
        {
            while (!_stop.WaitOne(0))
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    var ar = server.BeginWaitForConnection(null, null);
                    if (WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, _stop }) == 1) break;
                    server.EndWaitForConnection(ar);

                    var line = LineIo.ReadLine(server);
                    if (line == null) continue;

                    Response resp;
                    try { resp = _handler(Json.Deserialize<Request>(line)); }
                    catch (Exception ex) { resp = Response.Fail("bad_request", ex.Message); }

                    LineIo.WriteLine(server, Json.Serialize(resp));
                    server.WaitForPipeDrain();
                }
                catch (IOException)
                {
                    // 客户端中途断开：忽略，继续等下一个连接
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

        public void Dispose()
        {
            _stop.Set();
            _thread?.Join(2000);
            try { if (_discoveryFile != null) File.Delete(_discoveryFile); } catch (IOException) { }
        }
    }
}
