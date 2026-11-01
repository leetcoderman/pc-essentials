using System.Runtime.InteropServices;
using PCEssentials.Core;
using static PCEssentials.Core.Native;

namespace PCEssentials.Services;

/// Rectangle-style window snapping for the foreground window. Pressing the same shortcut again
/// while the window still sits where the last press put it cycles to the next size.
public sealed class WindowSnapper
{
    private readonly AppSettings _settings;
    private readonly Dictionary<IntPtr, RECT> _restoreRects = new();
    private (IntPtr Hwnd, string Action, int Index, RECT Rect)? _last;

    public static readonly string[] Actions =
    [
        "left", "right", "centerThird", "top", "bottom", "topLeft", "topRight", "bottomLeft", "bottomRight",
        "maximize", "almostMaximize", "maximizeHeight", "smaller", "larger", "center", "restore",
        "nextDisplay", "previousDisplay", "firstThird", "lastThird", "firstTwoThirds", "lastTwoThirds",
    ];

    public static string DisplayName(string action) => action switch
    {
        "left" => "Left", "right" => "Right", "centerThird" => "Centre section", "top" => "Top", "bottom" => "Bottom",
        "topLeft" => "Top left", "topRight" => "Top right", "bottomLeft" => "Bottom left", "bottomRight" => "Bottom right",
        "maximize" => "Maximise", "almostMaximize" => "Almost maximise", "maximizeHeight" => "Maximise height",
        "smaller" => "Make smaller", "larger" => "Make larger", "center" => "Centre", "restore" => "Restore",
        "nextDisplay" => "Next display", "previousDisplay" => "Previous display", "firstThird" => "First third",
        "lastThird" => "Last third", "firstTwoThirds" => "First two thirds", "lastTwoThirds" => "Last two thirds",
        _ => action,
    };

    public WindowSnapper(AppSettings settings) => _settings = settings;

    public void Run(string action)
    {
        var hwnd = GetForegroundWindow();
        if (!IsSnappable(hwnd)) return;

        var visual = VisualRect(hwnd);
        var work = MonitorInfoFor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)).rcWork;

        if (action == "restore")
        {
            if (_restoreRects.Remove(hwnd, out var original)) Apply(hwnd, original);
            _last = null;
            return;
        }
        if (action is "nextDisplay" or "previousDisplay")
        {
            MoveToDisplay(hwnd, visual, work, action == "nextDisplay" ? 1 : -1);
            return;
        }

        var candidates = Candidates(action, work, visual);
        if (candidates.Count == 0) return;

        var index = 0;
        if (_last is { } last && last.Hwnd == hwnd && last.Action == action && candidates.Count > 1 && Near(visual, last.Rect))
            index = (last.Index + 1) % candidates.Count;

        if (!_restoreRects.ContainsKey(hwnd)) _restoreRects[hwnd] = visual;
        var target = candidates[index];
        Apply(hwnd, target);
        _last = (hwnd, action, index, VisualRect(hwnd));
    }

    private List<RECT> Candidates(string action, RECT w, RECT current)
    {
        var gap = _settings.Snap.Gap;
        var fractions = _settings.Snap.CycleFractions.Where(f => f > 0 && f <= 1).DefaultIfEmpty(0.5).ToList();
        int W = w.Width, H = w.Height;
        RECT R(double x, double y, double width, double height) => Inset(new RECT(
            w.Left + (int)Math.Round(x), w.Top + (int)Math.Round(y),
            w.Left + (int)Math.Round(x + width), w.Top + (int)Math.Round(y + height)), gap);

        switch (action)
        {
            case "left": return fractions.Select(f => R(0, 0, W * f, H)).ToList();
            case "right": return fractions.Select(f => R(W * (1 - f), 0, W * f, H)).ToList();
            case "top": return fractions.Select(f => R(0, 0, W, H * f)).ToList();
            case "bottom": return fractions.Select(f => R(0, H * (1 - f), W, H * f)).ToList();
            case "topLeft": return fractions.Select(f => R(0, 0, W * f, H / 2.0)).ToList();
            case "topRight": return fractions.Select(f => R(W * (1 - f), 0, W * f, H / 2.0)).ToList();
            case "bottomLeft": return fractions.Select(f => R(0, H / 2.0, W * f, H / 2.0)).ToList();
            case "bottomRight": return fractions.Select(f => R(W * (1 - f), H / 2.0, W * f, H / 2.0)).ToList();
            case "centerThird": return [R(W / 3.0, 0, W / 3.0, H)];
            case "firstThird": return [R(0, 0, W / 3.0, H)];
            case "lastThird": return [R(W * 2 / 3.0, 0, W / 3.0, H)];
            case "firstTwoThirds": return [R(0, 0, W * 2 / 3.0, H)];
            case "lastTwoThirds": return [R(W / 3.0, 0, W * 2 / 3.0, H)];
            case "maximize": return [R(0, 0, W, H)];
            case "almostMaximize": return [R(W * 0.05, H * 0.05, W * 0.9, H * 0.9)];
            case "maximizeHeight": return [new RECT(current.Left, w.Top + gap, current.Right, w.Bottom - gap)];
            case "center":
            {
                int cw = Math.Min(current.Width, W), ch = Math.Min(current.Height, H);
                int x = w.Left + (W - cw) / 2, y = w.Top + (H - ch) / 2;
                return [new RECT(x, y, x + cw, y + ch)];
            }
            case "smaller" or "larger":
            {
                var step = action == "larger" ? 30 : -30;
                var r = new RECT(current.Left - step, current.Top - step, current.Right + step, current.Bottom + step);
                if (r.Width < 300 || r.Height < 200) return [];
                return [Clamp(r, w)];
            }
        }
        return [];
    }

    private void MoveToDisplay(IntPtr hwnd, RECT visual, RECT work, int direction)
    {
        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        if (screens.Count < 2) return;
        var current = screens.FindIndex(s => s.WorkingArea.Left == work.Left && s.WorkingArea.Top == work.Top);
        if (current < 0) current = 0;
        var next = screens[(current + direction + screens.Count) % screens.Count].WorkingArea;
        // Keep the window's position and size relative to the work area.
        double fx = (visual.Left - work.Left) / (double)work.Width, fy = (visual.Top - work.Top) / (double)work.Height;
        double fw = visual.Width / (double)work.Width, fh = visual.Height / (double)work.Height;
        var target = new RECT(
            next.Left + (int)(fx * next.Width), next.Top + (int)(fy * next.Height),
            next.Left + (int)((fx + fw) * next.Width), next.Top + (int)((fy + fh) * next.Height));
        if (!_restoreRects.ContainsKey(hwnd)) _restoreRects[hwnd] = visual;
        Apply(hwnd, target);
        _last = null;
    }

    /// Moves the window so its visible frame (not counting the invisible resize border) fills target.
    private static void Apply(IntPtr hwnd, RECT target)
    {
        if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        GetWindowRect(hwnd, out var outer);
        var visual = VisualRect(hwnd);
        int dl = visual.Left - outer.Left, dt = visual.Top - outer.Top;
        int dr = outer.Right - visual.Right, db = outer.Bottom - visual.Bottom;
        SetWindowPos(hwnd, IntPtr.Zero,
            target.Left - dl, target.Top - dt,
            target.Width + dl + dr, target.Height + dt + db,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static RECT VisualRect(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    private static bool Near(RECT a, RECT b) =>
        Math.Abs(a.Left - b.Left) <= 4 && Math.Abs(a.Top - b.Top) <= 4 &&
        Math.Abs(a.Right - b.Right) <= 4 && Math.Abs(a.Bottom - b.Bottom) <= 4;

    private static RECT Inset(RECT r, int gap) =>
        gap <= 0 ? r : new RECT(r.Left + gap, r.Top + gap, r.Right - gap, r.Bottom - gap);

    private static RECT Clamp(RECT r, RECT w)
    {
        int width = Math.Min(r.Width, w.Width), height = Math.Min(r.Height, w.Height);
        int left = Math.Clamp(r.Left, w.Left, w.Right - width), top = Math.Clamp(r.Top, w.Top, w.Bottom - height);
        return new RECT(left, top, left + width, top + height);
    }

    private static bool IsSnappable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, 64);
        if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
            or "Windows.UI.Core.CoreWindow") return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != Environment.ProcessId;
    }
}
