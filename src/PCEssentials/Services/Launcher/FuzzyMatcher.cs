using System.Globalization;

namespace PCEssentials.Services.Launcher;

/// Scores how well a typed query matches a name, the way Spotlight and PowerToys Run rank apps: a match at the
/// start of the name beats one at the start of a word, which beats initials ("vsc" → Visual Studio Code),
/// which beats a run of letters inside a word, which beats letters scattered in order. 0 means no match.
/// Case and accents are ignored. A query of several words matches when every word does.
public static class FuzzyMatcher
{
    public static int Score(string query, string name)
    {
        query = Fold(query.Trim());
        var target = Fold(name);
        if (query.Length == 0 || target.Length == 0) return 0;

        if (target == query) return 1000;
        if (target.StartsWith(query, StringComparison.Ordinal)) return 900 - Math.Min(100, target.Length - query.Length);

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            // Every word has to match on its own; the weakest one decides the score.
            var min = int.MaxValue;
            foreach (var w in words)
            {
                var s = Single(w, target);
                if (s == 0) return 0;
                min = Math.Min(min, s);
            }
            return min - 5;
        }
        return Single(query, target);
    }

    private static int Single(string q, string t)
    {
        if (t.StartsWith(q, StringComparison.Ordinal)) return 850 - Math.Min(100, t.Length - q.Length);
        var starts = WordStarts(t);
        foreach (var i in starts)
            if (string.CompareOrdinal(t, i, q, 0, q.Length) == 0) return 750 - Math.Min(100, i);
        if (q.Length >= 2 && IsInitials(q, t, starts)) return 700;
        var at = t.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0) return 550 - Math.Min(100, at);
        // Letters in order with gaps: the tighter the span, the better.
        int pos = 0, first = -1, last = 0;
        foreach (var c in q)
        {
            var found = t.IndexOf(c, pos);
            if (found < 0) return 0;
            if (first < 0) first = found;
            last = found;
            pos = found + 1;
        }
        var spread = last - first + 1 - q.Length;
        return Math.Max(1, 300 - spread * 10 - Math.Min(50, first));
    }

    /// True when the query's letters each begin a word, in order ("vsc" for "visual studio code"),
    /// letting a run of letters stay inside one word ("vscode": "vs" + "code").
    private static bool IsInitials(string q, string t, List<int> starts) => Initials(q, 0, t, starts, 0);

    private static bool Initials(string q, int qi, string t, List<int> starts, int fromWord)
    {
        if (qi == q.Length) return true;
        for (int s = fromWord; s < starts.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : t.Length;
            // Take 1..n letters of the query as the beginning of this word, then go on to later words.
            for (int k = 0; qi + k < q.Length && starts[s] + k < end && t[starts[s] + k] == q[qi + k]; k++)
                if (Initials(q, qi + k + 1, t, starts, s + 1)) return true;
        }
        return false;
    }

    /// Indexes where a word begins: the first letter, after a space or punctuation, and at a lower→upper
    /// change in the original name (PowerShell, OneDrive). Folded text is lower case, so camel case is
    /// recovered from the separators only; that is enough for app names.
    private static List<int> WordStarts(string t)
    {
        var list = new List<int> { 0 };
        for (int i = 1; i < t.Length; i++)
            if (!char.IsLetterOrDigit(t[i - 1]) && char.IsLetterOrDigit(t[i])) list.Add(i);
        return list;
    }

    /// Lower case without accents, so "é" matches "e".
    public static string Fold(string s)
    {
        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
