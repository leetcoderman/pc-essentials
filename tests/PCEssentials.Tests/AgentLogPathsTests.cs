using PCEssentials.Services.Agents;
using Xunit;

namespace PCEssentials.Tests;

public class AgentLogPathsTests
{
    [Theory]
    [InlineData(@"C:\code\app", "app")]
    [InlineData(@"C:\code\app\", "app")]
    [InlineData("/Users/me/code/app", "app")]
    [InlineData("/Users/me/code/app/", "app")]
    [InlineData(@"C:\code\app\.claude\worktrees\fix-bug", "app")]
    [InlineData("/Users/me/code/app/.claude/worktrees/fix-bug", "app")]
    [InlineData(@"C:\", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ProjectName(string? cwd, string expected) => Assert.Equal(expected, AgentLogPaths.ProjectName(cwd));

    [Theory]
    [InlineData(@"C:\Users\me\AgentLogs\mac-codex-sessions\2026\10\29\rollout-2026-10-29T22-11-39-01a1.jsonl", true)]
    [InlineData("/Users/me/.codex/archived_sessions/rollout-2026-10-29T22-11-39-01a1.jsonl", true)]
    [InlineData(@"C:\Users\me\AgentLogs\mac-claude-projects\-Users-me-code-app\0b6f3c1e-8d2a.jsonl", false)]
    [InlineData(@"C:\Users\me\AgentLogs\mac-claude-projects\-Users-me-code-rollout-tool\0b6f3c1e.jsonl", false)]
    public void IsCodexLog(string path, bool expected) => Assert.Equal(expected, AgentLogPaths.IsCodexLog(path));
}
