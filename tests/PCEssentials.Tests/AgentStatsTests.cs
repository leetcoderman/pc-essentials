using PCEssentials.Services.Agents;
using Xunit;

namespace PCEssentials.Tests;

/// AgentStats works everything out in one pass over the records. The plain LINQ below is the reference
/// it is checked against: the same totals, buckets, rankings and orders.
public class AgentStatsTests
{
    private static readonly string[] Models =
        ["claude-opus-5-5", "claude-sonnet-5-5-20260101", "claude-haiku-5-5[1m]", "claude-opus-4-1@20250805", "claude-fable-5-2", ""];
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

        // The three costliest models; no model here is wholly unpriced, so none takes a row for that.
        Assert.Equal(records.GroupBy(r => AgentStats.ModelDisplay(r.Model))
            .Select(g => new RankedRow(g.Key, g.Sum(r => r.Tokens), g.Sum(r => r.Cost ?? 0), g.Count(r => r.Cost is null)))
            .OrderByDescending(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal).Take(3).ToList(), stats.AllTimeModels);

        // Which buckets hold a request without a price.
        Assert.Equal(Flags(records, 7, i => (today.AddDays(i - 6), today.AddDays(i - 5))), stats.DailyUnpriced);
        Assert.Equal(Flags(records, 30, i => (today.AddDays(i - 29), today.AddDays(i - 28))), stats.Daily30Unpriced);
        Assert.Equal(Flags(records, 24, i => (today.AddHours(i), today.AddHours(i + 1))), stats.HourlyUnpriced);
        var unpricedDays = records.Where(r => r.Cost is null).Select(r => r.Time.Date).ToHashSet();
        Assert.Equal(Enumerable.Range(0, stats.Activity.Count).Select(i => unpricedDays.Contains(stats.ActivityFrom.AddDays(i))), stats.ActivityUnpriced);
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
    [InlineData("claude-fable-5-2", "Fable 5.2")]
    [InlineData("claude-mythos-5-2", "Mythos 5.2")]
    [InlineData("claude-fable-5-2-20261101", "Fable 5.2")]
    [InlineData("claude-fable-5-2[1m]", "Fable 5.2")]
    [InlineData("claude-mythos-5-2-20261101[1m]", "Mythos 5.2")]
    [InlineData("claude-fable-5-3", "Fable 5.3")] // a model no list knows yet still reads by name
    public void ModelDisplay(string model, string expected) => Assert.Equal(expected, AgentStats.ModelDisplay(model));

    private static PriceList Prices()
    {
        using var json = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "agent-prices.json"));
        return PriceList.Parse(json);
    }

    /// Fable 5.2 and Mythos 5.2 at their published prices (https://platform.claude.com/docs/en/about-claude/pricing),
    /// under every spelling of the id the logs use.
    [Theory]
    [InlineData("claude-fable-5-2")]
    [InlineData("claude-mythos-5-2")]
    [InlineData("claude-fable-5-2-20261101")]
    [InlineData("claude-fable-5-2[1m]")]
    [InlineData("claude-mythos-5-2-20261101[1m]")]
    [InlineData("claude-fable-5.2")]
    [InlineData("us.anthropic.claude-fable-5-2-20261101-v1:0")]
    public void Fable52IsPriced(string model)
    {
        var price = Prices().Match(model);
        Assert.NotNull(price);
        Assert.Equal((8d, 40d, 0.2, 10d, 16d, 2d), (price.Input, price.Output, price.CacheRead, price.CacheWrite ?? -1, price.CacheWriteLong ?? -1, price.FastMultiplier ?? -1));
    }

    [Fact]
    public void Fable52CostsWhatItsTokensBill()
    {
        var prices = Prices();
        // The shape of a real response: a little input, a one-hour cache write, a cache read and some output.
        var record = new AgentRecord { Model = "claude-fable-5-2", Input = 2, Output = 591, CacheRead = 28132, CacheWrite = 21603, CacheWriteLong = 21603 };
        Assert.Equal((2 * 8 + 21603 * 16 + 28132 * 0.2 + 591 * 40) / 1e6, prices.Cost(record)!.Value, 12);
        var fast = new AgentRecord { Model = "claude-fable-5-2", Input = 1_000_000, Fast = true };
        Assert.Equal(16d, prices.Cost(fast)!.Value, 12);
        // 5.2 does not borrow 5.1's or 5's price, nor they its.
        Assert.Equal(10d, prices.Match("claude-fable-5-1")!.Input);
        Assert.Equal(1d, prices.Match("claude-fable-5")!.CacheRead);
    }

    [Theory]
    [InlineData("claude-fable-5-3")]
    [InlineData("claude-opus-5-6")]
    [InlineData("claude-opus-6")]
    [InlineData("")]
    public void UnknownModelHasNoPrice(string model)
    {
        var prices = Prices();
        Assert.Null(prices.Match(model));
        Assert.Null(prices.Cost(new AgentRecord { Model = model, Input = 1000, Output = 500 }));
    }

    /// A model the price list does not know does not vanish: its requests and tokens count, the totals
    /// say they are a minimum, its day is marked, and it keeps a row among the models.
    [Fact]
    public void UnknownModelStaysVisible()
    {
        var now = new DateTime(2026, 11, 11, 15, 0, 0);
        var prices = Prices();
        AgentRecord Record(string model, DateTime time, long output)
        {
            var r = new AgentRecord { Time = time, Model = model, Input = 1000, Output = output };
            r.Cost = prices.Cost(r);
            return r;
        }
        var records = new List<AgentRecord>
        {
            Record("claude-opus-5-5", now.AddDays(-2), 1_000_000),
            Record("claude-sonnet-5-5", now.AddDays(-2), 1_000_000),
            Record("claude-haiku-5-5", now.AddDays(-2), 1_000_000),
            Record("claude-fable-5-2", now.AddHours(-1), 1_000_000),
            Record("claude-fable-5-3", now.AddHours(-2), 500),
            Record("claude-fable-5-3", now.AddDays(-3), 500),
        };
        var stats = AgentStats.Compute(records, now);

        Assert.Equal(2, stats.AllTime.Unpriced);
        Assert.Equal(6, stats.AllTime.Requests);
        Assert.Equal(records.Sum(r => r.Tokens), stats.AllTime.Tokens);
        Assert.Equal(1, stats.Today.Unpriced);
        Assert.Equal(40.008, stats.Today.Cost, 9); // Fable 5.2 is priced: 1M output and 1K input

        // Today's bar is a minimum; three days ago is worth nothing but saw work.
        Assert.True(stats.DailyUnpriced[6]);
        Assert.True(stats.DailyUnpriced[3]);
        Assert.Equal(0d, stats.Daily[3]);
        Assert.False(stats.DailyUnpriced[4]);
        Assert.True(stats.HourlyUnpriced[13]);
        Assert.True(stats.ActivityUnpriced[^1]);
        Assert.True(stats.ActivityUnpriced[^4]);
        Assert.Equal(0d, stats.Activity[^4]);

        // The two costliest, then the unknown model with its tokens and no cost.
        Assert.Equal(["Fable 5.2", "Opus 5.5", "Fable 5.3"], stats.AllTimeModels.Select(r => r.Name).ToList());
        var unknown = stats.AllTimeModels[^1];
        Assert.True(unknown.NoPrice);
        Assert.Equal((3000L, 0d, 2), (unknown.Tokens, unknown.Cost, unknown.Unpriced));
        Assert.All(stats.AllTimeModels.Take(2), r => Assert.False(r.NoPrice));
    }

    [Fact]
    public void RankModelsKeepsARowForUnpricedModels()
    {
        RankedRow[] priced = [new("Opus 5.5", 9000, 900), new("Sonnet 5.5", 800, 40), new("Haiku 5.5", 700, 3), new("Opus 4.8", 20, 1)];
        RankedRow[] unknown = [new("Fable 5.3", 50, 0, 12), new("Aaa 9", 90, 0, 1), new("Bbb 9", 5, 0, 1)];

        Assert.Equal(["Opus 5.5", "Sonnet 5.5", "Haiku 5.5"], AgentStats.RankModels(priced).Select(r => r.Name).ToList());
        Assert.Equal(["Opus 5.5", "Sonnet 5.5", "Fable 5.3"], AgentStats.RankModels([unknown[0], .. priced]).Select(r => r.Name).ToList());
        // Up to half the rows, two at most, the most tokens first.
        Assert.Equal(["Opus 5.5", "Sonnet 5.5", "Aaa 9"], AgentStats.RankModels([.. unknown, .. priced]).Select(r => r.Name).ToList());
        Assert.Equal(["Opus 5.5", "Sonnet 5.5", "Aaa 9", "Fable 5.3"], AgentStats.RankModels([.. unknown, .. priced], 4).Select(r => r.Name).ToList());
        // Nothing counted, no row; partly priced shows its value.
        Assert.Empty(AgentStats.RankModels([new("Idle", 0, 0)]));
        Assert.False(new RankedRow("Mixed", 5, 2, 3).NoPrice);
    }

    private static PeriodTotal Totals(List<AgentRecord> records, DateTime from)
    {
        var picked = records.Where(r => r.Time >= from).ToList();
        return new PeriodTotal(picked.Sum(r => r.Tokens), picked.Sum(r => r.Cost ?? 0), picked.Count, picked.Count(r => r.Cost is null));
    }

    private static List<bool> Flags(List<AgentRecord> records, int count, Func<int, (DateTime From, DateTime To)> range) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var (from, to) = range(i);
            return records.Any(r => r.Time >= from && r.Time < to && r.Cost is null);
        }).ToList();

    private static List<double> Buckets(List<AgentRecord> records, int count, Func<int, (DateTime From, DateTime To)> range) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var (from, to) = range(i);
            return records.Where(r => r.Time >= from && r.Time < to).Sum(r => r.Cost ?? 0);
        }).ToList();
}
