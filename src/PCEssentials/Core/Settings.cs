using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCEssentials.Core;

public sealed class TaskbarWidgetSettings
{
    public bool Enabled { get; set; } = true;
    /// Components in display order, each a two-line column: cpu (temperature over usage), gpu (the same),
    /// ram (usage over GB used) and net (upload over download).
    public List<string> Items { get; set; } = ["cpu", "gpu", "ram", "net"];
    /// Extra gap in pixels between the widget and the clock/tray area.
    public int GapFromTray { get; set; } = 8;
    public double FontSize { get; set; } = 11.5;
    public bool HideInFullscreen { get; set; } = true;
}

public sealed class IslandSettings
{
    public bool Enabled { get; set; } = true;
    /// Section ids in display order. Known ids: agents, keepAwake, music, mixer, calendar,
    /// clipboard, scratchpad, microphone, system.
    public List<string> Sections { get; set; } =
        ["agents", "keepAwake", "music", "mixer", "calendar", "clipboard", "scratchpad", "microphone", "system"];
    public bool HideInFullscreen { get; set; } = true;
    /// Where the island sits: "taskbar" (on the taskbar, right of the Widgets button, opening upward)
    /// or "top" (top centre of the screen, opening downward). Settings saved without it get "taskbar".
    public string Position { get; set; } = "taskbar";
    /// Optional iCal (.ics) feed URLs, e.g. a Google Calendar "secret address in iCal format".
    public List<string> CalendarFeeds { get; set; } = [];
    public bool ShowAgentsWhileWorking { get; set; } = true;
    /// Opens the island when the pointer rests on it, and closes it when the pointer leaves.
    public bool OpenOnHover { get; set; } = true;
    /// The open island as blurred, tinted glass over what is behind it, like the Mac's Liquid Glass mode.
    public bool Glass { get; set; } = true;
    /// Set once hover opening has been switched on for settings saved before it became the default.
    public bool HoverDefaultApplied { get; set; }
    /// What the closed island shows when nothing is happening: none, music or agents.
    public string IdleContent { get; set; } = "agents";
    /// How the resting island lays out the agents' limits: single, split (one per side) or cycle (rotate).
    public string AgentsRestingLayout { get; set; } = "all";
    /// Set once "all" has replaced an older saved layout, so a later choice sticks.
    public bool RestingAllApplied { get; set; }
    /// Limits read as what is left ("remaining") or what is spent ("used").
    public string AgentsLimitDisplay { get; set; } = "remaining";
    /// Which limit each resting number shows: mostUsed, session or weekly.
    public string AgentsLimitFocus { get; set; } = "mostUsed";
    /// Agents left out of the resting island ("Claude", "Codex").
    public List<string> AgentsRestingHidden { get; set; } = [];
    public bool ShowVolumeChanges { get; set; } = true;
    /// Hides Windows' own volume and media pop-up while the island shows volume changes. On laptops the same
    /// pop-up carries brightness, which then goes too.
    public bool ReplaceWindowsPopup { get; set; } = true;
    /// Shows Caps Lock turning on and off.
    public bool ShowCapsLock { get; set; } = true;
    /// Shows the microphone muting or unmuting and the sound output switching to another device.
    public bool ShowDeviceChanges { get; set; } = true;
    public bool ShowKeepAwakeInCapsule { get; set; } = true;
    public string LastPage { get; set; } = "agents";
}

public sealed class SnapSettings
{
    public bool Enabled { get; set; } = true;
    /// Action id to hotkey text. Pressing the same hotkey again cycles through that action's sizes.
    /// Defaults follow the user's Rectangle layout with Option -> Win, Command -> Alt, Control -> Ctrl.
    /// Empty text leaves an action unbound.
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["left"] = "Win+A",
        ["right"] = "Win+D",
        ["centerThird"] = "Win+Shift+X",
        ["top"] = "Win+Alt+Up",
        ["bottom"] = "Win+Alt+Down",
        ["topLeft"] = "Win+Q",
        ["topRight"] = "Win+E",
        ["bottomLeft"] = "Win+Z",
        ["bottomRight"] = "Win+C",
        ["maximize"] = "Win+W",
        ["almostMaximize"] = "",
        ["maximizeHeight"] = "Ctrl+Win+Shift+Up",
        ["smaller"] = "Ctrl+Win+Shift+Left",
        ["larger"] = "Ctrl+Win+Shift+Right",
        ["center"] = "Win+X",
        ["restore"] = "Ctrl+Win+Back",
        ["nextDisplay"] = "Ctrl+Win+Alt+Right",
        ["previousDisplay"] = "Ctrl+Win+Alt+Left",
        ["firstThird"] = "",
        ["lastThird"] = "",
        ["firstTwoThirds"] = "",
        ["lastTwoThirds"] = "",
    };
    /// Sizes the left/right hotkeys cycle through on repeated presses, as fractions of the screen width.
    public List<double> CycleFractions { get; set; } = [1.0 / 2, 1.0 / 3, 2.0 / 3];
    public int Gap { get; set; } = 0;
}

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 200;
    public List<string> IgnoredApps { get; set; } = ["KeePass", "KeePassXC", "1Password", "Bitwarden"];
}

public sealed class GameModeSettings
{
    /// Block the Windows key while a game is in front.
    public bool Enabled { get; set; } = true;
    public bool AutoDetectFullscreen { get; set; } = true;
    /// Exe names always treated as games, e.g. "cs2.exe". Matched ignoring case.
    public List<string> Games { get; set; } = [];
    /// Exe names never treated as games, even fullscreen.
    public List<string> Excluded { get; set; } =
        ["chrome.exe", "msedge.exe", "firefox.exe", "vlc.exe", "mpc-hc64.exe", "PotPlayerMini64.exe", "POWERPNT.EXE"];
    /// Toggles blocking off and on. Avoids Alt+Z/Alt+S (NVIDIA), Shift+0, Alt+Space and double Alt.
    public string PauseHotkey { get; set; } = "Ctrl+Alt+Shift+W";
    /// "off", "tray" (a dot on the tray icon while blocking) or "toast".
    public string Notify { get; set; } = "off";
}

public sealed class ScreenshotSettings
{
    public bool Enabled { get; set; } = true;
    /// Print Screen by itself opens the capture overlay.
    public bool UsePrintScreen { get; set; } = true;
    /// An extra shortcut for the overlay (needs a modifier), e.g. "Ctrl+Shift+S". Empty = none.
    public string Hotkey { get; set; } = "";
    /// Saves every monitor straight to the default folder, without the overlay. Empty = none.
    public string FullScreenHotkey { get; set; } = "Shift+PrintScreen";
    /// Where instant saves go, and where the save dialog starts until a folder is picked. Empty = Pictures\Screenshots.
    public string Folder { get; set; } = "";
    /// The folder last picked in the save dialog.
    public string LastFolder { get; set; } = "";
    /// File name; see ShotFiles.Name for the {…} tokens.
    public string FileNamePattern { get; set; } = "Screenshot {yyyy-MM-dd HH.mm.ss}";
    /// png, jpg or bmp.
    public string Format { get; set; } = "png";
    public int JpegQuality { get; set; } = 90;
    /// Also copies the image whenever it is saved or printed.
    public bool AutoCopy { get; set; }
    /// Starts the next capture with the previous selection.
    public bool KeepSelection { get; set; }
    public string LastSelection { get; set; } = "";
    public bool CaptureCursor { get; set; }
    /// Tray notifications after copying and saving.
    public bool Notify { get; set; } = true;
    /// Shows the magnifier next to the pointer while selecting.
    public bool Magnifier { get; set; } = true;
    /// ARGB of the drawing colour.
    public uint Color { get; set; } = 0xFFFF0000;
    /// Set once Lightshot's own options have been copied over.
    public bool LightshotImported { get; set; }
}

public sealed class MouseSettings
{
    /// Shake the pointer left and right to make it big for a moment, like macOS.
    public bool ShakeToFind { get; set; } = true;
    /// How big the pointer grows, as a multiple of its normal size.
    public double ShakeMaxScale { get; set; } = 4;
    /// "low", "normal" or "high": how little shaking it takes.
    public string ShakeSensitivity { get; set; } = "normal";
}

public sealed class LauncherSettings
{
    /// A Spotlight-style search box: apps, files, open windows, settings, a calculator and web search.
    public bool Enabled { get; set; } = true;
    /// Alt+Space is PowerToys Run's default and sits where Cmd+Space is on a Mac keyboard. It replaces the
    /// window menu that Alt+Space normally opens.
    public string Hotkey { get; set; } = "Alt+Space";
    /// Whether the shortcut opens Spotlight over a fullscreen app (videos, presentations). When off, the
    /// shortcut passes through to that app instead. Games in game mode never get it either way.
    public bool OpenInFullscreen { get; set; } = true;
    /// Files and folders from the Windows Search index.
    public bool SearchFiles { get; set; } = true;
    public bool SearchWindows { get; set; } = true;
    /// google, bing, duckduckgo or a URL with {q} in it.
    public string SearchEngine { get; set; } = "google";
    public int MaxResults { get; set; } = 8;
    /// Times each result was opened, by id, so frequent picks rise to the top.
    public Dictionary<string, int> Usage { get; set; } = new();
}

public sealed class AgentSettings
{
    /// More folders of Claude Code and Codex logs to count, such as ones synced from another computer.
    /// Each is searched with its subfolders; Codex logs are told apart by their "rollout-" file names.
    /// A folder that does not exist is skipped.
    public List<string> ExtraLogFolders { get; set; } = [Path.Combine(Paths.Home, "AgentLogs")];
    /// The Claude seats' order on the agents page, by email; seats not listed follow in their usual order.
    public List<string> SeatOrder { get; set; } = [];
}

public sealed class AppSettings
{
    public AgentSettings Agents { get; set; } = new();
    public ScreenshotSettings Screenshot { get; set; } = new();
    public TaskbarWidgetSettings TaskbarWidget { get; set; } = new();
    public IslandSettings Island { get; set; } = new();
    public SnapSettings Snap { get; set; } = new();
    public ClipboardSettings Clipboard { get; set; } = new();
    public GameModeSettings GameMode { get; set; } = new();
    public MouseSettings Mouse { get; set; } = new();
    public LauncherSettings Launcher { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool UseFahrenheit { get; set; }

    [JsonIgnore] public static string FilePath => Path.Combine(Paths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                // Readouts used to be listed one by one; they are now grouped per component.
                var known = new[] { "cpu", "gpu", "ram", "net" };
                if (loaded.TaskbarWidget.Items.Any(i => !known.Contains(i)))
                    loaded.TaskbarWidget.Items = [.. known];
                if (!loaded.Island.RestingAllApplied)
                {
                    loaded.Island.AgentsRestingLayout = "all";
                    loaded.Island.RestingAllApplied = true;
                }
                if (!loaded.Island.HoverDefaultApplied)
                {
                    loaded.Island.OpenOnHover = true;
                    loaded.Island.HoverDefaultApplied = true;
                }
                return loaded;
            }
        }
        catch (Exception ex) { Log.Error("settings load", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public event Action? Changed;
    public void NotifyChanged() { Save(); Changed?.Invoke(); }
}
