using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AcadClr.Cli;
using AcadClr.Cli.Mcp;
using AcadClr.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AcadClr.Tests
{
    public class McpServerTests : IDisposable
    {
        private readonly string _logDir = Path.Combine(Path.GetTempPath(), "acadclr-test-log-" + Guid.NewGuid().ToString("N"));

        public McpServerTests()
        {
            Environment.SetEnvironmentVariable("ACADCLR_LOG_DIR", _logDir);
            Dispatcher.AllowLisp = false; // 与 acadclr mcp 的缺省一致
        }

        public void Dispose()
        {
            Dispatcher.ReadOnly = false;
            Dispatcher.AllowLisp = true;
            Environment.SetEnvironmentVariable("ACADCLR_LOG_DIR", null);
            try { Directory.Delete(_logDir, true); } catch (IOException) { }
        }

        private const string Meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}";

        private static JObject? Send(string json, IDictionary<string, string>? headers, out int status) =>
            McpServer.Handle(JToken.Parse(json), new McpContext { Headers = headers }, out status);

        private static JObject Send(string json, IDictionary<string, string>? headers = null) => Send(json, headers, out _)!;

        private static Dictionary<string, string> Headers(string method, string? name = null, string version = "2026-07-28")
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MCP-Protocol-Version"] = version, ["Mcp-Method"] = method };
            if (name != null) h["Mcp-Name"] = name;
            return h;
        }

        [Fact]
        public void 新规范结果带resultType与serverInfo()
        {
            var r = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{" + Meta + "}}")["result"]!;
            Assert.Equal("complete", (string)r["resultType"]!);
            Assert.Equal("acadclr", (string)r["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!);
            Assert.Contains("2026-07-28", r["supportedVersions"]!.Select(v => (string)v!));
            Assert.Contains("2025-11-25", r["supportedVersions"]!.Select(v => (string)v!));
            Assert.NotNull(r["capabilities"]!["tools"]);
        }

        [Fact]
        public void 不支持的版本返回32022并列出支持的版本()
        {
            var e = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"1900-01-01\"}}}", null, out int status)!["error"]!;
            Assert.Equal(-32022, (int)e["code"]!);
            Assert.Equal("1900-01-01", (string)e["data"]!["requested"]!);
            Assert.Equal(McpServer.Supported, e["data"]!["supported"]!.Select(v => (string)v!));
            Assert.Equal(400, status);
        }

        [Fact]
        public void 旧协议initialize协商版本且不需要请求头()
        {
            var r = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}", new Dictionary<string, string>())["result"]!;
            Assert.Equal("2025-06-18", (string)r["protocolVersion"]!);
            Assert.Equal("acadclr", (string)r["serverInfo"]!["name"]!);
            var r2 = Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}")["result"]!;
            Assert.Equal(McpServer.Legacy[0], (string)r2["protocolVersion"]!);
        }

        [Fact]
        public void ping只在旧协议里有()
        {
            Assert.NotNull(Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}")["result"]);
            Assert.Equal(-32601, (int)Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{" + Meta + "}}")["error"]!["code"]!);
        }

        [Fact]
        public void 通知不回消息HTTP为202()
        {
            Assert.Null(Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", null, out int status));
            Assert.Equal(202, status);
        }

        [Fact]
        public void 未知方法404未知工具32602()
        {
            Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/list\",\"params\":{" + Meta + "}}", Headers("resources/list"), out int status);
            Assert.Equal(404, status);
            var e = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"config\",\"arguments\":{}," + Meta + "}}")["error"]!;
            Assert.Equal(-32602, (int)e["code"]!); // 只属于命令行的命令不是工具
        }

        [Theory]
        [InlineData(null, "tools/call", "help", "缺少请求头 MCP-Protocol-Version")]
        [InlineData("2025-11-25", "tools/call", "help", "MCP-Protocol-Version")]
        [InlineData("2026-07-28", "tools/list", "help", "Mcp-Method")]
        [InlineData("2026-07-28", "tools/call", null, "缺少请求头 Mcp-Name")]
        [InlineData("2026-07-28", "tools/call", "get", "Mcp-Name")]
        public void HTTP请求头必须与消息体一致(string? version, string method, string? name, string expected)
        {
            var h = Headers(method, name, version ?? "x");
            if (version == null) h.Remove("MCP-Protocol-Version");
            var e = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":{}," + Meta + "}}", h, out int status)!["error"]!;
            Assert.Equal(-32020, (int)e["code"]!);
            Assert.Contains(expected, (string)e["message"]!);
            Assert.Equal(400, status);
        }

        [Fact]
        public void Mcp_Name支持Base64哨兵格式()
        {
            var h = Headers("tools/call", "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("help")) + "?=");
            Assert.NotNull(Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":{}," + Meta + "}}", h)["result"]);
        }

        [Fact]
        public void 工具列表带缓存字段且顺序与命令表一致()
        {
            var r = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{" + Meta + "}}")["result"]!;
            Assert.Equal("private", (string)r["cacheScope"]!);
            Assert.True((int)r["ttlMs"]! > 0);
            var names = r["tools"]!.Select(t => (string)t["name"]!).ToList();
            Assert.Equal(Commands.All.Where(c => !c.CliOnly && c.Name != "lisp" && c.Name != "script").Select(c => c.Name), names);
        }

        [Fact]
        public void 工具参数与命令表一致()
        {
            foreach (var tool in McpTools.List())
            {
                var cmd = Commands.Find((string)tool["name"]!)!;
                var props = ((JObject)tool["inputSchema"]!["properties"]!).Properties().Select(p => p.Name);
                Assert.Equal(cmd.Args.Select(a => a.Name), props);
                var required = tool["inputSchema"]!["required"]?.Select(x => (string)x!) ?? Enumerable.Empty<string>();
                Assert.Equal(cmd.Args.Where(a => a.Required).Select(a => a.Name), required);
                Assert.Equal(!cmd.Writes, (bool)tool["annotations"]!["readOnlyHint"]!);
            }
        }

        [Fact]
        public void 动作参数带枚举()
        {
            var edit = McpTools.List().First(t => (string)t["name"]! == "edit");
            Assert.Contains("trim", edit["inputSchema"]!["properties"]!["action"]!["enum"]!.Select(x => (string)x!));
        }

        [Fact]
        public void 策略过滤工具列表()
        {
            Dispatcher.AllowLisp = true;
            Assert.Contains("lisp", McpTools.List().Select(t => (string)t["name"]!));
            Dispatcher.ReadOnly = true;
            var names = McpTools.List().Select(t => (string)t["name"]!).ToList();
            Assert.DoesNotContain("add", names);
            Assert.DoesNotContain("lisp", names);
            Assert.Contains("get", names);
            Assert.Contains("batch", names); // 纯查询的批处理照常可用
        }

        [Fact]
        public void 工具调用结果()
        {
            var ok = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":{\"topic\":\"line\"}}}")["result"]!;
            Assert.False((bool)ok["isError"]!);
            Assert.Contains("直线", (string)ok["content"]![0]!["text"]!);
            Assert.Equal("line", (string)ok["structuredContent"]!["data"]!["schema"]!["type"]!);

            var bad = Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get\",\"arguments\":{\"pth\":\"/\"}}}")["result"]!;
            Assert.True((bool)bad["isError"]!); // 参数错误是工具执行错误，模型可以据此改正
            Assert.Contains("是否想用 path", (string)bad["content"]![0]!["text"]!);

            var lisp = Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"lisp\",\"arguments\":{\"code\":\"(+ 1 2)\"}}}")["result"]!;
            Assert.Equal("lisp_disabled", (string)lisp["structuredContent"]!["error"]!["code"]!);
        }

        [Fact]
        public void 图片放在image内容块结构化结果里不带base64()
        {
            var resp = new Response { Data = new JObject { ["size"] = "10x10" }, Images = new List<ImageData> { new ImageData { Data = "AAAA", Width = 10, Height = 10 } } };
            var r = McpTools.Result(resp);
            Assert.Equal("image", (string)r["content"]![1]!["type"]!);
            Assert.Equal("AAAA", (string)r["content"]![1]!["data"]!);
            Assert.Null(r["structuredContent"]!["images"]);
            Assert.DoesNotContain("AAAA", (string)r["content"]![0]!["text"]!);
        }

        [Fact]
        public void 非法消息()
        {
            Assert.Equal(-32600, (int)Send("{\"id\":1,\"method\":\"tools/list\"}")["error"]!["code"]!);
            Assert.Equal(-32600, (int)Send("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}]")["error"]!["code"]!);
        }

        [Fact]
        public void stdio逐行收发并支持取消()
        {
            var input = new StringReader(string.Join("\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "not json",
                "{\"jsonrpc\":\"2.0\",\"id\":\"b\",\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":{\"topic\":\"circle\"}," + Meta + "}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"nope\"}}",
                ""));
            var output = new StringWriter();
            Assert.Equal(0, StdioHost.Run(input, output));
            var lines = output.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToList();
            Assert.Equal(3, lines.Count); // initialize、解析错误、tools/call；通知不回
            Assert.Contains(lines, l => (int?)l["error"]?["code"] == -32700);
            Assert.Contains(lines, l => (string?)l["id"] == "b" && (bool)l["result"]!["isError"]! == false);
        }
    }
}
