using System.Windows.Media;

namespace PCEssentials.Services.Launcher;

/// How a result was chosen: Enter, Ctrl+Enter (show it in its folder) or Ctrl+Shift+Enter (as administrator).
public enum LaunchMode { Open, Reveal, Admin }

/// One row in the launcher.
public sealed class LauncherResult
{
    /// Stable id for usage counting, e.g. "app:Microsoft.WindowsTerminal_8wekyb3d8bbwe!App".
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    /// Short label on the right: App, File, Folder, Window, Setting, Command, Web.
    public string Kind { get; init; } = "";
    /// Shell parsing name whose icon is shown (loaded lazily); otherwise Glyph is drawn.
    public string? IconSource { get; init; }
    public ImageSource? Icon { get; init; }
    public string Glyph { get; init; } = "";
    public required Action<LaunchMode> Run { get; init; }
    /// Text copied by Ctrl+C: a path, a URL or a calculation's result.
    public string? CopyText { get; init; }
    /// Things that can't be undone (shut down, empty the Recycle Bin) need a second Enter.
    public bool NeedsConfirm { get; init; }
    public bool CanReveal { get; init; }
    public bool CanRunAsAdmin { get; init; }
    /// Whether choosing it counts towards "frequently used" ranking.
    public bool Track { get; init; } = true;
    public int Score { get; set; }
}
