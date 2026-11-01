using System.IO;

namespace PCEssentials.Core;

/// Small local log at %APPDATA%\PCEssentials\pc-essentials.log. Nothing leaves the PC.
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Paths.DataDir, "pc-essentials.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string area, Exception ex) => Write("ERROR", $"{area}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}

public static class Paths
{
    public static string DataDir { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCEssentials")).FullName;

    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
