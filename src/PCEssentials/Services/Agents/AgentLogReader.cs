using System.IO;
using ZstdSharp;

namespace PCEssentials.Services.Agents;

/// Reads agent logs line by line as raw UTF-8, without turning lines into strings. Plain logs are
/// read from where the last read stopped; Codex compresses finished sessions to .jsonl.zst, and
/// those are read whole.
public sealed class AgentLogReader
{
    private const int StartBytes = 1 << 20;
    private readonly int _maxLineBytes;
    private byte[] _buffer = new byte[StartBytes];

    public AgentLogReader(int maxLineBytes = 48 << 20) => _maxLineBytes = maxLineBytes;

    public static bool IsLog(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);

    public static bool IsCompressed(string path) => path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);

    /// The first read of a large history leaves a big buffer behind; hand it back.
    public void Shrink() => _buffer = new byte[StartBytes];

    /// Reads the lines after offset and returns the offset to start from next time. A plain file
    /// that shrank is read again, and a trailing line without its newline is still being written,
    /// so it is left for the next read. For a compressed file the offset is its size: it is read
    /// again only if that changes.
    public long Read(string path, long offset, Action<ReadOnlyMemory<byte>> onLine)
    {
        var length = new FileInfo(path).Length;
        if (length == offset) return offset;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (IsCompressed(path))
        {
            try
            {
                using var zs = new DecompressionStream(fs);
                ReadLines(zs, onLine, final: true);
            }
            // A damaged file would fail the same way on every scan; keep what was read and move on.
            catch (ZstdException) { }
            return length;
        }
        if (length < offset) offset = 0;
        fs.Seek(offset, SeekOrigin.Begin);
        return offset + ReadLines(fs, onLine, final: false);
    }

    /// Returns the bytes consumed. When final, a last line without a newline counts too.
    private long ReadLines(Stream stream, Action<ReadOnlyMemory<byte>> onLine, bool final)
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
        if (final && filled > 0)
        {
            if (!skipping) onLine(new ReadOnlyMemory<byte>(_buffer, 0, filled));
            consumed += filled;
        }
        return consumed;
    }
}
