using System.Globalization;
using System.IO;
using System.Text;

namespace PCEssentials.Services.Screenshot;

public enum ShotTool { None, Pen, Line, Arrow, Rectangle, Circle, Marker, Text, Blur }

/// One annotation, in physical virtual-screen pixels. Pen and marker keep every point; the other
/// tools use the first and last.
public sealed class ShotShape
{
    public ShotTool Tool { get; init; }
    public List<(double X, double Y)> Points { get; } = [];
    /// ARGB.
    public uint Color { get; init; }
    public double Width { get; init; }
    public string Text { get; set; } = "";
    public double FontSize { get; init; }

    public (double X, double Y) Start => Points[0];
    public (double X, double Y) End => Points[^1];

    /// The rectangle spanned by the first and last points (rectangle, circle, blur).
    public PxRect Bounds => PxRect.FromCorners((int)Math.Round(Start.X), (int)Math.Round(Start.Y), (int)Math.Round(End.X), (int)Math.Round(End.Y));

    /// A shape too small to keep (a click without a drag). Text is kept if it has any.
    public bool IsEmpty => Tool switch
    {
        ShotTool.Text => string.IsNullOrWhiteSpace(Text),
        ShotTool.Pen or ShotTool.Marker => Points.Count == 0,
        _ => Points.Count < 2 || (Math.Abs(End.X - Start.X) < 2 && Math.Abs(End.Y - Start.Y) < 2),
    };
}

/// The annotations on a capture, oldest first, with undo.
public sealed class ShotShapes
{
    private readonly List<ShotShape> _items = [];
    public IReadOnlyList<ShotShape> Items => _items;
    public int Count => _items.Count;

    public void Add(ShotShape shape)
    {
        if (!shape.IsEmpty) _items.Add(shape);
    }

    /// Removes the newest annotation. False when there is nothing to undo.
    public bool Undo()
    {
        if (_items.Count == 0) return false;
        _items.RemoveAt(_items.Count - 1);
        return true;
    }
}

public static class ShotImage
{
    /// Pixelates a region of a 32-bit BGRA/BGR32 image for the blur tool: each block of
    /// <paramref name="block"/>×<paramref name="block"/> pixels becomes its average colour.
    /// Returns the region's own pixels (stride = width × 4). The region must lie inside the image.
    public static byte[] Pixelate(byte[] pixels, int stride, PxRect region, int block)
    {
        block = Math.Max(1, block);
        int w = region.Width, h = region.Height;
        var outPx = new byte[w * h * 4];
        for (var by = 0; by < h; by += block)
        for (var bx = 0; bx < w; bx += block)
        {
            int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
            long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (var y = 0; y < bh; y++)
            {
                var row = (region.Y + by + y) * stride + (region.X + bx) * 4;
                for (var x = 0; x < bw; x++)
                {
                    var i = row + x * 4;
                    s0 += pixels[i]; s1 += pixels[i + 1]; s2 += pixels[i + 2]; s3 += pixels[i + 3];
                }
            }
            var n = bw * bh;
            byte c0 = (byte)(s0 / n), c1 = (byte)(s1 / n), c2 = (byte)(s2 / n), c3 = (byte)(s3 / n);
            for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
            {
                var o = ((by + y) * w + bx + x) * 4;
                outPx[o] = c0; outPx[o + 1] = c1; outPx[o + 2] = c2; outPx[o + 3] = c3;
            }
        }
        return outPx;
    }
}

/// File names and formats for saved screenshots.
public static class ShotFiles
{
    public const string DefaultPattern = "Screenshot {yyyy-MM-dd HH.mm.ss}";

    public static string Extension(string format) => format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => ".jpg",
        "bmp" => ".bmp",
        _ => ".png",
    };

    /// The format a file name asks for by its extension, or <paramref name="fallback"/>.
    public static string FormatFromPath(string path, string fallback) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpg",
        ".png" => "png",
        ".bmp" => "bmp",
        _ => fallback,
    };

    /// Expands a pattern into a file name (without extension). Text in braces is a .NET date format
    /// for the capture time ({yyyy-MM-dd}), except {n}, a number that makes the name unused in
    /// <paramref name="exists"/>. Characters Windows forbids in file names are replaced with "-".
    public static string Name(string? pattern, DateTime time, Func<string, bool> exists, string extension)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;
        var hasCounter = pattern.Contains("{n}", StringComparison.OrdinalIgnoreCase);
        string Build(int n)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < pattern.Length)
            {
                var open = pattern.IndexOf('{', i);
                var close = open < 0 ? -1 : pattern.IndexOf('}', open + 1);
                if (open < 0 || close < 0) { sb.Append(pattern, i, pattern.Length - i); break; }
                sb.Append(pattern, i, open - i);
                var token = pattern.Substring(open + 1, close - open - 1);
                if (token.Equals("n", StringComparison.OrdinalIgnoreCase)) sb.Append(n);
                else
                {
                    try { sb.Append(time.ToString(token, CultureInfo.InvariantCulture)); }
                    catch (FormatException) { sb.Append(token); }
                }
                i = close + 1;
            }
            var name = sb.ToString();
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            name = name.Trim().TrimEnd('.');
            return name.Length == 0 ? "Screenshot" : name;
        }

        if (hasCounter)
        {
            for (var n = 1; ; n++)
            {
                var candidate = Build(n);
                if (!exists(candidate + extension)) return candidate;
            }
        }
        var plain = Build(1);
        if (!exists(plain + extension)) return plain;
        for (var n = 2; ; n++)
            if (!exists($"{plain} ({n}){extension}")) return $"{plain} ({n})";
    }
}

/// Windows 11's "Use the Print Screen key to open Snipping Tool" setting.
public static class SnippingKey
{
    public const string KeyPath = @"Control Panel\Keyboard";
    public const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// Whether Windows sends Print Screen to Snipping Tool. An unset value means on in Windows 11
    /// 22H2 (build 22621) and later, and off before that.
    public static bool IsOn(int? value, int build) => value is { } v ? v != 0 : build >= 22621;
}
