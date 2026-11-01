namespace PCEssentials.Services.Agents;

/// Path rules for agent logs, which may have been written on Windows or copied from a Mac.
public static class AgentLogPaths
{
    /// Codex names every session log "rollout-<time>-<id>.jsonl"; Claude Code names its logs by session id.
    public static bool IsCodexLog(string path)
    {
        var name = path[(path.Replace('\\', '/').LastIndexOf('/') + 1)..];
        return name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);
    }

    /// The project folder's name from a working directory such as "C:\code\app" or "/Users/me/code/app".
    /// A Claude worktree counts as the project it was made from.
    public static string ProjectName(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var path = cwd.Replace('\\', '/');
        var marker = path.IndexOf("/.claude/worktrees", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) path = path[..marker];
        path = path.TrimEnd('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(':') ? "" : name;
    }
}
