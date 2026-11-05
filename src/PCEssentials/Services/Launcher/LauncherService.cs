using System.Diagnostics;
using System.Runtime.InteropServices;
using PCEssentials.Core;

namespace PCEssentials.Services.Launcher;

/// An action PC Essentials itself offers in the launcher (open Settings, take a screenshot...).
public sealed record AppCommand(string Title, string Subtitle, string Glyph, Action Run, params string[] Keywords);

/// Turns what is typed into ranked results from every source. Apps, windows, commands, settings pages,
/// the calculator and web search answer at once; files come later from FilesAsync.
///
/// PC Essentials normally runs elevated (for CPU temperatures). Everything is therefore opened through
/// explorer.exe, which hands the request to the already-running, unelevated shell, so launched apps don't
/// inherit administrator rights. Ctrl+Shift+Enter asks for elevation on purpose.
public sealed class LauncherService
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<AppCommand> _appCommands;
    public AppIndex Apps { get; } = new();

    public LauncherService(AppSettings settings, IReadOnlyList<AppCommand> appCommands)
    {
        _settings = settings;
        _appCommands = appCommands;
        Apps.Refresh();
    }

    private LauncherSettings S => _settings.Launcher;
    public int MaxResults => Math.Max(3, S.MaxResults);

    /// FuzzyMatcher scores below this are letters scattered through a name rather than a real match.
    private const int ScatteredLimit = 300;

    /// Results that need no waiting, best first.
    public List<LauncherResult> Query(string text, IntPtr launcherWindow)
    {
        var q = text.Trim();
        var results = new List<LauncherResult>();
        if (q.Length == 0) return Suggestions();

        // Prefixes narrow the search the way PowerToys Run's do: "?" files only, "<" windows only.
        if (q.StartsWith('?') || q.StartsWith('<')) return results;

        if (Calculator.TryEvaluate(q, out var value))
        {
            var answer = Calculator.Format(value);
            results.Add(new LauncherResult
            {
                Id = "calc", Title = answer, Subtitle = $"{q.TrimStart('=').Trim()} · Enter copies the result", Kind = "Calculator",
                Glyph = "", CopyText = answer.Replace(",", ""), Track = false, Score = 2000,
                Run = _ => CopyToClipboard(answer.Replace(",", "")),
            });
        }

        foreach (var app in Apps.Apps)
        {
            var score = FuzzyMatcher.Score(q, app.Name);
            if (score == 0) continue;
            results.Add(AppResult(app, score));
        }

        foreach (var c in _appCommands)
        {
            var score = Math.Max(FuzzyMatcher.Score(q, c.Title), c.Keywords.Select(k => FuzzyMatcher.Score(q, k) - 50).DefaultIfEmpty(0).Max());
            if (score <= 0) continue;
            results.Add(new LauncherResult
            {
                Id = "pce:" + c.Title, Title = c.Title, Subtitle = c.Subtitle, Kind = "PC Essentials", Glyph = c.Glyph,
                Score = score, Run = _ => c.Run(),
            });
        }

        foreach (var cmd in SystemCommands)
        {
            var score = Math.Max(FuzzyMatcher.Score(q, cmd.Title), cmd.Keywords.Select(k => FuzzyMatcher.Score(q, k) - 50).DefaultIfEmpty(0).Max());
            if (score <= 0) continue;
            results.Add(new LauncherResult
            {
                Id = "sys:" + cmd.Title, Title = cmd.Title, Subtitle = cmd.Subtitle, Kind = "System", Glyph = cmd.Glyph,
                Score = score - 30, NeedsConfirm = cmd.Confirm, Run = _ => cmd.Run(),
            });
        }

        foreach (var page in SettingsPages)
        {
            var score = Math.Max(FuzzyMatcher.Score(q, page.Title), page.Keywords.Select(k => FuzzyMatcher.Score(q, k) - 60).DefaultIfEmpty(0).Max());
            if (score <= 0) continue;
            results.Add(new LauncherResult
            {
                Id = "ms-settings:" + page.Uri, Title = page.Title, Subtitle = "Windows Settings", Kind = "Setting", Glyph = "",
                Score = score - 60, Run = _ => Shell("ms-settings:" + page.Uri),
            });
        }

        if (S.SearchWindows) results.AddRange(WindowResults(q, launcherWindow, 0));

        Boost(results);
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        // Letters scattered through a name ("code" in "Steps Recorder") only help when nothing better matched.
        if (results.Count(r => r.Score >= ScatteredLimit + 150) >= 2) results.RemoveAll(r => r.Score < ScatteredLimit);
        var top = results.Take(S.MaxResults).ToList();
        top.AddRange(WebResults(q));
        return top;
    }

    /// "?" files only; otherwise files are added below the instant results when they rank well enough.
    public bool WantsFiles(string text)
    {
        var q = text.Trim();
        return S.SearchFiles && (q.StartsWith('?') ? q.Length > 2 : q.Length >= 2 && !q.StartsWith('<') && !Calculator.TryEvaluate(q, out _));
    }

    /// Windows-only results for the "<" prefix.
    public List<LauncherResult> WindowsOnly(string text, IntPtr launcherWindow)
    {
        var q = text.Trim().TrimStart('<').Trim();
        var list = WindowResults(q, launcherWindow, q.Length == 0 ? 1 : 0).ToList();
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return list.Take(Math.Max(S.MaxResults, 12)).ToList();
    }

    public async Task<List<LauncherResult>> FilesAsync(string text, CancellationToken ct)
    {
        var q = text.Trim().TrimStart('?').Trim();
        var found = await FileSearch.FindAsync(q, S.MaxResults, ct);
        var list = new List<LauncherResult>();
        foreach (var f in found)
        {
            var score = FuzzyMatcher.Score(q, f.Name);
            if (score == 0) score = 100; // Matched through the index on a word inside the name.
            var path = f.Path;
            list.Add(new LauncherResult
            {
                Id = "file:" + path, Title = f.Name, Subtitle = path, Kind = f.IsFolder ? "Folder" : "File",
                IconSource = path, CopyText = path, CanReveal = true, CanRunAsAdmin = IsExecutable(path),
                Score = score - 150,
                Run = mode =>
                {
                    if (mode == LaunchMode.Reveal) Reveal(path);
                    else if (mode == LaunchMode.Admin) RunAsAdmin(path);
                    else Shell(path);
                },
            });
        }
        Boost(list);
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return list.Take(S.MaxResults).ToList();
    }

    /// Counts a pick so it ranks higher next time.
    public void Record(LauncherResult r)
    {
        if (!r.Track) return;
        S.Usage[r.Id] = S.Usage.GetValueOrDefault(r.Id) + 1;
        // Keep the table small: drop the least used once it grows past 300 entries.
        if (S.Usage.Count > 300)
            foreach (var k in S.Usage.OrderBy(p => p.Value).Take(50).Select(p => p.Key).ToList()) S.Usage.Remove(k);
        _settings.Save();
    }

    private void Boost(List<LauncherResult> list)
    {
        foreach (var r in list)
            if (S.Usage.TryGetValue(r.Id, out var n)) r.Score += Math.Min(250, 40 + n * 15);
    }

    /// With nothing typed: the apps opened most often.
    private List<LauncherResult> Suggestions()
    {
        var byId = Apps.Apps.GroupBy(a => "app:" + a.ParsingName).ToDictionary(g => g.Key, g => g.First());
        return S.Usage.OrderByDescending(p => p.Value)
            .Where(p => byId.ContainsKey(p.Key)).Take(Math.Min(6, S.MaxResults))
            .Select(p => AppResult(byId[p.Key], 0)).ToList();
    }

    private static LauncherResult AppResult(AppEntry app, int score)
    {
        // Elevating and "show in folder" both work on the app's real file (exe or Start menu shortcut).
        var file = app.FilePath;
        var isExe = file is not null && !file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        return new LauncherResult
        {
            Id = "app:" + app.ParsingName, Title = app.Name, Kind = "App",
            Subtitle = isExe ? file! : app.ParsingName.Contains('!') ? "Microsoft Store app" : "Application",
            IconSource = app.ShellPath, Score = score, CanReveal = file is not null, CanRunAsAdmin = file is not null,
            CopyText = isExe ? file : null,
            Run = mode =>
            {
                if (mode == LaunchMode.Reveal && file is not null) Reveal(file);
                else if (mode == LaunchMode.Admin && file is not null) RunAsAdmin(file);
                else Shell(app.ShellPath);
            },
        };
    }

    private IEnumerable<LauncherResult> WindowResults(string q, IntPtr launcherWindow, int baseScore)
    {
        foreach (var w in WindowList.Enumerate(launcherWindow))
        {
            // The title decides; the process name ("Code", "chrome") only finds a window whose title doesn't
            // match, and then ranks it well below apps of that name.
            var byTitle = FuzzyMatcher.Score(q, w.Title);
            var score = q.Length == 0 ? baseScore : byTitle > 0 ? byTitle : Math.Max(0, FuzzyMatcher.Score(q, w.ProcessName) - 400);
            if (score == 0 && q.Length > 0) continue;
            var h = w.Handle;
            yield return new LauncherResult
            {
                Id = "win:" + w.ProcessName, Title = w.Title, Subtitle = $"Switch to window · {w.ProcessName}", Kind = "Window",
                IconSource = w.ExePath, Glyph = "", Score = score - 120, Track = false,
                Run = _ => WindowList.Activate(h),
            };
        }
    }

    private IEnumerable<LauncherResult> WebResults(string q)
    {
        if (LooksLikeUrl(q, out var url))
            yield return new LauncherResult
            {
                Id = "url", Title = $"Open {url}", Subtitle = "In your default browser", Kind = "Web", Glyph = "",
                Track = false, CopyText = url, Run = _ => Shell(url),
            };
        var (engine, template) = S.SearchEngine.ToLowerInvariant() switch
        {
            "bing" => ("Bing", "https://www.bing.com/search?q={q}"),
            "duckduckgo" => ("DuckDuckGo", "https://duckduckgo.com/?q={q}"),
            var custom when custom.Contains("{q}") => ("the web", S.SearchEngine),
            _ => ("Google", "https://www.google.com/search?q={q}"),
        };
        var search = template.Replace("{q}", Uri.EscapeDataString(q));
        yield return new LauncherResult
        {
            Id = "web", Title = $"Search {engine} for “{q}”", Subtitle = "In your default browser", Kind = "Web", Glyph = "",
            Track = false, CopyText = search, Run = _ => Shell(search),
        };
    }

    public static bool LooksLikeUrl(string q, out string url)
    {
        url = q;
        if (q.Contains(' ')) return false;
        if (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(q, UriKind.Absolute, out _);
        // example.com, sub.example.co.uk/path: letters before a dot and a 2+ letter ending.
        if (!System.Text.RegularExpressions.Regex.IsMatch(q, @"^(localhost(:\d+)?|[\w-]+(\.[\w-]+)*\.[a-zA-Z]{2,}(:\d+)?)(/\S*)?$")) return false;
        url = "https://" + q;
        return true;
    }

    // ---------------- Opening things ----------------

    /// Opens through the unelevated shell: see the class comment.
    public static void Shell(string target)
    {
        try
        {
            // explorer.exe silently drops a web address with a query string (?q=...), so web addresses go
            // through a temporary Internet shortcut, which explorer opens in the default browser, unelevated.
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                target = WebShortcut(target);
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(target)) { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("launcher open", ex); }
    }

    /// Writes url to a fresh .url file and returns its path. Shortcuts older than a minute are cleared away,
    /// long after explorer has read them.
    private static string WebShortcut(string url)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCEssentials", "open");
        System.IO.Directory.CreateDirectory(dir);
        foreach (var old in System.IO.Directory.EnumerateFiles(dir, "*.url"))
            try { if (System.IO.File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddMinutes(-1)) System.IO.File.Delete(old); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        var path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".url");
        System.IO.File.WriteAllText(path, $"[InternetShortcut]\r\nURL={url}\r\n");
        return path;
    }

    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,{Quote(path)}") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error("launcher reveal", ex); }
    }

    private static void RunAsAdmin(string target)
    {
        // From an elevated PC Essentials this starts the app elevated straight away; otherwise UAC asks.
        // An exe starts in its own folder, as it would from a shortcut; a shortcut brings its own.
        var dir = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? "" : System.IO.Path.GetDirectoryName(target) ?? "";
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir }); }
        catch (System.ComponentModel.Win32Exception) { /* UAC prompt declined */ }
        catch (Exception ex) { Log.Error("launcher admin", ex); }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private static bool IsExecutable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    private static void CopyToClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { System.Windows.Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(30); }
        }
    }

    // ---------------- System commands and settings pages ----------------

    private sealed record SystemCommand(string Title, string Subtitle, string Glyph, bool Confirm, Action Run, params string[] Keywords);

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    private static void StartProcess(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("launcher start", ex); }
    }

    private static void RunShutdown(string args)
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Log.Error("launcher shutdown", ex); }
    }

    private static readonly SystemCommand[] SystemCommands =
    [
        new("Lock", "Lock this PC", "", false, () => LockWorkStation(), "lock screen", "lock pc"),
        new("Sleep", "Put this PC to sleep", "", false, () => SetSuspendState(false, false, false), "suspend"),
        new("Restart", "Restart this PC now", "", true, () => RunShutdown("/r /t 0"), "reboot"),
        new("Shut down", "Turn this PC off now", "", true, () => RunShutdown("/s /t 0"), "shutdown", "power off", "turn off"),
        new("Sign out", "Sign out of Windows", "", true, () => RunShutdown("/l"), "log off", "logout", "log out"),
        new("Empty Recycle Bin", "Permanently delete everything in the Recycle Bin", "", true,
            () => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 /* no confirmation dialog: the launcher asked */), "recycle bin", "trash"),
        new("Open Recycle Bin", "Show the Recycle Bin", "", false, () => Shell("shell:RecycleBinFolder"), "recycle bin", "trash"),
        new("Task Manager", "See running apps and performance", "", false, () => StartProcess("taskmgr.exe"), "processes", "kill"),
    ];

    private sealed record SettingsPage(string Title, string Uri, params string[] Keywords);

    private static readonly SettingsPage[] SettingsPages =
    [
        new("Display settings", "display", "screen", "resolution", "scale", "brightness", "monitor"),
        new("Night light", "nightlight", "blue light"),
        new("Sound settings", "sound", "audio", "speaker", "microphone", "volume"),
        new("Notifications", "notifications", "do not disturb", "focus"),
        new("Power & battery", "powersleep", "battery", "power plan", "sleep settings"),
        new("Storage", "storagesense", "disk space", "storage sense"),
        new("Multitasking", "multitasking", "snap", "alt tab"),
        new("Clipboard settings", "clipboard"),
        new("About this PC", "about", "system info", "pc name", "specs", "windows version"),
        new("Bluetooth & devices", "bluetooth", "devices", "pair"),
        new("Printers & scanners", "printers", "printer", "scanner"),
        new("Mouse settings", "mousetouchpad", "mouse", "pointer speed"),
        new("Touchpad", "devices-touchpad", "trackpad", "gestures"),
        new("Network & internet", "network", "internet", "ethernet"),
        new("Wi-Fi", "network-wifi", "wifi", "wireless"),
        new("VPN", "network-vpn", "vpn"),
        new("Proxy", "network-proxy", "proxy"),
        new("Personalization", "personalization", "theme"),
        new("Background", "personalization-background", "wallpaper", "desktop background"),
        new("Colors", "colors", "dark mode", "light mode", "accent color"),
        new("Lock screen settings", "lockscreen", "lock screen"),
        new("Taskbar settings", "taskbar", "taskbar"),
        new("Start menu settings", "personalization-start", "start menu"),
        new("Fonts", "fonts", "font"),
        new("Installed apps", "appsfeatures", "uninstall", "programs", "apps and features"),
        new("Default apps", "defaultapps", "default browser", "open with"),
        new("Startup apps", "startupapps", "startup", "autostart"),
        new("Accounts", "accounts", "your info", "profile"),
        new("Sign-in options", "signinoptions", "password", "pin", "windows hello"),
        new("Date & time", "dateandtime", "time zone", "clock"),
        new("Language & region", "regionlanguage", "language", "region", "keyboard layout"),
        new("Typing", "typing", "autocorrect", "spell"),
        new("Gaming", "gaming-gamebar", "game bar", "game mode"),
        new("Accessibility", "easeofaccess", "ease of access"),
        new("Privacy & security", "privacy", "permissions"),
        new("Windows Security", "windowsdefender", "antivirus", "defender", "firewall"),
        new("Windows Update", "windowsupdate", "updates", "update"),
        new("Recovery", "recovery", "reset this pc"),
        new("Developer settings", "developers", "developer mode"),
    ];
}
