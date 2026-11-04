using System.Windows;
using System.Windows.Threading;
using PCEssentials.Core;
using PCEssentials.Services;
using PCEssentials.Services.Agents;
using PCEssentials.Services.GameMode;
using PCEssentials.Services.Launcher;
using PCEssentials.Services.Screenshot;
using PCEssentials.Services.ShakeToFind;
using PCEssentials.UI;
using PCEssentials.UI.Island;
using PCEssentials.UI.Launcher;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PCEssentials;

public partial class App : System.Windows.Application
{
    private Mutex? _single;
    private Forms.NotifyIcon? _tray;
    private AppSettings _settings = null!;
    private HardwareMonitor _monitor = null!;
    private AgentUsageService _agents = null!;
    private AudioService _audio = null!;
    private ClipboardHistory _clipboard = null!;
    private KeyboardHook _hook = null!;
    private WindowSnapper _snapper = null!;
    private IslandWindow _island = null!;
    private TaskbarWidget _widget = null!;
    private SettingsWindow? _settingsWindow;
    private ScreenshotService? _shots;
    private ShakeToFindService? _shake;
    private LauncherService? _launcher;
    private LauncherWindow? _launcherWindow;
    private string? _balloonPath;

    /// Lets an update script ask this copy to quit (it may run elevated, out of the script's reach):
    /// creating quit.request in %APPDATA%\PCEssentials or next to the exe quits within a second. The exe
    /// folder matters for tools running inside a packaged (MSIX) app such as the Claude desktop app: Windows
    /// redirects their %APPDATA% writes into the package's own copy, which this app never sees.
    private static readonly string[] QuitRequests =
        [System.IO.Path.Combine(Paths.DataDir, "quit.request"), System.IO.Path.Combine(AppContext.BaseDirectory, "quit.request")];

    /// Watches for request files from scripts: quit.request and island.request.
    private void StartRequestWatch()
    {
        foreach (var file in QuitRequests) try { System.IO.File.Delete(file); } catch { }
        var quitWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        quitWatch.Tick += (_, _) =>
        {
            // island.request opens the island on the page named inside it (e.g. "agents").
            var islandRequest = System.IO.Path.Combine(Paths.DataDir, "island.request");
            if (System.IO.File.Exists(islandRequest))
            {
                string page = "";
                try { page = System.IO.File.ReadAllText(islandRequest).Trim(); System.IO.File.Delete(islandRequest); } catch { }
                // Optionally "agents 400": open on that page, scrolled 400 px down.
                var parts = page.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _island.Open(parts.Length > 0 ? parts[0] : null);
                if (parts.Length > 1 && double.TryParse(parts[1], out var offset))
                    Dispatcher.BeginInvoke(() => _island.ScrollPage(offset), DispatcherPriority.Background);
            }
            var request = QuitRequests.FirstOrDefault(System.IO.File.Exists);
            if (request is null) return;
            try { System.IO.File.Delete(request); } catch { }
            Log.Info("quit requested");
            quitWatch.Stop();
            Quit();
        };
        quitWatch.Start();
    }

    public CalendarService Calendar { get; private set; } = null!;
    public GameKeyBlocker GameBlocker { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("domain", (Exception)ex.ExceptionObject);

        // One instance only. After "Restart as administrator" the old one may still be exiting, so wait a moment.
        _single = new Mutex(true, @"Local\PCEssentials.SingleInstance", out var first);
        if (!first && !(e.Args.Contains("--after-restart") && _single.WaitOne(TimeSpan.FromSeconds(8))))
        {
            Shutdown();
            return;
        }

        Log.Info($"start (admin: {HardwareMonitor.IsAdmin})");
        _settings = AppSettings.Load();
        Core.Startup.KeepPathCurrent(HardwareMonitor.IsAdmin);
        _monitor = new HardwareMonitor();
        _agents = new AgentUsageService(_settings);
        _audio = new AudioService();
        _clipboard = new ClipboardHistory(_settings);
        Calendar = new CalendarService(_settings);
        var media = new MediaService();
        _ = media.StartAsync();

        _hook = new KeyboardHook();
        _snapper = new WindowSnapper(_settings);
        _shots = new ScreenshotService(_settings);
        _shake = new ShakeToFindService(_settings);
        GameBlocker = new GameKeyBlocker(_settings, _hook);
        BindShortcuts();

        var ctx = new IslandContext
        {
            Settings = _settings,
            Monitor = _monitor,
            Agents = _agents,
            Media = media,
            Audio = _audio,
            KeepAwake = new KeepAwake(),
            Clipboard = _clipboard,
            Scratchpad = new Scratchpad(),
            Calendar = Calendar,
            OpenSettings = OpenSettings,
            Quit = Quit,
            TakeScreenshot = TakeScreenshotSoon,
        };
        _island = new IslandWindow(ctx);
        if (_settings.Island.Enabled) _island.Show();
        _launcher = new LauncherService(_settings,
        [
            new("PC Essentials Settings", "Open PC Essentials settings", Ui.GSettings, OpenSettings, "preferences", "options"),
            new("Take a screenshot", "Select an area of the screen", Ui.GImage, TakeScreenshotSoon, "screenshot", "capture", "snip"),
            new("Clipboard history", "Open the island's clipboard page", Ui.GClipboard, () => _island.Open("clipboard"), "clipboard"),
            new("Scratchpad", "Open the island's notes", Ui.GNote, () => _island.Open("scratchpad"), "notes", "scratchpad"),
            new("Keep awake", "Open the island's keep awake page", Ui.GCoffee, () => _island.Open("keepAwake"), "caffeine", "awake", "no sleep"),
            new("Volume mixer", "Open the island's mixer", Ui.GMixer, () => _island.Open("mixer"), "volume", "mixer"),
            new("AI agent limits", "Claude and Codex usage", Ui.GRobot, () => _island.Open("agents"), "claude", "codex", "agents", "limits"),
        ]);
        StartRequestWatch();
        BindShortcuts();

        _widget = new TaskbarWidget(_settings, _monitor) { Height = 48 };
        _widget.Clicked += () => _island.Toggle("system");
        if (_settings.TaskbarWidget.Enabled) _widget.Show();

        CreateTray();
    }

    private void BindShortcuts()
    {
        _hook.Clear();
        if (_shots is not null && _settings.Screenshot.Enabled)
        {
            var shot = _settings.Screenshot;
            if (shot.UsePrintScreen) _hook.Bind(new Hotkey(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.PrintScreen), _shots.Start);
            if (Hotkey.TryParse(shot.Hotkey, out var shotKey)) _hook.Bind(shotKey, _shots.Start);
            if (Hotkey.TryParse(shot.FullScreenHotkey, out var fullKey)) _hook.Bind(fullKey, _shots.SaveFullScreen);
        }
        if (_settings.GameMode.Enabled && Hotkey.TryParse(_settings.GameMode.PauseHotkey, out var pause))
            _hook.Bind(pause, GameBlocker.TogglePause);
        if (_settings.Launcher.Enabled && Hotkey.TryParse(_settings.Launcher.Hotkey, out var search))
            _hook.Bind(search, ToggleLauncher);
        if (!_settings.Snap.Enabled) return;
        foreach (var (action, text) in _settings.Snap.Hotkeys)
        {
            if (!Hotkey.TryParse(text, out var hotkey)) continue;
            var a = action;
            _hook.Bind(hotkey, () => _snapper.Run(a));
        }
    }

    /// The Spotlight-style search box. Built on first use; a game in front never gets it.
    private void ToggleLauncher()
    {
        if (_launcher is null) return;
        if (GameBlocker.IsBlocking && _launcherWindow is not { IsVisible: true }) return;
        _launcherWindow ??= new LauncherWindow(_launcher);
        _launcherWindow.Toggle();
    }

    /// Opens the screenshot overlay once a menu or the island that asked for it has gone from the screen.
    private void TakeScreenshotSoon()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); _shots?.Start(); };
        timer.Start();
    }

    private void CreateTray()
    {
        _trayIcon = MakeIcon();
        _trayIconBlocking = MakeIcon(true);
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "PC Essentials", Visible = true };
        if (_shots is not null)
        {
            _shots.Notify = (text, path) =>
            {
                _balloonPath = path;
                _tray.ShowBalloonTip(2500, "Screenshot", text, Forms.ToolTipIcon.None);
            };
            _tray.BalloonTipClicked += (_, _) =>
            {
                if (_balloonPath is { } file && System.IO.File.Exists(file))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
                _balloonPath = null;
            };
        }
        GameBlocker.BlockingChanged += () => UpdateGameModeIndicator(true);
        _settings.Changed += () => UpdateGameModeIndicator(false);
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(() => _island.Toggle(null));
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open island", null, (_, _) => _island.Open(null));
        menu.Items.Add("Search…", null, (_, _) => Dispatcher.BeginInvoke(ToggleLauncher, DispatcherPriority.Background));
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Take a screenshot", null, (_, _) => TakeScreenshotSoon());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var widgetItem = new Forms.ToolStripMenuItem("Taskbar readouts") { Checked = _settings.TaskbarWidget.Enabled, CheckOnClick = true };
        widgetItem.CheckedChanged += (_, _) => { _settings.TaskbarWidget.Enabled = widgetItem.Checked; _settings.NotifyChanged(); };
        menu.Items.Add(widgetItem);
        var islandItem = new Forms.ToolStripMenuItem("Dynamic Island") { Checked = _settings.Island.Enabled, CheckOnClick = true };
        islandItem.CheckedChanged += (_, _) => { _settings.Island.Enabled = islandItem.Checked; _settings.NotifyChanged(); };
        menu.Items.Add(islandItem);
        var snapItem = new Forms.ToolStripMenuItem("Window snapping") { Checked = _settings.Snap.Enabled, CheckOnClick = true };
        snapItem.CheckedChanged += (_, _) => { _settings.Snap.Enabled = snapItem.Checked; _settings.NotifyChanged(); BindShortcuts(); };
        menu.Items.Add(snapItem);
        var gameItem = new Forms.ToolStripMenuItem("Block Win key in games") { Checked = _settings.GameMode.Enabled, CheckOnClick = true };
        gameItem.CheckedChanged += (_, _) => { _settings.GameMode.Enabled = gameItem.Checked; _settings.NotifyChanged(); BindShortcuts(); };
        menu.Items.Add(gameItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (!HardwareMonitor.IsAdmin)
            menu.Items.Add("Restart as administrator (CPU temps)", null, (_, _) => { if (Core.Startup.RestartAsAdmin()) Quit(); });
        menu.Items.Add("Quit PC Essentials", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
    }

    // Built in CreateTray, once WPF resources (pack URIs) are available.
    private Drawing.Icon _trayIcon = null!, _trayIconBlocking = null!;

    /// Game mode's tray dot and toast. Both are off unless chosen in Settings.
    private void UpdateGameModeIndicator(bool announce)
    {
        if (_tray is null) return;
        var mode = _settings.GameMode.Notify;
        var blocking = GameBlocker.IsBlocking;
        var icon = blocking && mode != "off" ? _trayIconBlocking : _trayIcon;
        if (_tray.Icon != icon) _tray.Icon = icon;
        _tray.Text = blocking && mode != "off" ? "PC Essentials: Windows key blocked" : "PC Essentials";
        if (announce && mode == "toast")
        {
            var text = blocking ? $"Windows key blocked while {GameBlocker.Current.Exe ?? "the game"} is in front"
                : GameBlocker.Paused ? "Windows key blocking paused" : "Windows key works again";
            _tray.ShowBalloonTip(2000, "Game mode", text, Forms.ToolTipIcon.None);
        }
    }

    /// The app logo at the tray's small-icon size. With blocking, a red dot marks game mode.
    private static Drawing.Icon MakeIcon(bool blocking = false)
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Resources/PCEssentials.ico"))!.Stream;
        var logo = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        if (!blocking) return logo;
        using var bmp = logo.ToBitmap();
        logo.Dispose();
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var red = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 69, 58));
            var d = bmp.Width * 13f / 32;
            g.FillEllipse(red, bmp.Width - d, bmp.Height - d, d, d);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void OpenSettings()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow is { IsLoaded: true })
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_settings, _hook, BindShortcuts);
            _settingsWindow.Show();
            _settingsWindow.Activate();
        });
    }

    public void Quit()
    {
        try
        {
            _settings.Save();
            if (_tray is not null) _tray.Visible = false;
            _tray?.Dispose();
            GameBlocker.Dispose();
            _hook.Dispose();
            _shake?.Dispose();
            _clipboard.Dispose();
            _monitor.Dispose();
            _agents.Dispose();
            _audio.Dispose();
            Calendar.Dispose();
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            _single?.ReleaseMutex();
        }
        catch (Exception ex) { Log.Error("quit", ex); }
        Shutdown();
    }
}
