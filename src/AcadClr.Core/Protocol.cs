using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcadClr.Core
{
    /// <summary>
    /// batch 中的一条操作。字段与 OfficeCLI 的 batch 格式对齐：command/op、path、parent、type、props……
    /// 单条 CLI 命令（add / set / get ...）在内部也被包装成一条 BatchItem。
    /// </summary>
    public sealed class BatchItem
    {
        [JsonProperty("command", NullValueHandling = NullValueHandling.Ignore)]
        public string? Command { get; set; }

        [JsonProperty("op", NullValueHandling = NullValueHandling.Ignore)]
        public string? Op { get; set; }

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string? Path { get; set; }

        [JsonProperty("parent", NullValueHandling = NullValueHandling.Ignore)]
        public string? Parent { get; set; }

        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
        public string? Type { get; set; }

        [JsonProperty("from", NullValueHandling = NullValueHandling.Ignore)]
        public string? From { get; set; }

        [JsonProperty("selector", NullValueHandling = NullValueHandling.Ignore)]
        public string? Selector { get; set; }

        /// <summary>edit 的动作：offset / mirror / explode / break / join / array。</summary>
        [JsonProperty("action", NullValueHandling = NullValueHandling.Ignore)]
        public string? Action { get; set; }

        /// <summary>属性值允许写成 JSON 数字 / 布尔，统一在 <see cref="GetProps"/> 里转成字符串。</summary>
        [JsonProperty("props", NullValueHandling = NullValueHandling.Ignore)]
        public JObject? Props { get; set; }

        [JsonProperty("depth", NullValueHandling = NullValueHandling.Ignore)]
        public int? Depth { get; set; }

        [JsonProperty("limit", NullValueHandling = NullValueHandling.Ignore)]
        public int? Limit { get; set; }

        [JsonProperty("force", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Force { get; set; }

        [JsonIgnore]
        public string Verb => (Command ?? Op ?? "").Trim().ToLowerInvariant();

        /// <summary>按书写顺序返回属性（顺序有意义：几何属性先于 move/rotate 等变换）。</summary>
        public List<KeyValuePair<string, string>> GetProps()
        {
            var list = new List<KeyValuePair<string, string>>();
            if (Props == null) return list;
            foreach (var p in Props.Properties())
                list.Add(new KeyValuePair<string, string>(p.Name, TokenToString(p.Value)));
            return list;
        }

        private static string TokenToString(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.String: return (string)t!;
                case JTokenType.Boolean: return (bool)t ? "true" : "false";
                case JTokenType.Integer:
                case JTokenType.Float:
                    return Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture) ?? "";
                case JTokenType.Null: return "";
                default: return t.ToString(Formatting.None);
            }
        }
    }

    /// <summary>发往插件的一次请求。实时模式走命名管道，离线模式写成文件交给 accoreconsole。</summary>
    public sealed class Request
    {
        /// <summary>run | status | save | ping</summary>
        [JsonProperty("kind")] public string Kind { get; set; } = "run";

        [JsonProperty("items")] public List<BatchItem> Items { get; set; } = new List<BatchItem>();

        /// <summary>默认原子执行：任意一条失败则整批回滚。true 时保留成功的部分。</summary>
        [JsonProperty("bestEffort")] public bool BestEffort { get; set; }

        /// <summary>遇到第一条失败即停止，其余标记为 skipped。</summary>
        [JsonProperty("stopOnError")] public bool StopOnError { get; set; }

        /// <summary>save 的目标路径（另存为）。</summary>
        [JsonProperty("saveAs", NullValueHandling = NullValueHandling.Ignore)]
        public string? SaveAs { get; set; }

        /// <summary>lisp：要执行的 AutoLISP 代码；script：脚本文本。</summary>
        [JsonProperty("code", NullValueHandling = NullValueHandling.Ignore)]
        public string? Code { get; set; }

        /// <summary>lisp：走命令队列执行（(command ...) 需要文档上下文时用）。</summary>
        [JsonProperty("commandQueue")] public bool CommandQueue { get; set; }

        /// <summary>实时模式：本次请求的超时（毫秒）；不填用插件默认值（一般 120 秒，打印 180 秒）。</summary>
        [JsonProperty("timeoutMs", NullValueHandling = NullValueHandling.Ignore)]
        public int? TimeoutMs { get; set; }

        // ---- 以下仅离线模式使用 ----

        /// <summary>离线模式要操作的 DWG 路径。</summary>
        [JsonProperty("dwg", NullValueHandling = NullValueHandling.Ignore)]
        public string? Dwg { get; set; }

        /// <summary>离线模式：新建空白 DWG（文件已存在则报错）。</summary>
        [JsonProperty("create")] public bool Create { get; set; }

        /// <summary>
        /// 离线“文档模式”：accoreconsole 用 /i 把 DWG 打开成文档，插件直接操作该文档（而不是后台 Database）。
        /// 新建视口、打印必须这样做 —— 后台数据库没有图形系统，操作视口会让 accoreconsole 崩溃。
        /// 有修改时插件写一个标记文件（ResponsePath + ".save"），由脚本按原格式另存。
        /// </summary>
        [JsonProperty("useDocument")] public bool UseDocument { get; set; }

        /// <summary>离线模式：插件把 Response 写到这个文件。</summary>
        [JsonProperty("responsePath", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResponsePath { get; set; }
    }

    public sealed class ErrorInfo
    {
        [JsonProperty("code")] public string Code { get; set; } = "error";
        [JsonProperty("message")] public string Message { get; set; } = "";

        [JsonProperty("suggestion", NullValueHandling = NullValueHandling.Ignore)]
        public string? Suggestion { get; set; }
    }

    /// <summary>文档中一个元素（实体 / 图层 / 文档本身）的结构化表示。</summary>
    public sealed class Node
    {
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("type")] public string Type { get; set; } = "";
        [JsonProperty("props")] public Dictionary<string, string> Props { get; set; } = new Dictionary<string, string>();

        [JsonProperty("children", NullValueHandling = NullValueHandling.Ignore)]
        public List<Node>? Children { get; set; }

        /// <summary>children 因 limit 被截断时，记录总数。</summary>
        [JsonProperty("childCount", NullValueHandling = NullValueHandling.Ignore)]
        public int? ChildCount { get; set; }
    }

    public sealed class ItemResult
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("op")] public string Op { get; set; } = "";

        /// <summary>ok | failed | skipped</summary>
        [JsonProperty("status")] public string Status { get; set; } = "ok";

        /// <summary>add 返回新元素路径；get/set 返回目标路径。batch 里可用 "$序号" 引用。</summary>
        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string? Path { get; set; }

        [JsonProperty("node", NullValueHandling = NullValueHandling.Ignore)]
        public Node? Node { get; set; }

        [JsonProperty("nodes", NullValueHandling = NullValueHandling.Ignore)]
        public List<Node>? Nodes { get; set; }

        /// <summary>query / 选择器形式的 set、remove：命中数量。</summary>
        [JsonProperty("matched", NullValueHandling = NullValueHandling.Ignore)]
        public int? Matched { get; set; }

        [JsonProperty("truncated", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Truncated { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public ErrorInfo? Error { get; set; }

        [JsonIgnore] public bool Ok => Status == "ok";
    }

    public sealed class Response
    {
        [JsonProperty("ok")] public bool Ok { get; set; } = true;

        [JsonProperty("document", NullValueHandling = NullValueHandling.Ignore)]
        public string? Document { get; set; }

        [JsonProperty("items", NullValueHandling = NullValueHandling.Ignore)]
        public List<ItemResult>? Items { get; set; }

        [JsonProperty("succeeded", NullValueHandling = NullValueHandling.Ignore)]
        public int? Succeeded { get; set; }

        [JsonProperty("failed", NullValueHandling = NullValueHandling.Ignore)]
        public int? Failed { get; set; }

        [JsonProperty("skipped", NullValueHandling = NullValueHandling.Ignore)]
        public int? Skipped { get; set; }

        /// <summary>原子模式下有失败，整批已回滚，文档未被修改。</summary>
        [JsonProperty("atomicRolledBack", NullValueHandling = NullValueHandling.Ignore)]
        public bool? AtomicRolledBack { get; set; }

        /// <summary>false：本批含外部参照等数据库级操作，逐条执行、不支持回滚。</summary>
        [JsonProperty("atomic", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Atomic { get; set; }

        /// <summary>离线模式：改动已写回 DWG。</summary>
        [JsonProperty("saved", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Saved { get; set; }

        /// <summary>status / save 等非 batch 请求的结果。</summary>
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public JObject? Data { get; set; }

        /// <summary>图片结果（视图截图等），内嵌 base64。</summary>
        [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
        public List<ImageData>? Images { get; set; }

        /// <summary>请求级错误（连接失败、没有打开的图形等）。</summary>
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public ErrorInfo? Error { get; set; }

        public static Response Fail(string code, string message, string? suggestion = null) =>
            new Response { Ok = false, Error = new ErrorInfo { Code = code, Message = message, Suggestion = suggestion } };
    }

    public sealed class ImageData
    {
        /// <summary>image/png、image/jpeg 等。</summary>
        [JsonProperty("mimeType")] public string MimeType { get; set; } = "image/png";

        /// <summary>base64 编码的图片内容。</summary>
        [JsonProperty("data")] public string Data { get; set; } = "";

        [JsonProperty("width", NullValueHandling = NullValueHandling.Ignore)]
        public int? Width { get; set; }

        [JsonProperty("height", NullValueHandling = NullValueHandling.Ignore)]
        public int? Height { get; set; }
    }

    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture,
        };

        public static string Serialize(object o, bool indented = false) =>
            JsonConvert.SerializeObject(o, indented ? Formatting.Indented : Formatting.None, Settings);

        public static T Deserialize<T>(string s) =>
            JsonConvert.DeserializeObject<T>(s, Settings) ?? throw new InvalidDataException("JSON 为空。");
    }

    /// <summary>命名管道上的帧格式：一行一个 UTF-8 JSON。</summary>
    public static class LineIo
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static void WriteLine(Stream s, string line)
        {
            var bytes = Utf8.GetBytes(line.Replace("\r", "").Replace("\n", " ") + "\n");
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        public static string? ReadLine(Stream s)
        {
            var buf = new MemoryStream();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return buf.Length == 0 ? null : Utf8.GetString(buf.ToArray());
                if (b == '\n') return Utf8.GetString(buf.ToArray());
                buf.WriteByte((byte)b);
            }
        }
    }

    public static class Constants
    {
        public const string PipePrefix = "AutoCADCLR-";

        /// <summary>实例发现目录：每个加载了插件的 AutoCAD 进程写一个 &lt;pid&gt;.json。</summary>
        public static string InstancesDir =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoCADCLR", "instances");
    }

    /// <summary>实例发现文件内容。</summary>
    public sealed class InstanceInfo
    {
        [JsonProperty("pid")] public int Pid { get; set; }
        [JsonProperty("pipe")] public string Pipe { get; set; } = "";
        [JsonProperty("acadVersion")] public string AcadVersion { get; set; } = "";
        [JsonProperty("started")] public DateTime Started { get; set; }

        /// <summary>MCP HTTP 服务端口；未启动 HTTP 时为空。</summary>
        [JsonProperty("httpPort", NullValueHandling = NullValueHandling.Ignore)]
        public int? HttpPort { get; set; }
    }
}
