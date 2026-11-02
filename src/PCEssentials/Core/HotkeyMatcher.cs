namespace PCEssentials.Core;

/// Modifier flags with the same values as WPF's ModifierKeys, so the two convert with a cast. Kept separate
/// so the matching rules can be tested without WPF.
[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

/// Parses the modifier part of a hotkey string and decides whether a key press matches a binding.
public static class HotkeyMatcher
{
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// Splits "Ctrl+Alt+Shift+W" into its modifiers and the one remaining key name. False when a part
    /// repeats the key or nothing but modifiers is given.
    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None;
        key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Windows; break;
                default:
                    if (key.Length > 0) return false;
                    key = raw;
                    break;
            }
        }
        return key.Length > 0;
    }

    /// The modifiers held right now, either side counting. isDown is asked about left/right virtual keys;
    /// the hook passes GetAsyncKeyState, which is the live state and can't go stale the way a flag tracked
    /// from hook events does when a key-up never arrives (Ctrl+Alt+Del, Win+L, an elevated window).
    public static HotkeyModifiers Held(Func<int, bool> isDown)
    {
        var m = HotkeyModifiers.None;
        if (isDown(VK_LCONTROL) || isDown(VK_RCONTROL)) m |= HotkeyModifiers.Control;
        if (isDown(VK_LMENU) || isDown(VK_RMENU)) m |= HotkeyModifiers.Alt;
        if (isDown(VK_LSHIFT) || isDown(VK_RSHIFT)) m |= HotkeyModifiers.Shift;
        if (isDown(VK_LWIN) || isDown(VK_RWIN)) m |= HotkeyModifiers.Windows;
        return m;
    }

    /// Exact match: same key, and every one of Ctrl, Alt, Shift and Win held exactly when the binding
    /// wants it. An extra or missing modifier means no match.
    public static bool Matches(HotkeyModifiers wanted, int wantedVk, HotkeyModifiers held, int vk) =>
        vk == wantedVk && held == wanted;
}
