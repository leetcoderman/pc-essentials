using System.Windows;

namespace PCEssentials.UI;

public sealed partial class SettingsWindow
{
    // ---------------- Search ----------------

    private UIElement LauncherTab()
    {
        var p = Panel();
        var l = _s.Launcher;
        p.Children.Add(Heading("Search"));
        p.Children.Add(Check("Open a search box with a shortcut", l.Enabled, v => { l.Enabled = v; Changed(); _rebindShortcuts(); }));
        p.Children.Add(Note("Like Spotlight on a Mac: start typing to find apps, files, open windows, Windows settings and system " +
                            "commands, do sums (2*(3+4), sqrt(2)) or search the web. Results you pick often move up. " +
                            "Start with ? to search only files, or < to list open windows."));
        p.Children.Add(Labeled("Shortcut", ShortcutBox(() => l.Hotkey, v => l.Hotkey = v), 200));
        p.Children.Add(Note("Alt+Space sits where Cmd+Space is on a Mac keyboard, and is also what PowerToys Run uses. " +
                            "It replaces the window menu Alt+Space normally opens. Win+Space switches keyboard languages, so it is left alone."));

        p.Children.Add(Heading("Results"));
        p.Children.Add(Check("Include files and folders (from the Windows Search index)", l.SearchFiles, v => { l.SearchFiles = v; Changed(); }));
        p.Children.Add(Check("Include open windows", l.SearchWindows, v => { l.SearchWindows = v; Changed(); }));
        p.Children.Add(Labeled("Results shown", NumberSlider(4, 12, l.MaxResults, 1, v => { l.MaxResults = (int)v; Changed(); }), 200));
        p.Children.Add(Labeled("Web search", Choice(l.SearchEngine is "google" or "bing" or "duckduckgo" ? l.SearchEngine : "google",
            [("google", "Google"), ("bing", "Bing"), ("duckduckgo", "DuckDuckGo")], v => l.SearchEngine = v), 200));
        var reset = new System.Windows.Controls.Button { Content = "Forget frequently used results", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) => { l.Usage.Clear(); Changed(); };
        p.Children.Add(reset);
        return p;
    }
}
