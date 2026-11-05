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

public sealed record PeriodTotal(long Tokens, double Cost, int Requests, int Unpriced);

public sealed record RankedRow(string Name, AgentProvider Provider, long Tokens, double Cost);

/// A request's time and cost, kept for working out what a limit window has cost so far.
public readonly record struct TimedCost(string Seat, DateTime Time, double Cost);

/// Everything the agents page shows that comes from the usage records alone, worked out in one pass.
/// The history holds 100k+ records, so the earlier ~65 LINQ passes, with a copy of every record list
/// each time, cost a few hundred milliseconds and a full garbage collection on every refresh.
public sealed class AgentStats
{
    /// Records this recent are kept for the limit windows (5 hours long) and the Claude app's reset estimate (11 hours).
    public static readonly TimeSpan RecentSpan = TimeSpan.FromHours(12);

    /// The day the totals are for; a new day needs a new computation.
    public DateTime Day { get; private init; }
    public Dictionary<AgentProvider, PeriodTotal> Today { get; private init; } = new();
    public Dictionary<AgentProvider, PeriodTotal> Week { get; private init; } = new();
    public Dictionary<AgentProvider, PeriodTotal> Month { get; private init; } = new();
    public Dictionary<AgentProvider, PeriodTotal> AllTime { get; private init; } = new();
    public Dictionary<AgentProvider, double> CalendarMonth { get; private init; } = new();
    public List<Dictionary<AgentProvider, double>> Daily { get; private init; } = [];
    public List<Dictionary<AgentProvider, double>> Daily30 { get; private init; } = [];
    public List<Dictionary<AgentProvider, double>> Hourly { get; private init; } = [];
    public List<double> Heatmap { get; private init; } = [];
    public List<RankedRow> AllTimeModels { get; private init; } = [];
    public List<RankedRow> TopModels { get; private init; } = [];
    public List<RankedRow> TopProjects { get; private init; } = [];
    public Dictionary<AgentProvider, DateTime> LastActivity { get; private init; } = new();
    /// Claude cost since the 1st of the month, per profile that wrote it.
    public Dictionary<string, double> ClaudeMonthCost { get; private init; } = new();
    /// Claude and Codex requests of the last RecentSpan before the computation, in record order.
    public List<TimedCost> ClaudeRecent { get; private init; } = [];
    public List<TimedCost> CodexRecent { get; private init; } = [];

    private sealed class Totals
    {
        public long Tokens;
        public double Cost;
        public int Requests, Unpriced;
        public PeriodTotal ToPeriod() => new(Tokens, Cost, Requests, Unpriced);
    }

    private sealed class Ranked
    {
        public required AgentProvider Provider;
        public long Tokens;
        public double Cost;
    }

    public static AgentStats Compute(IEnumerable<AgentRecord> records, DateTime now)
    {
        var today = now.Date;
        var weekFrom = today.AddDays(-6);
        var monthFrom = today.AddDays(-29);
        var rankFrom = today.AddDays(-30);
        var heatFrom = today.AddDays(-90);
        var calendarFrom = new DateTime(now.Year, now.Month, 1);
        var recentFrom = now - RecentSpan;

        var todayT = new Dictionary<AgentProvider, Totals>();
        var weekT = new Dictionary<AgentProvider, Totals>();
        var monthT = new Dictionary<AgentProvider, Totals>();
        var allT = new Dictionary<AgentProvider, Totals>();
        var calendar = new Dictionary<AgentProvider, double>();
        var last = new Dictionary<AgentProvider, DateTime>();
        var daily30 = NewBuckets(30);
        var hourly = NewBuckets(24);
        var heatmap = new double[91];
        var allModels = new Dictionary<(AgentProvider, string), Ranked>();
        var monthModels = new Dictionary<(AgentProvider, string), Ranked>();
        var projects = new Dictionary<string, Ranked>();
        var claudeMonth = new Dictionary<string, double>();
        var claudeRecent = new List<TimedCost>();
        var codexRecent = new List<TimedCost>();
        var names = new Dictionary<string, string>();

        foreach (var r in records)
        {
            var cost = r.Cost ?? 0;
            var tokens = r.Tokens;
            var time = r.Time;
            var date = time.Date;
            var p = r.Provider;

            Add(allT, r, cost, tokens);
            if (time >= monthFrom) Add(monthT, r, cost, tokens);
            if (time >= weekFrom) Add(weekT, r, cost, tokens);
            if (time >= today) Add(todayT, r, cost, tokens);
            if (time >= calendarFrom)
            {
                calendar[p] = calendar.GetValueOrDefault(p) + cost;
                if (p == AgentProvider.Claude) claudeMonth[r.Seat] = claudeMonth.GetValueOrDefault(r.Seat) + cost;
            }
            if (!last.TryGetValue(p, out var latest) || time > latest) last[p] = time;

            var day = (date - monthFrom).Days;
            if (day is >= 0 and < 30) Sum(daily30[day], p, cost);
            if (time >= today && time < today.AddHours(24)) Sum(hourly[(int)((time - today).Ticks / TimeSpan.TicksPerHour)], p, cost);
            var heat = (date - heatFrom).Days;
            if (heat is >= 0 and < 91) heatmap[heat] += cost;

            if (!names.TryGetValue(r.Model, out var name)) names[r.Model] = name = ModelDisplay(r.Model);
            Rank(allModels, (p, name), p, tokens, cost);
            if (time >= rankFrom)
            {
                Rank(monthModels, (p, name), p, tokens, cost);
                if (r.Project.Length > 0) Rank(projects, r.Project, p, tokens, cost);
            }

            if (time >= recentFrom)
                (p == AgentProvider.Claude ? claudeRecent : codexRecent).Add(new TimedCost(r.Seat, time, cost));
        }

        return new AgentStats
        {
            Day = today,
            Today = Periods(todayT),
            Week = Periods(weekT),
            Month = Periods(monthT),
            AllTime = Periods(allT),
            CalendarMonth = calendar,
            // The last 7 of the 30 days.
            Daily = daily30.Skip(23).Select(d => new Dictionary<AgentProvider, double>(d)).ToList(),
            Daily30 = daily30,
            Hourly = hourly,
            Heatmap = [.. heatmap],
            AllTimeModels = Top(allModels.Select(kv => (kv.Key.Item2, kv.Value))),
            TopModels = Top(monthModels.Select(kv => (kv.Key.Item2, kv.Value))),
            TopProjects = Top(projects.Select(kv => (kv.Key, kv.Value))),
            LastActivity = last,
            ClaudeMonthCost = claudeMonth,
            ClaudeRecent = claudeRecent,
            CodexRecent = codexRecent,
        };
    }

    /// Cost inside a limit window: from its start (reset minus its length) until now.
    public static double WindowCost(IEnumerable<TimedCost> requests, DateTime? resetsAt, TimeSpan length) =>
        resetsAt is { } reset ? requests.Where(r => r.Time >= reset - length).Sum(r => r.Cost) : 0;

    private static List<Dictionary<AgentProvider, double>> NewBuckets(int count) =>
        Enumerable.Range(0, count).Select(_ => new Dictionary<AgentProvider, double>()).ToList();

    private static void Sum(Dictionary<AgentProvider, double> bucket, AgentProvider p, double cost) =>
        bucket[p] = bucket.GetValueOrDefault(p) + cost;

    private static void Add(Dictionary<AgentProvider, Totals> totals, AgentRecord r, double cost, long tokens)
    {
        if (!totals.TryGetValue(r.Provider, out var t)) totals[r.Provider] = t = new Totals();
        t.Tokens += tokens;
        t.Cost += cost;
        t.Requests++;
        if (r.Cost is null) t.Unpriced++;
    }

    private static void Rank<TKey>(Dictionary<TKey, Ranked> rows, TKey key, AgentProvider p, long tokens, double cost) where TKey : notnull
    {
        // The first record of a group decides its provider, as GroupBy(...).First() did.
        if (!rows.TryGetValue(key, out var row)) rows[key] = row = new Ranked { Provider = p };
        row.Tokens += tokens;
        row.Cost += cost;
    }

    private static Dictionary<AgentProvider, PeriodTotal> Periods(Dictionary<AgentProvider, Totals> totals) =>
        totals.ToDictionary(kv => kv.Key, kv => kv.Value.ToPeriod());

    /// The three costliest, then the most tokens; ties keep the order the groups first appeared in.
    private static List<RankedRow> Top(IEnumerable<(string Name, Ranked Row)> rows) =>
        rows.Select(x => new RankedRow(x.Name, x.Row.Provider, x.Row.Tokens, x.Row.Cost))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList();

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

    public static string Capitalize(string? s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];
}
