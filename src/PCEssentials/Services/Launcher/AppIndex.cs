using System.Diagnostics;
using PCEssentials.Core;

namespace PCEssentials.Services.Launcher;

/// One installed app as the Start menu's "All apps" list knows it.
/// ParsingName is the AppsFolder id: an AUMID for Store apps, or a path/known-folder id for desktop apps.
public sealed record AppEntry(string Name, string ParsingName)
{
    public string ShellPath => @"shell:AppsFolder\" + ParsingName;
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
        return result;
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
