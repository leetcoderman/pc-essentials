using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PCEssentials.Core;
using PCEssentials.UI.Screenshot;

namespace PCEssentials.Services.Screenshot;

/// Lightshot-style screenshots: the capture overlay, the instant full-screen save, and what happens
/// to a finished image (copy, save, print, open in Paint). Nothing is ever uploaded.
public sealed class ScreenshotService
{
    private readonly AppSettings _settings;
    private ShotOverlay? _overlay;

    /// Shows a notification: its text, and a file to reveal when it is clicked (or null).
    public Action<string, string?> Notify { get; set; } = (_, _) => { };

    public ScreenshotService(AppSettings settings)
    {
        _settings = settings;
        ImportLightshot();
    }

    private ScreenshotSettings S => _settings.Screenshot;

    public bool IsCapturing => _overlay is not null;

    /// Freezes the screen and opens the selection overlay.
    public void Start()
    {
        if (_overlay is not null) return;
        try
        {
            var frame = ScreenCapture.Grab(S.CaptureCursor);
            _overlay = new ShotOverlay(this, _settings, frame);
            _overlay.Closed += () => _overlay = null;
            _overlay.Show();
        }
        catch (Exception ex)
        {
            _overlay = null;
            Log.Error("screenshot", ex);
        }
    }

    /// Saves every monitor straight to the default folder, like Lightshot's "instant save of the fullscreen".
    public void SaveFullScreen()
    {
        if (_overlay is not null) return;
        try
        {
            var frame = ScreenCapture.Grab(S.CaptureCursor);
            var path = AutoPath();
            Write(frame.Image, path, S.Format);
            if (S.AutoCopy) Copy(frame.Image, notify: false);
            if (S.Notify) Notify($"Screenshot saved to {path}. Click to open the folder.", path);
        }
        catch (Exception ex) { Log.Error("screenshot full", ex); }
    }

    public string DefaultFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(S.Folder)) return S.Folder;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }

    private string AutoPath()
    {
        var folder = DefaultFolder;
        Directory.CreateDirectory(folder);
        var ext = ShotFiles.Extension(S.Format);
        var name = ShotFiles.Name(S.FileNamePattern, DateTime.Now, f => File.Exists(Path.Combine(folder, f)), ext);
        return Path.Combine(folder, name + ext);
    }

    public void Copy(BitmapSource image, bool notify = true)
    {
        // The clipboard can be briefly held by another app (clipboard managers, our own history).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetImage(image);
                if (notify && S.Notify) Notify("Your screenshot is copied to the clipboard.", null);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
        Log.Info("screenshot: clipboard busy, copy failed");
    }

    /// The save dialog. True when the image was saved.
    public bool SaveWithDialog(BitmapSource image, Window? owner)
    {
        var folder = Directory.Exists(S.LastFolder) ? S.LastFolder : DefaultFolder;
        try { Directory.CreateDirectory(folder); } catch { }
        var ext = ShotFiles.Extension(S.Format);
        var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            InitialDirectory = folder,
            FileName = ShotFiles.Name(S.FileNamePattern, DateTime.Now, f => File.Exists(Path.Combine(folder, f)), ext),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|Bitmap (*.bmp)|*.bmp",
            FilterIndex = S.Format switch { "jpg" => 2, "bmp" => 3, _ => 1 },
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(owner) != true) return false;
        var format = ShotFiles.FormatFromPath(dialog.FileName, dialog.FilterIndex switch { 2 => "jpg", 3 => "bmp", _ => "png" });
        try
        {
            Write(image, dialog.FileName, format);
        }
        catch (Exception ex)
        {
            Log.Error("screenshot save", ex);
            System.Windows.MessageBox.Show($"Couldn't save the screenshot:\n{ex.Message}", "PC Essentials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        S.LastFolder = Path.GetDirectoryName(dialog.FileName) ?? S.LastFolder;
        S.Format = format;
        _settings.NotifyChanged();
        if (S.AutoCopy) Copy(image, notify: false);
        if (S.Notify) Notify($"Screenshot is saved to {dialog.FileName}. Click to open the folder.", dialog.FileName);
        return true;
    }

    private void Write(BitmapSource image, string path, string format)
    {
        BitmapEncoder encoder = format switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(S.JpegQuality, 10, 100) },
            "bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp)) encoder.Save(stream);
        File.Move(tmp, path, true);
    }

    /// The print dialog, then the image fitted to the page (never enlarged). True when printed.
    public bool Print(BitmapSource image)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        var img = new System.Windows.Controls.Image
        {
            Source = image,
            Stretch = System.Windows.Media.Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
        };
        var page = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        img.Measure(page);
        img.Arrange(new Rect(new Point(0, 0), img.DesiredSize));
        dialog.PrintVisual(img, "Screenshot");
        if (S.AutoCopy) Copy(image, notify: false);
        return true;
    }

    /// Lightshot's "Edit online" without the upload: the image opens in Paint from a temporary file.
    public void OpenInPaint(BitmapSource image)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "PCEssentials");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "screenshot-*.png"))
                if (File.GetCreationTime(old) < DateTime.Now.AddDays(-1)) try { File.Delete(old); } catch { }
            var path = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            Write(image, path, "png");
            Process.Start(new ProcessStartInfo("mspaint.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("screenshot paint", ex); }
    }

    /// Whether Windows currently sends Print Screen to Snipping Tool.
    public static bool SnippingToolOwnsPrintScreen()
    {
        int? value = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SnippingKey.KeyPath);
            if (key?.GetValue(SnippingKey.ValueName) is int v) value = v;
        }
        catch { }
        return SnippingKey.IsOn(value, Environment.OSVersion.Version.Build);
    }

    public static bool LightshotRunning => Process.GetProcessesByName("Lightshot").Length > 0;

    /// Copies Lightshot's own options once (save folder, notifications, keep selection, cursor),
    /// so switching over keeps the same habits. Its hotkeys are not copied.
    private void ImportLightshot()
    {
        if (S.LightshotImported) return;
        S.LightshotImported = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SkillBrains\Lightshot");
            if (key is null) { _settings.Save(); return; }
            if (key.GetValue("LastSavedDir") is string dir && Directory.Exists(dir) && string.IsNullOrEmpty(S.LastFolder))
                S.LastFolder = dir;
            if (key.GetValue("ShowBubbles") is int bubbles) S.Notify = bubbles != 0;
            if (key.GetValue("KeepSelection") is int keep) S.KeepSelection = keep != 0;
            if (key.GetValue("CaptureCursor") is int cursor) S.CaptureCursor = cursor != 0;
            Log.Info("screenshot: imported Lightshot options");
        }
        catch (Exception ex) { Log.Error("lightshot import", ex); }
        _settings.Save();
    }
}
