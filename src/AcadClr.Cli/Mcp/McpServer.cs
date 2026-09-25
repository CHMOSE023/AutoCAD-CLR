using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using AcadClr.Core;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli.Mcp
{
    /// <summary>一次消息所在的传输环境。</summary>
    internal sealed class McpContext
    {
        /// <summary>HTTP 时为请求头（名称不区分大小写）；stdio 为 null。</summary>
        public IDictionary<string, string>? Headers { get; set; }

        /// <summary>客户端断开（HTTP）或发来 notifications/cancelled（stdio）时取消。</summary>
        public CancellationToken Cancel { get; set; }
    }

    /// <summary>协议错误：JSON-RPC error，HTTP 状态码随之确定。</summary>
    internal sealed class McpError : Exception
    {
        public int Code { get; }
        public int HttpStatus { get; }
        public JToken? ErrorData { get; }

        public McpError(int code, string message, int httpStatus = 400, JToken? data = null) : base(message)
        {
            Code = code; HttpStatus = httpStatus; ErrorData = data;
        }
    }

    /// <summary>
    /// MCP 协议处理（与传输无关）。“双代”服务端，逐请求判定：
    /// <list type="bullet">
    /// <item>请求 _meta 带 io.modelcontextprotocol/protocolVersion：按 2026-07-28 无状态规范处理
    ///       （server/discover、每个结果带 resultType 与 serverInfo、HTTP 校验 MCP-Protocol-Version / Mcp-Method / Mcp-Name）。</item>
    /// <item>否则按 2025-11-25 及更早的规范处理：响应 initialize（协商版本，不下发会话 ID）、ping；
    ///       tools/list、tools/call 与新规范相同，新增字段作为兼容的附加字段照常返回。</item>
    /// </list>
    /// 服务端不保存任何会话状态；跨调用的状态（文档、标记）都是普通参数。
    /// </summary>
    internal static class McpServer
    {
        public const string Modern = "2026-07-28";
        public static readonly string[] Legacy = { "2025-11-25", "2025-06-18", "2025-03-26" };
        public static IEnumerable<string> Supported => new[] { Modern }.Concat(Legacy);

        private const string MetaVersion = "io.modelcontextprotocol/protocolVersion";
        private const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

        // JSON-RPC / MCP 错误码
        public const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602, InternalError = -32603;
        public const int HeaderMismatch = -32020, UnsupportedProtocolVersion = -32022;

        /// <summary>工具列表在进程生命周期内不变（由命令表与启动参数决定）。</summary>
        private const int ToolsTtlMs = 3_600_000;

        public static JObject ServerInfo => new JObject
        {
            ["name"] = "acadclr",
            ["title"] = "AutoCADCLR",
            ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0",
        };

        private static string Instructions =>
            "操作 AutoCAD 图纸（DWG）。工具与 acadclr 命令一一对应、参数同名。" +
            "先调用 help（不带参数看总览，topic=类型名看属性，如 line；topic=命令名看参数），不要猜属性名。" +
            "目标用路径（/model、/entity[@handle=2A3]）、句柄或选择器（line[layer=WALL]）。" +
            "三条以上修改用 batch：一次往返、默认原子执行，\"$N\" 引用第 N 条的结果路径。" +
            "给 dwg 参数可离线读写 DWG 文件，否则操作运行中的 AutoCAD。" +
            (Dispatcher.ReadOnly ? "本服务以只读方式启动：修改图形的工具不可用。" : "") +
            (Dispatcher.AllowLisp ? "" : "lisp / script 未开放。");

        /// <summary>处理一条消息；通知返回 null。协议错误以 JSON-RPC error 返回，HTTP 状态码见 <paramref name="httpStatus"/>。</summary>
        public static JObject? Handle(JToken message, McpContext ctx, out int httpStatus)
        {
            httpStatus = 200;
            JToken? id = null;
            try
            {
                if (!(message is JObject msg) || (string?)msg["jsonrpc"] != "2.0")
                    throw new McpError(InvalidRequest, "不是 JSON-RPC 2.0 消息（批量数组不受支持）。");
                id = msg["id"];
                var method = (string?)msg["method"] ?? throw new McpError(InvalidRequest, "缺少 method。");
                var prms = msg["params"] as JObject ?? new JObject();
                bool isNotification = id == null;

                var meta = prms["_meta"] as JObject;
                var version = (string?)meta?[MetaVersion];
                if (version != null) CheckModern(version, method, prms, ctx);

                if (isNotification)
                {
                    // notifications/initialized、notifications/cancelled（stdio 在传输层处理）等：接受并忽略
                    httpStatus = 202;
                    return null;
                }

                var result = Dispatch(method, prms, version, ctx);
                // 新规范要求 resultType 与 serverInfo；对旧客户端是兼容的附加字段
                result["resultType"] = "complete";
                var rmeta = result["_meta"] as JObject ?? new JObject();
                rmeta[MetaServerInfo] = ServerInfo;
                result["_meta"] = rmeta;
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (McpError ex)
            {
                httpStatus = ex.HttpStatus;
                return Error(id, ex.Code, ex.Message, ex.ErrorData);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                httpStatus = 500;
                return Error(id, InternalError, ex.GetType().Name + "：" + ex.Message);
            }
        }

        public static JObject Error(JToken? id, int code, string message, JToken? data = null)
        {
            var err = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) err["data"] = data;
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["error"] = err };
        }

        /// <summary>新规范请求：版本必须支持；HTTP 上 MCP-Protocol-Version / Mcp-Method / Mcp-Name 必须存在且与消息体一致。</summary>
        private static void CheckModern(string version, string method, JObject prms, McpContext ctx)
        {
            if (version != Modern)
                throw new McpError(UnsupportedProtocolVersion, "Unsupported protocol version", 400,
                    new JObject { ["supported"] = new JArray(Supported), ["requested"] = version });
            if (ctx.Headers == null) return;

            void Expect(string header, string? bodyValue)
            {
                if (!ctx.Headers.TryGetValue(header, out var raw))
                    throw new McpError(HeaderMismatch, $"Header mismatch: 缺少请求头 {header}");
                var value = DecodeHeader(raw);
                if (value != bodyValue)
                    throw new McpError(HeaderMismatch, $"Header mismatch: {header} 的值 '{value}' 与消息体 '{bodyValue}' 不一致");
            }
            Expect("MCP-Protocol-Version", version);
            Expect("Mcp-Method", method);
            if (method == "tools/call" || method == "prompts/get") Expect("Mcp-Name", (string?)prms["name"]);
            else if (method == "resources/read") Expect("Mcp-Name", (string?)prms["uri"]);
        }

        /// <summary>请求头值的 Base64 哨兵格式 =?base64?…?=（非 ASCII 的工具名等）。</summary>
        public static string DecodeHeader(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("=?base64?", StringComparison.Ordinal) && raw.EndsWith("?=", StringComparison.Ordinal) && raw.Length >= 11)
            {
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw.Substring(9, raw.Length - 11))); }
                catch (FormatException) { throw new McpError(HeaderMismatch, "Header mismatch: Base64 编码的请求头无效"); }
            }
            return raw;
        }

        private static JObject Dispatch(string method, JObject prms, string? version, McpContext ctx)
        {
            bool modern = version != null;
            switch (method)
            {
                case "server/discover":
                    return new JObject
                    {
                        ["supportedVersions"] = new JArray(Supported),
                        ["capabilities"] = Capabilities(),
                        ["instructions"] = Instructions,
                        ["ttlMs"] = ToolsTtlMs,
                        ["cacheScope"] = "private",
                    };

                case "initialize" when !modern:
                {
                    // 旧协议握手：返回客户端请求的版本；不支持时给最新的旧版本。无状态，不下发 Mcp-Session-Id
                    var requested = (string?)prms["protocolVersion"];
                    var chosen = requested != null && Legacy.Contains(requested) ? requested : Legacy[0];
                    return new JObject
                    {
                        ["protocolVersion"] = chosen,
                        ["capabilities"] = Capabilities(),
                        ["serverInfo"] = ServerInfo,
                        ["instructions"] = Instructions,
                    };
                }

                case "ping" when !modern:          // 新规范已删除 ping
                case "logging/setLevel" when !modern: // 本服务不发日志通知，接受即可
                    return new JObject();

                case "tools/list":
                    return new JObject
                    {
                        ["tools"] = McpTools.List(),
                        ["ttlMs"] = ToolsTtlMs,
                        ["cacheScope"] = "private",
                    };

                case "tools/call":
                {
                    var name = (string?)prms["name"] ?? throw new McpError(InvalidParams, "tools/call 缺少 name。", 200);
                    var cmd = Commands.Find(name);
                    if (cmd == null || cmd.CliOnly || !string.Equals(cmd.Name, name, StringComparison.Ordinal))
                        throw new McpError(InvalidParams, $"Unknown tool: {name}", 200);
                    var args = prms["arguments"] as JObject ?? new JObject();
                    LiveTransport.Cancel = ctx.Cancel;
                    ctx.Cancel.ThrowIfCancellationRequested();
                    return McpTools.Result(Dispatcher.Dispatch(name, args));
                }

                default:
                    throw new McpError(MethodNotFound, $"Method not found: {method}", 404);
            }
        }

        private static JObject Capabilities() => new JObject { ["tools"] = new JObject { ["listChanged"] = false } };
    }
}
