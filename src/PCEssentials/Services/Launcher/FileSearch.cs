using System.Data.OleDb;
using PCEssentials.Core;

namespace PCEssentials.Services.Launcher;

public sealed record FoundFile(string Path, string Name, bool IsFolder);

/// Files and folders by name from the Windows Search index (what Start menu search uses), so results come
/// back in milliseconds without crawling the disk. Only indexed locations are found.
public static class FileSearch
{
    private const string Connection = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public static Task<List<FoundFile>> FindAsync(string query, int max, CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<FoundFile>();
        query = query.Trim();
        if (query.Length < 2) return list;
        // Single quotes end the SQL string; wildcards and quotes inside CONTAINS terms are dropped.
        var like = query.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(c => c is not ('"' or '\'' or '*' or '(' or ')')).ToArray()))
            .Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return list;
        var contains = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
        var sql = $"""
            SELECT TOP {max * 3} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType
            FROM SYSTEMINDEX
            WHERE SCOPE='file:' AND (System.ItemNameDisplay LIKE '{like}%' OR CONTAINS(System.FileName, '{contains.Replace("'", "''")}'))
              AND NOT System.ItemPathDisplay LIKE '%\AppData\%'
              AND NOT System.ItemPathDisplay LIKE '%\node_modules\%'
              AND NOT System.ItemPathDisplay LIKE '%\.git\%'
            ORDER BY System.DateModified DESC
            """;
        try
        {
            using var conn = new OleDbConnection(Connection);
            conn.Open();
            using var cmd = new OleDbCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            while (reader is not null && reader.Read())
            {
                if (ct.IsCancellationRequested) break;
                if (reader.GetValue(0) is not string path || reader.GetValue(1) is not string name) continue;
                var type = reader.GetValue(2) as string;
                list.Add(new FoundFile(path, name, string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Error("launcher file search", ex); }
        return list;
    }, ct);
}
