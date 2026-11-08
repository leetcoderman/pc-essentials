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
}
