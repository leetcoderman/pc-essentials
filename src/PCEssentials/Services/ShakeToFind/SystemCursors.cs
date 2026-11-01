using System.IO;
using System.Runtime.InteropServices;
using PCEssentials.Core;

namespace PCEssentials.Services.ShakeToFind;

/// Hides the pointer everywhere by swapping every standard system cursor for a blank one, and puts the
/// user's own cursors back with SPI_SETCURSORS, which reloads them from the registry at the user's size.
/// Nothing is written to the registry, so the pointer size and scheme settings are never touched.
/// A marker file is written while hidden, so a crash or a forced kill is undone at the next start.
internal static class SystemCursors
{
    private const uint SPI_SETCURSORS = 0x0057;
    // OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP, PIN, PERSON.
    private static readonly uint[] Ids = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672];

    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr data, uint winIni);
    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr inst, int x, int y, int w, int h, byte[] and, byte[] xor);

    private static readonly object Gate = new();
    private static readonly string Marker = Path.Combine(Paths.DataDir, "cursor-hidden");
    private static bool _hidden;

    public static bool Hidden { get { lock (Gate) return _hidden; } }

    public static void Hide()
    {
        lock (Gate)
        {
            if (_hidden) return;
            try { File.WriteAllText(Marker, DateTime.Now.ToString("O")); } catch { }
            _hidden = true;
            var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
            var xor = new byte[32 * 32 / 8];
            // SetSystemCursor takes ownership of the handle, so each id gets its own blank cursor.
            foreach (var id in Ids)
            {
                var blank = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
                if (blank != IntPtr.Zero) SetSystemCursor(blank, id);
            }
        }
    }

    public static void Restore()
    {
        lock (Gate)
        {
            if (!_hidden) return;
            _hidden = false;
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            try { File.Delete(Marker); } catch { }
        }
    }

    /// Run at startup: if a previous run left the cursors hidden, bring them back.
    public static void RestoreIfLeftHidden()
    {
        if (!File.Exists(Marker)) return;
        Log.Info("shake to find: restoring cursors left hidden by a previous run");
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        try { File.Delete(Marker); } catch { }
    }
}
