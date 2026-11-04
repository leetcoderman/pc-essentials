using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PCEssentials.Core;

namespace PCEssentials.Services.Agents;

/// One Claude account on this PC: the usual profile (~/.claude) or a Team seat signed in to a
/// profile folder of its own (~/.claude-<name>, Claude Code's CLAUDE_CONFIG_DIR).
public sealed class ClaudeSeat
{
    public required string Id { get; init; }
    public required string ConfigDir { get; init; }
    public required string ProfilePath { get; init; }
    public bool IsDefault { get; init; }
    public string? Email { get; set; }
    public string? OrgName { get; set; }
    public string? OrgId { get; set; }
    /// The account's own id; Team seats share the organization's.
    public string? AccountId { get; set; }
    public string? PlanName { get; set; }
    public double? PlanMonthly { get; set; }
    public LimitWindow? Session { get; set; }
    public LimitWindow? Weekly { get; set; }
    /// When Anthropic gave the reading shown, which a turned-away check leaves where it was.
    public DateTime? CheckedAt { get; set; }
    /// When the last check ran, whatever came of it; the next one is due five minutes later.
    public DateTime? TriedAt { get; set; }
    public string? Error { get; set; }
    public bool SignedIn => Email is not null;
}

/// Port of the Mac app's ClaudeSeatProfile + ClaudeSeatLimitsService. Each seat's limits come from
/// the official Claude Code CLI run against that seat's profile with a no-model usage request (the
/// CLI signs in and talks to Anthropic itself; PC Essentials never reads a token), falling back to the
/// usage copy Claude Code saves in the profile. One seat is checked at a time, every 5 minutes.
public sealed class ClaudeSeatService : IDisposable
{
    public const string Prefix = ".claude-";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
    private readonly PriceList _prices;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private List<ClaudeSeat> _seats = new();
    private int _busy;
    private DateTime _forceAfter = DateTime.MaxValue;

    public event Action? Changed;

    public ClaudeSeatService(PriceList prices)
    {
        _prices = prices;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(20));
    }

    public List<ClaudeSeat> Seats
    {
        get { lock (_gate) return _seats.ToList(); }
    }

    /// Check every seat again soon (the agents page just opened), if the last check is a minute old.
    public void RefreshSoon()
    {
        _forceAfter = DateTime.Now.AddMinutes(-1);
        ThreadPool.QueueUserWorkItem(_ => Tick());
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Discover();
            var force = _forceAfter;
            // A seat on the main profile's account is shown as the main profile, so checking it too only doubles the requests.
            var merged = ClaudeAccounts.MergedIntoMain(Seats.Select(s => (s.Id, s.AccountId, s.IsDefault)));
            var due = Seats.Where(s => s.SignedIn && !merged.ContainsKey(s.Id) && (s.TriedAt is null || DateTime.Now - s.TriedAt > CheckEvery || s.TriedAt < force)).ToList();
            _forceAfter = DateTime.MaxValue;
            foreach (var seat in due)
            {
                CheckLive(seat);
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("claude seats", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// The usual profile, then every ~/.claude-<name> folder in name order.
    private void Discover()
    {
        var found = new List<ClaudeSeat>
        {
            new() { Id = "default", ConfigDir = Path.Combine(Paths.Home, ".claude"), ProfilePath = Path.Combine(Paths.Home, ".claude.json"), IsDefault = true },
        };
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Paths.Home, Prefix + "*").Order(StringComparer.OrdinalIgnoreCase))
                found.Add(new ClaudeSeat { Id = Path.GetFileName(dir), ConfigDir = dir, ProfilePath = Path.Combine(dir, ".claude.json") });
        }
        catch (IOException) { }

        var changed = false;
        lock (_gate)
        {
            var next = new List<ClaudeSeat>();
            foreach (var f in found)
            {
                // Keep what was learned about a seat across rediscovery.
                var seat = _seats.FirstOrDefault(s => s.Id == f.Id) ?? f;
                ReadProfile(seat);
                next.Add(seat);
            }
            changed = next.Count != _seats.Count || next.Zip(_seats).Any(p => p.First.Id != p.Second.Id || p.First.Email != p.Second.Email);
            _seats = next;
        }
        if (changed) Changed?.Invoke();
    }

    /// Who the profile is signed in as, its plan, and the usage copy Claude Code saved in it.
    private void ReadProfile(ClaudeSeat seat)
    {
        try
        {
            if (!File.Exists(seat.ProfilePath)) { seat.Email = null; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(seat.ProfilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("oauthAccount", out var acct) && acct.ValueKind == JsonValueKind.Object)
            {
                seat.Email = Str(acct, "emailAddress");
                seat.OrgName = Str(acct, "organizationName");
                seat.OrgId = Str(acct, "organizationUuid");
                seat.AccountId = Str(acct, "accountUuid");
                // A Team or Enterprise organization names the plan, whatever the seat's own tier says.
                var orgType = Str(acct, "organizationType") ?? "";
                var plan = (orgType.Contains("team") || orgType.Contains("enterprise") ? _prices.Plan(AgentProvider.Claude, orgType) : null)
                        ?? _prices.Plan(AgentProvider.Claude, Str(acct, "userRateLimitTier"))
                        ?? _prices.Plan(AgentProvider.Claude, Str(acct, "organizationRateLimitTier"))
                        ?? _prices.Plan(AgentProvider.Claude, orgType);
                seat.PlanName = plan?.Name;
                seat.PlanMonthly = plan?.Monthly;
            }
            else { seat.Email = null; seat.AccountId = null; }

            // The saved copy only counts while it is newer than the last live reading.
            if (SavedUsage(root) is ({ } fetched, var u) && fetched > (seat.CheckedAt ?? DateTime.MinValue))
            {
                (seat.Session, seat.Weekly) = ParseLimits(u);
                seat.CheckedAt = fetched;
            }
        }
        catch (IOException) { }
        catch (Exception ex) { Log.Error("claude seat profile", ex); }
    }

    /// The usage copy Claude Code saves in a profile each time it reaches Anthropic, and when.
    private static (DateTime? At, JsonElement Utilization) SavedUsage(JsonElement root)
    {
        if (!root.TryGetProperty("cachedUsageUtilization", out var cache) || !cache.TryGetProperty("utilization", out var u)) return (null, default);
        var at = cache.TryGetProperty("fetchedAtMs", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms.GetInt64()).LocalDateTime : DateTime.MinValue;
        return (at, u);
    }

    private static DateTime? SavedAt(string profilePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
            return SavedUsage(doc.RootElement).At;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// Asks Claude Code itself for the seat's usage, the way the Mac app does: an initialize and a
    /// get_usage control request over stream-json, no model call, no tools, no MCP servers, no hooks.
    private void CheckLive(ClaudeSeat seat)
    {
        var started = DateTime.Now;
        // Stamped up front, so a failed check waits its turn like any other instead of running every tick.
        seat.TriedAt = started;
        var exe = FindClaude();
        if (exe is null)
        {
            seat.Error = "Claude Code isn't installed, so limits come from its saved copy.";
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe,
                "--print --input-format stream-json --output-format stream-json --verbose --no-session-persistence --tools \"\" " +
                "--strict-mcp-config --mcp-config \"{\\\"mcpServers\\\":{}}\" --setting-sources \"\" --settings \"{\\\"disableAllHooks\\\":true}\"")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetTempPath(),
            };
            psi.Environment["DISABLE_AUTOUPDATER"] = "1";
            // Started from a Claude session, PC Essentials inherits that session's sign-in and endpoint
            // (CLAUDE_CODE_*, ANTHROPIC_*), which would answer for that account instead of the seat's.
            foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(key);
            if (seat.IsDefault) psi.Environment.Remove("CLAUDE_CONFIG_DIR");
            else psi.Environment["CLAUDE_CONFIG_DIR"] = seat.ConfigDir;

            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            p.StandardInput.WriteLine("{\"type\":\"control_request\",\"request_id\":\"initialize\",\"request\":{\"subtype\":\"initialize\"}}");
            p.StandardInput.WriteLine("{\"type\":\"control_request\",\"request_id\":\"usage\",\"request\":{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}");
            p.StandardInput.Flush();
            var deadline = DateTime.Now.AddSeconds(45);
            string? answer = null;
            while (DateTime.Now < deadline)
            {
                var read = p.StandardOutput.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(5))) { if (p.HasExited) break; continue; }
                if (read.Result is not { } line) break;
                if (line.Contains("\"request_id\":\"usage\"")) { answer = line; break; }
            }
            // Let it exit by itself first: it saves the fresh usage copy that tells a live answer from a stale one.
            try { p.StandardInput.Close(); if (!p.WaitForExit(5000)) p.Kill(true); } catch { }

            if (answer is null) { seat.Error = "Claude Code didn't answer the usage check."; return; }
            using var doc = JsonDocument.Parse(answer);
            var r = doc.RootElement.GetProperty("response");
            if (Str(r, "subtype") == "error" || !r.TryGetProperty("response", out var body))
            {
                seat.Error = Str(r, "error") ?? "Claude Code couldn't read this seat's usage. Is it signed in?";
                return;
            }
            if (!body.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) return;
            (seat.Session, seat.Weekly) = ParseLimits(rl);
            // A turned-away check answers with the saved copy, as old as it is: keep its own time, and say so.
            var savedAt = SavedAt(seat.ProfilePath);
            if (ClaudeAccounts.AnsweredLive(started, savedAt))
            {
                seat.CheckedAt = savedAt;
                seat.Error = null;
            }
            else
            {
                seat.CheckedAt = savedAt ?? seat.CheckedAt;
                seat.Error = "Anthropic turned this seat's usage check away (too many checks), so this is its last reading"
                    + (savedAt is { } at && at > DateTime.MinValue ? $", from {at:HH:mm}." : ".");
            }
        }
        catch (Exception ex)
        {
            seat.Error = "Couldn't run Claude Code for this seat.";
            Log.Error("claude seat check", ex);
        }
    }

    /// The session window and the most used weekly window, from either the live answer or the saved copy.
    public static (LimitWindow? Session, LimitWindow? Weekly) ParseLimits(JsonElement u)
    {
        var session = Window(u, "five_hour", "Session");
        var weekly = Window(u, "seven_day", "Weekly") ?? Window(u, "seven_day_opus", "Weekly · Opus") ?? Window(u, "seven_day_sonnet", "Weekly · Sonnet");
        if (weekly is null && u.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in list.EnumerateArray())
            {
                if (Str(l, "group") != "weekly") continue;
                var label = "Weekly";
                if (l.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "display_name") is { } dn)
                    label = $"Weekly · {dn}";
                var resets = DateTime.TryParse(Str(l, "resets_at"), out var d) ? d.ToLocalTime() : (DateTime?)null;
                var used = l.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : 0;
                var candidate = new LimitWindow(label, used, resets);
                if (weekly is null || candidate.UsedPercent > weekly.UsedPercent) weekly = candidate;
            }
        }
        return (session, weekly);
    }

    private static LimitWindow? Window(JsonElement u, string name, string label)
    {
        if (!u.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        var used = w.TryGetProperty("utilization", out var ut) && ut.ValueKind == JsonValueKind.Number ? ut.GetDouble() : 0;
        var resets = DateTime.TryParse(Str(w, "resets_at"), out var d) ? d.ToLocalTime() : (DateTime?)null;
        return new LimitWindow(label, used, resets);
    }

    /// Makes the next free ~/.claude-seatN and opens Claude Code in it (not elevated) so the person
    /// can sign that seat in with /login. Returns the folder.
    public string AddSeat()
    {
        var n = Enumerable.Range(1, 99).First(i => !Directory.Exists(Path.Combine(Paths.Home, $"{Prefix}seat{i}")));
        var dir = Directory.CreateDirectory(Path.Combine(Paths.Home, $"{Prefix}seat{n}")).FullName;
        OpenSignIn(dir);
        RefreshSoon();
        return dir;
    }

    /// Takes a seat that never signed in off the list, moving its folder to the Recycle Bin (recoverable).
    /// A signed-in seat is left alone: its folder holds that account's login.
    public void RemoveSeat(string dir)
    {
        if (Path.GetFileName(dir) is not { } name || !name.StartsWith(Prefix) || File.Exists(Path.Combine(dir, ".credentials.json"))) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) { Log.Error("remove seat", ex); }
        RefreshSoon();
    }

    public void OpenSignIn(string dir)
    {
        var script = Path.Combine(dir, "sign-in.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $"title Sign in a Claude seat ({Path.GetFileName(dir)})\r\n" +
            "echo This terminal runs Claude Code with its own profile for one Claude seat.\r\n" +
            "echo Sign in with the seat's account (type /login if Claude Code doesn't ask), then close this window.\r\n" +
            "echo.\r\n" +
            $"set \"CLAUDE_CONFIG_DIR={dir}\"\r\n" +
            "claude\r\n");
        // Through Explorer, so the terminal runs as the signed-in user even when PC Essentials is elevated.
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{script}\"") { UseShellExecute = false });
    }

    private static string? FindClaude()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
            Path.Combine(Paths.Home, ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe"),
        };
        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is not null) return hit;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(exe)) return exe;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _timer.Dispose();
}
