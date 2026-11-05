using PCEssentials.Services.Agents;
using Xunit;

namespace PCEssentials.Tests;

/// AgentStats replaced ~65 LINQ passes over every record. The reference below is that old code, kept to check
/// the single pass gives the same totals, buckets, rankings and orders.
public class AgentStatsTests
{
    private static readonly string[] Models =
        ["claude-opus-5-5", "claude-sonnet-5-5-20260101", "claude-haiku-5-5[1m]", "gpt-6-sol", "gpt-5.6-luna", "o9-mini", ""];
    private static readonly string[] Projects = ["", "PCEssentials", "web", "api", "notes"];
    private static readonly string[] Seats = ["default", ".claude-seat1", ".claude-seat2"];

    private static List<AgentRecord> Records(int seed, DateTime now, int count)
    {
        var random = new Random(seed);
        var list = new List<AgentRecord>();
        for (var i = 0; i < count; i++)
        {
            var provider = random.Next(3) == 0 ? AgentProvider.Codex : AgentProvider.Claude;
            // Mostly the last 100 days, some today, a few in the future (clock skew, other time zones).
            var time = random.Next(10) switch
            {
                0 => now.Date.AddMinutes(random.Next(24 * 60)),
                1 => now.AddMinutes(random.Next(1, 600)),
                2 => now.AddMinutes(-random.Next(13 * 60)),
                _ => now.AddMinutes(-random.Next(100 * 24 * 60)),
            };
            list.Add(new AgentRecord
            {
                Provider = provider,
                Time = time,
                Model = Models[random.Next(Models.Length)],
                Project = Projects[random.Next(Projects.Length)],
                Seat = provider == AgentProvider.Claude ? Seats[random.Next(Seats.Length)] : "default",
                Input = random.Next(5000),
                Output = random.Next(2000),
                CacheRead = random.Next(50000),
                CacheWrite = random.Next(3000),
                Cost = random.Next(8) == 0 ? null : random.NextDouble() * 0.4,
            });
        }
        return list;
    }

    public static TheoryData<int, string> Cases => new()
    {
        { 1, "2026-11-06 01:20" },
        { 2, "2026-11-30 23:59" },
        { 3, "2026-12-01 00:05" },
        { 4, "2026-03-29 02:30" }, // a DST change day in many zones
        { 5, "2026-07-15 12:00" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesTheOldPasses(int seed, string at)
    {
        var now = DateTime.Parse(at);
        var records = Records(seed, now, 4000);
        var stats = AgentStats.Compute(records, now);
        var today = now.Date;

        Same(OldTotals(records, today), stats.Today);
        Same(OldTotals(records, today.AddDays(-6)), stats.Week);
        Same(OldTotals(records, today.AddDays(-29)), stats.Month);
        Same(OldTotals(records, DateTime.MinValue), stats.AllTime);

        var daily = Enumerable.Range(0, 7).Select(i => records.Where(r => r.Time.Date == today.AddDays(i - 6))
            .GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0))).ToList();
        Same(daily, stats.Daily);
        Same(OldBuckets(records, 30, i => (today.AddDays(i - 29), today.AddDays(i - 28))), stats.Daily30);
        Same(OldBuckets(records, 24, i => (today.AddHours(i), today.AddHours(i + 1))), stats.Hourly);

        var byDay = records.GroupBy(r => r.Time.Date).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0));
        Assert.Equal(Enumerable.Range(0, 91).Select(i => byDay.GetValueOrDefault(today.AddDays(i - 90))), stats.Heatmap);

        Assert.Equal(OldModels(records), stats.AllTimeModels);
        var month = records.Where(r => r.Time >= today.AddDays(-30)).ToList();
        Assert.Equal(OldModels(month), stats.TopModels);
        Assert.Equal(month.Where(r => r.Project.Length > 0).GroupBy(r => r.Project)
            .Select(g => new RankedRow(g.Key, g.First().Provider, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0)))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList(), stats.TopProjects);

        var monthStart = new DateTime(now.Year, now.Month, 1);
        Same([records.Where(r => r.Time >= monthStart).GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0))],
            [stats.CalendarMonth]);
        Assert.Equal(records.GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Max(r => r.Time)), stats.LastActivity);

        // Per seat, as Publish folds them: month cost, and the cost inside a limit window ending in the future.
        foreach (var seat in Seats)
        {
            var mine = records.Where(r => r.Provider == AgentProvider.Claude && r.Seat == seat).ToList();
            Assert.Equal(mine.Where(r => r.Time >= monthStart).Sum(r => r.Cost ?? 0), stats.ClaudeMonthCost.GetValueOrDefault(seat), 9);
            foreach (var resetIn in new[] { 1, 90, 299 })
            {
                var reset = now.AddMinutes(resetIn);
                var expected = mine.Where(r => r.Time >= reset - TimeSpan.FromHours(5)).Sum(r => r.Cost ?? 0);
                Assert.Equal(expected, AgentStats.WindowCost(stats.ClaudeRecent.Where(r => r.Seat == seat), reset, TimeSpan.FromHours(5)), 9);
            }
            Assert.Equal(mine.Where(r => r.Time > now.AddHours(-11)).Select(r => r.Time),
                stats.ClaudeRecent.Where(r => r.Seat == seat && r.Time > now.AddHours(-11)).Select(r => r.Time));
        }
        var codexReset = now.AddHours(2);
        Assert.Equal(records.Where(r => r.Provider == AgentProvider.Codex && r.Time >= codexReset - TimeSpan.FromHours(5)).Sum(r => r.Cost ?? 0),
            AgentStats.WindowCost(stats.CodexRecent, codexReset, TimeSpan.FromHours(5)), 9);
        Assert.Equal(0, AgentStats.WindowCost(stats.CodexRecent, null, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void EmptyHistory()
    {
        var stats = AgentStats.Compute([], new DateTime(2026, 11, 6, 9, 0, 0));
        Assert.Empty(stats.AllTime);
        Assert.Equal(7, stats.Daily.Count);
        Assert.Equal(30, stats.Daily30.Count);
        Assert.Equal(24, stats.Hourly.Count);
        Assert.Equal(91, stats.Heatmap.Count);
        Assert.All(stats.Heatmap, v => Assert.Equal(0, v));
        Assert.Empty(stats.AllTimeModels);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-5-5-20260101", "Sonnet 5.5")]
    [InlineData("claude-haiku-5-5[1m]", "Haiku 5.5")]
    [InlineData("claude-opus-4-1@20250805", "Opus 4.1")]
    [InlineData("gpt-6-sol", "GPT-6 Sol")]
    [InlineData("gpt-5.6-luna", "GPT-5.6 Luna")]
    [InlineData("o9-mini", "O9 Mini")]
    public void ModelDisplay(string model, string expected) => Assert.Equal(expected, AgentStats.ModelDisplay(model));

    private static Dictionary<AgentProvider, PeriodTotal> OldTotals(List<AgentRecord> records, DateTime from) => records
        .Where(r => r.Time >= from)
        .GroupBy(r => r.Provider)
        .ToDictionary(g => g.Key, g => new PeriodTotal(
            g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0), g.Count(), g.Count(r => r.Cost is null)));

    private static List<Dictionary<AgentProvider, double>> OldBuckets(List<AgentRecord> records, int count, Func<int, (DateTime From, DateTime To)> range) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var (from, to) = range(i);
            return records.Where(r => r.Time >= from && r.Time < to)
                .GroupBy(r => r.Provider).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0));
        }).ToList();

    private static List<RankedRow> OldModels(List<AgentRecord> records) =>
        records.GroupBy(r => (r.Provider, Name: AgentStats.ModelDisplay(r.Model)))
            .Select(g => new RankedRow(g.Key.Name, g.Key.Provider, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0)))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList();

    /// Same keys in the same order, and the same values (sums are taken in the same record order, so exactly equal).
    private static void Same<T>(Dictionary<AgentProvider, T> expected, Dictionary<AgentProvider, T> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (k, v) in expected) Assert.Equal(v, actual[k]);
    }

    private static void Same<T>(List<Dictionary<AgentProvider, T>> expected, List<Dictionary<AgentProvider, T>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Same(expected[i], actual[i]);
    }
}
