using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PCEssentials.Core;

namespace PCEssentials.Services.Agents;

public enum AgentProvider { Claude, Codex }

public sealed class AgentRecord
{
    public AgentProvider Provider { get; init; }
    public DateTime Time { get; init; }
    public string Model { get; init; } = "";
    public string Project { get; init; } = "";
    /// Which Claude profile wrote it: "default" (~/.claude) or a seat folder name (".claude-seat1").
    public string Seat { get; init; } = "default";
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long CacheWriteLong { get; set; }
    public bool Fast { get; init; }
    public bool UsOnly { get; init; }
    public int WebSearches { get; set; }
    public double? Cost { get; set; }
    public long Tokens => Input + Output + CacheRead + CacheWrite;
}

/// Estimated: the reset time was worked out from readings rather than reported (shown with "≈").
public sealed record LimitWindow(string Label, double UsedPercent, DateTime? ResetsAt, bool Estimated = false);

public sealed record AgentLimits(string? PlanName, double? PlanMonthly, LimitWindow? Session, LimitWindow? Weekly);

/// A turn in progress. Place is "app" (the Claude or Codex desktop app) or "terminal" (the CLI).
public sealed record LiveSession(AgentProvider Provider, string Project, DateTime Since, string Place = "terminal", string Seat = "default");

/// One Claude account as the agents page shows it.
public sealed record SeatSummary(string Id, string? Email, string? OrgName, string? PlanName, bool IsDefault, bool SignedIn,
    LimitWindow? Session, LimitWindow? Weekly, double SessionCost, double MonthCost, string? Error, string ConfigDir);

public sealed record PeriodTotal(long Tokens, double Cost, int Requests, int Unpriced);

public sealed record RankedRow(string Name, AgentProvider Provider, long Tokens, double Cost);

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
    private readonly Dictionary<string, long> _cursors = new();
    private readonly Dictionary<string, (string Status, DateTime Since, string Project, string Place, string Seat)> _claudeSessions = new();
    private readonly Dictionary<string, CodexFileState> _codexFiles = new();
    private CodexLimitState? _codexLimits;
    private readonly Timer _scanTimer, _liveTimer, _appWindowTimer;
    private ClaudeAppWindow.Reading? _appWindow;

    /// The Claude app's on-screen usage, read once a minute; republished only when it changes.
    private void ReadAppWindow()
    {
        var reading = ClaudeAppWindow.Read();
        if (reading is null) return;
        var changed = _appWindow is null || _appWindow.Session != reading.Session || _appWindow.Weekly != reading.Weekly;
        _appWindow = reading;
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
                    _records.Remove(key);
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
            }
            foreach (var (k, v) in cache.Cursors) _cursors[k] = v;
            foreach (var (k, v) in cache.CodexFiles) _codexFiles[k] = v;
            _codexLimits = cache.CodexLimits;
            _lastCacheWrite = File.GetLastWriteTime(CachePath);
        }
        catch (Exception ex)
        {
            Log.Error("agents cache load", ex);
            lock (_gate) _records.Clear();
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
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => AgentLogReader.IsLog(f) && File.GetLastWriteTime(f) >= cutoff)
                .OrderBy(File.GetLastWriteTime)
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

    private void Publish()
    {
        List<AgentRecord> records;
        lock (_gate) records = _records.Values.ToList();
        var now = DateTime.Now;
        var today = now.Date;

        Dictionary<AgentProvider, PeriodTotal> Totals(DateTime from) => records
            .Where(r => r.Time >= from)
            .GroupBy(r => r.Provider)
            .ToDictionary(g => g.Key, g => new PeriodTotal(
                g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0), g.Count(), g.Count(r => r.Cost is null)));

        var daily = Enumerable.Range(0, 7).Select(i =>
        {
            var day = today.AddDays(i - 6);
            return records.Where(r => r.Time.Date == day)
                .GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0));
        }).ToList();

        List<Dictionary<AgentProvider, double>> Buckets(int count, Func<int, (DateTime From, DateTime To)> range) =>
            Enumerable.Range(0, count).Select(i =>
            {
                var (from, to) = range(i);
                return records.Where(r => r.Time >= from && r.Time < to)
                    .GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0));
            }).ToList();
        var daily30 = Buckets(30, i => (today.AddDays(i - 29), today.AddDays(i - 28)));
        var hourly = Buckets(24, i => (today.AddHours(i), today.AddHours(i + 1)));
        var byDay = records.GroupBy(r => r.Time.Date).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0));
        var heatmap = Enumerable.Range(0, 91).Select(i => byDay.GetValueOrDefault(today.AddDays(i - 90))).ToList();
        var allTimeModels = records.GroupBy(r => (r.Provider, Name: ModelDisplay(r.Model)))
            .Select(g => new RankedRow(g.Key.Name, g.Key.Provider, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0)))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList();

        var month = records.Where(r => r.Time >= today.AddDays(-30)).ToList();
        var models = month.GroupBy(r => (r.Provider, Name: ModelDisplay(r.Model)))
            .Select(g => new RankedRow(g.Key.Name, g.Key.Provider, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0)))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList();
        var projects = month.Where(r => r.Project.Length > 0).GroupBy(r => r.Project)
            .Select(g => new RankedRow(g.Key, g.First().Provider, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0)))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList();

        var live = new List<LiveSession>();
        foreach (var s in _claudeSessions.Values.Where(s => s.Status == "busy"))
            live.Add(new LiveSession(AgentProvider.Claude, s.Project, s.Since, s.Place, s.Seat));
        foreach (var st in _codexFiles.Values)
            if (st.TurnStarted is { } started && !st.Remote && now - st.LastEvent < TimeSpan.FromMinutes(10))
                live.Add(new LiveSession(AgentProvider.Codex, st.Project, started,
                    st.Originator.Contains("desktop", StringComparison.OrdinalIgnoreCase) ? "app" : "terminal"));

        var installed = new HashSet<AgentProvider>();
        if (Directory.Exists(ClaudeDir)) installed.Add(AgentProvider.Claude);
        if (Directory.Exists(CodexDir)) installed.Add(AgentProvider.Codex);

        var limits = new Dictionary<AgentProvider, AgentLimits>();
        var monthStart = new DateTime(now.Year, now.Month, 1);
        // Cost inside a window: from its start (reset minus its length) until now.
        double WindowCost(IEnumerable<AgentRecord> rs, LimitWindow? w, TimeSpan length) =>
            w?.ResetsAt is { } reset ? rs.Where(r => r.Time >= reset - length).Sum(r => r.Cost ?? 0) : 0;
        var seats = new List<SeatSummary>();
        foreach (var seat in Seats.Seats)
        {
            var mine = records.Where(r => r.Provider == AgentProvider.Claude && r.Seat == seat.Id).ToList();
            var session = Expire(seat.Session);
            var weeklyWindow = Expire(seat.Weekly);
            // For the account the Claude app uses, what the app shows wins. On Windows, Claude Code's own
            // usage check can report a separate session window for the same account. Best is the app's
            // on-screen usage indicator (exact percent and reset), then its saved readings (15 minutes
            // apart, reset estimated).
            if (seat.IsDefault && _appWindow is { } shown && now - shown.At < TimeSpan.FromMinutes(3))
            {
                session = shown.Session ?? session;
                weeklyWindow = shown.Weekly ?? weeklyWindow;
            }
            else if (seat.IsDefault && ClaudeAppUsage.Limits(seat.OrgId, mine.Where(r => r.Time > now.AddHours(-11)).Select(r => r.Time)) is { } app)
            {
                session = app.Session ?? session;
                weeklyWindow = app.Weekly is { } aw ? (weeklyWindow is { ResetsAt: { } wr } && weeklyWindow.UsedPercent <= aw.UsedPercent ? aw with { ResetsAt = wr } : aw) : weeklyWindow;
            }
            seats.Add(new SeatSummary(seat.Id, seat.Email, seat.OrgName, seat.PlanName, seat.IsDefault, seat.SignedIn,
                session, weeklyWindow, WindowCost(mine, session, TimeSpan.FromHours(5)),
                mine.Where(r => r.Time >= monthStart).Sum(r => r.Cost ?? 0), seat.Error, seat.ConfigDir));
            if (seat.IsDefault && seat.SignedIn)
                limits[AgentProvider.Claude] = new AgentLimits(seat.PlanName, seat.PlanMonthly, session, weeklyWindow);
        }
        if (!limits.ContainsKey(AgentProvider.Claude) && ReadClaudeLimits() is { } cl) limits[AgentProvider.Claude] = cl;
        if (_codexLimits is { } cx)
        {
            var plan = _prices.Plan(AgentProvider.Codex, cx.Plan);
            limits[AgentProvider.Codex] = new AgentLimits(plan?.Name ?? Capitalize(cx.Plan), plan?.Monthly,
                Expire(cx.Session), Expire(cx.Weekly));
        }

        Summary = new AgentSummary
        {
            Today = Totals(today),
            Week = Totals(today.AddDays(-6)),
            Month = Totals(today.AddDays(-29)),
            AllTime = Totals(DateTime.MinValue),
            CalendarMonth = records.Where(r => r.Time >= monthStart).GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0)),
            CodexSessionCost = limits.TryGetValue(AgentProvider.Codex, out var cxl)
                ? WindowCost(records.Where(r => r.Provider == AgentProvider.Codex), cxl.Session, TimeSpan.FromHours(5)) : 0,
            Seats = seats,
            Daily = daily,
            Daily30 = daily30,
            Hourly = hourly,
            Heatmap = heatmap,
            AllTimeModels = allTimeModels,
            TopModels = models,
            TopProjects = projects,
            Limits = limits,
            Live = live.OrderBy(l => l.Since).ToList(),
            LastActivity = records.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Max(r => r.Time)),
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
    public static string ModelDisplay(string model)
    {
        var m = model.ToLowerInvariant();
        var at = m.IndexOfAny(['@', '[']);
        if (at > 0) m = m[..at];
        if (m.StartsWith("claude-"))
        {
            // Family, then its version numbers joined with dots; dates dropped.
            var parts = m["claude-".Length..].Split('-').Where(p => !(p.Length >= 8 && p.All(char.IsDigit))).ToList();
            var words = parts.Where(p => !p.All(char.IsDigit)).Select(Capitalize);
            var version = string.Join('.', parts.Where(p => p.Length > 0 && p.All(char.IsDigit)));
            return (string.Join(' ', words) + (version.Length > 0 ? " " + version : "")).Trim();
        }
        if (m.StartsWith("gpt-"))
        {
            var rest = m[4..].Split('-');
            return "GPT-" + rest[0] + (rest.Length > 1 ? " " + string.Join(' ', rest[1..].Select(Capitalize)) : "");
        }
        return string.Join(' ', m.Split('-').Select(Capitalize));
    }

    private static string Capitalize(string? s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

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
