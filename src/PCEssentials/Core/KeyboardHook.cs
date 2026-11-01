using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace PCEssentials.Core;

/// Global shortcuts through a low-level keyboard hook. Unlike RegisterHotKey this can take over
/// combinations Windows reserves for itself (Win+A, Win+D, Win+E, ...), which is what lets the
/// Rectangle-style Win bindings work. Matched keystrokes are swallowed so Windows never sees them.
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    /// An unassigned virtual key. Tapping it while Win is held stops the Start menu opening on release.
    private const ushort MaskKey = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private readonly HookProc _proc;
    private IntPtr _hook;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<(ModifierKeys, int), Action> _bindings = new();
    private readonly HashSet<int> _swallowedDown = new();
    private bool _lwin, _rwin, _lctrl, _rctrl, _lalt, _ralt, _lshift, _rshift;
    private bool _updateQueued;
    private Action<Hotkey>? _recorder;
    private Func<bool>? _blockWin;

    public KeyboardHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = Callback;
    }

    public void Bind(Hotkey hotkey, Action action)
    {
        _bindings[(hotkey.Modifiers, KeyInterop.VirtualKeyFromKey(hotkey.Key))] = action;
        QueueUpdate();
    }

    public void Clear()
    {
        _bindings.Clear();
        QueueUpdate();
    }

    /// While set, the next non-modifier key press (with whatever modifiers are held) is reported here
    /// and swallowed instead of running a binding. Used by the shortcut recorder in Settings, so it can
    /// capture combinations Windows would otherwise act on (Win+E, Win+D...).
    public Action<Hotkey>? Recorder { get => _recorder; set { _recorder = value; QueueUpdate(); } }

    /// Asked on every Windows key press; true swallows it, so the Start menu and Win+ combos (ours
    /// included) don't fire. Runs inside the hook, so it must only read a cached flag.
    public Func<bool>? BlockWin { get => _blockWin; set { _blockWin = value; QueueUpdate(); } }

    public bool Installed => _hook != IntPtr.Zero;

    /// The hook is only installed while something uses it. Changes apply once the current work is done,
    /// so Clear() followed by Bind() doesn't remove and reinstall it.
    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            _updateQueued = false;
            var needed = _bindings.Count > 0 || _recorder is not null || _blockWin is not null;
            if (needed && !Installed) Install();
            else if (!needed && Installed) Uninstall();
        });
    }

    private void Install()
    {
        _swallowedDown.Clear();
        static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        (_lwin, _rwin, _lctrl, _rctrl) = (Held(0x5B), Held(0x5C), Held(0xA2), Held(0xA3));
        (_lalt, _ralt, _lshift, _rshift) = (Held(0xA4), Held(0xA5), Held(0xA0), Held(0xA1));
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        Log.Info(_hook == IntPtr.Zero ? $"keyboard hook failed: {Marshal.GetLastWin32Error()}" : "keyboard hook installed");
    }

    private void Uninstall()
    {
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Info("keyboard hook removed");
    }

    /// Installs the hook again (after resume Windows may have dropped it without telling us).
    public void Rearm()
    {
        if (!Installed) return;
        Uninstall();
        Install();
    }

    private static bool IsModifier(int vk) => vk is 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x10 or 0x11 or 0x12;

    private ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (_lwin || _rwin) m |= ModifierKeys.Windows;
        if (_lctrl || _rctrl) m |= ModifierKeys.Control;
        if (_lalt || _ralt) m |= ModifierKeys.Alt;
        if (_lshift || _rshift) m |= ModifierKeys.Shift;
        return m;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var msg = wParam.ToInt32();
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var up = msg is WM_KEYUP or WM_SYSKEYUP;
        var vk = (int)info.vkCode;

        // Game mode. A swallowed Win press never counts as a modifier, so Win+<key> reaches the game as
        // a plain key and none of our Win bindings fire. A Win key Windows already saw go down (held
        // before the game took focus) is left alone, so its release gets through and it can't stick.
        // GetAsyncKeyState still shows the state from before this event, which also covers presses we
        // never saw (made in an elevated window).
        if (vk is 0x5B or 0x5C && (info.flags & LLKHF_INJECTED) == 0)
        {
            var held = (vk == 0x5B ? _lwin : _rwin) || (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down && (_swallowedDown.Contains(vk) || (!held && _blockWin?.Invoke() == true)))
            {
                _swallowedDown.Add(vk);
                return (IntPtr)1;
            }
            if (up && _swallowedDown.Remove(vk)) return (IntPtr)1;
        }

        if ((info.flags & LLKHF_INJECTED) == 0)
        {
            switch (vk)
            {
                case 0x5B: _lwin = down; break;
                case 0x5C: _rwin = down; break;
                case 0xA2: _lctrl = down; break;
                case 0xA3: _rctrl = down; break;
                case 0xA4: _lalt = down; break;
                case 0xA5: _ralt = down; break;
                case 0xA0: _lshift = down; break;
                case 0xA1: _rshift = down; break;
            }
        }
        else
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (down && Recorder is { } recorder && !IsModifier(vk) && CurrentModifiers() != ModifierKeys.None)
        {
            _swallowedDown.Add(vk);
            var combo = new Hotkey(CurrentModifiers(), KeyInterop.KeyFromVirtualKey(vk));
            if (combo.Modifiers.HasFlag(ModifierKeys.Windows) || combo.Modifiers.HasFlag(ModifierKeys.Alt))
                Native.SendKeyCombo(MaskKey);
            _dispatcher.BeginInvoke(() => recorder(combo));
            return (IntPtr)1;
        }

        if (down && Recorder is null && _bindings.TryGetValue((CurrentModifiers(), vk), out var action))
        {
            // Auto-repeat of a held key does not fire again.
            if (_swallowedDown.Add(vk))
            {
                var mods = CurrentModifiers();
                if (mods.HasFlag(ModifierKeys.Windows) || mods.HasFlag(ModifierKeys.Alt))
                    Native.SendKeyCombo(MaskKey);
                _dispatcher.BeginInvoke(() =>
                {
                    try { action(); } catch (Exception ex) { Log.Error("shortcut", ex); }
                });
            }
            return (IntPtr)1;
        }
        if (up && _swallowedDown.Remove(vk)) return (IntPtr)1;
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}
