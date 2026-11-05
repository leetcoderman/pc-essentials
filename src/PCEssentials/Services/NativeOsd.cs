using System.Runtime.InteropServices;
using System.Windows.Threading;
using PCEssentials.Core;

namespace PCEssentials.Services;

/// Hides Windows 11's own volume and media pop-up, so the island's notice is the only one. The pop-up is a
/// XAML island that Explorer hosts in a "XamlExplorerHostIslandWindow". Made layered (with no content
/// set, so nothing draws), click-through and minimized, it stays out of sight: Explorer still moves it on
/// each key press but leaves it minimized. Laptops show brightness in the same pop-up, so that goes too.
///
/// The window is undocumented and Explorer creates it the first time the pop-up shows, or again after
/// Explorer restarts, so it is looked up again whenever the handle stops being valid. In those cases the
/// pop-up can show once for a moment before it is hidden.
///
/// Derived from FluentFlyout (https://github.com/unchihugo/FluentFlyout, GPL-3.0-or-later), which in turn
/// derives from gpkgpk/HideVolumeOSD (https://github.com/gpkgpk/HideVolumeOSD, GPL-3.0).
public sealed class NativeOsd
{
    private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    private readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Func<bool> _wanted;
    private IntPtr _host;
    private int _originalExStyle;
    private bool _hidden;

    /// wanted says whether the island can stand in for the pop-up right now; it is asked on every Update.
    public NativeOsd(Func<bool> wanted)
    {
        _wanted = wanted;
        // Explorer may restart and make a new pop-up window, and the settings or the taskbar can change;
        // catch those without waiting for a volume change.
        _check.Tick += (_, _) => Update();
        _check.Start();
    }

    /// Hides the pop-up when wanted, or gives it back when not. Cheap when nothing changed: call it on every
    /// volume change, since Explorer makes the window the first time the pop-up shows.
    public void Update()
    {
        if (!_wanted())
        {
            if (_hidden) Restore();
            return;
        }
        try
        {
            if (_host == IntPtr.Zero || !IsWindow(_host))
            {
                _host = Find();
                if (_host == IntPtr.Zero) return;
                // Without the two hiding styles: a copy of this app that ended without restoring may have left them.
                _originalExStyle = GetWindowLong(_host, GWL_EXSTYLE) & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
                Log.Info("windows volume pop-up found");
            }
            _hidden = true;
            var style = GetWindowLong(_host, GWL_EXSTYLE);
            if (IsIconic(_host) && (style & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) == (WS_EX_LAYERED | WS_EX_TRANSPARENT)) return;
            SetWindowLong(_host, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TRANSPARENT);
            SetWindowPos(_host, IntPtr.Zero, -99999, -99999, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(_host, SW_SHOWMINNOACTIVE);
        }
        catch (Exception ex) { Log.Error("hide volume pop-up", ex); }
    }

    /// Gives the pop-up back to Windows: its own window style, then un-minimized and hidden, which is how it
    /// rests between key presses.
    public void Restore()
    {
        if (!_hidden) return;
        _hidden = false;
        try
        {
            if (_host == IntPtr.Zero || !IsWindow(_host)) return;
            SetWindowLong(_host, GWL_EXSTYLE, _originalExStyle);
            ShowWindow(_host, SW_SHOWNOACTIVATE);
            ShowWindow(_host, SW_HIDE);
        }
        catch (Exception ex) { Log.Error("restore volume pop-up", ex); }
    }

    /// The Explorer island host whose XAML input site has a place on screen. Other island hosts (one covers the
    /// whole screen) have an input site at 0,0 with no size.
    private static IntPtr Find()
    {
        var host = IntPtr.Zero;
        while ((host = FindWindowEx(IntPtr.Zero, host, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
        {
            var source = FindWindowEx(host, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource");
            if (source == IntPtr.Zero) continue;
            var input = FindWindowEx(source, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
            if (input == IntPtr.Zero || !GetWindowRect(input, out var r)) continue;
            if (r.Left == 0 && r.Top == 0 && r.Right == 0 && r.Bottom == 0) continue;
            return host;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
