using System.Windows.Controls;
using System.Windows.Media;
using PCEssentials.Core;
using PCEssentials.Services;
using PCEssentials.Services.Agents;

namespace PCEssentials.UI.Island;

/// Everything the island's pages read from or act on.
public sealed class IslandContext
{
    public required AppSettings Settings { get; init; }
    public required HardwareMonitor Monitor { get; init; }
    public required AgentUsageService Agents { get; init; }
    public required MediaService Media { get; init; }
    public required AudioService Audio { get; init; }
    public required KeepAwake KeepAwake { get; init; }
    public required ClipboardHistory Clipboard { get; init; }
    public required Scratchpad Scratchpad { get; init; }
    public required CalendarService Calendar { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action Quit { get; init; }
    /// Lets a page take keyboard focus (the island normally never steals it).
    public Action<bool> AllowKeyboard { get; set; } = _ => { };
    /// Closes the island; used before pasting into the app underneath.
    public Action Collapse { get; set; } = () => { };
    /// Opens the screenshot overlay (after the island has closed), or null when screenshots are off.
    public Action? TakeScreenshot { get; init; }
}

/// One page of the expanded island. Pages are built once and kept; Tick runs ~10 times a second
/// only while the page is on screen.
public abstract class IslandPage : Grid
{
    protected IslandPage(IslandContext ctx) => Ctx = ctx;

    protected IslandContext Ctx { get; }
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract Color Tint { get; }
    public virtual bool WantsKeyboard => false;

    public virtual void OnShown() { }
    public virtual void OnHidden() { }
    public virtual void Tick() { }
}
