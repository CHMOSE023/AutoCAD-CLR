using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadClr.Cli;
using AcadClr.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AcadClr.Tests
{
    /// <summary>命令表本身的一致性。</summary>
    public class CommandTableTests
    {
        [Fact]
        public void 命令名唯一()
        {
            var dup = Commands.All.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key);
            Assert.Empty(dup);
        }

        [Fact]
        public void 每个命令的参数名与位置唯一()
        {
            foreach (var c in Commands.All)
            {
                Assert.True(c.Args.Select(a => a.Name).Distinct().Count() == c.Args.Count, c.Name + " 有重名参数");
                var positions = c.Args.Where(a => a.Position >= 0).Select(a => a.Position).OrderBy(p => p).ToList();
                Assert.Equal(Enumerable.Range(0, positions.Count), positions);
                Assert.True(c.Args.Count(a => a.Rest) <= 1, c.Name + " 有多个 Rest 参数");
            }
        }

        [Fact]
        public void 同一选项在各命令中开关与取值一致()
        {
            // CliParser 的静态初始化会检查；这里触发一次
            CliParser.Parse(new[] { "status" });
            foreach (var g in Commands.All.SelectMany(c => c.Args).Where(a => a.Option != null).GroupBy(a => a.Option))
                Assert.Single(g.Select(a => a.Kind == ArgKind.Boolean).Distinct());
        }

        [Fact]
        public void 可进batch的命令插件都认识()
        {
            // 与 Executor.Execute 的 switch 保持一致
            var engineVerbs = new[] { "get", "query", "add", "set", "remove", "stats", "edit", "measure", "check" };
            foreach (var c in Commands.All.Where(c => c.Batchable)) Assert.Contains(c.Name, engineVerbs);
        }
    }

    /// <summary>命令行参数 → JSON 参数，以及两种写法构造出的 Request 相同。</summary>
    public class CliToJsonTests
    {
        public static IEnumerable<object[]> Cases => new List<object[]>
        {
            new object[] { new[] { "status" }, "{}" },
            new object[] { new[] { "get", "/model", "--depth", "2", "--limit", "5" }, "{\"path\":\"/model\",\"depth\":2,\"limit\":5}" },
            new object[] { new[] { "get", "plan.dwg", "/layers" }, "{\"dwg\":\"plan.dwg\",\"path\":\"/layers\"}" },
            new object[] { new[] { "query", "line[layer=WALL]" }, "{\"selector\":\"line[layer=WALL]\"}" },
            new object[] { new[] { "query", "--selector", "circle" }, "{\"selector\":\"circle\"}" },
            new object[] { new[] { "add", "/model", "--type", "circle", "--prop", "center=0,0", "--prop", "radius=500" },
                "{\"parent\":\"/model\",\"type\":\"circle\",\"props\":{\"center\":\"0,0\",\"radius\":\"500\"}}" },
            new object[] { new[] { "add", "--from", "$1", "--prop", "move=7000,0" }, "{\"from\":\"$1\",\"props\":{\"move\":\"7000,0\"}}" },
            new object[] { new[] { "set", "/entity[@handle=2A]", "--prop", "color=1" }, "{\"path\":\"/entity[@handle=2A]\",\"props\":{\"color\":\"1\"}}" },
            new object[] { new[] { "set", "circle[layer=WALL]", "--prop", "radius=600", "--force" }, "{\"selector\":\"circle[layer=WALL]\",\"props\":{\"radius\":\"600\"},\"force\":true}" },
            new object[] { new[] { "remove", "line[layer=TMP]" }, "{\"selector\":\"line[layer=TMP]\"}" },
            new object[] { new[] { "edit", "offset", "8A", "--prop", "distance=240" }, "{\"action\":\"offset\",\"path\":\"8A\",\"props\":{\"distance\":\"240\"}}" },
            new object[] { new[] { "edit", "mirror", "polyline[layer=WALL]", "--prop", "axis=0,0;0,1" }, "{\"action\":\"mirror\",\"selector\":\"polyline[layer=WALL]\",\"props\":{\"axis\":\"0,0;0,1\"}}" },
            new object[] { new[] { "edit", "a.dwg", "trim", "8D@13500,2000", "--prop", "edges=8B" },
                "{\"dwg\":\"a.dwg\",\"action\":\"trim\",\"selector\":\"8D@13500,2000\",\"props\":{\"edges\":\"8B\"}}" },
            new object[] { new[] { "measure", "area", "polyline[layer=ROOM]" }, "{\"action\":\"area\",\"selector\":\"polyline[layer=ROOM]\"}" },
            new object[] { new[] { "measure", "distance", "--prop", "from=0,0", "--prop", "to=3,4" }, "{\"action\":\"distance\",\"props\":{\"from\":\"0,0\",\"to\":\"3,4\"}}" },
            new object[] { new[] { "check", "a.dwg", "adjacent", "8A", "--prop", "with=8B" }, "{\"dwg\":\"a.dwg\",\"action\":\"adjacent\",\"path\":\"8A\",\"props\":{\"with\":\"8B\"}}" },
            new object[] { new[] { "view", "capture", "--prop", "zoom=extents" }, "{\"action\":\"capture\",\"props\":{\"zoom\":\"extents\"}}" },
            new object[] { new[] { "view", "zoom", "polyline[layer=ROOM]" }, "{\"action\":\"zoom\",\"selector\":\"polyline[layer=ROOM]\"}" },
            new object[] { new[] { "undo", "3" }, "{\"steps\":3}" },
            new object[] { new[] { "undo" }, "{}" },
            new object[] { new[] { "mark", "改前" }, "{\"label\":\"改前\"}" },
            new object[] { new[] { "rollback" }, "{}" },
            new object[] { new[] { "get", "/documents" }, "{\"path\":\"/documents\"}" },
            new object[] { new[] { "query", "line", "--doc", "b.dwg" }, "{\"selector\":\"line\",\"doc\":\"b.dwg\"}" },
            new object[] { new[] { "set", "/document[@name=b.dwg]", "--prop", "current=true" }, "{\"path\":\"/document[@name=b.dwg]\",\"props\":{\"current\":\"true\"}}" },
            new object[] { new[] { "batch", "--commands", "[{\"command\":\"stats\"}]", "--best-effort", "--force" },
                "{\"items\":[{\"command\":\"stats\"}],\"bestEffort\":true,\"force\":true}" },
            new object[] { new[] { "plot", "/layout[@name=A3]", "--prop", "output=D:/o.pdf" }, "{\"layout\":\"/layout[@name=A3]\",\"props\":{\"output\":\"D:/o.pdf\"}}" },
            new object[] { new[] { "plot", "a.dwg", "Model", "--prop", "area=extents" }, "{\"dwg\":\"a.dwg\",\"layout\":\"Model\",\"props\":{\"area\":\"extents\"}}" },
            new object[] { new[] { "lisp", "(+ 1 2)", "--cmd", "--timeout", "30" }, "{\"code\":\"(+ 1 2)\",\"commandQueue\":true,\"timeout\":30}" },
            new object[] { new[] { "lisp", "a.dwg", "(+ 1 2)", "--save" }, "{\"dwg\":\"a.dwg\",\"code\":\"(+ 1 2)\",\"save\":true}" },
            new object[] { new[] { "script", "--text", "_.ZOOM _E", "a.dwg", "b.dwg" }, "{\"code\":\"_.ZOOM _E\",\"dwg\":[\"a.dwg\",\"b.dwg\"]}" },
            new object[] { new[] { "script", "--text", "x", "--dwg", "a.dwg", "--save" }, "{\"code\":\"x\",\"dwg\":[\"a.dwg\"],\"save\":true}" },
            new object[] { new[] { "save", "--as", "D:/x.dwg", "--pid", "42" }, "{\"saveAs\":\"D:/x.dwg\",\"pid\":42}" },
            new object[] { new[] { "create", "new.dwg" }, "{\"dwg\":\"new.dwg\"}" },
            new object[] { new[] { "help", "edit", "trim" }, "{\"topic\":\"edit trim\"}" },
            new object[] { new[] { "stats", "--dwg", "a.dwg", "--json", "--acad", "2020" }, "{\"dwg\":\"a.dwg\"}" },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void 命令行解析成JSON参数(string[] argv, string expected)
        {
            var parsed = CliParser.Parse(argv);
            Assert.True(JToken.DeepEquals(JObject.Parse(expected), parsed.Args),
                $"得到 {parsed.Args.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void 两种写法构造出相同的Request(string[] argv, string json)
        {
            var name = CliParser.Parse(argv).Command.Name;
            var fromCli = Dispatcher.Prepare(name, CliParser.Parse(argv).Args);
            var fromJson = Dispatcher.Prepare(name, JObject.Parse(json));
            Assert.Equal(fromCli.Offline, fromJson.Offline);
            Assert.Equal(Serialize(fromCli.Request), Serialize(fromJson.Request));
        }

        private static string Serialize(Request? r) => r == null ? "" : Json.Serialize(r);
    }

    /// <summary>Dispatcher 构造的请求与校验。</summary>
    public class DispatcherTests
    {
        private static Call Prepare(string cmd, string json) => Dispatcher.Prepare(cmd, JObject.Parse(json));

        [Fact]
        public void 有dwg走离线否则走实时()
        {
            Assert.False(Prepare("get", "{\"path\":\"/model\"}").Offline);
            Assert.True(Prepare("get", "{\"path\":\"/model\",\"dwg\":\"a.dwg\"}").Offline);
        }

        [Fact]
        public void get缺省路径为根()
        {
            Assert.Equal("/", Prepare("get", "{}").Request!.Items[0].Path);
        }

        [Fact]
        public void doc参数交给插件且只用于实时模式()
        {
            Assert.Equal("b.dwg", Prepare("stats", "{\"doc\":\"b.dwg\"}").Request!.Doc);
            Assert.Null(Prepare("stats", "{}").Request!.Doc);
        }

        [Fact]
        public void 撤销类命令是undo请求()
        {
            var r = Prepare("undo", "{\"steps\":2}").Request!;
            Assert.Equal("undo", r.Kind);
            Assert.Equal("undo", r.Items[0].Command);
            Assert.Equal(2, (int)r.Items[0].Props!["steps"]!);
            Assert.Equal("mark", Prepare("mark", "{}").Request!.Items[0].Command);
        }

        [Fact]
        public void 超时实时按毫秒交给插件()
        {
            Assert.Equal(30_000, Prepare("stats", "{\"timeout\":30}").Request!.TimeoutMs);
            Assert.Null(Prepare("stats", "{}").Request!.TimeoutMs);
        }

        [Fact]
        public void 离线status读取文档节点()
        {
            var r = Prepare("status", "{\"dwg\":\"a.dwg\"}").Request!;
            Assert.Equal("run", r.Kind);
            Assert.Equal("/", r.Items.Single().Path);
            Assert.Equal("status", Prepare("status", "{}").Request!.Kind);
        }

        [Fact]
        public void batch的force作用到每一条且不覆盖显式值()
        {
            var r = Prepare("batch", "{\"items\":[{\"command\":\"remove\",\"selector\":\"line[layer=A]\"},{\"command\":\"remove\",\"path\":\"$0\",\"force\":false}],\"force\":true}").Request!;
            Assert.True(r.Items[0].Force);
            Assert.False(r.Items[1].Force);
        }

        [Fact]
        public void batch接受items包装对象与文件()
        {
            Assert.Single(Prepare("batch", "{\"items\":{\"items\":[{\"command\":\"stats\"}]}}").Request!.Items);
            var f = Path.GetTempFileName();
            try
            {
                File.WriteAllText(f, "[{\"command\":\"stats\"},{\"command\":\"get\",\"path\":\"/\"}]");
                Assert.Equal(2, Prepare("batch", new JObject { ["input"] = f }.ToString()).Request!.Items.Count);
            }
            finally { File.Delete(f); }
        }

        [Fact]
        public void 命令式edit生成LISP实时走命令队列()
        {
            var r = Prepare("edit", "{\"action\":\"fillet\",\"path\":\"8D\",\"props\":{\"with\":\"95\",\"radius\":300}}").Request!;
            Assert.Equal("lisp", r.Kind);
            Assert.True(r.CommandQueue);
            Assert.Contains("._fillet", r.Code);
            Assert.Contains("\"95\"", r.Code);
        }

        [Fact]
        public void 命令式edit离线由accoreconsole执行()
        {
            var c = Prepare("edit", "{\"action\":\"trim\",\"selector\":\"8D@1,2\",\"props\":{\"edges\":\"8B\"},\"dwg\":\"a.dwg\"}");
            Assert.True(c.Offline);
            Assert.Equal("a.dwg", c.Request!.Dwg);
            Assert.Contains("._trim", c.Request.Code);
        }

        [Fact]
        public void 普通edit是一条batch操作()
        {
            var item = Prepare("edit", "{\"action\":\"OFFSET\",\"path\":\"8A\",\"props\":{\"distance\":1}}").Request!.Items.Single();
            Assert.Equal("edit", item.Command);
            Assert.Equal("offset", item.Action);
        }

        [Fact]
        public void plot的布局写成路径()
        {
            Assert.Equal("/layout[@name=A3]", Prepare("plot", "{\"layout\":\"A3\"}").Request!.Items[0].Path);
            Assert.Equal("/layout[@name=A3]", Prepare("plot", "{\"layout\":\"/layout[@name=A3]\"}").Request!.Items[0].Path);
            Assert.Equal("/layout[@name=Model]", Prepare("plot", "{\"layout\":\"/model\"}").Request!.Items[0].Path);
            Assert.Null(Prepare("plot", "{}").Request!.Items[0].Path);
        }

        [Fact]
        public void lisp可从文件读代码()
        {
            var f = Path.GetTempFileName();
            try
            {
                File.WriteAllText(f, "(+ 1 2)");
                Assert.Equal("(+ 1 2)", Prepare("lisp", new JObject { ["file"] = f }.ToString()).Request!.Code);
            }
            finally { File.Delete(f); }
        }

        [Theory]
        [InlineData("get", "{\"pth\":\"/\"}", "是否想用 path")]
        [InlineData("query", "{}", "缺少参数 selector")]
        [InlineData("set", "{\"props\":{\"color\":1}}", "缺少目标")]
        [InlineData("set", "{\"path\":\"/\",\"selector\":\"line\"}", "只能给一个")]
        [InlineData("get", "{\"depth\":\"x\"}", "应为整数")]
        [InlineData("edit", "{\"action\":\"trm\",\"path\":\"8A\"}", "是否想用 trim")]
        [InlineData("edit", "{\"action\":\"trim\",\"path\":\"8A\"}", "缺少属性：edges")]
        [InlineData("plot", "{\"props\":{\"ouput\":\"x\"}}", "是否想用 output")]
        [InlineData("lisp", "{\"code\":\"(+ 1 2)\",\"save\":true}", "只用于离线模式")]
        [InlineData("lisp", "{\"code\":\"(+ 1 2)\",\"commandQueue\":true,\"dwg\":\"a.dwg\"}", "只用于实时模式")]
        [InlineData("measure", "{\"action\":\"distance\",\"path\":\"8A\",\"props\":{\"from\":\"0,0\",\"to\":\"1,1\"}}", "不需要目标")]
        [InlineData("measure", "{\"action\":\"distance\",\"props\":{\"from\":\"0,0\"}}", "缺少属性：to")]
        [InlineData("measure", "{\"action\":\"aera\",\"path\":\"8A\"}", "是否想用 area")]
        [InlineData("check", "{\"action\":\"overlap\"}", "缺少目标")]
        [InlineData("check", "{\"action\":\"inside\",\"path\":\"8A\"}", "缺少属性：boundary")]
        [InlineData("get", "{\"dwg\":\"a.dwg\",\"doc\":\"b.dwg\"}", "不能同时给")]
        [InlineData("edit", "{\"action\":\"trim\",\"path\":\"8A\",\"props\":{\"edges\":\"8B\"},\"doc\":\"b.dwg\"}", "只能作用于当前文档")]
        [InlineData("undo", "{\"steps\":500}", "1-200")]
        [InlineData("view", "{\"action\":\"capture\",\"props\":{\"output\":\"view.png\"}}", "绝对路径")]
        [InlineData("view", "{\"action\":\"capture\",\"dwg\":\"a.dwg\"}", "没有参数")]
        [InlineData("lisp", "{\"code\":\"(+ 1 2)\",\"doc\":\"b.dwg\"}", "没有参数")]
        [InlineData("batch", "{}", "需要 items")]
        [InlineData("config", "{}", "只能在命令行使用")]
        [InlineData("sttus", "{}", "是否想用 status")]
        public void 参数错误给出可操作的提示(string cmd, string json, string expected)
        {
            var ex = Record.Exception(() => Prepare(cmd, json));
            var err = Assert.IsType<CliError>(ex);
            Assert.Contains(expected, err.Message + err.Suggestion);
        }

        [Fact]
        public void Dispatch把参数错误作为Response返回()
        {
            var resp = Dispatcher.Dispatch("get", JObject.Parse("{\"pth\":\"/\"}"));
            Assert.False(resp.Ok);
            Assert.Equal("usage", resp.Error!.Code);
        }

        [Fact]
        public void help返回文本与类型结构()
        {
            var data = Dispatcher.Dispatch("help", JObject.Parse("{\"topic\":\"line\"}")).Data!;
            Assert.Contains("直线", (string)data["text"]!);
            Assert.Equal("line", (string)data["schema"]!["type"]!);
            Assert.Contains("--depth", (string)Dispatcher.Dispatch("help", JObject.Parse("{\"topic\":\"get\"}")).Data!["text"]!);
        }
    }

    /// <summary>选择器的窗口条件（对应 AutoCADMCP 的 select window / crossing）。</summary>
    public class SelectorWindowTests
    {
        private static bool Match(string selector, string bbox) =>
            Selector.Parse(selector).Matches("line", a => a == "bbox" ? bbox : a == "layer" ? "WALL" : null);

        [Theory]
        [InlineData("line[inside=0,0;100,100]", "10,10;90,90", true)]
        [InlineData("line[inside=100,100;0,0]", "10,10;90,90", true)]   // 角点顺序无关
        [InlineData("line[inside=0,0;100,100]", "10,10;110,90", false)]
        [InlineData("line[crossing=0,0;100,100]", "10,10;110,90", true)]
        [InlineData("line[crossing=0,0;100,100]", "200,200;300,300", false)]
        [InlineData("line[crossing=0,0;100,100]", "100,0;200,50", true)]   // 贴边算相交
        [InlineData("line[inside=0,0;100,100][layer=WALL]", "10,10;90,90", true)]
        [InlineData("line[inside=0,0;100,100][layer=AXIS]", "10,10;90,90", false)]
        public void 按包围盒判断窗口(string selector, string bbox, bool expected) => Assert.Equal(expected, Match(selector, bbox));

        [Fact]
        public void 没有包围盒的元素不匹配() =>
            Assert.False(Selector.Parse("entity[crossing=0,0;1,1]").Matches("text", a => null));

        [Theory]
        [InlineData("line[inside>0,0;1,1]", "只支持 =")]
        [InlineData("line[inside=0,0]", "两个角点")]
        public void 窗口写错时报错(string selector, string expected)
        {
            var err = Assert.IsType<CliError>(Record.Exception(() => Match(selector, "0,0;1,1")));
            Assert.Contains(expected, err.Message);
        }
    }

    /// <summary>命令行特有的解析规则。</summary>
    public class CliParserTests
    {
        [Theory]
        [InlineData(new[] { "add", "--best-effort" }, "add 不支持 --best-effort")]
        [InlineData(new[] { "save", "--dwg", "a.dwg" }, "无需 save")]
        [InlineData(new[] { "create", "a.dwg", "--pid", "1" }, "只用于离线模式")]
        [InlineData(new[] { "get", "/model", "extra" }, "多余的参数")]
        [InlineData(new[] { "view", "capture", "--dwg", "a.dwg" }, "只用于实时模式")]
        [InlineData(new[] { "get", "--dept", "1" }, "是否想用 --depth")]
        [InlineData(new[] { "get", "--depth", "x" }, "需要整数")]
        [InlineData(new[] { "add", "--prop", "novalue" }, "key=value")]
        [InlineData(new[] { "batch", "--commands", "[{op:add}]" }, "双引号丢失")]
        public void 命令行错误(string[] argv, string expected)
        {
            var err = Assert.IsType<CliError>(Record.Exception(() => CliParser.Parse(argv)));
            Assert.Contains(expected, err.Message + err.Suggestion);
        }

        [Fact]
        public void 没有参数或带help时显示帮助()
        {
            Assert.Equal("help", CliParser.Parse(new string[0]).Command.Name);
            var p = CliParser.Parse(new[] { "line", "--help" });
            Assert.Equal("help", p.Command.Name);
            Assert.Equal("line", (string)p.Args["topic"]!);
        }

        [Fact]
        public void 全局选项()
        {
            var p = CliParser.Parse(new[] { "--json", "stats", "--acad", "2014" });
            Assert.True(p.Json);
            Assert.Equal("2014", p.Acad);
        }

        [Fact]
        public void script位置参数里的dwg都是图纸其余是脚本文件()
        {
            var args = CliParser.Parse(new[] { "script", "a.dwg", "fix.scr", "b.dwg", "--dwg", "c.dwg" }).Args;
            Assert.Equal("fix.scr", (string)args["file"]!);
            Assert.Equal(new[] { "c.dwg", "a.dwg", "b.dwg" }, args["dwg"]!.Select(x => (string)x!).ToArray());
        }

        [Fact]
        public void create的第一个参数就是dwg不当作便捷写法()
        {
            Assert.Equal("new.dwg", (string)CliParser.Parse(new[] { "create", "new.dwg" }).Args["dwg"]!);
        }
    }
}
