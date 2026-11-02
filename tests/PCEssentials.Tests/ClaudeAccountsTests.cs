using PCEssentials.Services.Agents;
using Xunit;

namespace PCEssentials.Tests;

public class ClaudeAccountsTests
{
    private const string Dexter = "96a8fdb4-118b-40cd-aeda-74469d2b8a44";
    private const string Hello = "69a969fe-8ccf-4df6-829f-dfa42baffd48";

    [Fact]
    public void AppAccountReadsLastKnownAccount() =>
        Assert.Equal(Hello, ClaudeAccounts.AppAccount($$"""{"locale":"en-US","lastKnownAccountUuid":"{{Hello}}","oauth:tokenCache":"x"}"""));

    [Theory]
    [InlineData("""{"locale":"en-US"}""")]
    [InlineData("""{"lastKnownAccountUuid":""}""")]
    [InlineData("""{"lastKnownAccountUuid":null}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void AppAccountUnknown(string json) => Assert.Null(ClaudeAccounts.AppAccount(json));

    // The app on hello@ must not lend its readings to the dexter@ profile, though both are in one Team org.
    [Theory]
    [InlineData(Hello, Hello, true)]
    [InlineData(Dexter, Hello, false)]
    [InlineData(Dexter, null, false)]
    [InlineData(null, Hello, false)]
    [InlineData(null, null, false)]
    public void SameAccount(string? seat, string? app, bool expected) => Assert.Equal(expected, ClaudeAccounts.SameAccount(seat, app));

    [Fact]
    public void SameAccountIgnoresCase() => Assert.True(ClaudeAccounts.SameAccount(Hello.ToUpperInvariant(), Hello));

    [Fact]
    public void FreshSavedCopyMeansLiveAnswer()
    {
        var started = new DateTime(2026, 11, 2, 23, 34, 10);
        Assert.True(ClaudeAccounts.AnsweredLive(started, started.AddSeconds(4)));
        Assert.True(ClaudeAccounts.AnsweredLive(started, started.AddSeconds(-1)));
    }

    // Claude Code answers from a snapshot seconds old without asking; that is still current.
    [Fact]
    public void RecentSnapshotCountsAsLive()
    {
        var started = new DateTime(2026, 11, 3, 0, 0, 25);
        Assert.True(ClaudeAccounts.AnsweredLive(started, new DateTime(2026, 11, 3, 0, 0, 16)));
        Assert.False(ClaudeAccounts.AnsweredLive(started, started.AddMinutes(-5)));
    }

    // A 429 leaves the 23:06 copy in place, and get_usage hands it back as if it were new.
    [Fact]
    public void OlderSavedCopyMeansTurnedAway()
    {
        var started = new DateTime(2026, 11, 2, 23, 35, 40);
        Assert.False(ClaudeAccounts.AnsweredLive(started, new DateTime(2026, 11, 2, 23, 6, 2)));
        Assert.False(ClaudeAccounts.AnsweredLive(started, null));
    }

    [Fact]
    public void DuplicatesNameTheEarlierSeat()
    {
        var dupes = ClaudeAccounts.Duplicates([("default", Dexter), (".claude-seat1", Dexter), (".claude-seat2", Hello), (".claude-seat3", null)]);
        Assert.Equal(new Dictionary<string, string> { [".claude-seat1"] = "default" }, dupes);
    }

    [Fact]
    public void OrderedPutsSetOrderFirst()
    {
        string[] seats = ["dexter@caratos.co.in", "shikhar@caratos.co.in", "hello@caratos.co.in"];
        Assert.Equal(["shikhar@caratos.co.in", "dexter@caratos.co.in", "hello@caratos.co.in"],
            ClaudeAccounts.Ordered(seats, s => s, ["Shikhar@caratos.co.in", "dexter@caratos.co.in", "hello@caratos.co.in"]));
    }

    [Fact]
    public void OrderedKeepsUnlistedSeatsAfterInTheirOrder()
    {
        string?[] seats = ["dexter@x", null, "new@x", "hello@x"];
        Assert.Equal(["hello@x", "dexter@x", null, "new@x"], ClaudeAccounts.Ordered(seats, s => s, ["hello@x", "gone@x"]));
        Assert.Equal(seats, ClaudeAccounts.Ordered(seats, s => s, []));
    }

    [Theory]
    [InlineData("b", -1, "b,a,c")]
    [InlineData("b", 1, "a,c,b")]
    [InlineData("a", -1, "a,b,c")]
    [InlineData("c", 1, "a,b,c")]
    [InlineData("z", 1, "a,b,c")]
    public void Move(string email, int by, string expected) =>
        Assert.Equal(expected.Split(','), ClaudeAccounts.Move(["a", "b", "c"], email, by));

    [Fact]
    public void NoDuplicatesWhenAccountsDiffer() =>
        Assert.Empty(ClaudeAccounts.Duplicates([("default", Dexter), (".claude-seat1", Hello), (".claude-seat2", null), (".claude-seat3", null)]));
}
