using System;
using System.IO;
using System.Linq;
using AcadClr.Cli;
using AcadClr.Core;
using Newtonsoft.Json.Linq;
using Xunit;

// Dispatcher 的策略与来源是静态开关：测试串行执行，避免互相干扰
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AcadClr.Tests
{
    /// <summary>写操作判定（插件的只读模式、写前备份都据此判断）。</summary>
    public class WriteClassificationTests
    {
        private static BatchItem Item(string json) => JObject.Parse(json).ToObject<BatchItem>()!;

        [Theory]
        [InlineData("{\"command\":\"get\",\"path\":\"/\"}", false)]
        [InlineData("{\"command\":\"query\",\"selector\":\"line\"}", false)]
        [InlineData("{\"command\":\"stats\"}", false)]
        [InlineData("{\"command\":\"measure\",\"action\":\"area\",\"path\":\"8A\"}", false)]
        [InlineData("{\"command\":\"check\",\"action\":\"overlap\",\"selector\":\"line[layer=A]\"}", false)]
        [InlineData("{\"command\":\"add\",\"parent\":\"/model\",\"type\":\"line\"}", true)]
        [InlineData("{\"command\":\"set\",\"path\":\"/\",\"props\":{\"units\":\"m\"}}", true)]
        [InlineData("{\"command\":\"remove\",\"path\":\"8A\"}", true)]
        [InlineData("{\"command\":\"edit\",\"action\":\"offset\",\"path\":\"8A\"}", true)]
        [InlineData("{\"command\":\"set\",\"path\":\"/document[@name=a.dwg]\",\"props\":{\"current\":true}}", false)]
        [InlineData("{\"command\":\"set\",\"path\":\"/document[@name=a.dwg]\",\"props\":{\"current\":true,\"units\":\"m\"}}", true)]
        [InlineData("{\"command\":\"frobnicate\"}", true)]
        public void 条目按命令表判定(string json, bool writes) => Assert.Equal(writes, Commands.IsWrite(Item(json)));

        [Theory]
        [InlineData("status", false)]
        [InlineData("plot", false)]
        [InlineData("view", false)]
        [InlineData("lisp", true)]
        [InlineData("script", true)]
        [InlineData("cmdedit", true)]
        [InlineData("undo", true)]
        [InlineData("save", true)]
        public void 请求类型判定(string kind, bool writes) => Assert.Equal(writes, Commands.IsWrite(new Request { Kind = kind }));

        [Fact]
        public void 批处理只要有一条写就是写()
        {
            var req = new Request { Items = { Item("{\"command\":\"get\",\"path\":\"/\"}"), Item("{\"command\":\"remove\",\"path\":\"8A\"}") } };
            Assert.True(Commands.IsWrite(req));
            req.Items.RemoveAt(1);
            Assert.False(Commands.IsWrite(req));
        }
    }

    /// <summary>调用方策略（acadclr mcp 的 --read-only / --allow-lisp）与操作日志。</summary>
    public class PolicyTests : IDisposable
    {
        private readonly string _logDir = Path.Combine(Path.GetTempPath(), "acadclr-test-log-" + Guid.NewGuid().ToString("N"));

        public PolicyTests() => Environment.SetEnvironmentVariable("ACADCLR_LOG_DIR", _logDir);

        public void Dispose()
        {
            Dispatcher.ReadOnly = false;
            Dispatcher.AllowLisp = true;
            Dispatcher.Source = "cli";
            Environment.SetEnvironmentVariable("ACADCLR_LOG_DIR", null);
            try { Directory.Delete(_logDir, true); } catch (IOException) { }
        }

        private static Call Prepare(string cmd, string json) => Dispatcher.Prepare(cmd, JObject.Parse(json));

        private static CliError Denied(string cmd, string json) =>
            Assert.IsType<CliError>(Record.Exception(() => Prepare(cmd, json)));

        [Fact]
        public void 只读时拒绝写操作放行查询()
        {
            Dispatcher.ReadOnly = true;
            Assert.Equal("read_only", Denied("add", "{\"type\":\"line\"}").Code);
            Assert.Equal("read_only", Denied("edit", "{\"action\":\"offset\",\"path\":\"8A\",\"props\":{\"distance\":1}}").Code);
            Assert.Equal("read_only", Denied("batch", "{\"items\":[{\"command\":\"get\"},{\"command\":\"remove\",\"path\":\"8A\"}]}").Code);
            Assert.Equal("read_only", Denied("create", "{\"dwg\":\"x.dwg\"}").Code);
            Assert.Equal("read_only", Denied("undo", "{}").Code);
            Prepare("get", "{\"path\":\"/model\"}");
            Prepare("batch", "{\"items\":[{\"command\":\"get\"},{\"command\":\"query\",\"selector\":\"line\"}]}");
            Prepare("measure", "{\"action\":\"distance\",\"props\":{\"from\":\"0,0\",\"to\":\"1,1\"}}");
            Prepare("view", "{\"action\":\"capture\"}");
            Prepare("plot", "{}");
            Prepare("set", "{\"path\":\"/document[@name=a.dwg]\",\"props\":{\"current\":true}}");
        }

        [Fact]
        public void 不允许LISP时拒绝lisp和script但命令式编辑照常()
        {
            Dispatcher.AllowLisp = false;
            Assert.Equal("lisp_disabled", Denied("lisp", "{\"code\":\"(+ 1 2)\"}").Code);
            Assert.Equal("lisp_disabled", Denied("script", "{\"code\":\"_.REGEN\"}").Code);
            var r = Prepare("edit", "{\"action\":\"trim\",\"path\":\"8D@1,2\",\"props\":{\"edges\":\"8B\"}}").Request!;
            Assert.Equal("cmdedit", r.Kind);
            Assert.Null(r.Code); // 实时模式不发代码，由插件生成
        }

        [Fact]
        public void 来源写进请求()
        {
            Dispatcher.Source = "mcp-http";
            Assert.Equal("mcp-http", Prepare("stats", "{}").Request!.Source);
        }

        [Fact]
        public void 被拒绝的调用记日志()
        {
            Dispatcher.ReadOnly = true;
            Denied("remove", "{\"path\":\"8A\"}");
            var lines = OpLog.Tail(10).Data!["lines"]!.Select(l => (string)l!).ToList();
            var last = Assert.Single(lines);
            Assert.Contains("remove", last);
            Assert.Contains("read_only", last);
            Assert.Contains("ERR", last);
        }

        [Fact]
        public void log命令读取最近几行()
        {
            Directory.CreateDirectory(_logDir);
            File.WriteAllLines(OpLog.FileFor(DateTime.Now), Enumerable.Range(1, 30).Select(i => "line " + i));
            var lines = Dispatcher.Dispatch("log", JObject.Parse("{\"lines\":5}")).Data!["lines"]!.Select(l => (string)l!).ToList();
            Assert.Equal(new[] { "line 26", "line 27", "line 28", "line 29", "line 30" }, lines);
            Assert.Equal("usage", Dispatcher.Dispatch("log", JObject.Parse("{\"lines\":0}")).Error!.Code);
        }
    }
}
