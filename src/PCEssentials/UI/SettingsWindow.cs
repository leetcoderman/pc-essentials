using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCEssentials.Core;
using PCEssentials.Services;

namespace PCEssentials.UI;

/// Settings: General, Taskbar readouts, Island, Window snapping, Clipboard and Games (SettingsWindow.Games.cs).
public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly KeyboardHook _hook;
    private readonly Action _rebindShortcuts;

    public SettingsWindow(AppSettings settings, KeyboardHook hook, Action rebindShortcuts)
    {
        _s = settings;
        _hook = hook;
        _rebindShortcuts = rebindShortcuts;
        Title = "PC Essentials Settings";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Resources/PCEssentials.ico"));
        Width = 760;
        Height = 640;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui.Font;

        var tabs = new TabControl { Margin = new Thickness(12) };
        tabs.Items.Add(Tab("General", General()));
        tabs.Items.Add(Tab("Taskbar readouts", Taskbar()));
        tabs.Items.Add(Tab("Island", Island()));
        tabs.Items.Add(Tab("Window snapping", Snapping()));
        tabs.Items.Add(Tab("Clipboard", ClipboardTab()));
        tabs.Items.Add(Tab("Games", GamesTab()));
        tabs.Items.Add(Tab("Screenshots", ScreenshotsTab()));
        tabs.Items.Add(Tab("Mouse", MouseTab()));
        tabs.Items.Add(Tab("Spotlight", LauncherTab()));
        Content = tabs;
        Closed += (_, _) => _hook.Recorder = null;
    }

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, Padding = new Thickness(8, 12, 16, 12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
    };

    private void Changed() => _s.NotifyChanged();

    private static StackPanel Panel() => new();

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 2, 0, 8),
    };

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement control, double labelWidth = 220)
    {
        var g = Ui.Columns((new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, Ui.Px(labelWidth)), (control, Ui.Star));
        g.Margin = new Thickness(0, 4, 0, 4);
        return g;
    }

    private ComboBox Choice(string current, (string Id, string Name)[] options, Action<string> set)
    {
        var box = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (id, name) in options) box.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        box.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Id == current));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string id }) { set(id); Changed(); }
        };
        return box;
    }

    private static Slider NumberSlider(double min, double max, double value, double step, Action<double> set)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        s.ValueChanged += (_, e) => set(e.NewValue);
        return s;
    }

    // ---------------- General ----------------

    private UIElement General()
    {
        var p = Panel();
        p.Children.Add(Heading("Startup"));
        p.Children.Add(Check("Start PC Essentials when I sign in", Startup.RunKeyEnabled || Startup.AdminTaskEnabled, on =>
        {
            if (on && HardwareMonitor.IsAdmin) Startup.SetAdminTask(true);
            else if (on) Startup.SetRunKey(true);
            else
            {
                Startup.SetRunKey(false);
                if (HardwareMonitor.IsAdmin) Startup.SetAdminTask(false);
            }
        }));
        p.Children.Add(Note(HardwareMonitor.IsAdmin
            ? "Running as administrator, so sign-in start uses a scheduled task with highest privileges (no UAC prompt) and CPU temperatures can be read."
            : "PC Essentials is running without administrator rights. Everything works except the CPU temperature reading."));

        p.Children.Add(Heading("CPU temperature"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var pawn = Startup.PawnIoInstalled;
        status.Text = $"Administrator: {(HardwareMonitor.IsAdmin ? "yes" : "no")}    ·    PawnIO driver: {(pawn ? "installed" : "not installed")}";
        p.Children.Add(status);
        p.Children.Add(Note("Ryzen and Intel CPU temperatures are read through LibreHardwareMonitor, which needs administrator rights and the signed PawnIO driver. " +
                            "Install it yourself from an elevated terminal with:  winget install namazso.PawnIO  — GPU temperature, RAM and network work without it."));
        if (!HardwareMonitor.IsAdmin)
        {
            var restart = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
            restart.Click += (_, _) =>
            {
                if (Startup.RestartAsAdmin()) ((App)System.Windows.Application.Current).Quit();
            };
            p.Children.Add(restart);
        }

        p.Children.Add(Heading("Units"));
        p.Children.Add(Check("Show temperatures in °F", _s.UseFahrenheit, v => { _s.UseFahrenheit = v; Changed(); }));

        p.Children.Add(Heading("Data"));
        p.Children.Add(Note($"Settings, clipboard history and notes are stored only on this PC in {Paths.DataDir}. Nothing is sent anywhere."));
        var open = new Button { Content = "Open data folder", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        open.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Paths.DataDir);
        p.Children.Add(open);
        return p;
    }

    // ---------------- Taskbar ----------------

    private static readonly (string Id, string Name)[] WidgetItems =
    [
        ("cpu", "CPU — temperature and usage"), ("gpu", "GPU — temperature and usage"),
        ("ram", "RAM — usage and GB used"), ("net", "Network — upload and download"),
    ];

    private UIElement Taskbar()
    {
        var p = Panel();
        p.Children.Add(Heading("Readouts beside the clock"));
        p.Children.Add(Check("Show readouts on the taskbar", _s.TaskbarWidget.Enabled, v => { _s.TaskbarWidget.Enabled = v; Changed(); }));
        p.Children.Add(Check("Hide while a fullscreen app is in front", _s.TaskbarWidget.HideInFullscreen, v => { _s.TaskbarWidget.HideInFullscreen = v; Changed(); }));
        p.Children.Add(Heading("What to show"));
        p.Children.Add(Note("Each component is one column with its two readings stacked, left to right in this order. Use the arrows to reorder."));
        var list = new StackPanel();
        void Build()
        {
            list.Children.Clear();
            var order = _s.TaskbarWidget.Items.Concat(WidgetItems.Select(w => w.Id).Where(id => !_s.TaskbarWidget.Items.Contains(id))).ToList();
            foreach (var id in order)
            {
                var name = WidgetItems.First(w => w.Id == id).Name;
                var on = _s.TaskbarWidget.Items.Contains(id);
                var check = Check(name, on, v =>
                {
                    if (v && !_s.TaskbarWidget.Items.Contains(id)) _s.TaskbarWidget.Items.Add(id);
                    if (!v) _s.TaskbarWidget.Items.Remove(id);
                    Changed();
                    Build();
                });
                var row = Ui.Columns((check, Ui.Px(240)), (Move(id, -1, Build), Ui.Auto), (Move(id, 1, Build), Ui.Auto));
                list.Children.Add(row);
            }
        }
        Build();
        p.Children.Add(list);
        p.Children.Add(Heading("Look"));
        p.Children.Add(Labeled("Text size", NumberSlider(9, 14, _s.TaskbarWidget.FontSize, 0.5, v => { _s.TaskbarWidget.FontSize = v; Changed(); })));
        p.Children.Add(Labeled("Gap from the clock (px)", NumberSlider(0, 200, _s.TaskbarWidget.GapFromTray, 2, v => { _s.TaskbarWidget.GapFromTray = (int)v; Changed(); })));
        return p;
    }

    private Button Move(string id, int dir, Action rebuild)
    {
        var b = new Button { Content = dir < 0 ? "▲" : "▼", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) =>
        {
            var items = _s.TaskbarWidget.Items;
            var i = items.IndexOf(id);
            var j = i + dir;
            if (i < 0 || j < 0 || j >= items.Count) return;
            (items[i], items[j]) = (items[j], items[i]);
            Changed();
            rebuild();
        };
        return b;
    }

    // ---------------- Island ----------------

    private static readonly (string Id, string Name)[] Sections =
    [
        ("agents", "AI agents"), ("keepAwake", "Keep awake"), ("music", "Now playing"), ("mixer", "Volume mixer"),
        ("calendar", "Calendar"), ("clipboard", "Clipboard"), ("scratchpad", "Scratchpad"), ("microphone", "Microphone"),
        ("system", "System monitor"),
    ];

    private UIElement Island()
    {
        var p = Panel();
        var i = _s.Island;
        p.Children.Add(Heading("Dynamic Island"));
        p.Children.Add(Check("Show the island", i.Enabled, v => { i.Enabled = v; Changed(); }));
        p.Children.Add(Labeled("Position", Choice(i.Position, [("taskbar", "On the taskbar, right of Widgets"), ("top", "Top centre of the screen")], v => i.Position = v)));
        p.Children.Add(Check("Open on hover and close when the pointer leaves (otherwise click to open)", i.OpenOnHover, v => { i.OpenOnHover = v; Changed(); }));
        p.Children.Add(Check("Glass: show the open island as blurred, tinted glass", i.Glass, v => { i.Glass = v; Changed(); }));
        p.Children.Add(Check("Hide while a fullscreen app is in front", i.HideInFullscreen, v => { i.HideInFullscreen = v; Changed(); }));
        p.Children.Add(Check("Show agents while they work, with an elapsed timer", i.ShowAgentsWhileWorking, v => { i.ShowAgentsWhileWorking = v; Changed(); }));
        p.Children.Add(Check("Show volume changes", i.ShowVolumeChanges, v => { i.ShowVolumeChanges = v; Changed(); }));
        p.Children.Add(Check("Show keep awake time left in the closed island", i.ShowKeepAwakeInCapsule, v => { i.ShowKeepAwakeInCapsule = v; Changed(); }));

        p.Children.Add(Heading("At rest"));
        p.Children.Add(Note("What the closed island shows when nothing else is happening. With AI agents, each agent rests as its mark inside a ring of the limit it shows, " +
                            "with the number and when it renews. Rings and numbers turn yellow at 80% used and red at 95%. \"Limit to show\" picks every number's limit."));
        p.Children.Add(Labeled("Show", Choice(i.IdleContent, [("agents", "AI agents' limits"), ("music", "Music"), ("none", "Nothing")], v => i.IdleContent = v)));
        p.Children.Add(Labeled("Layout", Choice(i.AgentsRestingLayout, [("all", "Every signed-in account side by side"), ("single", "Single (the limit closest to running out)"), ("split", "One per side"), ("cycle", "Rotate every 4 seconds")], v => i.AgentsRestingLayout = v)));
        p.Children.Add(Labeled("Limits read as", Choice(i.AgentsLimitDisplay, [("remaining", "What's left"), ("used", "What's used")], v => i.AgentsLimitDisplay = v)));
        p.Children.Add(Labeled("Limit to show", Choice(i.AgentsLimitFocus, [("mostUsed", "Most used"), ("session", "Session"), ("weekly", "Weekly")], v => i.AgentsLimitFocus = v)));
        foreach (var agent in new[] { "Claude", "Codex" })
            p.Children.Add(Check($"Show {agent} at rest", !i.AgentsRestingHidden.Contains(agent), v =>
            {
                i.AgentsRestingHidden.Remove(agent);
                if (!v) i.AgentsRestingHidden.Add(agent);
                Changed();
            }));

        p.Children.Add(Heading("Sections"));
        p.Children.Add(Note("Choose the island's pages and their order in its top bar. Settings and Quit always end the bar."));
        var list = new StackPanel();
        void Build()
        {
            list.Children.Clear();
            var order = i.Sections.Concat(Sections.Select(x => x.Id).Where(id => !i.Sections.Contains(id))).ToList();
            foreach (var id in order)
            {
                var name = Sections.First(x => x.Id == id).Name;
                var check = Check(name, i.Sections.Contains(id), v =>
                {
                    if (v && !i.Sections.Contains(id)) i.Sections.Add(id);
                    if (!v && i.Sections.Count > 1) i.Sections.Remove(id);
                    Changed();
                    Build();
                });
                var up = new Button { Content = "▲", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
                var down = new Button { Content = "▼", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
                void Swap(int dir)
                {
                    var a = i.Sections.IndexOf(id);
                    var b = a + dir;
                    if (a < 0 || b < 0 || b >= i.Sections.Count) return;
                    (i.Sections[a], i.Sections[b]) = (i.Sections[b], i.Sections[a]);
                    Changed();
                    Build();
                }
                up.Click += (_, _) => Swap(-1);
                down.Click += (_, _) => Swap(1);
                list.Children.Add(Ui.Columns((check, Ui.Px(240)), (up, Ui.Auto), (down, Ui.Auto)));
            }
        }
        Build();
        p.Children.Add(list);

        p.Children.Add(Heading("Calendar"));
        p.Children.Add(Note("Paste iCal (.ics) links, one per line. Google Calendar: Settings → your calendar → Integrate calendar → \"Secret address in iCal format\". Outlook: Settings → Calendar → Shared calendars → Publish a calendar → ICS link."));
        var feeds = new TextBox
        {
            Text = string.Join(Environment.NewLine, i.CalendarFeeds), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = Ui.Mono, FontSize = 11.5,
        };
        var save = new Button { Content = "Save calendars", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0) };
        save.Click += async (_, _) =>
        {
            i.CalendarFeeds = feeds.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            Changed();
            save.Content = "Loading…";
            await ((App)System.Windows.Application.Current).Calendar.RefreshAsync();
            save.Content = "Saved";
        };
        p.Children.Add(feeds);
        p.Children.Add(save);
        return p;
    }

    // ---------------- Snapping ----------------

    private UIElement Snapping()
    {
        var p = Panel();
        var snap = _s.Snap;
        p.Children.Add(Heading("Window snapping"));
        p.Children.Add(Check("Enable snapping shortcuts", snap.Enabled, v => { snap.Enabled = v; Changed(); _rebindShortcuts(); }));
        p.Children.Add(Note("Shortcuts act on the window in front. Press Left, Right, Top, Bottom or a corner again to cycle its size. " +
                            "Defaults follow Rectangle with ⌥ → Win, ⌘ → Alt and ⌃ → Ctrl. Windows' own Win+A/D/E/Q/W/X/Z/C actions are replaced while these are bound."));
        p.Children.Add(Labeled("Cycle sizes (fractions of the screen)", CycleBox(snap), 260));
        p.Children.Add(Labeled("Gap between windows and edges (px)", NumberSlider(0, 24, snap.Gap, 1, v => { snap.Gap = (int)v; Changed(); }), 260));

        p.Children.Add(Heading("Shortcuts"));
        p.Children.Add(Note("Click a box, then press the new shortcut. Use ✕ to leave an action unbound."));
        foreach (var action in WindowSnapper.Actions)
        {
            snap.Hotkeys.TryGetValue(action, out var current);
            var box = new TextBox { Text = current ?? "", IsReadOnly = true, Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand };
            box.GotKeyboardFocus += (_, _) =>
            {
                box.Text = "Press a shortcut…";
                _hook.Recorder = combo =>
                {
                    _hook.Recorder = null;
                    // Taking a combination away from another action keeps every shortcut unique.
                    foreach (var other in snap.Hotkeys.Where(kv => kv.Value == combo.ToString() && kv.Key != action).Select(kv => kv.Key).ToList())
                        snap.Hotkeys[other] = "";
                    snap.Hotkeys[action] = combo.ToString();
                    box.Text = combo.ToString();
                    Changed();
                    _rebindShortcuts();
                    Keyboard_ClearFocus();
                    RefreshShortcutBoxes();
                };
            };
            box.LostKeyboardFocus += (_, _) =>
            {
                if (_hook.Recorder is not null) _hook.Recorder = null;
                snap.Hotkeys.TryGetValue(action, out var v);
                box.Text = v ?? "";
            };
            var clear = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            clear.Click += (_, _) => { snap.Hotkeys[action] = ""; box.Text = ""; Changed(); _rebindShortcuts(); };
            _shortcutBoxes[action] = box;
            p.Children.Add(Labeled(WindowSnapper.DisplayName(action), Ui.Columns((box, Ui.Auto), (clear, Ui.Auto), (Ui.Spacer(), Ui.Star)), 200));
        }
        var reset = new Button { Content = "Reset to Rectangle defaults", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) =>
        {
            snap.Hotkeys = new SnapSettings().Hotkeys;
            Changed();
            _rebindShortcuts();
            RefreshShortcutBoxes();
        };
        p.Children.Add(reset);
        return p;
    }

    private readonly Dictionary<string, TextBox> _shortcutBoxes = new();

    private void RefreshShortcutBoxes()
    {
        foreach (var (action, box) in _shortcutBoxes)
            box.Text = _s.Snap.Hotkeys.TryGetValue(action, out var v) ? v : "";
    }

    private void Keyboard_ClearFocus() => System.Windows.Input.Keyboard.ClearFocus();

    private TextBox CycleBox(SnapSettings snap)
    {
        static string Frac(double f) => Math.Abs(f - 0.5) < 0.01 ? "1/2" : Math.Abs(f - 1 / 3.0) < 0.01 ? "1/3" : Math.Abs(f - 2 / 3.0) < 0.01 ? "2/3"
            : Math.Abs(f - 0.25) < 0.01 ? "1/4" : Math.Abs(f - 0.75) < 0.01 ? "3/4" : f.ToString("0.##");
        var box = new TextBox { Text = string.Join(", ", snap.CycleFractions.Select(Frac)), Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
        box.LostFocus += (_, _) =>
        {
            var parsed = new List<double>();
            foreach (var part in box.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split('/');
                if (bits.Length == 2 && double.TryParse(bits[0], out var a) && double.TryParse(bits[1], out var b) && b > 0) parsed.Add(a / b);
                else if (double.TryParse(part, out var d)) parsed.Add(d);
            }
            parsed = parsed.Where(f => f > 0 && f <= 1).ToList();
            if (parsed.Count == 0) parsed = [0.5];
            snap.CycleFractions = parsed;
            box.Text = string.Join(", ", parsed.Select(Frac));
            Changed();
        };
        return box;
    }

    // ---------------- Clipboard ----------------

    private UIElement ClipboardTab()
    {
        var p = Panel();
        var c = _s.Clipboard;
        p.Children.Add(Heading("Clipboard history"));
        p.Children.Add(Check("Keep a clipboard history", c.Enabled, v => { c.Enabled = v; Changed(); }));
        p.Children.Add(Note("Items copied from password managers that mark them private are never recorded."));
        p.Children.Add(Labeled("Keep this many items (pins excluded)", NumberSlider(20, 1000, c.MaxItems, 10, v => { c.MaxItems = (int)v; Changed(); }), 260));
        p.Children.Add(Heading("Ignored apps"));
        p.Children.Add(Note("Process names, comma separated. Nothing copied while these apps are in front is recorded."));
        var ignored = new TextBox { Text = string.Join(", ", c.IgnoredApps) };
        ignored.LostFocus += (_, _) =>
        {
            c.IgnoredApps = ignored.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            Changed();
        };
        p.Children.Add(ignored);
        return p;
    }
}
