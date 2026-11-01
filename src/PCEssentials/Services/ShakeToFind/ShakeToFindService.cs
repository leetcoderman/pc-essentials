using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PCEssentials.Core;

namespace PCEssentials.Services.ShakeToFind;

/// macOS's "shake mouse pointer to locate". A low-level mouse hook on its own thread feeds the user's
/// pointer movement to ShakeDetector; when it spots a shake, an effect thread hides the real cursor and
/// draws a growing arrow at the pointer until shaking stops and it has eased back to normal size.
public sealed class ShakeToFindService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
        WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_QUIT = 0x12;
    private const uint LLMHF_INJECTED = 0x1, LLMHF_LOWER_IL_INJECTED = 0x2;
    private const int CURSOR_SHOWING = 0x1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private readonly AppSettings _settings;
    private readonly HookProc _proc;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;

    // Owned by the hook thread.
    private readonly ShakeDetector _detector = new(ShakeProfile.For("normal"));
    private int _buttons;

    // Shared between threads.
    private volatile ShakeProfile _profile = ShakeProfile.For("normal");
    private volatile bool _enabled;
    private double _maxScale = 4;
    private long _activeUntil; // BitConverter-packed double, ms on the Now() clock
    private int _effectRunning;
    private volatile bool _stopEffect;
    private Thread? _effectThread;

    public ShakeToFindService(AppSettings settings)
    {
        _settings = settings;
        _proc = Callback;
        Volatile.Write(ref _activeUntil, BitConverter.DoubleToInt64Bits(double.NegativeInfinity));
        SystemCursors.RestoreIfLeftHidden();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SystemCursors.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SystemCursors.Restore();
        _settings.Changed += Apply;
        Apply();
    }

    private static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// Picks up the Settings values, and starts or stops the hook.
    private void Apply()
    {
        var m = _settings.Mouse;
        _profile = ShakeProfile.For(m.ShakeSensitivity);
        Volatile.Write(ref _maxScale, Math.Clamp(m.ShakeMaxScale, 1.5, 8));
        if (m.ShakeToFind && _hookThread is null) Start();
        else if (!m.ShakeToFind && _hookThread is not null) Stop();
    }

    private void Start()
    {
        _enabled = true;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "Shake to find hook" };
        _hookThread.Start();
        ready.Wait(2000);
    }

    private void Stop()
    {
        _enabled = false;
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread?.Join(1000);
        _hookThread = null;
        _hookThreadId = 0;
        StopEffect();
    }

    private void HookLoop(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        _detector.PixelScale = GetDpiForSystem() / 96.0;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        Log.Info(_hook == IntPtr.Zero ? $"shake to find: mouse hook failed: {Marshal.GetLastWin32Error()}" : "shake to find: mouse hook installed");
        ready.Set();
        if (_hook == IntPtr.Zero) return;
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Info("shake to find: mouse hook removed");
    }

    /// Runs for every mouse event in the system, so it only does a little arithmetic and returns.
    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try { OnMouse(wParam.ToInt32(), Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)); }
            catch { /* never let an exception stall the input queue */ }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void OnMouse(int msg, MSLLHOOKSTRUCT info)
    {
        if ((info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0) return;
        if (!ReferenceEquals(_detector.Profile, _profile)) _detector.Profile = _profile;
        switch (msg)
        {
            case WM_LBUTTONDOWN: _buttons |= 1; break;
            case WM_RBUTTONDOWN: _buttons |= 2; break;
            case WM_MBUTTONDOWN: _buttons |= 4; break;
            case WM_XBUTTONDOWN: _buttons |= 8; break;
            case WM_LBUTTONUP: _buttons &= ~1; break;
            case WM_RBUTTONUP: _buttons &= ~2; break;
            case WM_MBUTTONUP: _buttons &= ~4; break;
            case WM_XBUTTONUP: _buttons &= ~8; break;
            case WM_MOUSEMOVE:
                if (_buttons != 0)
                {
                    // A release we never saw (it happened on the secure desktop, say) must not block shaking for good.
                    if (!AnyButtonHeld()) _buttons = 0;
                    else return;
                }
                var now = Now();
                _detector.Feed(now, info.pt.X, info.pt.Y);
                if (!_detector.IsActive(now)) return;
                Volatile.Write(ref _activeUntil, BitConverter.DoubleToInt64Bits(_detector.ActiveUntil));
                if (Volatile.Read(ref _effectRunning) == 0 && CursorVisible()) StartEffect();
                return;
        }
        // Dragging, selecting and clicking are never a shake.
        if (msg != WM_MOUSEMOVE) _detector.Reset();
    }

    private static bool AnyButtonHeld() =>
        new[] { 0x01, 0x02, 0x04, 0x05, 0x06 }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// Games and video players that hide the pointer don't get a giant arrow.
    private static bool CursorVisible()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && (info.flags & CURSOR_SHOWING) != 0;
    }

    private void StartEffect()
    {
        if (Interlocked.CompareExchange(ref _effectRunning, 1, 0) != 0) return;
        _stopEffect = false;
        _effectThread = new Thread(RunEffect) { IsBackground = true, Name = "Shake to find pointer" };
        _effectThread.SetApartmentState(ApartmentState.STA);
        _effectThread.Start();
    }

    private void StopEffect()
    {
        _stopEffect = true;
        _effectThread?.Join(1000);
        _effectThread = null;
        SystemCursors.Restore();
    }

    private void RunEffect()
    {
        PointerOverlay? overlay = null;
        try
        {
            var growth = new ShakeGrowth();
            var unit = CursorBaseSize() / 32.0;
            overlay = new PointerOverlay();
            SystemCursors.Hide();
            var last = Now();
            while (!_stopEffect && _enabled)
            {
                // Paced by the compositor, so the arrow moves once per refresh.
                if (DwmFlush() != 0) Thread.Sleep(8);
                var now = Now();
                var active = now < BitConverter.Int64BitsToDouble(Volatile.Read(ref _activeUntil));
                var scale = growth.Step(now - last, active, Volatile.Read(ref _maxScale));
                last = now;
                if (growth.AtRest && !active) break;
                GetCursorPos(out var pt);
                var monitor = MonitorFromPoint(pt, 2); // MONITOR_DEFAULTTONEAREST
                var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
                overlay.Show(pt.X, pt.Y, unit * dpi / 96.0, scale);
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) // PM_REMOVE
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
        }
        catch (Exception ex) { Log.Error("shake to find", ex); }
        finally
        {
            SystemCursors.Restore();
            try { overlay?.Dispose(); } catch { }
            Volatile.Write(ref _effectRunning, 0);
        }
    }

    /// The user's pointer size (Settings > Accessibility > Mouse pointer), read only: 32 is the normal size.
    private static int CursorBaseSize()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Cursors", "CursorBaseSize", 32) is int size && size >= 32) return size;
        }
        catch { }
        return 32;
    }

    public void Dispose()
    {
        _settings.Changed -= Apply;
        if (_hookThread is not null) Stop();
        else StopEffect();
        SystemCursors.Restore();
    }
}
