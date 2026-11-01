using System.Text;
using PCEssentials.Services.Agents;
using Xunit;
using ZstdSharp;

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

    private string Compressed(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        using var compressor = new Compressor();
        File.WriteAllBytes(path, compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray());
        return path;
    }

    [Theory]
    [InlineData("rollout-1.jsonl", true)]
    [InlineData("rollout-1.jsonl.zst", true)]
    [InlineData("ROLLOUT-1.JSONL.ZST", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.json", false)]
    [InlineData("rollout-1.zst", false)]
    public void IsLog(string name, bool expected) => Assert.Equal(expected, AgentLogReader.IsLog(name));

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
    public void CompressedLogIsReadWholeOnce()
    {
        var path = Compressed("rollout-1.jsonl.zst", "one\ntwo\nlast without newline");
        var reader = new AgentLogReader();
        long offset = 0;

        Assert.Equal(["one", "two", "last without newline"], Read(reader, path, ref offset));
        Assert.Equal(new FileInfo(path).Length, offset);
        Assert.Empty(Read(reader, path, ref offset));
    }

    [Fact]
    public void ReplacedCompressedLogIsReadAgainFromTheStart()
    {
        var path = Compressed("rollout-1.jsonl.zst", "one\n");
        var reader = new AgentLogReader();
        long offset = 0;
        Read(reader, path, ref offset);

        Compressed("rollout-1.jsonl.zst", "one\ntwo\nthree\n");
        Assert.Equal(["one", "two", "three"], Read(reader, path, ref offset));
    }

    [Fact]
    public void DamagedCompressedLogIsSkipped()
    {
        var path = Path.Combine(_dir, "rollout-1.jsonl.zst");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not zstd at all\n"));
        var reader = new AgentLogReader();
        long offset = 0;

        Assert.Empty(Read(reader, path, ref offset));
        Assert.Equal(new FileInfo(path).Length, offset);
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
