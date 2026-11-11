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

    /// A response from the newest model, as Claude Code writes it, reaches the parser whole: the reader
    /// passes the line on untouched, with the model id and the usage the price is worked out from.
    [Fact]
    public void Fable52ResponseIsReadWhole()
    {
        const string line = "{\"type\":\"assistant\",\"requestId\":\"req_1\",\"timestamp\":\"2026-11-10T17:43:39.618Z\",\"cwd\":\"Z:\\\\Suite\","
            + "\"message\":{\"id\":\"msg_1\",\"model\":\"claude-fable-5-2\",\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":21603,"
            + "\"cache_read_input_tokens\":28132,\"output_tokens\":591,\"cache_creation\":{\"ephemeral_1h_input_tokens\":21603,\"ephemeral_5m_input_tokens\":0},"
            + "\"speed\":\"standard\",\"inference_geo\":\"global\"}}}";
        var path = Path.Combine(_dir, "0b6f3c1e.jsonl");
        File.WriteAllText(path, line + "\n");
        long offset = 0;

        var read = Assert.Single(Read(new AgentLogReader(), path, ref offset));
        Assert.Equal(line, read);
        using var doc = System.Text.Json.JsonDocument.Parse(read);
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal("claude-fable-5-2", message.GetProperty("model").GetString());
        Assert.Equal("Fable 5.2", AgentStats.ModelDisplay(message.GetProperty("model").GetString()!));
        Assert.Equal(591, message.GetProperty("usage").GetProperty("output_tokens").GetInt64());
    }

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
