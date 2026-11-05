using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PCEssentials.Core;

namespace PCEssentials.Services.Agents;

/// Estimated: the reset time was worked out from readings rather than reported (shown with "≈").
public sealed record LimitWindow(string Label, double UsedPercent, DateTime? ResetsAt, bool Estimated = false);

public sealed record AgentLimits(string? PlanName, double? PlanMonthly, LimitWindow? Session, LimitWindow? Weekly);

/// A turn in progress. Place is "app" (the Claude or Codex desktop app) or "terminal" (the CLI).
public sealed record LiveSession(AgentProvider Provider, string Project, DateTime Since, string Place = "terminal", string Seat = "default");

/// One Claude account as the agents page shows it.
public sealed record SeatSummary(string Id, string? Email, string? OrgName, string? PlanName, bool IsDefault, bool SignedIn,
    LimitWindow? Session, LimitWindow? Weekly, double SessionCost, double MonthCost, string? Error, string ConfigDir,
    string? SameAccountAs = null);

public sealed record AgentSummary
{
    public Dictionary<AgentProvider, PeriodTotal> Today { get; init; } = new();
    public Dictionary<AgentProvider, PeriodTotal> Week { get; init; } = new();
    public Dictionary<AgentProvider, PeriodTotal> Month { get; init; } = new();
    /// Everything in the kept history (all of it).
    public Dictionary<AgentProvider, PeriodTotal> AllTime { get; init; } = new();
    /// Cost since the 1st of this month.
    public Dictionary<AgentProvider, double> CalendarMonth { get; init; } = new();
    /// Codex's cost inside its current session window.
    public double CodexSessionCost { get; init; }
    /// Claude accounts: the usual profile first, then Team seats.
    public List<SeatSummary> Seats { get; init; } = [];
    /// Cost per provider per day for the last 7 days, oldest first.
    public List<Dictionary<AgentProvider, double>> Daily { get; init; } = [];
    /// The same for the last 30 days, and for each hour of today.
    public List<Dictionary<AgentProvider, double>> Daily30 { get; init; } = [];
    public List<Dictionary<AgentProvider, double>> Hourly { get; init; } = [];
    /// Total cost per day for the last 13 weeks (91 days, oldest first), for the activity grid.
    public List<double> Heatmap { get; init; } = [];
    /// The most valuable models over the whole history.
    public List<RankedRow> AllTimeModels { get; init; } = [];
    public List<RankedRow> TopModels { get; init; } = [];
    public List<RankedRow> TopProjects { get; init; } = [];
    public Dictionary<AgentProvider, AgentLimits> Limits { get; init; } = new();
    public List<LiveSession> Live { get; init; } = [];
    public Dictionary<AgentProvider, DateTime> LastActivity { get; init; } = new();
    public HashSet<AgentProvider> Installed { get; init; } = [];
}

public sealed record AgentFinished(AgentProvider Provider, string Project, TimeSpan Duration);

/// Reads Claude Code and Codex session logs from the home folder, plus any extra log folders in
/// the settings (logs synced from another computer). Everything is local: no credentials are read
/// and nothing is sent anywhere. Plan limits come from the caches the CLIs write themselves
/// (~/.claude.json and the Codex rate-limit events in its logs).
public sealed class AgentUsageService : IDisposable
{
    private readonly AppSettings _settings;
    private static readonly string ClaudeDir = Path.Combine(Paths.Home, ".claude");
    private static readonly string CodexDir = Path.Combine(Paths.Home, ".codex");
    /// History is kept for good: the Spending card shows everything ever recorded on this PC.
    private const int KeepDays = 36500;

    private readonly PriceList _prices = PriceList.Load();
    public ClaudeSeatService Seats { get; }
    public CodexResetService Resets { get; } = new();
    private readonly Dictionary<string, AgentRecord> _records = new();
    /// Bumped on every change to _records (under _gate), so Publish knows when its cached stats are stale.
    private int _recordsVersion;
    private AgentStats? _stats;
    private int _statsVersion = -1;
    private readonly Dictionary<string, long> _cursors = new();
    private readonly Dictionary<string, (string Status, DateTime Since, string Project, string Place, string Seat)> _claudeSessions = new();
    private readonly Dictionary<string, CodexFileState> _codexFiles = new();
    private CodexLimitState? _codexLimits;
    private readonly Timer _scanTimer, _liveTimer, _appWindowTimer;
    private ClaudeAppWindow.Reading? _appWindow;
    /// The account the Claude app is signed in to, and since when (unknown before PC Essentials started).
    private string? _appAccount;
    private DateTime _appAccountSince = DateTime.MinValue;

    /// The Claude app's account and on-screen usage, read once a minute; republished only when either changes.
    private void ReadAppWindow()
    {
        var account = ClaudeAppUsage.Account();
        var changed = account != _appAccount;
        if (changed)
        {
            // What was read for the last account is not this one's.
            if (_appAccount is not null) _appAccountSince = DateTime.Now;
            _appAccount = account;
            _appWindow = null;
        }
        if (ClaudeAppWindow.Read() is { } reading)
        {
            changed |= _appWindow is null || _appWindow.Session != reading.Session || _appWindow.Weekly != reading.Weekly;
            _appWindow = reading;
        }
        if (changed) lock (_stateGate) Publish();
    }
    private readonly object _gate = new();
    /// Guards the session state shared by the scan and live timers.
    private readonly object _stateGate = new();
    private int _scanning;
    private bool _dirty, _cacheLoaded;
    private DateTime _lastCacheWrite = DateTime.MinValue;
    private readonly AgentLogReader _reader = new();
    private static readonly string CachePath = Path.Combine(Paths.DataDir, "agents-cache.json");

    public AgentSummary Summary { get; private set; } = new();
    public event Action<AgentSummary>? Updated;
    public event Action<AgentFinished>? Finished;

    public sealed class CodexFileState
    {
        public string Session { get; set; } = "";
        public string Project { get; set; } = "";
        public string Model { get; set; } = "";
        public bool Fast { get; set; }
        public DateTime? TurnStarted { get; set; }
        public DateTime LastEvent { get; set; }
        public bool HasUsageRecords { get; set; }
        public string Originator { get; set; } = "";
        /// Read from an extra log folder: another computer's session, never live on this one.
        public bool Remote { get; set; }
    }

    public sealed class CodexLimitState
    {
        public DateTime At { get; set; }
        public LimitWindow? Session { get; set; }
        public LimitWindow? Weekly { get; set; }
        public string? Plan { get; set; }
    }

    /// Parsed records and how far each log was read, so a restart only reads what is new.
    private sealed class Cache
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, AgentRecord> Records { get; set; } = new();
        public Dictionary<string, long> Cursors { get; set; } = new();
        public Dictionary<string, CodexFileState> CodexFiles { get; set; } = new();
        public CodexLimitState? CodexLimits { get; set; }
    }

    public AgentUsageService(AppSettings settings)
    {
        _settings = settings;
        Seats = new ClaudeSeatService(_prices);
        Seats.Changed += () => ThreadPool.QueueUserWorkItem(_ => { lock (_stateGate) Publish(); });
        _scanTimer = new Timer(_ => Scan(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        _liveTimer = new Timer(_ => RefreshLive(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        _appWindowTimer = new Timer(_ => ReadAppWindow(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    }

    public void RefreshNow() => ThreadPool.QueueUserWorkItem(_ => Scan());

    private void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        Monitor.Enter(_stateGate);
        try
        {
            var firstScan = !_cacheLoaded;
            if (firstScan)
            {
                LoadCache();
                _cacheLoaded = true;
            }
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var (seat, dir) in ClaudeLogDirs())
                foreach (var file in Files(dir, cutoff)) Tail(file, line => ParseClaude(file, line, seat));
            foreach (var dir in new[] { Path.Combine(CodexDir, "sessions"), Path.Combine(CodexDir, "archived_sessions") })
                foreach (var file in Files(dir, cutoff)) Tail(file, line => ParseCodex(file, line));
            // Synced logs count toward the usual Claude account, which is assumed to be the one used elsewhere too.
            foreach (var dir in ExtraLogDirs())
                foreach (var file in Files(dir, cutoff))
                    if (AgentLogPaths.IsCodexLog(file)) Tail(file, line => ParseCodex(file, line, remote: true));
                    else Tail(file, line => ParseClaude(file, line, "default"));
            lock (_gate)
            {
                foreach (var key in _records.Where(kv => kv.Value.Time < cutoff).Select(kv => kv.Key).ToList())
                {
                    _records.Remove(key);
                    _recordsVersion++;
                }
            }
            Publish();
            if (_dirty && (firstScan || DateTime.Now - _lastCacheWrite > TimeSpan.FromMinutes(10))) SaveCache();
            if (firstScan)
            {
                // The first read of a large history leaves big buffers behind; hand them back.
                _reader.Shrink();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
        }
        catch (Exception ex) { Log.Error("agents scan", ex); }
        finally
        {
            Monitor.Exit(_stateGate);
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var fs = File.OpenRead(CachePath);
            var cache = JsonSerializer.Deserialize<Cache>(fs);
            if (cache is null || cache.Version != 1) return;
            lock (_gate)
            {
                foreach (var (k, v) in cache.Records)
                {
                    // Prices may have changed since the cache was written.
                    v.Cost = _prices.Cost(v);
                    _records[k] = v;
                }
                _recordsVersion++;
            }
            foreach (var (k, v) in cache.Cursors) _cursors[k] = v;
            foreach (var (k, v) in cache.CodexFiles) _codexFiles[k] = v;
            _codexLimits = cache.CodexLimits;
            _lastCacheWrite = File.GetLastWriteTime(CachePath);
        }
        catch (Exception ex)
        {
            Log.Error("agents cache load", ex);
            lock (_gate)
            {
                _records.Clear();
                _recordsVersion++;
            }
            _cursors.Clear();
            _codexFiles.Clear();
        }
    }

    private void SaveCache()
    {
        try
        {
            Cache cache;
            lock (_gate)
                cache = new Cache
                {
                    Records = new(_records),
                    Cursors = new(_cursors),
                    CodexFiles = _codexFiles.Where(kv => _cursors.ContainsKey(kv.Key)).ToDictionary(),
                    CodexLimits = _codexLimits,
                };
            var tmp = CachePath + ".tmp";
            using (var fs = File.Create(tmp)) JsonSerializer.Serialize(fs, cache);
            File.Move(tmp, CachePath, true);
            _dirty = false;
            _lastCacheWrite = DateTime.Now;
        }
        catch (Exception ex) { Log.Error("agents cache save", ex); }
    }

    /// Each Claude profile's log folder with the seat it belongs to.
    private static IEnumerable<(string Seat, string Dir)> ClaudeLogDirs()
    {
        yield return ("default", Path.Combine(ClaudeDir, "projects"));
        yield return ("default", Path.Combine(Paths.Home, ".config", "claude", "projects"));
        string[] seats;
        try { seats = Directory.GetDirectories(Paths.Home, ClaudeSeatService.Prefix + "*"); }
        catch (IOException) { yield break; }
        foreach (var dir in seats) yield return (Path.GetFileName(dir), Path.Combine(dir, "projects"));
    }

    private IEnumerable<string> ExtraLogDirs()
    {
        List<string> dirs;
        try { dirs = _settings.Agents.ExtraLogFolders.ToList(); }
        catch (InvalidOperationException) { return []; }
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Environment.ExpandEnvironmentVariables(d.Trim()));
    }

    private static IEnumerable<(string Seat, string Dir)> ClaudeSessionDirs() =>
        ClaudeLogDirs().Where(d => d.Dir.EndsWith("projects") && !d.Dir.Contains(Path.Combine(".config", "claude")))
            .Select(d => (d.Seat, Path.Combine(Path.GetDirectoryName(d.Dir)!, "sessions")));

    private static IEnumerable<string> Files(string dir, DateTime cutoff)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            // The times come with the directory listing instead of two more lookups per file: the scan runs every
            // 30 seconds over thousands of logs, and that cut it from ~420 to ~170 ms. The listing can lag for a log
            // still open for writing, which is fine for sorting; AgentLogReader.Read asks the file for its real size.
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => AgentLogReader.IsLog(f.FullName) && f.LastWriteTime >= cutoff)
                .OrderBy(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .ToList();
        }
        catch { return []; }
    }

    /// Reads only what was added to a log since the last scan (a compressed log, only once).
    private void Tail(string path, Action<ReadOnlyMemory<byte>> onLine)
    {
        try
        {
            _cursors.TryGetValue(path, out var offset);
            var consumed = _reader.Read(path, offset, onLine);
            if (consumed != offset) _dirty = true;
            _cursors[path] = consumed;
        }
        catch (IOException) { }
        catch (Exception ex) { Log.Error("agents tail", ex); }
    }

    private static bool Has(ReadOnlySpan<byte> span, ReadOnlySpan<byte> needle) => span.IndexOf(needle) >= 0;

    private void ParseClaude(string file, ReadOnlyMemory<byte> line, string seat)
    {
        var span = line.Span;
        if (!Has(span, "\"type\":\"assistant\""u8) || !Has(span, "\"usage\""u8)) return;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (Str(root, "type") != "assistant" || !root.TryGetProperty("message", out var msg)) return;
        var model = Str(msg, "model") ?? "";
        if (model.Length == 0 || model.StartsWith('<')) return;
        if (!msg.TryGetProperty("usage", out var u)) return;

        var time = DateTime.TryParse(Str(root, "timestamp"), out var t) ? t.ToLocalTime() : File.GetLastWriteTime(file);
        var key = $"claude:{Str(msg, "id")}:{Str(root, "requestId")}";
        if (key == "claude::") key = $"claude:{Path.GetFileNameWithoutExtension(file)}:{time.Ticks}";
        long longWrite = 0;
        if (u.TryGetProperty("cache_creation", out var cc)) longWrite = Long(cc, "ephemeral_1h_input_tokens");
        int searches = 0;
        if (u.TryGetProperty("server_tool_use", out var stu)) searches = (int)Long(stu, "web_search_requests");

        var rec = new AgentRecord
        {
            Provider = AgentProvider.Claude,
            Time = time,
            Model = model,
            Project = ProjectName(Str(root, "cwd")),
            Seat = seat,
            Input = Long(u, "input_tokens"),
            Output = Long(u, "output_tokens"),
            CacheRead = Long(u, "cache_read_input_tokens"),
            CacheWrite = Long(u, "cache_creation_input_tokens"),
            CacheWriteLong = longWrite,
            Fast = Str(u, "speed") == "fast",
            UsOnly = Str(u, "inference_geo") == "us",
            WebSearches = searches,
        };
        Add(key, rec);
    }

    private void ParseCodex(string file, ReadOnlyMemory<byte> line, bool remote = false)
    {
        // Codex logs are dominated by huge response_item lines; decide from the first bytes whether
        // a line can matter before parsing anything.
        var head = line.Span[..Math.Min(line.Length, 200)];
        if (Has(head, "\"type\":\"event_msg\""u8))
        {
            if (!Has(head, "\"token_count\""u8) && !Has(head, "\"task_started\""u8) && !Has(head, "\"task_complete\""u8)
                && !Has(head, "\"turn_aborted\""u8)) return;
        }
        else if (!Has(head, "\"session_meta\""u8) && !Has(head, "\"turn_context\""u8) && !Has(head, "\"token_usage_record\""u8))
            return;
        if (!_codexFiles.TryGetValue(file, out var st)) _codexFiles[file] = st = new CodexFileState { Session = Path.GetFileName(file).Split('.')[0], Remote = remote };
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = Str(root, "type");
        if (!root.TryGetProperty("payload", out var p)) return;
        var time = DateTime.TryParse(Str(root, "timestamp"), out var t) ? t.ToLocalTime() : DateTime.Now;
        st.LastEvent = time;

        switch (type)
        {
            case "session_meta":
                if (st.Project.Length == 0)
                {
                    st.Session = Str(p, "id") ?? st.Session;
                    st.Project = ProjectName(Str(p, "cwd"));
                    st.Originator = Str(p, "originator") ?? "";
                }
                break;
            case "turn_context":
                st.Model = Str(p, "model") ?? st.Model;
                var tier = Str(p, "service_tier");
                st.Fast = tier is "fast" or "priority";
                if (Str(p, "cwd") is { } cwd) st.Project = ProjectName(cwd);
                break;
            case "token_usage_record":
                if (p.TryGetProperty("usage", out var usage))
                {
                    st.HasUsageRecords = true;
                    AddCodex($"codex:{Str(p, "response_id")}", usage, st, time);
                }
                break;
            case "event_msg":
                switch (Str(p, "type"))
                {
                    case "task_started": st.TurnStarted = time; break;
                    case "task_complete" or "turn_aborted":
                        if (st.TurnStarted is { } started && !st.Remote && Str(p, "type") == "task_complete")
                            MaybeFinished(AgentProvider.Codex, st.Project, time - started, time);
                        st.TurnStarted = null;
                        break;
                    case "token_count":
                        if (p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && !st.HasUsageRecords
                            && info.TryGetProperty("last_token_usage", out var last) && info.TryGetProperty("total_token_usage", out var total))
                            AddCodex($"codex:{st.Session}:total:{Long(total, "total_tokens")}", last, st, time);
                        if (p.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                            ReadCodexLimits(rl, time);
                        break;
                }
                break;
        }
    }

    private void AddCodex(string key, JsonElement u, CodexFileState st, DateTime time)
    {
        var input = Long(u, "input_tokens");
        var cacheRead = Math.Min(input, Long(u, "cached_input_tokens"));
        var cacheWrite = Math.Min(input - cacheRead, Long(u, "cache_write_input_tokens"));
        Add(key, new AgentRecord
        {
            Provider = AgentProvider.Codex,
            Time = time,
            Model = st.Model.Length > 0 ? st.Model : "codex",
            Project = st.Project,
            Input = input - cacheRead - cacheWrite,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            Output = Long(u, "output_tokens"),
            Fast = st.Fast,
        });
    }

    private void ReadCodexLimits(JsonElement rl, DateTime time)
    {
        var id = Str(rl, "limit_id");
        if (!string.IsNullOrEmpty(id) && id != "codex") return;
        if (_codexLimits is { } existing && existing.At > time) return;
        LimitWindow? session = null, weekly = null;
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!rl.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            var minutes = Dbl(w, "window_minutes");
            DateTime? resets = null;
            if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                resets = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime;
            else if (w.TryGetProperty("resets_in_seconds", out var rin) && rin.ValueKind == JsonValueKind.Number)
                resets = time.AddSeconds(rin.GetDouble());
            var window = new LimitWindow(minutes <= 720 ? "Session" : "Weekly", Dbl(w, "used_percent"), resets);
            if (minutes <= 720) session = window;
            else if (minutes is >= 6 * 1440 and <= 8 * 1440) weekly = window;
        }
        _codexLimits = new CodexLimitState { At = time, Session = session, Weekly = weekly, Plan = Str(rl, "plan_type") };
    }

    private void Add(string key, AgentRecord rec)
    {
        lock (_gate)
        {
            _recordsVersion++;
            if (_records.TryGetValue(key, out var existing))
            {
                // Claude writes one line per content block of the same message; keep the largest counts.
                existing.Input = Math.Max(existing.Input, rec.Input);
                existing.Output = Math.Max(existing.Output, rec.Output);
                existing.CacheRead = Math.Max(existing.CacheRead, rec.CacheRead);
                existing.CacheWrite = Math.Max(existing.CacheWrite, rec.CacheWrite);
                existing.CacheWriteLong = Math.Max(existing.CacheWriteLong, rec.CacheWriteLong);
                existing.WebSearches = Math.Max(existing.WebSearches, rec.WebSearches);
                existing.Cost = _prices.Cost(existing);
                return;
            }
            rec.Cost = _prices.Cost(rec);
            _records[key] = rec;
        }
    }

    /// Claude Code keeps one ~/.claude/sessions/<pid>.json per running session with its status.
    private void RefreshLive()
    {
        if (!Monitor.TryEnter(_stateGate)) return;
        try
        {
            var seen = new HashSet<string>();
            var changed = false;
            foreach (var (seatId, dir) in ClaudeSessionDirs())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("pid", out var pidEl) || pidEl.ValueKind != JsonValueKind.Number) continue;
                        var pid = pidEl.GetInt32();
                        if (!ProcessAlive(pid)) continue;
                        var id = Str(root, "sessionId") ?? file;
                        seen.Add(id);
                        var status = Str(root, "status") ?? "idle";
                        var project = ProjectName(Str(root, "cwd"));
                        var place = Str(root, "entrypoint") == "claude-desktop" ? "app" : "terminal";
                        var since = root.TryGetProperty("statusUpdatedAt", out var su) && su.ValueKind == JsonValueKind.Number
                            ? DateTimeOffset.FromUnixTimeMilliseconds(su.GetInt64()).LocalDateTime : DateTime.Now;
                        if (_claudeSessions.TryGetValue(id, out var prev))
                        {
                            if (prev.Status == "busy" && status != "busy")
                                MaybeFinished(AgentProvider.Claude, project, DateTime.Now - prev.Since, DateTime.Now);
                            if (prev.Status != status) changed = true;
                            if (prev.Status == status) since = prev.Since;
                        }
                        else changed = true;
                        _claudeSessions[id] = (status, since, project, place, seatId);
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
            }
            foreach (var gone in _claudeSessions.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _claudeSessions.Remove(gone);
                changed = true;
            }
            if (changed) Publish();
        }
        catch (Exception ex) { Log.Error("agents live", ex); }
        finally { Monitor.Exit(_stateGate); }
    }

    private void MaybeFinished(AgentProvider provider, string project, TimeSpan duration, DateTime at)
    {
        // Only report fresh endings of tasks long enough to have walked away from.
        if (DateTime.Now - at > TimeSpan.FromMinutes(5) || duration < TimeSpan.FromSeconds(60)) return;
        Finished?.Invoke(new AgentFinished(provider, project, duration));
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// The record-derived part of the summary, worked out again only when the records change or a new day starts.
    /// Session status changes, seat checks and the Claude app's readings republish often, but leave the records alone.
    private AgentStats Stats(DateTime now)
    {
        lock (_gate)
        {
            if (_stats is null || _statsVersion != _recordsVersion || _stats.Day != now.Date)
            {
                _stats = AgentStats.Compute(_records.Values, now);
                _statsVersion = _recordsVersion;
            }
            return _stats;
        }
    }

    private void Publish()
    {
        var now = DateTime.Now;
        var stats = Stats(now);

        var seatList = Seats.Seats;
        // A seat folder on the main profile's account is the same account: one column, its costs and sessions folded in.
        var merged = ClaudeAccounts.MergedIntoMain(seatList.Select(s => (s.Id, s.AccountId, s.IsDefault)));
        string SeatOf(string id) => merged.TryGetValue(id, out var main) ? main : id;

        var live = new List<LiveSession>();
        foreach (var s in _claudeSessions.Values.Where(s => s.Status == "busy"))
            live.Add(new LiveSession(AgentProvider.Claude, s.Project, s.Since, s.Place, SeatOf(s.Seat)));
        foreach (var st in _codexFiles.Values)
            if (st.TurnStarted is { } started && !st.Remote && now - st.LastEvent < TimeSpan.FromMinutes(10))
                live.Add(new LiveSession(AgentProvider.Codex, st.Project, started,
                    st.Originator.Contains("desktop", StringComparison.OrdinalIgnoreCase) ? "app" : "terminal"));

        var installed = new HashSet<AgentProvider>();
        if (Directory.Exists(ClaudeDir)) installed.Add(AgentProvider.Claude);
        if (Directory.Exists(CodexDir)) installed.Add(AgentProvider.Codex);

        var limits = new Dictionary<AgentProvider, AgentLimits>();
        var seats = new List<SeatSummary>();
        var sameAs = ClaudeAccounts.Duplicates(seatList.Where(s => !merged.ContainsKey(s.Id)).Select(s => (s.Id, s.AccountId)));
        foreach (var seat in seatList.Where(s => !merged.ContainsKey(s.Id)))
        {
            // Limit windows are a few hours long and end in the future, so the stats' recent requests cover them.
            var mine = stats.ClaudeRecent.Where(r => SeatOf(r.Seat) == seat.Id).ToList();
            var folded = seatList.Where(s => merged.TryGetValue(s.Id, out var m) && m == seat.Id).ToList();
            var session = Expire(seat.Session) ?? folded.Select(s => Expire(s.Session)).FirstOrDefault(w => w is not null);
            var weeklyWindow = Expire(seat.Weekly) ?? folded.Select(s => Expire(s.Weekly)).FirstOrDefault(w => w is not null);
            // For the account the Claude app is signed in to, what the app shows wins. On Windows, Claude Code's
            // own usage check can report a separate session window for the same account. Best is the app's
            // on-screen usage indicator (exact percent and reset), then its saved readings (15 minutes
            // apart, reset estimated). The app can be on any account, this PC's profiles or none of them.
            var appsAccount = ClaudeAccounts.SameAccount(seat.AccountId, _appAccount);
            if (appsAccount && _appWindow is { } shown && now - shown.At < TimeSpan.FromMinutes(3))
            {
                session = shown.Session ?? session;
                weeklyWindow = shown.Weekly ?? weeklyWindow;
            }
            else if (appsAccount && ClaudeAppUsage.Limits(seat.OrgId, mine.Where(r => r.Time > now.AddHours(-11)).Select(r => r.Time), _appAccountSince) is { } app)
            {
                session = app.Session ?? session;
                weeklyWindow = app.Weekly is { } aw ? (weeklyWindow is { ResetsAt: { } wr } && weeklyWindow.UsedPercent <= aw.UsedPercent ? aw with { ResetsAt = wr } : aw) : weeklyWindow;
            }
            seats.Add(new SeatSummary(seat.Id, seat.Email, seat.OrgName, seat.PlanName, seat.IsDefault, seat.SignedIn,
                session, weeklyWindow, AgentStats.WindowCost(mine, session?.ResetsAt, TimeSpan.FromHours(5)),
                stats.ClaudeMonthCost.Where(kv => SeatOf(kv.Key) == seat.Id).Sum(kv => kv.Value), seat.Error, seat.ConfigDir,
                sameAs.TryGetValue(seat.Id, out var first) ? seatList.First(s => s.Id == first).ConfigDir : null));
            if (seat.IsDefault && seat.SignedIn)
                limits[AgentProvider.Claude] = new AgentLimits(seat.PlanName, seat.PlanMonthly, session, weeklyWindow);
        }
        if (!limits.ContainsKey(AgentProvider.Claude) && ReadClaudeLimits() is { } cl) limits[AgentProvider.Claude] = cl;
        if (_codexLimits is { } cx)
        {
            var plan = _prices.Plan(AgentProvider.Codex, cx.Plan);
            limits[AgentProvider.Codex] = new AgentLimits(plan?.Name ?? AgentStats.Capitalize(cx.Plan), plan?.Monthly,
                Expire(cx.Session), Expire(cx.Weekly));
        }

        Summary = new AgentSummary
        {
            Today = stats.Today,
            Week = stats.Week,
            Month = stats.Month,
            AllTime = stats.AllTime,
            CalendarMonth = stats.CalendarMonth,
            CodexSessionCost = limits.TryGetValue(AgentProvider.Codex, out var cxl)
                ? AgentStats.WindowCost(stats.CodexRecent, cxl.Session?.ResetsAt, TimeSpan.FromHours(5)) : 0,
            Seats = seats,
            Daily = stats.Daily,
            Daily30 = stats.Daily30,
            Hourly = stats.Hourly,
            Heatmap = stats.Heatmap,
            AllTimeModels = stats.AllTimeModels,
            TopModels = stats.TopModels,
            TopProjects = stats.TopProjects,
            Limits = limits,
            Live = live.OrderBy(l => l.Since).ToList(),
            LastActivity = stats.LastActivity,
            Installed = installed,
        };
        Updated?.Invoke(Summary);
    }

    /// A window whose reset time has passed has started over, so nothing of it is used yet.
    private static LimitWindow? Expire(LimitWindow? w) =>
        w is { ResetsAt: { } r } && r < DateTime.Now ? w with { UsedPercent = 0, ResetsAt = null } : w;

    private AgentLimits? ReadClaudeLimits()
    {
        try
        {
            var path = Path.Combine(Paths.Home, ".claude.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? planName = null;
            double? monthly = null;
            if (root.TryGetProperty("oauthAccount", out var acct))
            {
                var plan = _prices.Plan(AgentProvider.Claude, Str(acct, "userRateLimitTier"))
                        ?? _prices.Plan(AgentProvider.Claude, Str(acct, "organizationRateLimitTier"))
                        ?? _prices.Plan(AgentProvider.Claude, Str(acct, "organizationType"));
                planName = plan?.Name;
                monthly = plan?.Monthly;
            }
            LimitWindow? session = null, weekly = null;
            if (root.TryGetProperty("cachedUsageUtilization", out var cache) && cache.TryGetProperty("utilization", out var u))
            {
                session = Window(u, "five_hour", "Session");
                weekly = Window(u, "seven_day", "Weekly") ?? Window(u, "seven_day_opus", "Weekly · Opus") ?? Window(u, "seven_day_sonnet", "Weekly · Sonnet");
                if (weekly is null && u.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in list.EnumerateArray())
                    {
                        if (Str(l, "group") != "weekly") continue;
                        var label = "Weekly";
                        if (l.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                            && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "display_name") is { } dn)
                            label = $"Weekly · {dn}";
                        var candidate = new LimitWindow(label, Dbl(l, "percent"), ParseDate(Str(l, "resets_at")));
                        if (weekly is null || candidate.UsedPercent > weekly.UsedPercent) weekly = candidate;
                    }
                }
            }
            return new AgentLimits(planName, monthly, Expire(session), Expire(weekly));
        }
        catch (Exception ex)
        {
            Log.Error("claude limits", ex);
            return null;
        }
    }

    private static LimitWindow? Window(JsonElement u, string name, string label)
    {
        if (!u.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return new LimitWindow(label, Dbl(w, "utilization"), ParseDate(Str(w, "resets_at")));
    }

    private static DateTime? ParseDate(string? s) => DateTime.TryParse(s, out var d) ? d.ToLocalTime() : null;

    /// "claude-opus-5-5-20260101" → "Opus 5.5", "gpt-6.1-sol" → "GPT-6.1 Sol", as the Mac app names them.
    private static string ProjectName(string? cwd) => AgentLogPaths.ProjectName(cwd);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public void Dispose()
    {
        _scanTimer.Dispose();
        _liveTimer.Dispose();
        _appWindowTimer.Dispose();
        Seats.Dispose();
        Resets.Dispose();
        lock (_stateGate) if (_dirty) SaveCache();
    }
}
