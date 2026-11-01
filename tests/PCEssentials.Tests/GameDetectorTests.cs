using PCEssentials.Services.GameMode;
using Xunit;

namespace PCEssentials.Tests;

public class GameDetectorTests
{
    private static readonly IntPtr Window = new(0x1234);

    private sealed class FakeProbe : IForegroundProbe
    {
        public string? Exe = "game.exe";
        public bool Shell;
        public NotificationState State = NotificationState.AcceptsNotifications;
        public bool Covers;
        public bool Elevated;
        public int StateQueries, ElevatedQueries;

        public string? ExeName(IntPtr hwnd) => Exe;
        public bool IsShellOrDesktop(IntPtr hwnd) => Shell;
        public NotificationState QueryNotificationState() { StateQueries++; return State; }
        public bool CoversMonitor(IntPtr hwnd) => Covers;
        public bool IsElevated(IntPtr hwnd) { ElevatedQueries++; return Elevated; }
    }

    private static GameRules Rules(string[]? games = null, string[]? excluded = null, bool auto = true, bool admin = false) =>
        new(games ?? [], excluded ?? [], auto, admin);

    private static GameDecision Run(FakeProbe probe, GameRules rules) => new GameDetector(probe).Evaluate(Window, rules);

    [Fact]
    public void ListedExeIsAGameIgnoringCase()
    {
        var probe = new FakeProbe { Exe = "VALORANT.exe" };
        var d = Run(probe, Rules(games: ["valorant.exe"]));
        Assert.True(d.IsGame);
        Assert.Equal(GameReason.Listed, d.Reason);
        Assert.Equal("VALORANT.exe", d.Exe);
    }

    [Fact]
    public void ListEntriesWithoutExtensionOrPathStillMatch()
    {
        Assert.True(Run(new FakeProbe { Exe = "cs2.exe" }, Rules(games: ["cs2"])).IsGame);
        Assert.True(Run(new FakeProbe { Exe = "cs2.exe" }, Rules(games: [@"C:\Games\CS2.EXE"])).IsGame);
    }

    [Fact]
    public void ListedWindowedGameNeedsNoFullscreenCheck()
    {
        var probe = new FakeProbe { Exe = "cs2.exe" };
        Assert.True(Run(probe, Rules(games: ["cs2.exe"])).IsGame);
        Assert.Equal(0, probe.StateQueries);
    }

    [Theory]
    [InlineData(NotificationState.D3DFullScreen)]
    [InlineData(NotificationState.Busy)]
    public void UnlistedFullscreenAppIsAGameWithAutoDetect(NotificationState state)
    {
        var d = Run(new FakeProbe { State = state }, Rules());
        Assert.True(d.IsGame);
        Assert.Equal(GameReason.Fullscreen, d.Reason);
    }

    [Theory]
    [InlineData(NotificationState.AcceptsNotifications)]
    [InlineData(NotificationState.Unknown)]
    public void FallsBackToMonitorCoverage(NotificationState state)
    {
        Assert.True(Run(new FakeProbe { State = state, Covers = true }, Rules()).IsGame);
        Assert.False(Run(new FakeProbe { State = state, Covers = false }, Rules()).IsGame);
    }

    [Fact]
    public void FullscreenIsIgnoredWhenAutoDetectIsOff()
    {
        var probe = new FakeProbe { State = NotificationState.D3DFullScreen, Covers = true };
        Assert.False(Run(probe, Rules(auto: false)).IsGame);
        Assert.Equal(0, probe.StateQueries);
    }

    [Fact]
    public void ExcludedFullscreenAppIsNotAGame()
    {
        var d = Run(new FakeProbe { Exe = "chrome.exe", State = NotificationState.Busy, Covers = true }, Rules(excluded: ["Chrome.exe"]));
        Assert.False(d.IsGame);
        Assert.Equal(GameReason.Excluded, d.Reason);
    }

    [Fact]
    public void ExcludeListWinsOverGameList()
    {
        Assert.False(Run(new FakeProbe { Exe = "vlc.exe" }, Rules(games: ["vlc.exe"], excluded: ["vlc.exe"])).IsGame);
    }

    [Fact]
    public void DesktopAndShellAreNeverGames()
    {
        var probe = new FakeProbe { Exe = "explorer.exe", Shell = true, State = NotificationState.D3DFullScreen, Covers = true };
        Assert.False(Run(probe, Rules(games: ["explorer.exe"])).IsGame);
        Assert.Null(Run(probe, Rules()).Exe);
    }

    [Fact]
    public void NoForegroundWindowIsNotAGame()
    {
        Assert.False(new GameDetector(new FakeProbe { State = NotificationState.D3DFullScreen }).Evaluate(IntPtr.Zero, Rules()).IsGame);
    }

    [Fact]
    public void UnreadableExeStillCountsWhenFullscreen()
    {
        var d = Run(new FakeProbe { Exe = null, State = NotificationState.D3DFullScreen }, Rules(excluded: ["chrome.exe"]));
        Assert.True(d.IsGame);
        Assert.Null(d.Exe);
    }

    [Fact]
    public void NormalWindowIsNotAGame()
    {
        var d = Run(new FakeProbe { Exe = "notepad.exe" }, Rules(games: ["cs2.exe"]));
        Assert.False(d.IsGame);
        Assert.Equal("notepad.exe", d.Exe);
        Assert.Equal(GameReason.None, d.Reason);
    }

    [Fact]
    public void ElevatedGameIsFlaggedOnlyWhenWeAreNotAdmin()
    {
        var probe = new FakeProbe { Exe = "cs2.exe", Elevated = true };
        Assert.True(Run(probe, Rules(games: ["cs2.exe"])).ElevatedGame);
        Assert.False(Run(probe, Rules(games: ["cs2.exe"], admin: true)).ElevatedGame);
    }

    [Fact]
    public void ElevationIsOnlyCheckedForGames()
    {
        var probe = new FakeProbe { Exe = "notepad.exe", Elevated = true };
        Assert.False(Run(probe, Rules()).ElevatedGame);
        Assert.Equal(0, probe.ElevatedQueries);
    }

    [Theory]
    [InlineData("cs2", "cs2.exe")]
    [InlineData("  cs2.exe  ", "cs2.exe")]
    [InlineData("\"C:\\Riot Games\\VALORANT\\VALORANT.exe\"", "VALORANT.exe")]
    [InlineData("D:/games/eldenring.EXE", "eldenring.EXE")]
    [InlineData("   ", "")]
    public void NormalizeExe(string input, string expected) => Assert.Equal(expected, GameDetector.NormalizeExe(input));
}
