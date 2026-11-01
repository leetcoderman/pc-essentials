using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PCEssentials.Core;

namespace PCEssentials.Services.GameMode;

/// Raises Changed when the foreground window changes or a window is minimized/restored, through
/// SetWinEventHook (out of context, so nothing is injected into other processes). A slow timer also
/// fires it, because a game often goes fullscreen a moment after it takes focus.
internal sealed class ForegroundTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    private readonly WinEventProc _proc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IntPtr _foregroundHook, _minimizeHook;

    public event Action? Changed;

    public ForegroundTracker()
    {
        _proc = (_, _, _, idObject, _, _, _) => { if (idObject == 0) Changed?.Invoke(); };
        _timer.Tick += (_, _) => Changed?.Invoke();
    }

    public bool Running => _foregroundHook != IntPtr.Zero;

    public void Start()
    {
        if (Running) return;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == IntPtr.Zero) Log.Info("foreground tracker: SetWinEventHook failed");
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_minimizeHook != IntPtr.Zero) UnhookWinEvent(_minimizeHook);
        _foregroundHook = _minimizeHook = IntPtr.Zero;
    }

    public void Dispose() => Stop();
}

/// The real Win32 side of IForegroundProbe. Called on the UI thread, never from the keyboard hook.
internal sealed class Win32ForegroundProbe : IForegroundProbe
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int size, out int returned);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private uint _cachedPid;
    private string? _cachedExe;

    public string? ExeName(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        if (pid == _cachedPid) return _cachedExe;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        string? exe = null;
        if (process != IntPtr.Zero)
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            if (QueryFullProcessImageName(process, 0, sb, ref size)) exe = Path.GetFileName(sb.ToString());
            CloseHandle(process);
        }
        _cachedPid = pid;
        _cachedExe = exe;
        return exe;
    }

    public bool IsShellOrDesktop(IntPtr hwnd)
    {
        if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return true;
        var cls = new StringBuilder(64);
        Native.GetClassName(hwnd, cls, 64);
        return cls.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    public NotificationState QueryNotificationState() =>
        SHQueryUserNotificationState(out var state) == 0 ? (NotificationState)state : NotificationState.Unknown;

    public bool CoversMonitor(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd) || !Native.GetWindowRect(hwnd, out var r)) return false;
        var mi = Native.MonitorInfoFor(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// A non-elevated process can't open an elevated process's token, so "access denied" counts as elevated.
    public bool IsElevated(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return Marshal.GetLastWin32Error() == 5;
            try { return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
