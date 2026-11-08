namespace PCEssentials.Services.Agents;

/// One Claude request as read from a Claude Code log.
public sealed class AgentRecord
{
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

public sealed record PeriodTotal(long Tokens, double Cost, int Requests, int Unpriced)
{
    public static readonly PeriodTotal Empty = new(0, 0, 0, 0);
}

public sealed record RankedRow(string Name, long Tokens, double Cost);

/// A request's time and cost, kept for working out what a limit window has cost so far.
public readonly record struct TimedCost(string Seat, DateTime Time, double Cost);

/// Everything the agents page shows that comes from the usage records alone, worked out in one pass.
/// The history holds 100k+ records, so it is read once per change rather than once per figure.
public sealed class AgentStats
{
    /// Records this recent are kept for the limit windows (5 hours long) and the Claude app's reset estimate (11 hours).
    public static readonly TimeSpan RecentSpan = TimeSpan.FromHours(12);

    /// The day the totals are for; a new day needs a new computation.
    public DateTime Day { get; private init; }
    public PeriodTotal Today { get; private init; } = PeriodTotal.Empty;
    public PeriodTotal Week { get; private init; } = PeriodTotal.Empty;
    public PeriodTotal Month { get; private init; } = PeriodTotal.Empty;
    public PeriodTotal AllTime { get; private init; } = PeriodTotal.Empty;
    /// Cost per day for the last 7 and 30 days (oldest first), and per hour of today.
    public List<double> Daily { get; private init; } = [];
    public List<double> Daily30 { get; private init; } = [];
    public List<double> Hourly { get; private init; } = [];
    /// The activity grid's first day: the Sunday 52 weeks before this week's, so the grid is 53 whole columns.
    public DateTime ActivityFrom { get; private init; }
    /// Cost per day from ActivityFrom to today, oldest first.
    public List<double> Activity { get; private init; } = [];
    /// The most valuable models over the whole history.
    public List<RankedRow> AllTimeModels { get; private init; } = [];
    public DateTime? LastActivity { get; private init; }
    /// Cost since the 1st of the month, per profile that wrote it.
    public Dictionary<string, double> SeatMonthCost { get; private init; } = new();
    /// Requests of the last RecentSpan before the computation, in record order.
    public List<TimedCost> Recent { get; private init; } = [];

    private sealed class Totals
    {
        public long Tokens;
        public double Cost;
        public int Requests, Unpriced;
        public PeriodTotal ToPeriod() => new(Tokens, Cost, Requests, Unpriced);
    }

    public static DateTime ActivityStart(DateTime today) => today.AddDays(-(int)today.DayOfWeek - 52 * 7);

    public static AgentStats Compute(IEnumerable<AgentRecord> records, DateTime now)
    {
        var today = now.Date;
        var weekFrom = today.AddDays(-6);
        var monthFrom = today.AddDays(-29);
        var activityFrom = ActivityStart(today);
        var calendarFrom = new DateTime(now.Year, now.Month, 1);
        var recentFrom = now - RecentSpan;

        Totals todayT = new(), weekT = new(), monthT = new(), allT = new();
        DateTime? last = null;
        var daily30 = new double[30];
        var hourly = new double[24];
        var activity = new double[(today - activityFrom).Days + 1];
        var models = new Dictionary<string, (long Tokens, double Cost)>();
        var seatMonth = new Dictionary<string, double>();
        var recent = new List<TimedCost>();
        var names = new Dictionary<string, string>();

        foreach (var r in records)
        {
            var cost = r.Cost ?? 0;
            var tokens = r.Tokens;
            var time = r.Time;
            var date = time.Date;

            Add(allT, r, cost, tokens);
            if (time >= monthFrom) Add(monthT, r, cost, tokens);
            if (time >= weekFrom) Add(weekT, r, cost, tokens);
            if (time >= today) Add(todayT, r, cost, tokens);
            if (time >= calendarFrom) seatMonth[r.Seat] = seatMonth.GetValueOrDefault(r.Seat) + cost;
            if (last is null || time > last) last = time;

            var day = (date - monthFrom).Days;
            if (day is >= 0 and < 30) daily30[day] += cost;
            if (time >= today && time < today.AddHours(24)) hourly[(int)((time - today).Ticks / TimeSpan.TicksPerHour)] += cost;
            var cell = (date - activityFrom).Days;
            if (cell >= 0 && cell < activity.Length) activity[cell] += cost;

            if (!names.TryGetValue(r.Model, out var name)) names[r.Model] = name = ModelDisplay(r.Model);
            var m = models.GetValueOrDefault(name);
            models[name] = (m.Tokens + tokens, m.Cost + cost);

            if (time >= recentFrom) recent.Add(new TimedCost(r.Seat, time, cost));
        }

        return new AgentStats
        {
            Day = today,
            Today = todayT.ToPeriod(),
            Week = weekT.ToPeriod(),
            Month = monthT.ToPeriod(),
            AllTime = allT.ToPeriod(),
            // The last 7 of the 30 days.
            Daily = daily30[23..].ToList(),
            Daily30 = [.. daily30],
            Hourly = [.. hourly],
            ActivityFrom = activityFrom,
            Activity = [.. activity],
            // The three costliest, then the most tokens; ties keep the order the models first appeared in.
            AllTimeModels = models.Select(kv => new RankedRow(kv.Key, kv.Value.Tokens, kv.Value.Cost))
                .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList(),
            LastActivity = last,
            SeatMonthCost = seatMonth,
            Recent = recent,
        };
    }

    /// Cost inside a limit window: from its start (reset minus its length) until now.
    public static double WindowCost(IEnumerable<TimedCost> requests, DateTime? resetsAt, TimeSpan length) =>
        resetsAt is { } reset ? requests.Where(r => r.Time >= reset - length).Sum(r => r.Cost) : 0;

    private static void Add(Totals t, AgentRecord r, double cost, long tokens)
    {
        t.Tokens += tokens;
        t.Cost += cost;
        t.Requests++;
        if (r.Cost is null) t.Unpriced++;
    }

    /// "claude-opus-5-5-20260101" → "Opus 5.5".
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
        return string.Join(' ', m.Split('-').Select(Capitalize));
    }

    public static string Capitalize(string? s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];
}
