using System.Windows.Input;
using System.Windows.Interop;

namespace PCEssentials.Core;

/// A key combination such as "Ctrl+Alt+Left". Parsed from and saved as text in settings.
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (!HotkeyMatcher.TryParse(text, out var mods, out var name)) return false;
        if (mods == HotkeyModifiers.None || !Enum.TryParse<Key>(name, true, out var key)) return false;
        hotkey = new Hotkey((ModifierKeys)mods, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}

/// Registers system-wide hotkeys on a hidden message window.
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("PCEssentialsHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// Returns false when another app already owns the combination.
    public bool Register(Hotkey hotkey, Action action)
    {
        uint mods = Native.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Native.MOD_WIN;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        var id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception ex) { Log.Error("hotkey", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
