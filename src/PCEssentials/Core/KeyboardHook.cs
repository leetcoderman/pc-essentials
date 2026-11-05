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
    private readonly List<(HotkeyModifiers Modifiers, int Vk, Action Action, Func<bool>? When)> _bindings = new();
    private readonly HashSet<int> _swallowedDown = new();
    private bool _updateQueued;
    private Action<Hotkey>? _recorder;
    private Func<bool>? _blockWin;

    public KeyboardHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = Callback;
    }

    /// when, if given, is asked on each press: false lets the press through to the app untouched. It runs
    /// inside the hook, so it must be quick and must not send messages to other windows.
    public void Bind(Hotkey hotkey, Action action, Func<bool>? when = null)
    {
        var vk = KeyInterop.VirtualKeyFromKey(hotkey.Key);
        _bindings.RemoveAll(b => b.Modifiers == (HotkeyModifiers)hotkey.Modifiers && b.Vk == vk);
        _bindings.Add(((HotkeyModifiers)hotkey.Modifiers, vk, action, when));
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

    /// Live modifier state. Inside the hook GetAsyncKeyState shows the keyboard as it was before the current
    /// event, which is exactly what a non-modifier key press needs. Presses this hook swallowed never reach
    /// it, so a Win press blocked in game mode doesn't count as held.
    private static HotkeyModifiers CurrentModifiers() => HotkeyMatcher.Held(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Keys sent by software (our own mask key included) always pass untouched.
        if ((info.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, code, wParam, lParam);
        var msg = wParam.ToInt32();
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var up = msg is WM_KEYUP or WM_SYSKEYUP;
        var vk = (int)info.vkCode;

        // Key-ups are swallowed only when the matching press was, so apps never see a lone release
        // or miss one.
        if (up) return _swallowedDown.Remove(vk) ? (IntPtr)1 : CallNextHookEx(_hook, code, wParam, lParam);
        if (!down) return CallNextHookEx(_hook, code, wParam, lParam);

        // Game mode. A swallowed Win press never counts as a modifier, so Win+<key> reaches the game as
        // a plain key and none of our Win bindings fire. A Win key Windows already saw go down (held
        // before the game took focus) is left alone, so its release gets through and it can't stick.
        if (vk is 0x5B or 0x5C && (GetAsyncKeyState(vk) & 0x8000) == 0 && _blockWin?.Invoke() == true)
        {
            _swallowedDown.Add(vk);
            return (IntPtr)1;
        }

        var held = CurrentModifiers();
        if (Recorder is { } recorder && !IsModifier(vk) && held != HotkeyModifiers.None)
        {
            _swallowedDown.Add(vk);
            var combo = new Hotkey((ModifierKeys)held, KeyInterop.KeyFromVirtualKey(vk));
            if (held.HasFlag(HotkeyModifiers.Windows) || held.HasFlag(HotkeyModifiers.Alt))
                Native.SendKeyCombo(MaskKey);
            _dispatcher.BeginInvoke(() => recorder(combo));
            return (IntPtr)1;
        }

        if (Recorder is null)
        {
            foreach (var binding in _bindings)
            {
                if (!HotkeyMatcher.Matches(binding.Modifiers, binding.Vk, held, vk)) continue;
                if (binding.When is { } when && !when()) break;
                // Auto-repeat of a held key does not fire again.
                if (_swallowedDown.Add(vk))
                {
                    if (held.HasFlag(HotkeyModifiers.Windows) || held.HasFlag(HotkeyModifiers.Alt))
                        Native.SendKeyCombo(MaskKey);
                    var action = binding.Action;
                    _dispatcher.BeginInvoke(() =>
                    {
                        try { action(); } catch (Exception ex) { Log.Error("shortcut", ex); }
                    });
                }
                return (IntPtr)1;
            }
        }

        // This press reaches the app, so its release must too, even if an earlier press of the same key
        // was swallowed and its release never arrived.
        _swallowedDown.Remove(vk);
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}
