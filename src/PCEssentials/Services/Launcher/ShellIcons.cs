using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCEssentials.Services.Launcher;

/// Icons for apps, files and folders through the shell (IShellItemImageFactory), which handles .lnk
/// shortcuts, Store apps ("shell:AppsFolder\…") and file types alike. Results are cached by parsing name.
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const int SIIGBF_ICONONLY = 0x4, SIIGBF_BIGGERSIZEOK = 0x1;

    /// A frozen image, or null when the shell has none. Safe to call from any thread with COM.
    public static ImageSource? Get(string parsingName, int size = 32)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        return Cache.GetOrAdd(parsingName, p => Load(p, size));
    }

    private static ImageSource? Load(string parsingName, int size)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp) != 0 || hbmp == IntPtr.Zero)
                    return null;
            }
            finally { Marshal.ReleaseComObject(factory); }
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // The shell's bitmap holds premultiplied alpha, but WPF reads it as Bgr32 (alpha ignored), which
            // would draw a black box. Copying the raw bytes keeps the alpha byte; reinterpret them as Pbgra32.
            int w = src.PixelWidth, h = src.PixelHeight;
            var pixels = new byte[w * h * 4];
            src.CopyPixels(pixels, w * 4, 0);
            var anyAlpha = false;
            for (int i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
            if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            result.Freeze();
            return result;
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }
}
