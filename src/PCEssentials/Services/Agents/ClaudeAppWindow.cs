using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using PCEssentials.Core;

namespace PCEssentials.Services.Agents;

/// Reads the Claude desktop app's own usage indicator ("Usage: 20% of 5-hour limit, Resets at 11:00 PM")
/// through UI Automation, the read-only accessibility interface screen readers use. It is the exact
/// figure the app shows, from the server answer only the app can ask for; no sign-in or token is
/// touched. Read on a background thread, about once a minute, only while the app has a window.
public static partial class ClaudeAppWindow
{
    public sealed record Reading(LimitWindow? Session, LimitWindow? Weekly, DateTime At);

    [GeneratedRegex(@"^Usage:\s*(?<pct>\d+(?:\.\d+)?)%\s+of\s+(?<kind>.+?)\s+limit(?:,\s*Resets\s+(?:at\s+)?(?<when>.+?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UsagePattern();

    /// Null when the app has no window or shows no usage indicator.
    public static Reading? Read()
    {
        try
        {
            LimitWindow? session = null, weekly = null;
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var win = AutomationElement.FromHandle(p.MainWindowHandle);
                    var buttons = win.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement b in buttons)
                    {
                        var name = b.Current.Name;
                        if (string.IsNullOrEmpty(name) || !name.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase)) continue;
                        var m = UsagePattern().Match(name);
                        if (!m.Success) continue;
                        var pct = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture);
                        var kind = m.Groups["kind"].Value.ToLowerInvariant();
                        var resets = ParseWhen(m.Groups["when"].Value);
                        if (kind.Contains("5-hour") || kind.Contains("session")) session = new LimitWindow("Session", pct, resets);
                        else if (kind.Contains("week")) weekly = new LimitWindow("Weekly", pct, resets);
                    }
                }
            }
            return session is null && weekly is null ? null : new Reading(session, weekly, DateTime.Now);
        }
        catch (Exception ex)
        {
            Log.Error("claude app window", ex);
            return null;
        }
    }

    /// "11:00 PM" (today, or tomorrow once past), "Thu 9:00 AM", "Nov 7, 9:00 AM" and the like.
    private static DateTime? ParseWhen(string text)
    {
        text = text.Trim().TrimEnd('.');
        if (text.Length == 0) return null;
        var en = CultureInfo.GetCultureInfo("en-US");
        var now = DateTime.Now;
        if (DateTime.TryParse(text, en, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var t))
        {
            // Time only: the next time the clock reads it.
            if (t.Date == DateTime.MinValue.Date)
            {
                var at = now.Date + t.TimeOfDay;
                return at < now.AddMinutes(-1) ? at.AddDays(1) : at;
            }
            return t;
        }
        // "Thu 9:00 AM": the next such weekday.
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && DateTime.TryParse(parts[1], en, DateTimeStyles.NoCurrentDateDefault, out var time))
        {
            for (var i = 0; i < 8; i++)
            {
                var day = now.Date.AddDays(i);
                if (day.ToString("ddd", en).Equals(parts[0][..Math.Min(3, parts[0].Length)], StringComparison.OrdinalIgnoreCase)
                    && day + time.TimeOfDay > now)
                    return day + time.TimeOfDay;
            }
        }
        return null;
    }
}
