using System.IO;

namespace PCEssentials.Services.Agents;

/// Reads Claude Code logs line by line as raw UTF-8, without turning lines into strings, from
/// where the last read stopped.
public sealed class AgentLogReader
{
    private const int StartBytes = 1 << 20;
    private readonly int _maxLineBytes;
    private byte[] _buffer = new byte[StartBytes];

    public AgentLogReader(int maxLineBytes = 48 << 20) => _maxLineBytes = maxLineBytes;

    /// A Claude Code session log, "<session id>.jsonl". A synced folder can still hold other agents'
    /// logs from before only Claude was tracked (Codex names its "rollout-<time>-<id>.jsonl"); those are left alone.
    public static bool IsLog(string path)
    {
        var name = path[(path.Replace('\\', '/').LastIndexOf('/') + 1)..];
        return name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);
    }

    /// The first read of a large history leaves a big buffer behind; hand it back.
    public void Shrink() => _buffer = new byte[StartBytes];

    /// Reads the lines after offset and returns the offset to start from next time. A file that
    /// shrank is read again, and a trailing line without its newline is still being written, so it
    /// is left for the next read.
    public long Read(string path, long offset, Action<ReadOnlyMemory<byte>> onLine)
    {
        var length = new FileInfo(path).Length;
        if (length == offset) return offset;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (length < offset) offset = 0;
        fs.Seek(offset, SeekOrigin.Begin);
        return offset + ReadLines(fs, onLine);
    }

    /// Returns the bytes consumed, up to the last newline.
    private long ReadLines(Stream stream, Action<ReadOnlyMemory<byte>> onLine)
    {
        var filled = 0;
        var skipping = false;
        long consumed = 0;
        while (true)
        {
            if (filled == _buffer.Length)
            {
                if (_buffer.Length >= _maxLineBytes)
                {
                    // A line larger than any we care about: drop what we have and skip to its end.
                    consumed += filled;
                    filled = 0;
                    skipping = true;
                }
                else Array.Resize(ref _buffer, _buffer.Length * 2);
            }
            var read = stream.Read(_buffer, filled, _buffer.Length - filled);
            if (read == 0) break;
            filled += read;
            var start = 0;
            while (true)
            {
                var nl = Array.IndexOf(_buffer, (byte)'\n', start, filled - start);
                if (nl < 0) break;
                if (!skipping && nl > start) onLine(new ReadOnlyMemory<byte>(_buffer, start, nl - start));
                skipping = false;
                consumed += nl - start + 1;
                start = nl + 1;
            }
            if (start > 0)
            {
                Buffer.BlockCopy(_buffer, start, _buffer, 0, filled - start);
                filled -= start;
            }
        }
        return consumed;
    }
}
