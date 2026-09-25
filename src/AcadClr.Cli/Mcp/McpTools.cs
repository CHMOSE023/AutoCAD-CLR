using System.Collections.Generic;
using System.Linq;
using AcadClr.Core;
using Newtonsoft.Json.Linq;

namespace AcadClr.Cli.Mcp
{
    /// <summary>
    /// MCP 工具目录：由命令表生成，工具名 = 命令名，参数 = 命令的 JSON 参数。
    /// 调用原样交给 <see cref="Dispatcher"/>；这里没有按工具分支的代码。
    /// </summary>
    internal static class McpTools
    {
        /// <summary>对外暴露的命令：跳过只属于命令行的；按调用方策略隐藏写操作与 lisp / script。</summary>
        public static IEnumerable<CommandDef> Exposed() => Commands.All.Where(c =>
            !c.CliOnly &&
            (Dispatcher.AllowLisp || (c.Name != "lisp" && c.Name != "script")) &&
            // 只读时隐藏写命令；batch 保留（纯查询的批处理照常可用，含写操作时由 Dispatcher 拒绝）
            (!Dispatcher.ReadOnly || !c.Writes || c.Name == "batch"));

        public static JArray List() => new JArray(Exposed().Select(Tool));

        private static readonly HashSet<string> Destructive = new HashSet<string> { "remove", "rollback", "undo", "lisp", "script", "batch" };

        public static JObject Tool(CommandDef c)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var a in c.Args)
            {
                props[a.Name] = ArgSchema(c, a);
                if (a.Required) required.Add(a.Name);
            }
            var schema = new JObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
            if (required.Count > 0) schema["required"] = required;

            return new JObject
            {
                ["name"] = c.Name,
                ["description"] = Describe(c),
                ["inputSchema"] = schema,
                ["annotations"] = new JObject
                {
                    ["readOnlyHint"] = !c.Writes,
                    ["destructiveHint"] = c.Writes && Destructive.Contains(c.Name),
                    ["idempotentHint"] = !c.Writes,
                    ["openWorldHint"] = false,
                },
            };
        }

        /// <summary>工具说明保持简短：一句用途 + 命令行写法；类型与属性按需用 help 查询，不常驻上下文。</summary>
        private static string Describe(CommandDef c)
        {
            var d = c.Summary + "。等价命令行：acadclr " + c.Synopsis + "。";
            if (c.Name == "add" || c.Name == "set")
                d += "props 的键由类型决定，先用 help 工具查（topic=类型名，如 line）。";
            else if (Schema.ActionVerbs.Contains(c.Name))
                d += $"各动作的 props 用 help 工具查（topic=\"{c.Name} <动作>\"）。";
            if (c.Batchable) d += "可放进 batch。";
            if (c.Modes == CommandModes.Live) d += "仅实时模式（需要运行中的 AutoCAD）。";
            else if ((c.Modes & CommandModes.Offline) != 0) d += "给 dwg 参数则离线读写该文件，不需要打开 AutoCAD。";
            return d;
        }

        private static JObject ArgSchema(CommandDef c, ArgDef a)
        {
            JObject s;
            switch (a.Kind)
            {
                case ArgKind.Integer: s = new JObject { ["type"] = "integer" }; break;
                case ArgKind.Boolean: s = new JObject { ["type"] = "boolean" }; break;
                case ArgKind.Object:
                    // 值可以是字符串、数字或布尔，插件统一按字符串解析
                    s = new JObject { ["type"] = "object", ["additionalProperties"] = new JObject { ["type"] = new JArray("string", "number", "boolean") } };
                    break;
                case ArgKind.Array:
                    s = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } };
                    break;
                case ArgKind.StringList:
                    s = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } };
                    break;
                default:
                    s = new JObject { ["type"] = "string" };
                    break;
            }
            var desc = a.Description;
            if (a.Option != null || a.Position >= 0)
                desc += a.Position >= 0 ? $"（命令行第 {a.Position + 1} 个位置参数）" : $"（命令行 {a.Option}）";
            s["description"] = desc;
            if (a.Default != null) s["default"] = a.Kind == ArgKind.Integer && int.TryParse(a.Default, out int n) ? (JToken)n : a.Default;
            if (a.Name == "action" && Schema.ActionVerbs.Contains(c.Name))
                s["enum"] = new JArray(Schema.ActionsOf(c.Name).Select(x => x.Name));
            return s;
        }

        /// <summary>
        /// 工具结果：structuredContent 是完整的 Response（去掉图片数据），content 放同一 JSON 的文本
        /// （规范建议给不读 structuredContent 的客户端），截图作为 image 内容块；Response.ok=false 时 isError。
        /// </summary>
        public static JObject Result(Response resp)
        {
            var images = resp.Images;
            resp.Images = null;
            var structured = JObject.FromObject(resp, Newtonsoft.Json.JsonSerializer.Create(Json.Settings));
            var content = new JArray();
            // help 的正文是给人读的文本，直接放文本；其余放 JSON
            var text = resp.Items == null && resp.Data?["text"] is JValue t && resp.Data.Count <= 2 ? (string)t! : Json.Serialize(resp);
            content.Add(new JObject { ["type"] = "text", ["text"] = text });
            foreach (var img in images ?? new List<ImageData>())
                content.Add(new JObject { ["type"] = "image", ["data"] = img.Data, ["mimeType"] = img.MimeType });
            return new JObject { ["content"] = content, ["structuredContent"] = structured, ["isError"] = !resp.Ok };
        }
    }
}
