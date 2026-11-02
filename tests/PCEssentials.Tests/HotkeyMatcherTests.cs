using PCEssentials.Core;
using Xunit;

namespace PCEssentials.Tests;

public class HotkeyMatcherTests
{
    private const int VK_W = 0x57;
    private const int LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5, LWin = 0x5B, RWin = 0x5C;

    /// Presses W with the given virtual keys held and asks whether the hotkey text matches.
    private static bool Fires(string hotkey, params int[] held)
    {
        Assert.True(HotkeyMatcher.TryParse(hotkey, out var mods, out var key));
        Assert.Equal("W", key);
        var state = HotkeyMatcher.Held(vk => held.Contains(vk));
        return HotkeyMatcher.Matches(mods, VK_W, state, VK_W);
    }

    [Fact]
    public void PauseHotkeyNeedsAllThreeModifiers()
    {
        const string pause = "Ctrl+Alt+Shift+W";
        Assert.False(Fires(pause, LShift));
        Assert.False(Fires(pause, LCtrl, LShift));
        Assert.False(Fires(pause, LAlt, LShift));
        Assert.False(Fires(pause));
        Assert.True(Fires(pause, LCtrl, LAlt, LShift));
    }

    [Fact]
    public void EitherSideCounts()
    {
        Assert.True(Fires("Ctrl+Alt+Shift+W", RCtrl, RAlt, RShift));
        Assert.True(Fires("Ctrl+Alt+Shift+W", LCtrl, RAlt, RShift));
        Assert.True(Fires("Win+W", RWin));
    }

    [Fact]
    public void ExtraModifierDoesNotMatch()
    {
        Assert.False(Fires("Ctrl+Alt+Shift+W", LCtrl, LAlt, LShift, LWin));
        Assert.False(Fires("Win+W", LWin, LShift));
    }

    [Fact]
    public void WinBindingIgnoresPlainAndShiftedW()
    {
        Assert.False(Fires("Win+W"));
        Assert.False(Fires("Win+W", LShift));
        Assert.False(Fires("Win+W", RShift));
        Assert.True(Fires("Win+W", LWin));
    }

    [Fact]
    public void OtherKeyDoesNotMatch()
    {
        Assert.True(HotkeyMatcher.TryParse("Win+W", out var mods, out _));
        Assert.False(HotkeyMatcher.Matches(mods, VK_W, HotkeyModifiers.Windows, 0x45));
    }

    [Theory]
    [InlineData("ctrl + alt + shift + w", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift)]
    [InlineData("Windows+W", HotkeyModifiers.Windows)]
    [InlineData("Control+W", HotkeyModifiers.Control)]
    public void ParsesModifiers(string text, HotkeyModifiers expected)
    {
        Assert.True(HotkeyMatcher.TryParse(text, out var mods, out var key));
        Assert.Equal(expected, mods);
        Assert.Equal("w", key.ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+W+E")]
    public void RejectsBadText(string text) => Assert.False(HotkeyMatcher.TryParse(text, out _, out _));
}
