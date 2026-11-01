namespace PCEssentials.Services;

/// Pure helpers for turning Core Audio sessions into mixer rows, kept free of COM so they can be unit tested.
/// Rows group sessions by app across every output device, the way Windows' own volume mixer does.
public static class AudioSessionKeys
{
    public const string System = "system";

    /// The exe file name inside a session identifier, or null for the system sounds session.
    /// Identifiers look like "{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Apps\chrome.exe%b{00000000-...}".
    public static string? ExeFromIdentifier(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var bar = id.IndexOf('|');
        if (bar < 0) return null;
        var rest = id[(bar + 1)..];
        var end = rest.IndexOf("%b", StringComparison.Ordinal);
        if (end >= 0) rest = rest[..end];
        if (rest.Length == 0 || rest[0] == '#') return null;
        var file = rest[(rest.LastIndexOfAny(['\\', '/']) + 1)..];
        return file.Length == 0 ? null : file;
    }

    /// The row an app's sessions share: the exe name, lower case and without ".exe". Null when nothing identifies it.
    public static string? Key(bool isSystem, string? sessionIdentifier, string? processName)
    {
        if (isSystem) return System;
        var exe = ExeFromIdentifier(sessionIdentifier);
        var name = exe is not null ? StripExe(exe) : processName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();
    }

    /// What the row is called: the name the app gave its session, else the exe's description, else its file name.
    /// Display names starting with "@" are resource references (the system sounds session uses one).
    public static string DisplayName(string? sessionName, string? fileDescription, string? processName, string? sessionIdentifier)
    {
        if (!string.IsNullOrWhiteSpace(sessionName) && !sessionName.StartsWith('@')) return sessionName.Trim();
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription.Trim();
        if (!string.IsNullOrWhiteSpace(processName)) return processName;
        return ExeFromIdentifier(sessionIdentifier) is { } exe ? StripExe(exe) : "Unknown app";
    }

    /// Changes whenever the rows on screen would: an app appears or leaves, gains a session, or starts or stops playing.
    public static string Signature(IEnumerable<(string Key, int Sessions, bool Active)> apps) =>
        string.Join("|", apps.Select(a => $"{a.Key}:{a.Sessions}:{(a.Active ? 1 : 0)}"));

    private static string StripExe(string file) =>
        file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
}
