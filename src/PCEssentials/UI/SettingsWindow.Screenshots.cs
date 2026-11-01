using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCEssentials.Core;
using PCEssentials.Services.Screenshot;

namespace PCEssentials.UI;

public sealed partial class SettingsWindow
{
    // ---------------- Screenshots ----------------

    private UIElement ScreenshotsTab()
    {
        var p = Panel();
        var c = _s.Screenshot;

        p.Children.Add(Heading("Screenshots"));
        p.Children.Add(Check("Enable the screenshot tool", c.Enabled, v => { c.Enabled = v; Changed(); _rebindShortcuts(); }));
        p.Children.Add(Note("Works like Lightshot: the screen freezes, you drag to select an area, then draw on it and copy, save or print it. " +
                            "Ctrl+A or a double click selects the whole monitor, Esc cancels. Screenshots never leave this PC: there is no upload."));

        p.Children.Add(Heading("Print Screen key"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        p.Children.Add(Check("Use the Print Screen key to open the screenshot tool", c.UsePrintScreen, v => { c.UsePrintScreen = v; Changed(); _rebindShortcuts(); UpdateStatus(); }));
        p.Children.Add(status);
        var openKeyboard = new Button { Content = "Open Windows keyboard settings", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        openKeyboard.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open settings", ex); }
        };
        p.Children.Add(openKeyboard);
        void UpdateStatus()
        {
            var lines = new List<string>();
            if (ScreenshotService.SnippingToolOwnsPrintScreen())
            {
                lines.Add("Windows' \"Use the Print Screen key to open screen capture\" (Snipping Tool) setting is on. " +
                          "Windows can then act on Print Screen before PC Essentials does. Turn it off under Settings → Accessibility → Keyboard. " +
                          "PC Essentials doesn't change it for you.");
                status.Foreground = Ui.B(Ui.Orange);
            }
            else
            {
                lines.Add("Windows' Snipping Tool setting for Print Screen is off, so the key reaches PC Essentials.");
                status.ClearValue(TextBlock.ForegroundProperty);
            }
            if (ScreenshotService.LightshotRunning)
                lines.Add("Lightshot is running too. PC Essentials' shortcuts take precedence over its hotkeys; quit Lightshot and remove it from startup when you've switched.");
            status.Text = string.Join("\n\n", lines);
        }
        UpdateStatus();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateStatus(); };
        Activated += (_, _) => UpdateStatus();

        p.Children.Add(Heading("Shortcuts"));
        p.Children.Add(Note("Click a box, then press the shortcut. Shortcuts need Ctrl, Alt, Shift or Win; Print Screen by itself is the checkbox above."));
        p.Children.Add(Labeled("Take a screenshot (extra shortcut)", ShortcutBox(() => c.Hotkey, v => c.Hotkey = v), 240));
        p.Children.Add(Labeled("Instant save of the full screen", ShortcutBox(() => c.FullScreenHotkey, v => c.FullScreenHotkey = v), 240));

        p.Children.Add(Heading("Saving"));
        var folder = new TextBox { Text = c.Folder, Width = 300, IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Left };
        void ShowFolder() => folder.Text = string.IsNullOrWhiteSpace(c.Folder) ? "Pictures\\Screenshots (default)" : c.Folder;
        ShowFolder();
        var browse = new Button { Content = "Choose…", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Default screenshot folder" };
            if (!string.IsNullOrWhiteSpace(c.Folder)) dialog.InitialDirectory = c.Folder;
            if (dialog.ShowDialog(this) != true) return;
            c.Folder = dialog.FolderName;
            ShowFolder();
            Changed();
        };
        var resetFolder = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Use Pictures\\Screenshots" };
        resetFolder.Click += (_, _) => { c.Folder = ""; ShowFolder(); Changed(); };
        p.Children.Add(Labeled("Default folder", Ui.Columns((folder, Ui.Auto), (browse, Ui.Auto), (resetFolder, Ui.Auto), (Ui.Spacer(), Ui.Star)), 240));
        p.Children.Add(Note("Instant full-screen saves go here. The save dialog opens in the folder you last saved to."));

        var pattern = new TextBox { Text = c.FileNamePattern, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var preview = new TextBlock { Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
        void Preview() => preview.Text = "Example: " + ShotFiles.Name(pattern.Text, DateTime.Now, _ => false, ShotFiles.Extension(c.Format)) + ShotFiles.Extension(c.Format) +
                                         "    ·    {…} holds a date format such as {yyyy-MM-dd HH.mm.ss}; {n} is a running number.";
        pattern.TextChanged += (_, _) => Preview();
        pattern.LostFocus += (_, _) =>
        {
            c.FileNamePattern = string.IsNullOrWhiteSpace(pattern.Text) ? ShotFiles.DefaultPattern : pattern.Text.Trim();
            pattern.Text = c.FileNamePattern;
            Changed();
        };
        p.Children.Add(Labeled("File name", pattern, 240));
        p.Children.Add(preview);
        p.Children.Add(Labeled("Format", Choice(c.Format, [("png", "PNG"), ("jpg", "JPEG"), ("bmp", "BMP")], v => { c.Format = v; Preview(); }), 240));
        Preview();
        p.Children.Add(Labeled("JPEG quality", NumberSlider(10, 100, c.JpegQuality, 5, v => { c.JpegQuality = (int)v; Changed(); }), 240));

        p.Children.Add(Heading("Behaviour"));
        p.Children.Add(Check("Also copy to the clipboard when saving or printing", c.AutoCopy, v => { c.AutoCopy = v; Changed(); }));
        p.Children.Add(Check("Show notifications about copying and saving", c.Notify, v => { c.Notify = v; Changed(); }));
        p.Children.Add(Check("Keep the selected area position", c.KeepSelection, v => { c.KeepSelection = v; Changed(); }));
        p.Children.Add(Check("Capture the cursor on a screenshot", c.CaptureCursor, v => { c.CaptureCursor = v; Changed(); }));
        p.Children.Add(Check("Show the magnifier while selecting", c.Magnifier, v => { c.Magnifier = v; Changed(); }));
        return p;
    }

    /// A shortcut recorder box with a ✕ to clear it, for the screenshot shortcuts.
    private FrameworkElement ShortcutBox(Func<string> get, Action<string> set)
    {
        var box = new TextBox { Text = get(), IsReadOnly = true, Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
        box.GotKeyboardFocus += (_, _) =>
        {
            box.Text = "Press a shortcut…";
            _hook.Recorder = combo =>
            {
                _hook.Recorder = null;
                set(combo.ToString());
                box.Text = get();
                Changed();
                _rebindShortcuts();
                Keyboard_ClearFocus();
            };
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (_hook.Recorder is not null) _hook.Recorder = null;
            box.Text = get();
        };
        var clear = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { set(""); box.Text = ""; Changed(); _rebindShortcuts(); };
        return Ui.Columns((box, Ui.Auto), (clear, Ui.Auto), (Ui.Spacer(), Ui.Star));
    }
}
