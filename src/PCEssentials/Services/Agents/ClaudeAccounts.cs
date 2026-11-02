using System.Text.Json;

namespace PCEssentials.Services.Agents;

/// Which Claude account a reading belongs to. Every seat of a Team shares one organization, so the
/// account's own id (oauthAccount.accountUuid in a profile, lastKnownAccountUuid in the Claude app's
/// config.json) is what tells them apart.
public static class ClaudeAccounts
{
    /// The account the Claude app is signed in to, from its config.json. Null when it can't be told.
    public static string? AppAccount(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("lastKnownAccountUuid", out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } id ? id : null;
        }
        catch (JsonException) { return null; }
    }

    /// The app's usage readings belong to the seat signed in to the app's account, and to no other.
    public static bool SameAccount(string? seatAccount, string? appAccount) =>
        seatAccount is not null && appAccount is not null && string.Equals(seatAccount, appAccount, StringComparison.OrdinalIgnoreCase);

    /// Claude Code refreshes the profile's saved usage copy whenever it reaches Anthropic, and answers from
    /// that copy without asking while it is seconds old. When the check is turned away (a 429), it answers
    /// with the saved copy too, however old: older than that, the answer is not current.
    public static readonly TimeSpan SnapshotReuse = TimeSpan.FromMinutes(2);

    public static bool AnsweredLive(DateTime checkStarted, DateTime? savedAt) =>
        savedAt is { } at && at >= checkStarted - SnapshotReuse;

    /// Seats in the order the person set (by email), then the rest in the order they came.
    public static List<T> Ordered<T>(IEnumerable<T> seats, Func<T, string?> email, IList<string> order)
    {
        int Rank(T s)
        {
            var e = email(s);
            for (var i = 0; e is not null && i < order.Count; i++)
                if (order[i].Equals(e, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
        return seats.Select((s, i) => (s, i)).OrderBy(x => Rank(x.s)).ThenBy(x => x.i).Select(x => x.s).ToList();
    }

    /// The new order after moving one seat a place left (-1) or right (+1) among the seats shown.
    public static List<string> Move(IList<string> shown, string email, int by)
    {
        var list = shown.ToList();
        var i = list.FindIndex(e => e.Equals(email, StringComparison.OrdinalIgnoreCase));
        var j = i + by;
        if (i < 0 || j < 0 || j >= list.Count) return list;
        (list[i], list[j]) = (list[j], list[i]);
        return list;
    }

    /// Seats signed in to an account an earlier seat already uses, each with that earlier seat's id.
    public static Dictionary<string, string> Duplicates(IEnumerable<(string Id, string? Account)> seats)
    {
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dupes = new Dictionary<string, string>();
        foreach (var (id, account) in seats)
        {
            if (account is null) continue;
            if (first.TryGetValue(account, out var earlier)) dupes[id] = earlier;
            else first[account] = id;
        }
        return dupes;
    }
}
