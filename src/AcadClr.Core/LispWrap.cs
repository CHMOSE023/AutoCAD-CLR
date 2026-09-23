using System.Text;
using Newtonsoft.Json.Linq;

namespace AcadClr.Core
{
    /// <summary>
    /// AutoLISP 执行的包装与结果解析。实时模式（插件）和离线模式（CLI 生成 accoreconsole 脚本）共用。
    /// 用户代码作为 lambda 体执行（可以有多个表达式，返回最后一个的值），vl-catch-all-apply 捕获错误，
    /// 结果写入文件：第一行 OK / ERR，其后为 vl-prin1-to-string 的值或错误信息。
    /// </summary>
    public static class LispWrap
    {
        /// <summary>
        /// 检查括号与字符串是否配对。不完整的 LISP 送进 AutoCAD 命令行后，读取器会停在 “(((_>” 提示处一直等输入，
        /// 之后送进去的所有内容都被当成这段代码的续行吞掉，AutoCAD 等于被卡死（accoreconsole 则一直挂到超时）。
        /// 规则：字符串内反斜杠转义下一个字符；字符串外 ; 到行尾为注释；多出的右括号同样视为错误。
        /// </summary>
        public static void CheckBalanced(string code)
        {
            int depth = 0, line = 1, stringLine = 0;
            bool inString = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '\n') line++;
                if (inString)
                {
                    if (c == '\\') { i++; if (i < code.Length && code[i] == '\n') line++; }
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; stringLine = line; break;
                    case ';': while (i + 1 < code.Length && code[i + 1] != '\n') i++; break;
                    case '(': depth++; break;
                    case ')':
                        if (--depth < 0)
                            throw new CliError("lisp_syntax", $"LISP 第 {line} 行多了一个右括号。", "检查括号是否配对");
                        break;
                }
            }
            if (inString)
                throw new CliError("lisp_syntax", $"LISP 第 {stringLine} 行开始的字符串没有结束（缺少右引号）。",
                    "字符串里的引号要写成 \\\"；另外注意 shell 是否把参数里的引号或反斜杠改掉了（PowerShell 尤其常见），可改用 --file");
            if (depth > 0)
                throw new CliError("lisp_syntax", $"LISP 缺少 {depth} 个右括号。", "检查括号是否配对；较长的代码建议写进文件用 --file");
            if (code.Trim().Length == 0)
                throw new CliError("lisp_syntax", "LISP 代码为空。");
        }

        /// <summary>
        /// 压成一行：去掉字符串外的 ; 注释，换行改为空格，字符串内的换行转成 \n 转义。
        ///
        /// 送进命令行（SendStringToExecute）的 LISP 必须是一整行：中间有换行时，命令行先进入 “(((_>” 续行状态，
        /// 括号一配平就立即求值，末尾用来提交的回车于是落在“命令:”提示下，成了空回车，重复上一条命令
        /// （实测重复了 NETLOAD / ZOOM），随后的输入被那条命令吞掉，AutoCAD 2014 与 2020 都因此崩溃。
        /// </summary>
        public static string Flatten(string code)
        {
            var sb = new StringBuilder(code.Length);
            bool inString = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < code.Length) { sb.Append(c).Append(code[++i]); continue; }
                    if (c == '"') inString = false;
                    if (c == '\r') continue;
                    sb.Append(c == '\n' ? "\\n" : c.ToString());
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); }
                else if (c == ';') { while (i + 1 < code.Length && code[i + 1] != '\n') i++; }
                else if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <param name="singleLine">送进命令行执行时必须为 true，见 <see cref="Flatten"/>。</param>
        public static string Wrap(string code, string outFile, bool singleLine = false)
        {
            CheckBalanced(code);
            var f = outFile.Replace("\\", "/");
            // 多行形式：代码后补换行，防止最后一行的 ; 注释吞掉收尾括号；单行形式注释已去掉，不能再有换行
            var body = singleLine ? Flatten(code) + " " : code + "\n";
            return "(progn (vl-load-com)" +
                   "(setq #acr (vl-catch-all-apply (function (lambda () " + body + "))))" +
                   "(setq #acf (open \"" + f + "\" \"w\"))" +
                   "(if #acf (progn" +
                   " (if (vl-catch-all-error-p #acr)" +
                   "  (progn (write-line \"ERR\" #acf) (write-line (vl-catch-all-error-message #acr) #acf))" +
                   "  (progn (write-line \"OK\" #acf) (write-line (vl-prin1-to-string #acr) #acf)))" +
                   " (close #acf)))" +
                   "(setq #acr nil #acf nil)" +
                   "(princ))";
        }

        /// <summary>
        /// LISP 写出的文本按 AutoCAD 版本解码：2021 起 AutoLISP 是 Unicode（UTF-8），之前是系统 ANSI 代码页。
        /// 不能“先试 UTF-8、失败再退回 ANSI”：GBK 编码的中文常常恰好也是合法的 UTF-8
        /// （“实时”的 GBK 字节 CA B5 CA B1 会被解成 “ʵʱ”），必须按版本确定。
        /// </summary>
        public static string Decode(byte[] bytes, bool utf8)
        {
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return utf8 ? Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip) : Encoding.Default.GetString(bytes);
        }

        /// <summary>AutoCAD 年份（2014、2020…）对应的 LISP 文本编码是否为 UTF-8。</summary>
        public static bool IsUtf8Year(int year) => year >= 2021;

        public static Response Parse(byte[] bytes, bool utf8)
        {
            var text = Decode(bytes, utf8);

            var parts = text.Replace("\r\n", "\n").Split(new[] { '\n' }, 2);
            var status = parts[0].Trim();
            var body = parts.Length > 1 ? parts[1].TrimEnd('\n') : "";
            if (status == "ERR") return Response.Fail("lisp_error", "LISP 错误：" + body);
            return new Response { Data = new JObject { ["value"] = body } };
        }
    }
}
