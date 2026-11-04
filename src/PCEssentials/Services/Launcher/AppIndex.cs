using System.Diagnostics;
using System.IO;
using PCEssentials.Core;

namespace PCEssentials.Services.Launcher;

/// One installed app as the Start menu's "All apps" list knows it.
/// ParsingName is the AppsFolder id: an AUMID for Store apps, or a path/known-folder id for desktop apps.
public sealed record AppEntry(string Name, string ParsingName)
{
    public string ShellPath => @"shell:AppsFolder\" + ParsingName;

    /// A real file behind the app (its exe, or its Start menu shortcut), or null for Store apps, URLs and
    /// games launched through steam://. Elevation needs it: Windows ignores "run as administrator" on a
    /// shell:AppsFolder id and starts the app normally.
    public string? FilePath { get; init; }
}

/// The Start menu's app list (shell:AppsFolder), which covers desktop shortcuts and Store apps alike.
/// Read on a background STA thread at start and again when the launcher opens after 5 minutes.
public sealed class AppIndex
{
    private volatile IReadOnlyList<AppEntry> _apps = [];
    private DateTime _loaded = DateTime.MinValue;
    private int _loading;

    public IReadOnlyList<AppEntry> Apps => _apps;
    public event Action? Updated;

    public void RefreshIfStale()
    {
        if (DateTime.UtcNow - _loaded < TimeSpan.FromMinutes(5)) return;
        Refresh();
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var list = Enumerate();
                if (list.Count > 0)
                {
                    _apps = list;
                    _loaded = DateTime.UtcNow;
                    Log.Info($"launcher: {list.Count} apps indexed in {sw.ElapsedMilliseconds} ms");
                    Updated?.Invoke();
                }
            }
            catch (Exception ex) { Log.Error("launcher apps", ex); }
            finally { Interlocked.Exchange(ref _loading, 0); }
        }) { IsBackground = true, Name = "Launcher app index" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static List<AppEntry> Enumerate()
    {
        var result = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return result;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic? folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return result;
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                // Uninstallers, readmes and help links clutter results without being apps anyone launches.
                if (IsClutter(name, path)) continue;
                if (!seen.Add(name + "|" + path)) continue;
                result.Add(new AppEntry(name, path));
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        var shortcuts = StartMenuShortcuts();
        for (int i = 0; i < result.Count; i++)
            result[i] = result[i] with { FilePath = ResolveFile(result[i], shortcuts) };
        return result;
    }

    /// The app's own file when its id is a path ("C:\…\app.exe" or "{known folder id}\cmd.exe"),
    /// otherwise the Start menu shortcut with the same name, which is where AppsFolder found it.
    private static string? ResolveFile(AppEntry app, Dictionary<string, string> shortcuts)
    {
        var id = app.ParsingName;
        if (id.Length > 3 && id[1] == ':' && File.Exists(id)) return id;
        if (id.StartsWith('{') && id.IndexOf("}\\", StringComparison.Ordinal) is var end and > 0
            && Guid.TryParse(id[1..end], out var folderId) && KnownFolder(folderId) is { } folder)
        {
            var full = Path.Combine(folder, id[(end + 2)..]);
            if (File.Exists(full)) return full;
        }
        // Store apps (Package!App) have no shortcut, and URLs are not programs.
        if (id.Contains('!') || id.Contains("://")) return null;
        return shortcuts.GetValueOrDefault(app.Name);
    }

    /// Start menu shortcuts by display name, the user's own first.
    private static Dictionary<string, string> StartMenuShortcuts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    map.TryAdd(Path.GetFileNameWithoutExtension(lnk), lnk);
            }
            catch (Exception ex) { Log.Error("launcher shortcuts", ex); }
        }
        return map;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr); }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr); }
    }

    private static bool IsClutter(string name, string path)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("uninstall") || n.Contains(" uninstall") || n.StartsWith("readme") || n.EndsWith(" help")
            || n.EndsWith(" documentation") || n.EndsWith(" website") || n.EndsWith(" release notes")) return true;
        var p = path.ToLowerInvariant();
        return p.EndsWith(".url") || p.EndsWith(".chm") || p.EndsWith(".txt") || p.EndsWith(".pdf") || p.EndsWith(".html") || p.EndsWith(".htm");
    }
}
