using PCEssentials.Services.Agents;
using Xunit;

namespace PCEssentials.Tests;

/// AgentStats works everything out in one pass over the records. The plain LINQ below is the reference
/// it is checked against: the same totals, buckets, rankings and orders.
public class AgentStatsTests
{
    private static readonly string[] Models =
        ["claude-opus-5-5", "claude-sonnet-5-5-20260101", "claude-haiku-5-5[1m]", "claude-opus-4-1@20250805", ""];
    private static readonly string[] Projects = ["", "PCEssentials", "web", "api", "notes"];
    private static readonly string[] Seats = ["default", ".claude-seat1", ".claude-seat2"];

    private static List<AgentRecord> Records(int seed, DateTime now, int count)
    {
        var random = new Random(seed);
        var list = new List<AgentRecord>();
        for (var i = 0; i < count; i++)
        {
            // Mostly the last 400 days, some today, a few in the future (clock skew, other time zones).
            var time = random.Next(10) switch
            {
                0 => now.Date.AddMinutes(random.Next(24 * 60)),
                1 => now.AddMinutes(random.Next(1, 600)),
                2 => now.AddMinutes(-random.Next(13 * 60)),
                _ => now.AddMinutes(-random.Next(400 * 24 * 60)),
            };
            list.Add(new AgentRecord
            {
                Time = time,
                Model = Models[random.Next(Models.Length)],
                Project = Projects[random.Next(Projects.Length)],
                Seat = Seats[random.Next(Seats.Length)],
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
        { 6, "2026-11-08 18:00" }, // a Sunday: the activity grid's last column holds one day
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesPlainLinq(int seed, string at)
    {
        var now = DateTime.Parse(at);
        var records = Records(seed, now, 4000);
        var stats = AgentStats.Compute(records, now);
        var today = now.Date;

        Assert.Equal(Totals(records, today), stats.Today);
        Assert.Equal(Totals(records, today.AddDays(-6)), stats.Week);
        Assert.Equal(Totals(records, today.AddDays(-29)), stats.Month);
        Assert.Equal(Totals(records, DateTime.MinValue), stats.AllTime);

        Assert.Equal(Buckets(records, 7, i => (today.AddDays(i - 6), today.AddDays(i - 5))), stats.Daily);
        Assert.Equal(Buckets(records, 30, i => (today.AddDays(i - 29), today.AddDays(i - 28))), stats.Daily30);
        Assert.Equal(Buckets(records, 24, i => (today.AddHours(i), today.AddHours(i + 1))), stats.Hourly);

        // 53 weekly columns starting on a Sunday and ending today.
        Assert.Equal(DayOfWeek.Sunday, stats.ActivityFrom.DayOfWeek);
        Assert.Equal(53, (stats.Activity.Count + 6) / 7);
        Assert.Equal(today, stats.ActivityFrom.AddDays(stats.Activity.Count - 1));
        var byDay = records.GroupBy(r => r.Time.Date).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost ?? 0));
        Assert.Equal(Enumerable.Range(0, stats.Activity.Count).Select(i => byDay.GetValueOrDefault(stats.ActivityFrom.AddDays(i))), stats.Activity);

        Assert.Equal(records.GroupBy(r => AgentStats.ModelDisplay(r.Model))
            .Select(g => new RankedRow(g.Key, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0)))
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Tokens).Take(3).ToList(), stats.AllTimeModels);
        Assert.Equal(records.Max(r => r.Time), stats.LastActivity);

        // Per seat, as Publish folds them: month cost, and the cost inside a limit window ending in the future.
        var monthStart = new DateTime(now.Year, now.Month, 1);
        foreach (var seat in Seats)
        {
            var mine = records.Where(r => r.Seat == seat).ToList();
            Assert.Equal(mine.Where(r => r.Time >= monthStart).Sum(r => r.Cost ?? 0), stats.SeatMonthCost.GetValueOrDefault(seat), 9);
            foreach (var resetIn in new[] { 1, 90, 299 })
            {
                var reset = now.AddMinutes(resetIn);
                var expected = mine.Where(r => r.Time >= reset - TimeSpan.FromHours(5)).Sum(r => r.Cost ?? 0);
                Assert.Equal(expected, AgentStats.WindowCost(stats.Recent.Where(r => r.Seat == seat), reset, TimeSpan.FromHours(5)), 9);
            }
            Assert.Equal(mine.Where(r => r.Time > now.AddHours(-11)).Select(r => r.Time),
                stats.Recent.Where(r => r.Seat == seat && r.Time > now.AddHours(-11)).Select(r => r.Time));
        }
        Assert.Equal(0, AgentStats.WindowCost(stats.Recent, null, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void EmptyHistory()
    {
        var stats = AgentStats.Compute([], new DateTime(2026, 11, 6, 9, 0, 0));
        Assert.Equal(PeriodTotal.Empty, stats.AllTime);
        Assert.Equal(7, stats.Daily.Count);
        Assert.Equal(30, stats.Daily30.Count);
        Assert.Equal(24, stats.Hourly.Count);
        Assert.Equal(53 * 7 - 1, stats.Activity.Count); // Friday: Saturday's cell is still to come
        Assert.All(stats.Activity, v => Assert.Equal(0, v));
        Assert.Empty(stats.AllTimeModels);
        Assert.Null(stats.LastActivity);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-5-5-20260101", "Sonnet 5.5")]
    [InlineData("claude-haiku-5-5[1m]", "Haiku 5.5")]
    [InlineData("claude-opus-4-1@20250805", "Opus 4.1")]
    public void ModelDisplay(string model, string expected) => Assert.Equal(expected, AgentStats.ModelDisplay(model));

    private static PeriodTotal Totals(List<AgentRecord> records, DateTime from)
    {
        var picked = records.Where(r => r.Time >= from).ToList();
        return new PeriodTotal(picked.Sum(r => r.Tokens), picked.Sum(r => r.Cost ?? 0), picked.Count, picked.Count(r => r.Cost is null));
    }

    private static List<double> Buckets(List<AgentRecord> records, int count, Func<int, (DateTime From, DateTime To)> range) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var (from, to) = range(i);
            return records.Where(r => r.Time >= from && r.Time < to).Sum(r => r.Cost ?? 0);
        }).ToList();
}
