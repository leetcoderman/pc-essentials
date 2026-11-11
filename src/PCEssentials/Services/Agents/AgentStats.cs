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

/// Unpriced counts the model's requests the price list has no price for, which are left out of Cost.
public sealed record RankedRow(string Name, long Tokens, double Cost, int Unpriced = 0)
{
    /// Tokens but no value at all: none of the model's requests could be priced.
    public bool NoPrice => Unpriced > 0 && Cost <= 0;
}

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
    /// Whether each bucket of Daily, Daily30 and Hourly holds a request without a known price: its
    /// cost is then a minimum, and a bucket worth nothing still saw work.
    public List<bool> DailyUnpriced { get; private init; } = [];
    public List<bool> Daily30Unpriced { get; private init; } = [];
    public List<bool> HourlyUnpriced { get; private init; } = [];
    /// The activity grid's first day: the Sunday 52 weeks before this week's, so the grid is 53 whole columns.
    public DateTime ActivityFrom { get; private init; }
    /// Cost per day from ActivityFrom to today, oldest first.
    public List<double> Activity { get; private init; } = [];
    /// The same for each day of Activity.
    public List<bool> ActivityUnpriced { get; private init; } = [];
    /// The models the Spend card lists over the whole history, as RankModels picks them.
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
        var daily30Unpriced = new bool[30];
        var hourlyUnpriced = new bool[24];
        var activityUnpriced = new bool[activity.Length];
        var models = new Dictionary<string, (long Tokens, double Cost, int Unpriced)>();
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

            var unpriced = r.Cost is null;
            var day = (date - monthFrom).Days;
            if (day is >= 0 and < 30)
            {
                daily30[day] += cost;
                daily30Unpriced[day] |= unpriced;
            }
            if (time >= today && time < today.AddHours(24))
            {
                var hour = (int)((time - today).Ticks / TimeSpan.TicksPerHour);
                hourly[hour] += cost;
                hourlyUnpriced[hour] |= unpriced;
            }
            var cell = (date - activityFrom).Days;
            if (cell >= 0 && cell < activity.Length)
            {
                activity[cell] += cost;
                activityUnpriced[cell] |= unpriced;
            }

            if (!names.TryGetValue(r.Model, out var name)) names[r.Model] = name = ModelDisplay(r.Model);
            var m = models.GetValueOrDefault(name);
            models[name] = (m.Tokens + tokens, m.Cost + cost, m.Unpriced + (unpriced ? 1 : 0));

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
            DailyUnpriced = daily30Unpriced[23..].ToList(),
            Daily30Unpriced = [.. daily30Unpriced],
            HourlyUnpriced = [.. hourlyUnpriced],
            ActivityFrom = activityFrom,
            Activity = [.. activity],
            ActivityUnpriced = [.. activityUnpriced],
            AllTimeModels = RankModels(models.Select(kv => new RankedRow(kv.Key, kv.Value.Tokens, kv.Value.Cost, kv.Value.Unpriced))),
            LastActivity = last,
            SeatMonthCost = seatMonth,
            Recent = recent,
        };
    }

    /// The Spend card's model rows, the same way the Mac app picks them: the costliest first, then the
    /// models the price list does not know, which have tokens but no value. Such a model always keeps a
    /// row (up to half the rows, two at most, the most tokens first) so new work never vanishes behind
    /// the priced models; a model with nothing counted is left out. Ties go by name.
    public static List<RankedRow> RankModels(IEnumerable<RankedRow> models, int limit = 3)
    {
        var all = models.ToList();
        var unknown = all.Where(r => r.NoPrice)
            .OrderByDescending(r => r.Tokens).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(Math.Min(2, Math.Max(0, limit) / 2)).ToList();
        var priced = all.Where(r => r.Cost > 0)
            .OrderByDescending(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, limit - unknown.Count));
        return [.. priced, .. unknown];
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
