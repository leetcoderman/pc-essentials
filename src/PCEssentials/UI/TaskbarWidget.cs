using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PCEssentials.Core;
using PCEssentials.Services;

namespace PCEssentials.UI;

/// Live readouts (network speed, temperatures, CPU, RAM) sitting on the taskbar just left of the
/// clock and tray icons. Windows 11 has no taskbar toolbars any more, so this is a small topmost
/// window kept glued to the tray area's position.
public sealed class TaskbarWidget : Window
{
    private readonly AppSettings _settings;
    private readonly HardwareMonitor _monitor;
    private readonly StackPanel _columns = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _placeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, TextBlock> _values = new();
    private IntPtr _hwnd;
    private bool _light;
    private Native.RECT _lastPlacement;

    public event Action? Clicked;

    public TaskbarWidget(AppSettings settings, HardwareMonitor monitor)
    {
        _settings = settings;
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Width;
        // Nearly transparent rather than transparent, so the whole strip takes clicks and hover.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Content = new Border { Padding = new Thickness(4, 0, 4, 0), Child = _columns };
        Title = "PC Essentials taskbar readouts";

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd, noActivate: true);
        };
        Loaded += (_, _) => { Build(); Place(); };
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        _placeTimer.Tick += (_, _) => Place();
        _placeTimer.Start();
        _monitor.Updated += snap => Dispatcher.BeginInvoke(() => Update(snap));
        _settings.Changed += () => Dispatcher.BeginInvoke(() => { Build(); Update(_monitor.Current); Place(); });
        ToolTipService.SetInitialShowDelay(this, 400);
    }

    /// Each component is one column: its name on the left, two readings stacked on the right.
    private static readonly Dictionary<string, (string Label, double ValueWidth)> Known = new()
    {
        ["cpu"] = ("CPU", 27), ["gpu"] = ("GPU", 27), ["ram"] = ("RAM", 33), ["net"] = ("", 30),
    };

    private void Build()
    {
        _light = IsLightTaskbar();
        _columns.Children.Clear();
        _values.Clear();
        var items = _settings.TaskbarWidget.Items.Where(Known.ContainsKey).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var col = Column(items[i]);
            col.Margin = new Thickness(i == 0 ? 0 : 7, 0, 0, 0);
            _columns.Children.Add(col);
        }
    }

    private FrameworkElement Column(string id)
    {
        var (label, width) = Known[id];
        var size = _settings.TaskbarWidget.FontSize;
        var values = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var line in new[] { "top", "bottom" })
        {
            var v = new TextBlock
            {
                FontFamily = Ui.Font, FontSize = size, TextAlignment = TextAlignment.Right, Width = width,
                Foreground = Ui.B(_light ? Colors.Black : Colors.White, 0.95),
            };
            Typography.SetNumeralAlignment(v, FontNumeralAlignment.Tabular);
            _values[$"{id}.{line}"] = v;
            if (id == "net")
            {
                // Network keeps an arrow on each line: upload over download.
                var arrow = new TextBlock
                {
                    Text = line == "top" ? "↑" : "↓", FontFamily = Ui.Font, FontSize = size - 2, FontWeight = FontWeights.SemiBold,
                    Foreground = Ui.B(line == "top" ? Ui.Orange : Ui.Green), Width = 7, VerticalAlignment = VerticalAlignment.Center,
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(arrow);
                row.Children.Add(v);
                values.Children.Add(row);
            }
            else values.Children.Add(v);
        }
        if (label.Length == 0) return values;
        var name = new TextBlock
        {
            Text = label, FontFamily = Ui.Font, FontSize = size - 2.5, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.B(LabelColor(id), 0.95), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
        };
        var column = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(name);
        column.Children.Add(values);
        return column;
    }

    private Color LabelColor(string id) => id switch
    {
        "cpu" => Ui.Blue,
        "gpu" => Ui.Green,
        "ram" => Ui.Purple,
        _ => _light ? Ui.C("#555555") : Ui.C("#B5B5BA"),
    };

    /// "62°": the unit is left to Settings to keep the strip narrow.
    private static string Short(double? celsius, bool fahrenheit) =>
        celsius is double c ? $"{(fahrenheit ? c * 9 / 5 + 32 : c):0}°" : "--";

    /// "0K", "22K", "1.2M", "48M": at most four characters.
    private static string ShortSpeed(double bps)
    {
        var kb = bps / 1024;
        if (kb < 999.5) return $"{kb:0}K";
        var mb = kb / 1024;
        if (mb < 9.95) return $"{mb:0.0}M";
        if (mb < 999.5) return $"{mb:0}M";
        return $"{mb / 1024:0.0}G";
    }

    private void Update(HardwareSnapshot s)
    {
        var f = _settings.UseFahrenheit;
        var normal = Ui.B(_light ? Colors.Black : Colors.White, 0.95);
        void Set(string key, string text, bool hot)
        {
            if (!_values.TryGetValue(key, out var tb)) return;
            tb.Text = text;
            // Red when something runs hot or busy.
            tb.Foreground = hot ? Ui.B(Ui.Red) : normal;
        }
        Set("cpu.top", Short(s.CpuTemp, f), s.CpuTemp >= 85);
        Set("cpu.bottom", Format.Percent(s.CpuLoad), s.CpuLoad >= 90);
        Set("gpu.top", Short(s.GpuTemp, f), s.GpuTemp >= 83);
        Set("gpu.bottom", Format.Percent(s.GpuLoad), s.GpuLoad >= 95);
        Set("ram.top", Format.Percent(s.RamLoad), s.RamLoad >= 90);
        Set("ram.bottom", $"{s.RamUsedGb:0.0}G", false);
        Set("net.top", ShortSpeed(s.NetUpBps), false);
        Set("net.bottom", ShortSpeed(s.NetDownBps), false);
        ToolTip = $"{s.CpuName}\nCPU {Format.Percent(s.CpuLoad)} · {Format.Temp(s.CpuTemp, f)}" +
                  (s.CpuTemp is null ? " (needs admin + PawnIO)" : "") +
                  $"\n{s.GpuName}\nGPU {Format.Percent(s.GpuLoad)} · {Format.Temp(s.GpuTemp, f)}" +
                  $"\nRAM {s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB" +
                  $"\nNetwork ↓ {Format.Speed(s.NetDownBps)}  ↑ {Format.Speed(s.NetUpBps)}" +
                  $"\nThis session ↓ {Format.Bytes(s.SessionDownBytes)}  ↑ {Format.Bytes(s.SessionUpBytes)}";
    }

    /// Glues the window to the left edge of the tray/clock area and keeps it above the taskbar.
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var cfg = _settings.TaskbarWidget;
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (!cfg.Enabled || tray == IntPtr.Zero || (cfg.HideInFullscreen && Native.ForegroundIsFullscreen()))
        {
            if (IsVisible) Hide();
            return;
        }
        Native.GetWindowRect(tray, out var bar);
        var notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        var mon = Native.MonitorInfoFor(Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        // An auto-hidden taskbar slides mostly off screen; hide with it.
        var visibleHeight = Math.Min(bar.Bottom, mon.Bottom) - Math.Max(bar.Top, mon.Top);
        if (visibleHeight < bar.Height - 4 || bar.Width < bar.Height)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();

        var right = bar.Right;
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var n) && n.Width > 0) right = n.Left;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Ceiling(ActualWidth * dpi);
        var gap = (int)(cfg.GapFromTray * dpi);
        var target = new Native.RECT(right - width - gap, bar.Top, right - gap, bar.Bottom);
        if (_light != IsLightTaskbar()) { Build(); Update(_monitor.Current); }
        // Re-assert topmost every tick: clicking the taskbar raises it above other topmost windows.
        var flags = Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW;
        if (target.Left == _lastPlacement.Left && target.Top == _lastPlacement.Top && target.Height == _lastPlacement.Height)
            flags |= Native.SWP_NOMOVE | Native.SWP_NOSIZE;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, target.Left, target.Top, width, target.Height, flags);
        _lastPlacement = target;
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }
}
