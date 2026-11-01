using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PCEssentials.Core;

namespace PCEssentials.Services;

public enum ClipKind { Text, Image, Files }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public List<string>? Files { get; set; }
    /// PNG stored under %APPDATA%\PCEssentials\clipboard.
    public string? ImagePath { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public DateTime Copied { get; set; } = DateTime.Now;
    public string? SourceApp { get; set; }
    public bool Pinned { get; set; }

    public string Preview => Kind switch
    {
        ClipKind.Text => (Text ?? "").Trim().Replace("\r", "").Replace('\n', ' ').Replace('\t', ' '),
        ClipKind.Image => $"Image {ImageWidth} × {ImageHeight}",
        ClipKind.Files => Files is { Count: 1 } f ? Path.GetFileName(f[0]) : $"{Files?.Count ?? 0} files",
        _ => "",
    };
}

/// Local clipboard history: text, images and files. Stored only in %APPDATA%\PC Essentials.
public sealed class ClipboardHistory : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HwndSource _source;
    private readonly string _file = Path.Combine(Paths.DataDir, "clipboard.json");
    private readonly string _imageDir = Directory.CreateDirectory(Path.Combine(Paths.DataDir, "clipboard")).FullName;
    private bool _ignoreNext;

    public List<ClipItem> Items { get; } = new();
    public int UnseenCount { get; private set; }
    public event Action? Changed;

    public ClipboardHistory(AppSettings settings)
    {
        _settings = settings;
        Load();
        _source = new HwndSource(new HwndSourceParameters("PCEssentialsClipboard") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        Native.AddClipboardFormatListener(_source.Handle);
    }

    public void MarkSeen()
    {
        if (UnseenCount == 0) return;
        UnseenCount = 0;
        Changed?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            if (_ignoreNext) _ignoreNext = false;
            else if (_settings.Clipboard.Enabled) Capture();
        }
        return IntPtr.Zero;
    }

    private void Capture()
    {
        try
        {
            var data = RetryGet(System.Windows.Clipboard.GetDataObject);
            if (data is null) return;
            // Password managers mark secrets this way; never record them.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
                return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms
                && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0)
                return;
            var app = ForegroundApp();
            if (app is not null && _settings.Clipboard.IgnoredApps.Any(a => app.Equals(a, StringComparison.OrdinalIgnoreCase))) return;

            ClipItem? item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = new ClipItem { Kind = ClipKind.Files, Files = files.ToList() };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
                item = new ClipItem { Kind = ClipKind.Text, Text = text.Length > 200_000 ? text[..200_000] : text };
            else if (data.GetDataPresent(DataFormats.Bitmap) && System.Windows.Clipboard.GetImage() is BitmapSource img)
                item = SaveImage(img);
            if (item is null) return;
            item.SourceApp = app;

            // Copying the same thing again moves it to the top instead of duplicating it.
            var dup = Items.FindIndex(i => i.Kind == item.Kind && i.Text == item.Text && i.Kind != ClipKind.Image
                && (i.Files is null || item.Files is null || i.Files.SequenceEqual(item.Files)));
            if (dup >= 0)
            {
                item.Pinned = Items[dup].Pinned;
                Items.RemoveAt(dup);
            }
            Items.Insert(0, item);
            Trim();
            UnseenCount++;
            Save();
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("clipboard capture", ex); }
    }

    private ClipItem SaveImage(BitmapSource img)
    {
        var item = new ClipItem { Kind = ClipKind.Image, ImageWidth = img.PixelWidth, ImageHeight = img.PixelHeight };
        item.ImagePath = Path.Combine(_imageDir, item.Id + ".png");
        using var fs = File.Create(item.ImagePath);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(fs);
        return item;
    }

    private void Trim()
    {
        var max = Math.Max(10, _settings.Clipboard.MaxItems);
        while (Items.Count(i => !i.Pinned) > max)
        {
            var oldest = Items.FindLastIndex(i => !i.Pinned);
            Delete(Items[oldest], false);
        }
    }

    /// Puts the item back on the clipboard. With paste, also presses Ctrl+V in the app in front.
    public void Use(ClipItem item, bool paste)
    {
        try
        {
            _ignoreNext = true;
            var data = new DataObject();
            switch (item.Kind)
            {
                case ClipKind.Text: data.SetText(item.Text ?? ""); break;
                case ClipKind.Files:
                    var list = new StringCollection();
                    list.AddRange(item.Files!.ToArray());
                    data.SetFileDropList(list);
                    break;
                case ClipKind.Image:
                    if (item.ImagePath is null || !File.Exists(item.ImagePath)) return;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(item.ImagePath);
                    bmp.EndInit();
                    data.SetImage(bmp);
                    break;
            }
            RetryDo(() => System.Windows.Clipboard.SetDataObject(data, true));
            Items.Remove(item);
            Items.Insert(0, item);
            item.Copied = DateTime.Now;
            Save();
            Changed?.Invoke();
            if (paste) Native.SendKeyCombo(0x11, 0x56); // Ctrl+V
        }
        catch (Exception ex) { Log.Error("clipboard use", ex); }
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
        Changed?.Invoke();
    }

    public void Delete(ClipItem item, bool notify = true)
    {
        Items.Remove(item);
        if (item.ImagePath is not null) try { File.Delete(item.ImagePath); } catch { }
        if (!notify) return;
        Save();
        Changed?.Invoke();
    }

    public void ClearUnpinned()
    {
        foreach (var item in Items.Where(i => !i.Pinned).ToList()) Delete(item, false);
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            Items.AddRange(JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(_file)) ?? []);
        }
        catch (Exception ex) { Log.Error("clipboard load", ex); }
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(Items)); }
        catch (Exception ex) { Log.Error("clipboard save", ex); }
    }

    private static string? ForegroundApp()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    // The clipboard is a shared resource another app may hold open for a moment.
    private static T? RetryGet<T>(Func<T> get)
    {
        for (var i = 0; i < 5; i++)
        {
            try { return get(); }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return default;
    }

    private static void RetryDo(Action action)
    {
        for (var i = 0; i < 5; i++)
        {
            try { action(); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
