using System.Text;
using PCEssentials.Services.Agents;
using Xunit;

namespace PCEssentials.Tests;

public sealed class AgentLogReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pce-logs-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static List<string> Read(AgentLogReader reader, string path, ref long offset)
    {
        var lines = new List<string>();
        offset = reader.Read(path, offset, line => lines.Add(Encoding.UTF8.GetString(line.Span)));
        return lines;
    }

    [Theory]
    [InlineData("0b6f3c1e-8d2a.jsonl", true)]
    [InlineData("0B6F3C1E.JSONL", true)]
    [InlineData(@"C:\Users\me\AgentLogs\mac-claude-projects\-Users-me-code-rollout-tool\0b6f3c1e.jsonl", true)]
    [InlineData(@"C:\Users\me\AgentLogs\mac-codex-sessions\2026\10\29\rollout-2026-10-29T22-11-39-01a1.jsonl", false)]
    [InlineData("/Users/me/.codex/archived_sessions/rollout-2026-10-29T22-11-39-01a1.jsonl", false)]
    [InlineData("rollout-1.jsonl.zst", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.json", false)]
    public void IsLog(string path, bool expected) => Assert.Equal(expected, AgentLogReader.IsLog(path));

    [Fact]
    public void PlainLogIsReadFromWhereItStopped()
    {
        var path = Path.Combine(_dir, "a.jsonl");
        File.WriteAllText(path, "one\ntwo\npart");
        var reader = new AgentLogReader();
        long offset = 0;

        Assert.Equal(["one", "two"], Read(reader, path, ref offset));
        Assert.Equal(8, offset);

        File.AppendAllText(path, "ial\nthree\n");
        Assert.Equal(["partial", "three"], Read(reader, path, ref offset));
        Assert.Empty(Read(reader, path, ref offset));
    }

    [Fact]
    public void PlainLogThatShrankIsReadAgain()
    {
        var path = Path.Combine(_dir, "a.jsonl");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var reader = new AgentLogReader();
        long offset = 0;
        Read(reader, path, ref offset);

        File.WriteAllText(path, "new\n");
        Assert.Equal(["new"], Read(reader, path, ref offset));
    }

    [Fact]
    public void OverlongLinesAreSkipped()
    {
        var path = Path.Combine(_dir, "a.jsonl");
        File.WriteAllText(path, "short\n" + new string('x', 5_000_000) + "\nafter\n");
        var reader = new AgentLogReader(maxLineBytes: 2 << 20);
        long offset = 0;

        Assert.Equal(["short", "after"], Read(reader, path, ref offset));
    }
}
