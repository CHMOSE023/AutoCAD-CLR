using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli.Mcp
{
    /// <summary>
    /// Streamable HTTP 传输：只监听 127.0.0.1，端点 POST /mcp，一个请求一个 JSON 响应。
    /// 用 TcpListener 自己处理 HTTP：HttpListener 在非管理员账户下要看 urlacl 配置，TcpListener 绑定回环地址没有这个限制。
    /// 每个连接只处理一个请求（Connection: close），读完请求后在连接上挂一个读：客户端断开即取消该请求。
    /// </summary>
    internal sealed class HttpHost
    {
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 32 * 1024 * 1024;

        private readonly int _port;
        private readonly string? _token;
        private TcpListener? _listener;

        public HttpHost(int port, string? token)
        {
            _port = port;
            _token = string.IsNullOrWhiteSpace(token) ? null : token!.Trim();
        }

        /// <summary>实际监听的端口（构造时给 0 则由系统分配，测试用）。</summary>
        public int Port => ((IPEndPoint)_listener!.LocalEndpoint).Port;

        /// <summary>命令行入口：开始监听并阻塞到 Ctrl+C。</summary>
        public int Run()
        {
            try { Start(); }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"错误：无法监听 127.0.0.1:{_port}（{ex.SocketErrorCode}），端口可能已被占用。");
                Console.Error.WriteLine("建议：用 --port 换一个端口；旧的 AutoCadMCP 插件默认占用 7130");
                return 2;
            }
            Console.Error.WriteLine($"[acadclr mcp] 已在 http://127.0.0.1:{Port}/mcp 监听（协议 {McpServer.Modern}，兼容 {string.Join(" / ", McpServer.Legacy)}）" +
                                    (_token != null ? "，需要 Bearer token" : "") + "。Ctrl+C 退出");
            var done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Stop(); done.Set(); };
            done.Wait();
            return 0;
        }

        /// <summary>开始监听并在后台接受连接；端口被占用时抛 SocketException。</summary>
        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            var listener = _listener;
            Task.Run(() =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (SocketException) { break; }        // Stop() 之后
                    catch (ObjectDisposedException) { break; }
                    Task.Run(() => Serve(client));
                }
            });
        }

        public void Stop() => _listener?.Stop();

        private sealed class HttpRequest
        {
            public string Method = "";
            public string Path = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];
        }

        private void Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var req = Read(stream);
                    if (req == null) return;
                    Handle(req, stream);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine("[acadclr mcp] " + ex.Message); }
            }
        }

        private void Handle(HttpRequest req, NetworkStream stream)
        {
            var path = req.Path.Split('?')[0].TrimEnd('/');
            if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)) { Plain(stream, 404, "Not Found", "MCP 端点是 /mcp"); return; }

            // 防 DNS 重绑定：Host 必须是回环地址；带 Origin 时也必须来自本机
            if (!req.Headers.TryGetValue("Host", out var host) || !IsLoopback(host)) { Plain(stream, 403, "Forbidden", "Host 必须是 127.0.0.1 / localhost"); return; }
            if (req.Headers.TryGetValue("Origin", out var origin) && !IsLocalOrigin(origin))
            {
                Json(stream, 403, "Forbidden", McpServer.Error(null, McpServer.InvalidRequest, "Origin 不被允许：" + origin));
                return;
            }
            if (_token != null && !(req.Headers.TryGetValue("Authorization", out var auth) && auth.Trim() == "Bearer " + _token))
            {
                Json(stream, 401, "Unauthorized", McpServer.Error(null, McpServer.InvalidRequest, "需要 Authorization: Bearer <token>"),
                    "WWW-Authenticate: Bearer realm=\"acadclr\"");
                return;
            }
            // 旧规范的 GET（独立 SSE 流）与 DELETE（结束会话）在新规范里都已取消
            if (req.Method != "POST") { Plain(stream, 405, "Method Not Allowed", "只接受 POST", "Allow: POST"); return; }

            JToken message;
            try { message = JToken.Parse(new UTF8Encoding(false).GetString(req.Body)); }
            catch (JsonException ex)
            {
                Json(stream, 400, "Bad Request", McpServer.Error(null, McpServer.ParseError, "JSON 解析失败：" + ex.Message));
                return;
            }

            // 请求读完后，客户端不会再发数据：连接上的读返回 0 或出错即表示断开，取消该请求
            using (var gone = new CancellationTokenSource())
            {
                var probe = new byte[1];
                stream.ReadAsync(probe, 0, 1).ContinueWith(t => { try { gone.Cancel(); } catch (ObjectDisposedException) { } });
                var resp = McpServer.Handle(message, new McpContext { Headers = req.Headers, Cancel = gone.Token }, out int status);
                if (gone.IsCancellationRequested) return;
                if (resp == null) { Plain(stream, 202, "Accepted", null); return; }
                Json(stream, status, Reason(status), resp);
            }
        }

        private static bool IsLoopback(string hostHeader)
        {
            var h = hostHeader.Trim().ToLowerInvariant();
            if (h.StartsWith("[", StringComparison.Ordinal)) h = h.Substring(0, h.IndexOf(']') + 1);
            else if (h.Contains(":")) h = h.Substring(0, h.LastIndexOf(':'));
            return h == "127.0.0.1" || h == "localhost" || h == "[::1]";
        }

        private static bool IsLocalOrigin(string origin)
        {
            if (origin == "null") return false;
            return Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") &&
                   (u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]" || u.Host == "::1");
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 500 => "Internal Server Error", _ => "Status",
        };

        // ------------------------------------------------------------------ HTTP 读写

        private static HttpRequest? Read(NetworkStream stream)
        {
            var buf = new MemoryStream();
            var one = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = stream.Read(one, 0, one.Length);
                if (n <= 0) return null;
                buf.Write(one, 0, n);
                if (buf.Length > MaxHeaderBytes) throw new IOException("请求头过大");
                headerEnd = IndexOf(buf.GetBuffer(), (int)buf.Length, new byte[] { 13, 10, 13, 10 });
            }
            var all = buf.ToArray();
            var head = Encoding.ASCII.GetString(all, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = head[0].Split(' ');
            if (first.Length < 2) throw new IOException("请求行无效");
            var req = new HttpRequest { Method = first[0].ToUpperInvariant(), Path = first[1] };
            foreach (var line in head.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) req.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            var rest = new MemoryStream();
            rest.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
            if (req.Headers.TryGetValue("Transfer-Encoding", out var te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                req.Body = ReadChunked(stream, rest.ToArray());
            else if (req.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int len))
            {
                if (len > MaxBodyBytes) throw new IOException("请求体过大");
                while (rest.Length < len)
                {
                    int n = stream.Read(one, 0, Math.Min(one.Length, len - (int)rest.Length));
                    if (n <= 0) throw new IOException("请求体不完整");
                    rest.Write(one, 0, n);
                }
                req.Body = rest.ToArray().Take(len).ToArray();
            }
            return req;
        }

        private static byte[] ReadChunked(NetworkStream stream, byte[] already)
        {
            var src = new MemoryStream();
            src.Write(already, 0, already.Length);
            var body = new MemoryStream();
            int pos = 0;
            var one = new byte[4096];
            byte[] Data() => src.GetBuffer();
            void Need(int count)
            {
                while (src.Length - pos < count)
                {
                    int n = stream.Read(one, 0, one.Length);
                    if (n <= 0) throw new IOException("分块请求体不完整");
                    src.Write(one, 0, n);
                }
            }
            string Line()
            {
                while (true)
                {
                    int idx = IndexOf(Data(), (int)src.Length, new byte[] { 13, 10 }, pos);
                    if (idx >= 0) { var s = Encoding.ASCII.GetString(Data(), pos, idx - pos); pos = idx + 2; return s; }
                    Need((int)(src.Length - pos) + 1);
                }
            }
            while (true)
            {
                var sizeLine = Line().Split(';')[0].Trim();
                int size = Convert.ToInt32(sizeLine, 16);
                if (size == 0) { Line(); return body.ToArray(); }
                if (body.Length + size > MaxBodyBytes) throw new IOException("请求体过大");
                Need(size + 2);
                body.Write(Data(), pos, size);
                pos += size + 2;
            }
        }

        private static int IndexOf(byte[] data, int length, byte[] pattern, int start = 0)
        {
            for (int i = start; i + pattern.Length <= length; i++)
            {
                int k = 0;
                while (k < pattern.Length && data[i + k] == pattern[k]) k++;
                if (k == pattern.Length) return i;
            }
            return -1;
        }

        private static void Json(NetworkStream stream, int status, string reason, JObject body, string? extraHeader = null) =>
            Send(stream, status, reason, "application/json; charset=utf-8", new UTF8Encoding(false).GetBytes(body.ToString(Formatting.None)), extraHeader);

        private static void Plain(NetworkStream stream, int status, string reason, string? text, string? extraHeader = null) =>
            Send(stream, status, reason, "text/plain; charset=utf-8", text == null ? new byte[0] : new UTF8Encoding(false).GetBytes(text), extraHeader);

        private static void Send(NetworkStream stream, int status, string reason, string contentType, byte[] body, string? extraHeader)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            if (body.Length > 0) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Connection: close\r\n");
            if (extraHeader != null) head.Append(extraHeader).Append("\r\n");
            head.Append("\r\n");
            var h = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(h, 0, h.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
