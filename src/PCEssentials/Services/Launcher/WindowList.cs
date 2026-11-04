using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PCEssentials.Core;

namespace PCEssentials.Services.Launcher;

public sealed record OpenWindow(IntPtr Handle, string Title, string ProcessName, string? ExePath);

/// The windows Alt+Tab would show: visible, titled, unowned, not tool windows and not cloaked (on another
/// virtual desktop or a suspended Store app).
public static class WindowList
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    public static List<OpenWindow> Enumerate(IntPtr exclude)
    {
        var list = new List<OpenWindow>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (h == exclude || !Native.IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            var exe = ExePath(pid);
            var name = exe is null ? "" : System.IO.Path.GetFileNameWithoutExtension(exe);
            if (name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)) return true;
            list.Add(new OpenWindow(h, sb.ToString(), name, exe));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string? ExePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// Brings a window forward, restoring it when minimized.
    public static void Activate(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        ForceForeground(hwnd);
    }

    /// SetForegroundWindow is refused unless this thread owns the foreground; sharing input with the current
    /// foreground thread for a moment gets around that, as PowerToys Run and other launchers do.
    public static void ForceForeground(IntPtr hwnd)
    {
        var fg = Native.GetForegroundWindow();
        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
    }
}
