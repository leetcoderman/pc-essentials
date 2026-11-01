using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace PCEssentials.Services.ShakeToFind;

/// A click-through, topmost, per-pixel-alpha window that draws the arrow pointer at any size. The arrow
/// is a vector path redrawn at the monitor's DPI on every size change, so it stays sharp when big.
/// Must be created, used and disposed on one thread.
internal sealed class PointerOverlay : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT ptDst, ref SIZE size, IntPtr src, ref POINT ptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    /// The Windows arrow, tip at (0,0), in the units of a 32 px cursor cell.
    private static readonly Drawing.PointF[] Arrow =
    [
        new(0, 0), new(0, 16.5f), new(4, 12.8f), new(6.8f, 19.3f), new(9.4f, 18.2f), new(6.7f, 11.9f), new(11.8f, 11.9f),
    ];

    private readonly Forms.NativeWindow _window = new();
    private Drawing.Bitmap? _bitmap;
    private double _drawnSize = -1;
    private int _pad;
    private bool _shown;

    public PointerOverlay()
    {
        _window.CreateHandle(new Forms.CreateParams
        {
            Caption = "PC Essentials pointer",
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        });
    }

    /// Puts the arrow's tip at (x, y) in physical pixels. unit is physical pixels per cursor-cell unit at
    /// normal size (cursor size / 32 × DPI scale); scale is how much bigger than normal to draw it.
    public void Show(int x, int y, double unit, double scale)
    {
        var k = unit * scale;
        if (Math.Abs(k - _drawnSize) > 0.001)
        {
            Render(x, y, k);
            _drawnSize = k;
        }
        else
        {
            SetWindowPos(_window.Handle, HWND_TOPMOST, x - _pad, y - _pad, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
        if (!_shown)
        {
            SetWindowPos(_window.Handle, HWND_TOPMOST, x - _pad, y - _pad, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _shown = true;
        }
    }

    private void Render(int x, int y, double k)
    {
        _pad = (int)Math.Ceiling(2 * k) + 1;
        var w = (int)Math.Ceiling(15 * k) + 2 * _pad;
        var h = (int)Math.Ceiling(22 * k) + 2 * _pad;
        if (_bitmap is null || _bitmap.Width < w || _bitmap.Height < h)
        {
            _bitmap?.Dispose();
            // A little headroom so a growing arrow doesn't reallocate every frame.
            _bitmap = new Drawing.Bitmap(w + w / 4, h + h / 4, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }

        using (var g = Drawing.Graphics.FromImage(_bitmap))
        {
            g.Clear(Drawing.Color.Transparent);
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            var outline = (float)Math.Max(1, k);
            // The outline's sharp tip sits on the hotspot.
            var ox = _pad + outline * 0.5f;
            var oy = _pad + outline * 1.2f;
            using var path = Path(ox, oy, (float)k);

            // Soft shadow: the arrow nudged down-right, with widening faint strokes for a blurred edge.
            using (var shadow = Path(ox + (float)(0.6 * k), oy + (float)(1.1 * k), (float)k))
            {
                using var shadowFill = new Drawing.SolidBrush(Drawing.Color.FromArgb(40, 0, 0, 0));
                g.FillPath(shadowFill, shadow);
                for (var i = 3; i >= 1; i--)
                {
                    using var pen = new Drawing.Pen(Drawing.Color.FromArgb(16, 0, 0, 0), (float)(i * 0.9 * k)) { LineJoin = Drawing2D.LineJoin.Round };
                    g.DrawPath(pen, shadow);
                }
            }
            g.FillPath(Drawing.Brushes.White, path);
            using var stroke = new Drawing.Pen(Drawing.Color.Black, outline) { LineJoin = Drawing2D.LineJoin.Miter, MiterLimit = 4 };
            g.DrawPath(stroke, path);
        }

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hBitmap = _bitmap.GetHbitmap(Drawing.Color.FromArgb(0));
        var old = SelectObject(mem, hBitmap);
        try
        {
            var dst = new POINT { X = x - _pad, Y = y - _pad };
            var size = new SIZE { Cx = w, Cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            UpdateLayeredWindow(_window.Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hBitmap);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static Drawing2D.GraphicsPath Path(float ox, float oy, float k)
    {
        var path = new Drawing2D.GraphicsPath();
        path.AddPolygon(Arrow.Select(p => new Drawing.PointF(ox + p.X * k, oy + p.Y * k)).ToArray());
        return path;
    }

    public void Dispose()
    {
        _window.DestroyHandle();
        _bitmap?.Dispose();
    }
}
