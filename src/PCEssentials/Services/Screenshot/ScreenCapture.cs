using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCEssentials.Core;

namespace PCEssentials.Services.Screenshot;

/// A frozen copy of every monitor, in physical pixels.
public sealed class ShotFrame
{
    public required BitmapSource Image { get; init; }
    /// BGR32 pixels of <see cref="Image"/>, for the blur tool and the magnifier's colour readout.
    public required byte[] Pixels { get; init; }
    public int Stride => Bounds.Width * 4;
    /// The virtual screen: the frame's top-left pixel is at (Bounds.X, Bounds.Y).
    public required PxRect Bounds { get; init; }
    public required IReadOnlyList<(PxRect Bounds, bool Primary)> Monitors { get; init; }

    /// The monitor holding the point, or the nearest one.
    public PxRect MonitorAt(double x, double y)
    {
        foreach (var (b, _) in Monitors) if (b.Contains(x, y)) return b;
        return Monitors.MinBy(m => Distance(m.Bounds, x, y)).Bounds;
    }

    private static double Distance(PxRect r, double x, double y)
    {
        var dx = Math.Max(Math.Max(r.X - x, 0), x - r.Right);
        var dy = Math.Max(Math.Max(r.Y - y, 0), y - r.Bottom);
        return dx * dx + dy * dy;
    }

    /// The colour of one pixel as #RRGGBB.
    public string HexAt(int x, int y)
    {
        if (!Bounds.Contains(x, y)) return "";
        var i = (y - Bounds.Y) * Stride + (x - Bounds.X) * 4;
        return $"#{Pixels[i + 2]:X2}{Pixels[i + 1]:X2}{Pixels[i]:X2}";
    }
}

/// Copies the whole virtual screen with GDI. The app is per-monitor DPI aware, so every
/// coordinate here is a physical pixel.
internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public System.Drawing.Point pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    public static ShotFrame Grab(bool includeCursor)
    {
        var bounds = new PxRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bounds.Width, biHeight = -bounds.Height, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        var dib = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        try
        {
            BitBlt(mem, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            if (includeCursor) DrawCursor(mem, bounds);
            GdiFlush();
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null, pixels, bounds.Width * 4);
            image.Freeze();
            var monitors = System.Windows.Forms.Screen.AllScreens
                .Select(s => (new PxRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), s.Primary)).ToList();
            return new ShotFrame { Image = image, Pixels = pixels, Bounds = bounds, Monitors = monitors };
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void DrawCursor(IntPtr dc, PxRect bounds)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & 1) == 0 || info.hCursor == IntPtr.Zero) return;
        if (!GetIconInfo(info.hCursor, out var icon)) return;
        try
        {
            DrawIconEx(dc, info.pt.X - bounds.X - icon.xHotspot, info.pt.Y - bounds.Y - icon.yHotspot, info.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
    }
}
