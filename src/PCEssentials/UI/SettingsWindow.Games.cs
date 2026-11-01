using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCEssentials.Core;
using PCEssentials.Services;
using PCEssentials.Services.GameMode;

namespace PCEssentials.UI;

public sealed partial class SettingsWindow
{
    // ---------------- Games ----------------

    private UIElement GamesTab()
    {
        var p = Panel();
        var g = _s.GameMode;
        var blocker = ((App)System.Windows.Application.Current).GameBlocker;

        p.Children.Add(Heading("Game mode"));
        p.Children.Add(Check("Block the Windows key while a game is in front", g.Enabled, v => { g.Enabled = v; Changed(); _rebindShortcuts(); }));
        p.Children.Add(Check("Auto-detect fullscreen games", g.AutoDetectFullscreen, v => { g.AutoDetectFullscreen = v; Changed(); }));
        p.Children.Add(Note("While a game is in front the Start menu doesn't open and Win shortcuts (Windows' own and PC Essentials' snapping) don't fire. " +
                            "Everything works normally again as soon as another app is in front. Win+L and Ctrl+Alt+Del can't be blocked."));

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        p.Children.Add(status);
        var elevated = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var elevatedText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.B(Ui.Orange), Margin = new Thickness(0, 0, 0, 6) };
        elevated.Children.Add(elevatedText);
        var restart = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        restart.Click += (_, _) =>
        {
            if (Startup.RestartAsAdmin()) ((App)System.Windows.Application.Current).Quit();
        };
        elevated.Children.Add(restart);
        p.Children.Add(elevated);

        p.Children.Add(Heading("Pause"));
        p.Children.Add(Note("A shortcut that turns blocking off until you press it again. It can't use Win, since Win is blocked in games."));
        var pauseHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 };
        var pauseBox = new TextBox { Text = g.PauseHotkey, IsReadOnly = true, Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
        pauseBox.GotKeyboardFocus += (_, _) =>
        {
            pauseBox.Text = "Press a shortcut…";
            pauseHint.Text = "";
            _hook.Recorder = combo =>
            {
                _hook.Recorder = null;
                var text = combo.ToString();
                if (combo.Modifiers.HasFlag(ModifierKeys.Windows))
                    pauseHint.Text = "Pick a shortcut without Win: Win is blocked while a game is in front.";
                else if (_s.Snap.Hotkeys.ContainsValue(text))
                    pauseHint.Text = $"{text} is already used by window snapping.";
                else
                {
                    g.PauseHotkey = text;
                    Changed();
                    _rebindShortcuts();
                }
                Keyboard_ClearFocus();
            };
        };
        pauseBox.LostKeyboardFocus += (_, _) =>
        {
            if (_hook.Recorder is not null) _hook.Recorder = null;
            pauseBox.Text = g.PauseHotkey;
        };
        var clearPause = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        clearPause.Click += (_, _) => { g.PauseHotkey = ""; pauseBox.Text = ""; Changed(); _rebindShortcuts(); };
        var pauseNow = new Button { Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(12, 0, 0, 0) };
        pauseNow.Click += (_, _) => blocker.TogglePause();
        p.Children.Add(Labeled("Pause shortcut", Ui.Columns((pauseBox, Ui.Auto), (clearPause, Ui.Auto), (pauseNow, Ui.Auto), (Ui.Spacer(), Ui.Star)), 200));
        p.Children.Add(pauseHint);

        p.Children.Add(Heading("Notifications"));
        var notify = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        string[] modes = ["off", "tray", "toast"];
        notify.Items.Add("Quiet");
        notify.Items.Add("Red dot on the tray icon while blocking");
        notify.Items.Add("Notification when blocking turns on or off");
        notify.SelectedIndex = Math.Max(0, Array.IndexOf(modes, g.Notify));
        notify.SelectionChanged += (_, _) => { g.Notify = modes[Math.Max(0, notify.SelectedIndex)]; Changed(); };
        p.Children.Add(Labeled("When blocking turns on", notify, 200));

        p.Children.Add(Heading("Games"));
        p.Children.Add(Note("Apps listed here always count as games, windowed or not. Type the exe name (for example cs2.exe) or pick a running app."));
        var addLast = new Button { Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(ExeList(g.Games, addLast));
        p.Children.Add(addLast);

        p.Children.Add(Heading("Never treat as games"));
        p.Children.Add(Note("Fullscreen apps that aren't games, such as browsers and video players. This list wins over the games list."));
        p.Children.Add(ExeList(g.Excluded, null));

        void Refresh()
        {
            status.Text = !g.Enabled ? "Game mode is off."
                : blocker.Paused ? "Paused: the Windows key works everywhere until you resume."
                : blocker.IsBlocking ? $"Blocking the Windows key now: {blocker.Current.Exe} ({(blocker.Current.Reason == GameReason.Listed ? "on your games list" : "running fullscreen")})."
                : $"Not blocking right now. Last app in front: {blocker.LastActiveExe ?? "none yet"}.";
            pauseNow.Content = blocker.Paused ? "Resume blocking" : "Pause blocking";
            pauseNow.IsEnabled = g.Enabled;
            var game = blocker.ElevatedGame;
            elevated.Visibility = g.Enabled && game is not null && !HardwareMonitor.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            elevatedText.Text = $"{game} runs as administrator and PC Essentials doesn't, so Windows hides that game's keys from PC Essentials " +
                                "and the Windows key can't be blocked in it. Restart PC Essentials as administrator to fix this.";
            addLast.Content = $"Add last active app ({blocker.LastActiveExe ?? "none yet"})";
            addLast.IsEnabled = blocker.LastActiveExe is not null;
        }
        blocker.StateChanged += Refresh;
        _s.Changed += Refresh;
        Closed += (_, _) => { blocker.StateChanged -= Refresh; _s.Changed -= Refresh; };
        Refresh();
        return p;
    }

    /// An editable list of exe names with a box to type one, a picker of running apps and Remove.
    /// When given, addLast becomes a button that adds the last app that was in front.
    private FrameworkElement ExeList(List<string> list, Button? addLast)
    {
        var panel = new StackPanel();
        var items = new ListBox { Height = 120, Margin = new Thickness(0, 0, 0, 6) };
        void Build()
        {
            items.Items.Clear();
            foreach (var exe in list) items.Items.Add(exe);
        }
        void Add(string? text)
        {
            if (text is null) return;
            var exe = GameDetector.NormalizeExe(text);
            if (exe.Length == 0 || list.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(exe);
            Changed();
            Build();
        }
        Build();

        var input = new TextBox { Width = 200 };
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(input.Text); input.Text = ""; } };
        var add = new Button { Content = "Add", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(6, 0, 0, 0) };
        add.Click += (_, _) => { Add(input.Text); input.Text = ""; };
        var remove = new Button { Content = "Remove", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            if (items.SelectedItem is not string exe) return;
            list.Remove(exe);
            Changed();
            Build();
        };
        var running = new ComboBox { Width = 220, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Add a running app" };
        running.DropDownOpened += (_, _) =>
        {
            running.Items.Clear();
            foreach (var name in RunningApps()) running.Items.Add(name);
        };
        running.SelectionChanged += (_, _) =>
        {
            if (running.SelectedItem is not string exe) return;
            Add(exe);
            Dispatcher.BeginInvoke(() => running.SelectedIndex = -1);
        };

        panel.Children.Add(items);
        var or = new TextBlock { Text = "or pick a running app", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65, Margin = new Thickness(16, 0, 0, 0) };
        running.Margin = new Thickness(8, 0, 0, 0);
        panel.Children.Add(Ui.Columns((input, Ui.Auto), (add, Ui.Auto), (remove, Ui.Auto), (or, Ui.Auto), (running, Ui.Auto), (Ui.Spacer(), Ui.Star)));
        if (addLast is not null)
            addLast.Click += (_, _) => Add(((App)System.Windows.Application.Current).GameBlocker.LastActiveExe);
        return panel;
    }

    /// Exe names of running apps that have a window, for the picker.
    private static List<string> RunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && process.Id != Environment.ProcessId) names.Add(process.ProcessName + ".exe");
            }
            catch { }
            finally { process.Dispose(); }
        }
        return names.ToList();
    }
}
