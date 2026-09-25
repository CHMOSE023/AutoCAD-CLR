using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli.Mcp
{
    /// <summary>
    /// stdio 传输：一行一个 JSON-RPC 消息，stdout 只写协议消息，日志写 stderr。
    /// 请求并发处理（AutoCAD 主线程与离线文件锁负责必要的串行），响应按完成顺序写出；
    /// notifications/cancelled 取消对应请求（关闭到插件的管道）。stdin 关闭后等进行中的请求收尾再退出。
    /// </summary>
    internal static class StdioHost
    {
        public static int Run(TextReader? input = null, TextWriter? output = null)
        {
            input ??= new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            output ??= new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Console.Error.WriteLine($"[acadclr mcp] stdio 已启动（协议 {McpServer.Modern}，兼容 {string.Join(" / ", McpServer.Legacy)}）");

            var writeLock = new object();
            var inflight = new ConcurrentDictionary<string, CancellationTokenSource>();
            var tasks = new List<Task>();

            void Write(JObject msg)
            {
                var line = msg.ToString(Formatting.None);
                lock (writeLock) output!.WriteLine(line);
            }

            string? raw;
            while ((raw = input.ReadLine()) != null)
            {
                if (raw.Trim().Length == 0) continue;
                JToken msg;
                try { msg = JToken.Parse(raw); }
                catch (JsonException ex)
                {
                    Write(McpServer.Error(null, McpServer.ParseError, "JSON 解析失败：" + ex.Message));
                    continue;
                }

                if (msg is JObject o && (string?)o["method"] == "notifications/cancelled")
                {
                    var target = o["params"]?["requestId"]?.ToString(Formatting.None);
                    if (target != null && inflight.TryGetValue(target, out var c)) c.Cancel();
                    continue;
                }

                var key = (msg as JObject)?["id"]?.ToString(Formatting.None);
                var cts = new CancellationTokenSource();
                if (key != null) inflight[key] = cts;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var resp = McpServer.Handle(msg, new McpContext { Cancel = cts.Token }, out _);
                        // 已取消的请求不再回写任何消息
                        if (resp != null && !cts.IsCancellationRequested) Write(resp);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Console.Error.WriteLine("[acadclr mcp] " + ex); }
                    finally
                    {
                        if (key != null) inflight.TryRemove(key, out _);
                        cts.Dispose();
                    }
                }));
                tasks.RemoveAll(t => t.IsCompleted);
            }

            // stdin 关闭：客户端要求退出。给进行中的请求留一点时间写回结果
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10));
            return 0;
        }
    }
}
