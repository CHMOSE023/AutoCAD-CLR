# 检测 AutoCAD 的“未经处理的异常”错误窗口，读出完整堆栈（“详细信息”折叠时也能读到）。
# 由实时测试用点号引入：. (Join-Path $PSScriptRoot "acad-dialog.ps1")；Find-AcadErrorDialog <AutoCAD 进程号>
# AutoCAD 弹出这个模态窗口后，主线程停在窗口里，之后的请求会超时；测试应当在它出现时立即报出是哪一步触发的。
if (-not ("AcadDialog" -as [type])) {
    Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class AcadDialog {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    static string Text(IntPtr h) {
        int n = (int)SendMessage(h, 0x0E /* WM_GETTEXTLENGTH */, IntPtr.Zero, IntPtr.Zero);
        var sb = new StringBuilder(n + 1);
        SendMessage(h, 0x0D /* WM_GETTEXT */, (IntPtr)(n + 1), sb);
        return sb.ToString();
    }
    /// 返回错误窗口里的全部文本（含异常堆栈）；没有时返回 null
    public static string Find(uint pid) {
        string found = null;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h) || Text(h) != "AutoCAD") return true;
            var sb = new StringBuilder();
            EnumChildWindows(h, (c, l2) => { var s = Text(c); if (s.Length > 20) sb.AppendLine(s); return true; }, IntPtr.Zero);
            if (sb.ToString().Contains("Exception")) found = sb.ToString();
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}

function Find-AcadErrorDialog([int]$AcadPid) { [AcadDialog]::Find([uint32]$AcadPid) }

# 只取异常类型与 AutoCAD 侧的调用栈（去掉“已加载的程序集”列表）
function Format-AcadErrorDialog([string]$text) {
    ($text -split "`r?`n" | Where-Object { $_ -match "Exception|^\s+(在|at) " } | Select-Object -First 12) -join "`n"
}
