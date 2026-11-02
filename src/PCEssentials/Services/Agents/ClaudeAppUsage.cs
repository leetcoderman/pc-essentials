using System.IO;
using System.Text.Json;
using PCEssentials.Core;

namespace PCEssentials.Services.Agents;

/// Port of the Mac app's AgentClaudeAppUsage. The Claude desktop app checks the plan's limits itself
/// every few minutes and keeps the readings (percent used per window) in plan-usage-history.json.
/// That is the number the app's own "Your usage limits" shows, so it is preferred for the account
/// the app is signed in to. Read locally; nothing is sent.
public static class ClaudeAppUsage
{
    /// The app checks every five to fifteen minutes; an older reading has missed a check.
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Session = TimeSpan.FromHours(5);

    public sealed record Sample(DateTime Time, string? Org, Dictionary<string, double> Used);

    /// The Claude app's data folder differs by installer (classic, MSIX); the newest file wins.
    public static string? HistoryPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string> { Path.Combine(roaming, "Claude", "plan-usage-history.json") };
        try
        {
            var data = Path.Combine(local, "Claude-Data");
            if (Directory.Exists(data))
                candidates.AddRange(Directory.GetDirectories(data).Select(d => Path.Combine(d, "plan-usage-history.json")));
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
                candidates.AddRange(Directory.GetDirectories(packages, "Claude_*").Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude", "plan-usage-history.json")));
        }
        catch (IOException) { }
        return candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    }

    /// The account the app is signed in to (its config.json sits beside the readings), or null.
    public static string? Account()
    {
        try
        {
            var path = HistoryPath() is { } history ? Path.Combine(Path.GetDirectoryName(history)!, "config.json") : null;
            return path is not null && File.Exists(path) ? ClaudeAccounts.AppAccount(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
    }

    public static List<Sample>? Read()
    {
        var path = HistoryPath();
        if (path is null || new FileInfo(path).Length > 4 << 20) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (version is not (1 or 2) || !root.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Sample>();
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                // The first version kept its windows beside the time.
                var values = version == 1 ? e : e.TryGetProperty("u", out var u) ? u : default;
                var used = new Dictionary<string, double>();
                if (values.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "fh", "sd", "so", "sn" })
                        if (values.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number)
                            used[key] = Math.Clamp(n.GetDouble(), 0, 100);
                var org = e.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                list.Add(new Sample(DateTimeOffset.FromUnixTimeMilliseconds((long)t.GetDouble()).LocalDateTime, org, used));
            }
            return list.OrderBy(s => s.Time).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("claude app usage", ex);
            return null;
        }
    }

    /// The latest reading as limit windows, when it is fresh. The file has percentages only: the
    /// session renews five hours after its first request, which the readings bracket and Claude
    /// Code's own first request inside that bracket pins down. A week's renewal is not known.
    /// The readings name only the organization, which a Team's seats share, so those from before the
    /// app last changed account (since) are left out.
    public static (LimitWindow? Session, LimitWindow? Weekly, DateTime At)? Limits(string? org, IEnumerable<DateTime> claudeRequests, DateTime since)
    {
        var all = Read();
        if (all is null) return null;
        var history = all.Where(s => s.Time >= since && (org is null || s.Org is null || s.Org == org)).ToList();
        if (history.Count == 0) return null;
        var latest = history[^1];
        var now = DateTime.Now;
        if (now - latest.Time > Freshness) return null;

        LimitWindow? session = null;
        if (latest.Used.TryGetValue("fh", out var fh))
        {
            DateTime? resets = null;
            if (fh > 0)
            {
                // The run of non-zero readings ending with the latest; a drop is a renewal too.
                var first = history.Count - 1;
                while (first > 0)
                {
                    var prev = history[first - 1];
                    if (!prev.Used.TryGetValue("fh", out var p) || p <= 0 || p > history[first].Used.GetValueOrDefault("fh")) break;
                    if (latest.Time - prev.Time >= Session) break;
                    first--;
                }
                var latestStart = history[first].Time;
                var earliestStart = first > 0 ? history[first - 1].Time : latestStart - Session;
                var request = claudeRequests.Where(t => t > earliestStart && t <= latestStart).DefaultIfEmpty().Min();
                resets = (request != default ? request : latestStart) + Session;
                if (resets <= now) resets = null;
            }
            session = new LimitWindow("Session", fh, resets, Estimated: true);
        }
        LimitWindow? weekly = null;
        foreach (var (key, label) in new[] { ("sd", "Weekly"), ("so", "Weekly · Opus"), ("sn", "Weekly · Sonnet") })
            if (latest.Used.TryGetValue(key, out var w) && (weekly is null || w > weekly.UsedPercent))
                weekly = new LimitWindow(label, w, null);
        return (session, weekly, latest.Time);
    }
}
