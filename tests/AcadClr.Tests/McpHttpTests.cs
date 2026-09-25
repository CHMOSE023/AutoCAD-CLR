using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using AcadClr.Cli;
using AcadClr.Cli.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AcadClr.Tests
{
    /// <summary>
    /// HTTP 传输（移植自 AutoCadMCP 的 test/AcadMcp.ProtocolTest，并补上新规范的用例）：
    /// 在进程内启动 HttpHost（系统分配端口），用 HttpClient 走真实的 TCP 连接。不需要 AutoCAD。
    /// </summary>
    public class McpHttpTests : IDisposable
    {
        private const string Token = "t0ken";
        private readonly HttpHost _host;
        private readonly HttpClient _http = new HttpClient();
        private readonly string _url;
        private readonly string _logDir = Path.Combine(Path.GetTempPath(), "acadclr-test-log-" + Guid.NewGuid().ToString("N"));

        private const string Meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}";

        public McpHttpTests()
        {
            Environment.SetEnvironmentVariable("ACADCLR_LOG_DIR", _logDir);
            _host = new HttpHost(0, Token);
            _host.Start();
            _url = $"http://127.0.0.1:{_host.Port}/mcp";
        }

        public void Dispose()
        {
            _host.Stop();
            _http.Dispose();
            Environment.SetEnvironmentVariable("ACADCLR_LOG_DIR", null);
            try { Directory.Delete(_logDir, true); } catch (IOException) { }
        }

        private (int status, string body, HttpResponseMessage resp) Send(HttpMethod method, string? json,
            bool auth = true, string? origin = null, string? version = null, string? mcpMethod = null, string? mcpName = null, string? contentType = "application/json")
        {
            var req = new HttpRequestMessage(method, _url);
            if (json != null)
            {
                req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
                if (contentType != null) req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (auth) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
            if (version != null) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
            if (mcpMethod != null) req.Headers.TryAddWithoutValidation("Mcp-Method", mcpMethod);
            if (mcpName != null) req.Headers.TryAddWithoutValidation("Mcp-Name", mcpName);
            var resp = _http.SendAsync(req).GetAwaiter().GetResult();
            return ((int)resp.StatusCode, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(), resp);
        }

        private static string Call(string tool, string args, bool modern = true) =>
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + (modern ? "," + Meta : "") + "}}";

        [Fact]
        public void 旧协议握手与通知()
        {
            var (st, body, resp) = Send(HttpMethod.Post, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}");
            Assert.Equal(200, st);
            Assert.Equal("2025-06-18", (string)JObject.Parse(body)["result"]!["protocolVersion"]!);
            Assert.False(resp.Headers.Contains("Mcp-Session-Id")); // 无状态：不下发会话 ID
            var (st2, body2, _) = Send(HttpMethod.Post, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            Assert.Equal(202, st2);
            Assert.Equal("", body2);
        }

        [Fact]
        public void 新协议调用需要一致的请求头()
        {
            var ok = Send(HttpMethod.Post, Call("help", "{\"topic\":\"circle\"}"), version: "2026-07-28", mcpMethod: "tools/call", mcpName: "help");
            Assert.Equal(200, ok.status);
            Assert.Equal("complete", (string)JObject.Parse(ok.body)["result"]!["resultType"]!);

            var missing = Send(HttpMethod.Post, Call("help", "{}"), version: "2026-07-28", mcpMethod: "tools/call");
            Assert.Equal(400, missing.status);
            Assert.Equal(-32020, (int)JObject.Parse(missing.body)["error"]!["code"]!);

            var wrongVersion = Send(HttpMethod.Post, Call("help", "{}"), version: "2025-06-18", mcpMethod: "tools/call", mcpName: "help");
            Assert.Equal(400, wrongVersion.status);
            Assert.Equal(-32020, (int)JObject.Parse(wrongVersion.body)["error"]!["code"]!);
        }

        [Fact]
        public void 不支持的版本400未知方法404()
        {
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2099-01-01\"}}}";
            var (st, text, _) = Send(HttpMethod.Post, body, version: "2099-01-01", mcpMethod: "tools/list");
            Assert.Equal(400, st);
            Assert.Equal(-32022, (int)JObject.Parse(text)["error"]!["code"]!);

            var (st2, text2, _) = Send(HttpMethod.Post, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"prompts/list\",\"params\":{" + Meta + "}}", version: "2026-07-28", mcpMethod: "prompts/list");
            Assert.Equal(404, st2);
            Assert.Equal(-32601, (int)JObject.Parse(text2)["error"]!["code"]!);
        }

        [Fact]
        public void token与Origin()
        {
            Assert.Equal(401, Send(HttpMethod.Post, Call("help", "{}", false), auth: false).status);
            Assert.Equal(403, Send(HttpMethod.Post, Call("help", "{}", false), origin: "http://evil.example.com").status);
            Assert.Equal(403, Send(HttpMethod.Post, Call("help", "{}", false), origin: "null").status);
            Assert.Equal(200, Send(HttpMethod.Post, Call("help", "{}", false), origin: "http://localhost:5173").status);
        }

        [Fact]
        public void GET与DELETE返回405()
        {
            var (st, _, resp) = Send(HttpMethod.Get, null);
            Assert.Equal(405, st);
            Assert.Contains("POST", resp.Content.Headers.Allow); // .NET 把 Allow 归为内容头
            Assert.Equal(405, Send(HttpMethod.Delete, null).status);
        }

        [Fact]
        public void 错误的路径与JSON()
        {
            var bad = _http.PostAsync(_url.Replace("/mcp", "/other"), new StringContent("{}")).GetAwaiter().GetResult();
            Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
            var (st, body, _) = Send(HttpMethod.Post, "{not json");
            Assert.Equal(400, st);
            Assert.Equal(-32700, (int)JObject.Parse(body)["error"]!["code"]!);
        }

        [Fact]
        public void 不带charset的UTF8请求体不乱码()
        {
            // 旧插件踩过的坑：Content-Type 不写 charset 时按 ISO-8859-1 解码，中文变乱码
            var (st, body, _) = Send(HttpMethod.Post, Call("get", "{\"pth\":\"客厅 α β\"}", false), contentType: "application/json");
            Assert.Equal(200, st);
            var text = (string)JObject.Parse(body)["result"]!["content"]![0]!["text"]!;
            Assert.Contains("pth", text); // 参数名原样回到错误信息里
            var (_, body2, _) = Send(HttpMethod.Post, Call("help", "{\"topic\":\"text\"}", false), contentType: null);
            Assert.Contains("单行文字", (string)JObject.Parse(body2)["result"]!["content"]![0]!["text"]!);
        }

        [Fact]
        public void 工具调用记进操作日志()
        {
            Send(HttpMethod.Post, Call("get", "{\"pth\":\"/\"}", false));
            var line = OpLog.Tail(5).Data!["lines"]!.Select(l => (string)l!).LastOrDefault();
            // 参数错误在 Prepare 阶段就被拒绝，不连接 AutoCAD，也不写日志；调用成功或执行失败才记
            Assert.Null(line);
            Dispatcher.ReadOnly = true;
            try { Send(HttpMethod.Post, Call("remove", "{\"path\":\"8A\"}", false)); }
            finally { Dispatcher.ReadOnly = false; }
            Assert.Contains("read_only", OpLog.Tail(5).Data!["lines"]!.Select(l => (string)l!).Last());
        }
    }
}
