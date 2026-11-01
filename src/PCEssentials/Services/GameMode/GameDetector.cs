namespace PCEssentials.Services.GameMode;

/// Result of SHQueryUserNotificationState (QUERY_USER_NOTIFICATION_STATE). Unknown when the call fails.
public enum NotificationState
{
    Unknown = 0,
    NotPresent = 1,
    Busy = 2,
    D3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

/// The OS calls the detector needs, behind an interface so the decision logic can be tested without Win32.
public interface IForegroundProbe
{
    /// File name of the window's process, e.g. "cs2.exe", or null when it can't be read.
    string? ExeName(IntPtr hwnd);
    /// The desktop, the shell window or the taskbar.
    bool IsShellOrDesktop(IntPtr hwnd);
    NotificationState QueryNotificationState();
    /// The window fills its whole monitor (including the taskbar area).
    bool CoversMonitor(IntPtr hwnd);
    /// The window's process runs elevated (or can't be inspected because it is).
    bool IsElevated(IntPtr hwnd);
}

public sealed record GameRules(IReadOnlyCollection<string> Games, IReadOnlyCollection<string> Excluded, bool AutoDetectFullscreen, bool SelfIsAdmin);

public enum GameReason { None, Listed, Fullscreen, Excluded }

public readonly record struct GameDecision(bool IsGame, string? Exe, GameReason Reason, bool ElevatedGame)
{
    public static readonly GameDecision NotAGame = new(false, null, GameReason.None, false);
}

/// Decides whether the foreground window is a game: the exclude list wins, then the game list,
/// then (when auto-detect is on) whether it runs fullscreen.
public sealed class GameDetector(IForegroundProbe probe)
{
    /// Never games, whatever the lists say: PC Essentials' own full-screen views (the lock screen
    /// widgets, capture overlays) and screen-capture tools whose overlays cover the whole screen.
    public static readonly IReadOnlyCollection<string> BuiltInExcluded =
        ["PCEssentials.exe", "Lightshot.exe", "SnippingTool.exe", "ScreenClippingHost.exe", "ShareX.exe", "Greenshot.exe"];

    public GameDecision Evaluate(IntPtr hwnd, GameRules rules)
    {
        if (hwnd == IntPtr.Zero || probe.IsShellOrDesktop(hwnd)) return GameDecision.NotAGame;
        var exe = probe.ExeName(hwnd);
        if (exe is not null && (Contains(rules.Excluded, exe) || Contains(BuiltInExcluded, exe)))
            return new(false, exe, GameReason.Excluded, false);

        var reason = GameReason.None;
        if (exe is not null && Contains(rules.Games, exe)) reason = GameReason.Listed;
        else if (rules.AutoDetectFullscreen && IsFullscreen(hwnd)) reason = GameReason.Fullscreen;
        if (reason == GameReason.None) return new(false, exe, GameReason.None, false);

        return new(true, exe, reason, !rules.SelfIsAdmin && probe.IsElevated(hwnd));
    }

    /// SHQueryUserNotificationState reports exclusive (D3D) and borderless (busy) fullscreen apps;
    /// when it reports neither, fall back to checking whether the window covers its monitor.
    private bool IsFullscreen(IntPtr hwnd) =>
        probe.QueryNotificationState() is NotificationState.D3DFullScreen or NotificationState.Busy || probe.CoversMonitor(hwnd);

    private static bool Contains(IReadOnlyCollection<string> list, string exe) =>
        list.Any(e => string.Equals(NormalizeExe(e), exe, StringComparison.OrdinalIgnoreCase));

    /// "C:\Games\CS2.exe", " cs2 " and "cs2.exe" all become "cs2.exe" (case kept, compared ignoring case).
    public static string NormalizeExe(string text)
    {
        var name = text.Trim().Trim('"');
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name;
    }
}
