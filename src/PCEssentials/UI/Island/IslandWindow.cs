using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCEssentials.Core;
using PCEssentials.Services;
using PCEssentials.Services.Agents;
using UIA = System.Windows.Automation;

namespace PCEssentials.UI.Island;

/// The Dynamic Island: a black capsule on the taskbar (right of the Widgets button) or at the top
/// centre of the screen that shows live activities and opens into a panel of pages (agents, keep
/// awake, music, mixer, calendar, clipboard, scratchpad, microphone, system), with Settings and Quit
/// at the end of its top bar. On the taskbar it opens upward and rightward from the capsule.
public sealed class IslandWindow : Window
{
    private const double ClosedHeight = 32, OpenWidth = 560, PageHeight = 420, TopMargin = 8;
    // Taskbar mode: gap right of the Widgets button, offset from the taskbar's left edge when there is
    // no Widgets button, and how far the open panel's bottom sits above the taskbar's top.
    private const double WidgetsGap = 8, FallbackLeft = 12, PanelLift = 6;
    private const double BarHeight = 34, Inset = 20;
    private static double OpenHeight => 12 + BarHeight + 10 + PageHeight + 16;

    private readonly IslandContext _ctx;
    private readonly Border _surface;
    private readonly Grid _closedLayer = new(), _openLayer = new();
    /// The sections share the bar's width evenly (the selected one widens for its label), so the
    /// row reaches across to Settings and Quit instead of leaving a gap.
    private readonly Grid _bar = new();
    private readonly StackPanel _barEnd = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
    private readonly ContentControl _pageHost = new();
    private readonly Dictionary<string, IslandPage> _pages = new();
    private readonly Dictionary<string, (Pressable Button, TextBlock Glyph, TextBlock Label, Border Badge, Border Dot)> _barItems = new();
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _exitTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string? _restKey;
    private IntPtr _hwnd, _previousForeground;
    private bool _open, _keyboard, _hovering;
    /// Set by scrolling on the closed capsule (it changes the volume), so hovering doesn't open the island
    /// under the scroll; cleared when the pointer leaves.
    private bool _scrolled;
    private int _wheel;
    private bool? _capsLock;
    private IslandPage? _current;

    // Capsule parts.
    private readonly Border _capLeft = new(), _capRight = new();
    private readonly TextBlock _capTitle = Ui.Text("", 12, FontWeights.SemiBold);
    private readonly EqBars _capBars = new(5, Ui.Rose, 2, 12, 2);
    private (string Glyph, Color Tint, string Title, string? Detail, double? Level, DateTime Until)? _notice;

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(IslandWindow),
        new PropertyMetadata(16.0, (d, _) => ((IslandWindow)d).UpdateClip()));

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public IslandWindow(IslandContext ctx)
    {
        _ctx = ctx;
        ctx.AllowKeyboard = AllowKeyboard;
        ctx.Collapse = () => Close(restoreFocus: true);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "PC Essentials Island";
        Width = OpenWidth + 80;
        Height = OpenHeight + TopMargin + 40;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _surface = new Border
        {
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TopMargin, 0, 0),
            Width = 140,
            Height = ClosedHeight,
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
        };
        var layers = new Grid();
        BuildGlass(layers);
        layers.Children.Add(_closedLayer);
        layers.Children.Add(_openLayer);
        _surface.Child = layers;
        _surface.SizeChanged += (_, _) => UpdateClip();
        var root = new Grid();
        root.Children.Add(_surface);
        Content = root;

        BuildCapsule();
        BuildOpenLayer();
        _openLayer.Opacity = 0;
        _openLayer.Visibility = Visibility.Collapsed;

        _surface.MouseLeftButtonUp += (_, e) => { if (!_open) { e.Handled = true; Open(null); } };
        _surface.MouseEnter += (_, _) =>
        {
            _hovering = true;
            _exitTimer.Stop();
            if (!_open) { UpdateCapsule(); if (_ctx.Settings.Island.OpenOnHover) _hoverTimer.Start(); }
        };
        _surface.MouseLeave += (_, _) =>
        {
            _hovering = false;
            _scrolled = false;
            _hoverTimer.Stop();
            if (!_open) UpdateCapsule();
            else if (_ctx.Settings.Island.OpenOnHover) _exitTimer.Start();
        };
        _surface.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowContextMenu(); };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (_hovering && !_open && !_scrolled) Open(null); };
        // Scrolling on the closed capsule changes the volume, a notch at a time like the volume keys (2%).
        // Touchpads send small steps, so they add up to whole notches.
        _surface.MouseWheel += (_, e) =>
        {
            if (_open) return;
            e.Handled = true;
            _hoverTimer.Stop();
            _scrolled = true;
            _wheel += e.Delta;
            var notches = _wheel / 120;
            if (notches == 0) return;
            _wheel -= notches * 120;
            var audio = _ctx.Audio;
            audio.OutputVolume = (float)(Math.Round(Math.Clamp(audio.OutputVolume + notches * 0.02, 0, 1) * 50) / 50);
            // The volume up key unmutes too.
            if (notches > 0 && audio.OutputMuted) audio.OutputMuted = false;
        };
        // Leaving the open island closes it, like the Mac's hover-opened island (0.18 s grace).
        // A menu the island opened sits outside it, so the island stays while one is up.
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            var inside = _surface.IsMouseOver || PointerOnTaskbarBridge();
            if (_open && !inside && !Menus.AnyOpen && Mouse.Captured is null) Close(true);
            else if (_open && !_surface.IsMouseOver) _exitTimer.Start();
        };
        PreviewKeyDown += OnKey;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd, noActivate: true);
            Position();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Position);

        _fast.Tick += (_, _) => FastTick();
        _slow.Tick += (_, _) => SlowTick();
        _fast.Start();
        _slow.Start();

        ctx.Media.Changed += () => { if (!_open) UpdateCapsule(); };
        ctx.KeepAwake.Changed += () => { UpdateBarState(); if (!_open) UpdateCapsule(); };
        ctx.Clipboard.Changed += UpdateBarState;
        ctx.Agents.Updated += _ => Dispatcher.BeginInvoke(() => { UpdateBarState(); if (!_open) UpdateCapsule(); });
        ctx.Agents.Finished += f => Dispatcher.BeginInvoke(() => ShowAgentFinished(f));
        ctx.Audio.OutputVolumeChanged += (level, muted) =>
        {
            if (!_ctx.Settings.Island.ShowVolumeChanges || (_open && _current?.Id == "mixer")) return;
            ShowNotice(muted ? Ui.GMute : Ui.GVolume, Colors.White, "", null, muted ? 0 : level, TimeSpan.FromSeconds(1.6));
        };
        ctx.Audio.InputMuteChanged += muted =>
        {
            if (!_ctx.Settings.Island.ShowDeviceChanges) return;
            ShowNotice(muted ? Ui.GMicOff : Ui.GMic, muted ? Ui.Red : Colors.White, muted ? "Microphone muted" : "Microphone on",
                null, null, TimeSpan.FromSeconds(1.8));
        };
        ctx.Audio.OutputDeviceChanged += name =>
        {
            if (!_ctx.Settings.Island.ShowDeviceChanges) return;
            ShowNotice(Ui.GSpeaker, Colors.White, name, null, null, TimeSpan.FromSeconds(2.5));
        };
        ctx.Settings.Changed += () => Dispatcher.BeginInvoke(() => { ApplyLayout(); RebuildBar(); UpdateCapsule(); });
        ApplyLayout();
    }

    // ---------- placement ----------

    private bool OnTaskbar => _ctx.Settings.Island.Position != "top";
    private string? _layoutMode;
    private double _band = 48;          // taskbar height, DIPs
    private int? _anchorPx;             // screen x the capsule follows (Widgets button's right edge), pixels
    private DateTime _anchorAt = DateTime.MinValue;
    private int _anchorBusy;
    private bool _taskbarHidden;
    private int _fastCount;

    private Thickness ClosedMargin => new(0, 0, 0, Math.Max(0, (_band - ClosedHeight) / 2));
    private Thickness OpenMargin => new(0, 0, 0, _band + PanelLift);

    /// Anchors the surface for the chosen position: top centre growing down, or the taskbar's
    /// bottom-left growing up and right.
    private void ApplyLayout()
    {
        var mode = _ctx.Settings.Island.Position;
        if (mode == _layoutMode) return;
        _layoutMode = mode;
        if (_open) Close(false);
        _surface.BeginAnimation(MarginProperty, null);
        var taskbar = OnTaskbar;
        _surface.HorizontalAlignment = _glassImage.HorizontalAlignment = taskbar ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _surface.VerticalAlignment = _glassImage.VerticalAlignment = taskbar ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        _surface.Margin = taskbar ? ClosedMargin : new Thickness(0, TopMargin, 0, 0);
        if (taskbar) RefreshAnchor();
        Position();
    }

    private void Position()
    {
        Width = OpenWidth + 80;
        if (!OnTaskbar)
        {
            var area = SystemParameters.WorkArea;
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            Height = OpenHeight + TopMargin + 40;
            Left = (screenWidth - Width) / 2;
            Top = area.Top;
            return;
        }
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var bar) || bar.Width < bar.Height)
        {
            _taskbarHidden = true;
            return;
        }
        var mon = Native.MonitorInfoFor(Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        // An auto-hidden taskbar slides mostly off screen; the closed island hides with it.
        var visibleHeight = Math.Min(bar.Bottom, mon.Bottom) - Math.Max(bar.Top, mon.Top);
        _taskbarHidden = visibleHeight < bar.Height - 4;

        var dpi = VisualTreeHelper.GetDpi(this);
        var band = bar.Height / dpi.DpiScaleY;
        if (Math.Abs(band - _band) > 0.5)
        {
            _band = band;
            if (!_open) { _surface.BeginAnimation(MarginProperty, null); _surface.Margin = ClosedMargin; }
        }
        // Placed as if docked even while auto-hidden, so opening by hotkey still lands in the right spot.
        var x = _anchorPx is int a ? a + WidgetsGap * dpi.DpiScaleX : bar.Left + FallbackLeft * dpi.DpiScaleX;
        x = Math.Max(mon.Left, Math.Min(x, mon.Right - OpenWidth * dpi.DpiScaleX));
        Height = OpenHeight + _band + PanelLift + 40;
        Left = x / dpi.DpiScaleX;
        Top = mon.Bottom / dpi.DpiScaleY - Height;
    }

    /// Looks the anchor up again at most every 5 seconds, off the UI thread: UI Automation calls
    /// into the taskbar can take a while.
    private void RefreshAnchor()
    {
        if (DateTime.Now - _anchorAt < TimeSpan.FromSeconds(5) || Interlocked.Exchange(ref _anchorBusy, 1) == 1) return;
        _anchorAt = DateTime.Now;
        Task.Run(() =>
        {
            try
            {
                var anchor = FindAnchor();
                Dispatcher.BeginInvoke(() =>
                {
                    if (anchor == _anchorPx) return;
                    _anchorPx = anchor;
                    if (OnTaskbar) Position();
                });
            }
            finally { Volatile.Write(ref _anchorBusy, 0); }
        });
    }

    /// The screen x (pixels) the capsule goes right of: the Widgets button's right edge with centred
    /// taskbar icons, or the last left-side button's right edge with left-aligned icons (Widgets then
    /// moves next to the tray). Null when neither is there, which falls back to the taskbar's left edge.
    private static int? FindAnchor()
    {
        try
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;
            var root = UIA.AutomationElement.FromHandle(tray);
            if (IconsLeftAligned())
            {
                var cond = new UIA.OrCondition(
                    new UIA.PropertyCondition(UIA.AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"),
                    new UIA.PropertyCondition(UIA.AutomationElement.AutomationIdProperty, "StartButton"),
                    new UIA.PropertyCondition(UIA.AutomationElement.AutomationIdProperty, "SearchButton"),
                    new UIA.PropertyCondition(UIA.AutomationElement.AutomationIdProperty, "SearchBoxButton"),
                    new UIA.PropertyCondition(UIA.AutomationElement.AutomationIdProperty, "TaskViewButton"));
                double right = double.NaN;
                foreach (UIA.AutomationElement e in root.FindAll(UIA.TreeScope.Descendants, cond))
                {
                    var r = e.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Width > 0 && !(r.Right <= right)) right = r.Right;
                }
                return double.IsNaN(right) ? null : (int)right;
            }
            var widgets = root.FindFirst(UIA.TreeScope.Descendants,
                new UIA.PropertyCondition(UIA.AutomationElement.AutomationIdProperty, "WidgetsButton"));
            var w = widgets?.Current.BoundingRectangle ?? Rect.Empty;
            return w.IsEmpty || w.Width <= 0 ? null : (int)w.Right;
        }
        catch { return null; }
    }

    private static bool IconsLeftAligned()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int v && v == 0;
        }
        catch { return false; }
    }

    private void UpdateClip()
    {
        var r = Radius;
        _surface.CornerRadius = new CornerRadius(r);
        if (_surface.ActualWidth > 0)
            _surface.Child.Clip = new RectangleGeometry(new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight), r, r);
    }

    // ---------- open / close ----------

    public void Open(string? pageId)
    {
        _hoverTimer.Stop();
        pageId ??= _ctx.Settings.Island.LastPage;
        if (!_ctx.Settings.Island.Sections.Contains(pageId)) pageId = _ctx.Settings.Island.Sections.FirstOrDefault() ?? "system";
        if (!IsVisible) Show();
        if (!_open)
        {
            _open = true;
            _closedLayer.Visibility = Visibility.Collapsed;
            _openLayer.Visibility = Visibility.Visible;
            Fade(_openLayer, 1, 220, 60);
            _surface.Cursor = Cursors.Arrow;
            AnimateSize(OpenWidth, OpenHeight, 28, grow: true);
            StartGlass();
        }
        ShowPage(pageId);
    }

    /// Scrolls the page on screen, if it scrolls (used by island.request for checks).
    public void ScrollPage(double offset)
    {
        static ScrollViewer? Find(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                if (Find(VisualTreeHelper.GetChild(d, i)) is { } hit) return hit;
            return null;
        }
        if (_current is not null) Find(_current)?.ScrollToVerticalOffset(offset);
    }

    public void Toggle(string? pageId)
    {
        if (_open && (pageId is null || _current?.Id == pageId)) Close(true);
        else Open(pageId);
    }

    public void Close(bool restoreFocus)
    {
        if (!_open) return;
        _open = false;
        _current?.OnHidden();
        AllowKeyboard(false, restoreFocus);
        _openLayer.BeginAnimation(OpacityProperty, null);
        _openLayer.Opacity = 0;
        _openLayer.Visibility = Visibility.Collapsed;
        _closedLayer.Visibility = Visibility.Visible;
        _closedLayer.Opacity = 0;
        Fade(_closedLayer, 1, 200, 120);
        _surface.Cursor = Cursors.Hand;
        StopGlass();
        UpdateCapsule(animate: false);
        AnimateSize(CapsuleWidth(), ClosedHeight, 16, grow: false);
    }

    // ---------- glass ----------
    // WPF cannot blur what lies behind a transparent window, so the open island takes a snapshot of
    // the screen under it (leaving itself out of the capture), blurs it and tints it, like the Mac's
    // Liquid Glass island. The snapshot follows what is behind every 1.5 seconds while open.

    private readonly Image _glassImage = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 36, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
    private readonly Border _glassTint = new() { Background = new SolidColorBrush(Color.FromArgb(178, 44, 44, 46)) };
    private readonly Grid _glass = new() { Visibility = Visibility.Collapsed, ClipToBounds = true };
    private readonly DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _capturing;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private void BuildGlass(Grid layers)
    {
        _glassImage.Width = OpenWidth;
        _glassImage.Height = OpenHeight;
        _glass.Children.Add(_glassImage);
        _glass.Children.Add(_glassTint);
        layers.Children.Insert(0, _glass);
        _glassTimer.Tick += async (_, _) => await CaptureGlass();
    }

    private async void StartGlass()
    {
        if (!_ctx.Settings.Island.Glass) return;
        _glass.Visibility = Visibility.Visible;
        _surface.Background = new SolidColorBrush(Color.FromRgb(40, 40, 42));
        _glassImage.Source = null;
        await CaptureGlass();
        if (_open) _glassTimer.Start();
    }

    private void StopGlass()
    {
        _glassTimer.Stop();
        _glass.Visibility = Visibility.Collapsed;
        _surface.Background = Brushes.Black;
    }

    private async Task CaptureGlass()
    {
        if (_capturing || _hwnd == IntPtr.Zero || !_open) return;
        _capturing = true;
        try
        {
            // Where the open island sits on screen, in pixels.
            var dpi = VisualTreeHelper.GetDpi(this);
            var left = (OnTaskbar ? Left : Left + (ActualWidth - OpenWidth) / 2) * dpi.DpiScaleX;
            var top = (OnTaskbar ? Top + ActualHeight - OpenMargin.Bottom - OpenHeight : Top + TopMargin) * dpi.DpiScaleY;
            int w = (int)(OpenWidth * dpi.DpiScaleX), h = (int)(OpenHeight * dpi.DpiScaleY);
            // WDA_EXCLUDEFROMCAPTURE for one frame: the screen keeps showing the island, the copy doesn't.
            SetWindowDisplayAffinity(_hwnd, 0x11);
            await Task.Delay(40);
            BitmapSource? shot = null;
            try
            {
                using var bmp = new System.Drawing.Bitmap(Math.Max(1, w / 2), Math.Max(1, h / 2));
                using (var full = new System.Drawing.Bitmap(Math.Max(1, w), Math.Max(1, h)))
                {
                    using (var g = System.Drawing.Graphics.FromImage(full)) g.CopyFromScreen((int)left, (int)top, 0, 0, full.Size);
                    using var gs = System.Drawing.Graphics.FromImage(bmp);
                    gs.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    gs.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
                }
                var hbmp = bmp.GetHbitmap();
                try { shot = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
                finally { DeleteObject(hbmp); }
                shot.Freeze();
            }
            finally { SetWindowDisplayAffinity(_hwnd, 0); }
            if (_open && shot is not null) _glassImage.Source = shot;
        }
        catch (Exception ex) { Log.Error("island glass", ex); }
        finally { _capturing = false; }
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private void AnimateSize(double width, double height, double radius, bool grow)
    {
        // Growth overshoots a little like the Mac spring (bounce ~0.25); shrinking settles without bounce.
        IEasingFunction ease = grow ? new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var wDur = TimeSpan.FromMilliseconds(grow ? 440 : 300);
        var hDur = TimeSpan.FromMilliseconds(grow ? 380 : 260);
        _surface.BeginAnimation(WidthProperty, new DoubleAnimation(width, wDur) { EasingFunction = ease });
        _surface.BeginAnimation(HeightProperty, new DoubleAnimation(height, hDur) { EasingFunction = ease });
        BeginAnimation(RadiusProperty, new DoubleAnimation(radius, hDur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // On the taskbar the capsule sits inside the taskbar band; the open panel lifts to just above it.
        if (OnTaskbar)
            _surface.BeginAnimation(MarginProperty, new ThicknessAnimation(grow ? OpenMargin : ClosedMargin, hDur) { EasingFunction = ease });
    }

    private static void Fade(UIElement el, double to, int ms, int delayMs = 0) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delayMs) });

    /// The island never takes focus, except while a page needs typing (scratchpad, clipboard search).
    private void AllowKeyboard(bool allow) => AllowKeyboard(allow, true);

    private void AllowKeyboard(bool allow, bool restoreFocus)
    {
        if (_hwnd == IntPtr.Zero || allow == _keyboard) return;
        _keyboard = allow;
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        if (allow)
        {
            _previousForeground = Native.GetForegroundWindow();
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_NOACTIVATE);
            Activate();
            Native.SetForegroundWindow(_hwnd);
        }
        else
        {
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE);
            Keyboard.ClearFocus();
            if (restoreFocus && _previousForeground != IntPtr.Zero) Native.SetForegroundWindow(_previousForeground);
            _previousForeground = IntPtr.Zero;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_open) return;
        if (e.Key == Key.Escape) { Close(true); e.Handled = true; }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var ids = _ctx.Settings.Island.Sections;
            var i = ids.IndexOf(_current?.Id ?? "");
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            ShowPage(ids[(i + step + ids.Count) % ids.Count]);
            e.Handled = true;
        }
    }

    // ---------- open layer: top bar + pages ----------

    private void BuildOpenLayer()
    {
        _openLayer.Margin = new Thickness(Inset, 12, Inset, 16);
        _openLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });
        _openLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        _openLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PageHeight) });
        var barGrid = Ui.Columns((_bar, Ui.Star), (_barEnd, Ui.Auto));
        _openLayer.Children.Add(barGrid);
        Grid.SetRow(_pageHost, 2);
        _openLayer.Children.Add(_pageHost);
        RebuildBar();
    }

    private IslandPage? Page(string id)
    {
        if (_pages.TryGetValue(id, out var page)) return page;
        page = id switch
        {
            "agents" => new AgentsPage(_ctx),
            "keepAwake" => new KeepAwakePage(_ctx),
            "music" => new MusicPage(_ctx),
            "mixer" => new MixerPage(_ctx),
            "calendar" => new CalendarPage(_ctx),
            "clipboard" => new ClipboardPage(_ctx),
            "scratchpad" => new ScratchpadPage(_ctx),
            "microphone" => new MicrophonePage(_ctx),
            "system" => new SystemPage(_ctx),
            _ => null,
        };
        if (page is not null) _pages[id] = page;
        return page;
    }

    private void RebuildBar()
    {
        _bar.Children.Clear();
        _bar.ColumnDefinitions.Clear();
        _barEnd.Children.Clear();
        _barItems.Clear();
        foreach (var id in _ctx.Settings.Island.Sections)
        {
            var page = Page(id);
            if (page is null) continue;
            var glyph = Ui.Icon(page.Glyph, 16, page.Tint);
            var label = Ui.Text(page.Title, 12, FontWeights.SemiBold, 1, page.Tint);
            label.Margin = new Thickness(6, 0, 0, 0);
            label.Visibility = Visibility.Collapsed;
            var inner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            inner.Children.Add(glyph);
            inner.Children.Add(label);
            var badge = new Border
            {
                MinWidth = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Ui.B(page.Tint),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -4, 0), Padding = new Thickness(3, 0, 3, 0), Visibility = Visibility.Collapsed,
                Child = new TextBlock { FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var dot = new Border
            {
                Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.B(page.Tint),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed,
            };
            var holder = new Grid();
            holder.Children.Add(inner);
            holder.Children.Add(badge);
            holder.Children.Add(dot);
            var button = new Pressable
            {
                Height = 32, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(10),
                Margin = new Thickness(1.5, 0, 1.5, 0), Child = holder, ToolTip = page.Title,
                HoverBackground = Ui.B(page.Tint, 0.14), VerticalAlignment = VerticalAlignment.Center,
            };
            var pageId = id;
            button.Click += () => ShowPage(pageId);
            if (id == "keepAwake") button.RightClick += _ctx.KeepAwake.Toggle;
            if (id == "microphone") button.RightClick += () => { _ctx.Audio.ToggleMic(); UpdateBarState(); };
            _barItems[id] = (button, glyph, label, badge, dot);
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, _bar.ColumnDefinitions.Count - 1);
            _bar.Children.Add(button);
        }
        _barEnd.Children.Add(Ui.IconButton(Ui.GSettings, "Settings", () => { Close(false); _ctx.OpenSettings(); }, Ui.Gray, 32, 16));
        _barEnd.Children.Add(Ui.IconButton(Ui.GPower, "Quit PC Essentials", _ctx.Quit, Ui.Red, 32, 16));
        UpdateBarState();
    }

    private void ShowPage(string id)
    {
        var page = Page(id);
        if (page is null) return;
        if (_current != page)
        {
            _current?.OnHidden();
            _current = page;
            _pageHost.Content = page;
            page.Opacity = 0;
            Fade(page, 1, 180);
            page.OnShown();
            AllowKeyboard(page.WantsKeyboard);
        }
        _ctx.Settings.Island.LastPage = id;
        if (id == "clipboard") _ctx.Clipboard.MarkSeen();
        UpdateBarState();
    }

    private void UpdateBarState()
    {
        foreach (var (id, item) in _barItems)
        {
            var page = _pages[id];
            var selected = _open && _current?.Id == id;
            item.Button.SetRestBackground(selected ? Ui.B(page.Tint, 0.24) : Brushes.Transparent);
            item.Glyph.Opacity = selected ? 1 : 0.62;
            item.Label.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            // The selected section sizes to its label; the rest share what is left.
            _bar.ColumnDefinitions[Grid.GetColumn(item.Button)].Width = selected ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            var count = id switch
            {
                "agents" => _ctx.Agents.Summary.Live.Count,
                "clipboard" => _ctx.Clipboard.UnseenCount,
                _ => 0,
            };
            item.Badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)item.Badge.Child).Text = count > 9 ? "9+" : count.ToString();
            switch (id)
            {
                case "keepAwake":
                    item.Glyph.Text = _ctx.KeepAwake.IsActive ? Ui.GEye : Ui.GCoffee;
                    item.Glyph.Foreground = Ui.B(_ctx.KeepAwake.IsActive ? Ui.AwakeOn : Ui.Amber);
                    item.Dot.Visibility = _ctx.KeepAwake.IsActive ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "microphone":
                    var muted = _ctx.Audio.InputMuted;
                    item.Glyph.Text = muted ? Ui.GMicOff : Ui.GMic;
                    item.Glyph.Foreground = Ui.B(muted ? Ui.Red : Ui.Green);
                    break;
            }
        }
    }

    // ---------- capsule ----------

    private void BuildCapsule()
    {
        _closedLayer.Margin = new Thickness(10, 0, 10, 0);
        _capTitle.Margin = new Thickness(8, 0, 8, 0);
        _capTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var row = Ui.Columns((_capLeft, Ui.Auto), (_capTitle, Ui.Star), (_capRight, Ui.Auto));
        row.VerticalAlignment = VerticalAlignment.Center;
        _closedLayer.Children.Add(row);
    }

    private double _capsuleWidth = 140;

    private double CapsuleWidth() => _capsuleWidth + (_hovering && !_ctx.Settings.Island.OpenOnHover ? 20 : 0);

    /// Picks the most important live activity for the closed capsule, like the Mac island:
    /// a transient notice, then agents at work, then music, then keep awake.
    private void UpdateCapsule(bool animate = true)
    {
        if (_open) return;
        var s = _ctx.Settings.Island;
        double width;
        if (_notice is { } n && n.Until > DateTime.Now)
        {
            _capLeft.Child = Ui.Icon(n.Glyph, 13, n.Tint);
            if (n.Level is double level)
            {
                _capTitle.Text = "";
                var meter = Ui.Meter(level, Colors.White, 120, 4);
                _capRight.Child = Ui.Columns((meter, Ui.Px(120)), (Ui.Spacer(8), Ui.Auto), (Ui.Text($"{level * 100:0}%", 12, FontWeights.SemiBold, 0.95, null, true), Ui.Px(34)));
                width = 220;
            }
            else
            {
                _capTitle.Text = n.Title;
                _capTitle.Foreground = Ui.B(Colors.White, 0.95);
                _capRight.Child = n.Detail is null ? null : Ui.Text(n.Detail, 12, FontWeights.Medium, 0.62);
                width = 320;
            }
        }
        else if (s.ShowAgentsWhileWorking && _ctx.Agents.Summary.Live.Count > 0)
        {
            var live = _ctx.Agents.Summary.Live;
            var first = live[0];
            _capLeft.Child = AgentsPage.Mark(live.Count > 1 ? 14 : 16);
            _capTitle.Text = live.Count > 1 ? $"{live.Count} agents working" : first.Project;
            _capTitle.Foreground = Ui.B(Colors.White, 0.62);
            _capRight.Child = Ui.Text(Ui.Clock(DateTime.Now - first.Since), 13, FontWeights.SemiBold, 1, Ui.Clay, true);
            width = 260;
        }
        else if (_ctx.Media.Current is { IsPlaying: true } np)
        {
            _capLeft.Child = Cover(np, 20);
            _capTitle.Text = _hovering ? np.Title : "";
            _capTitle.Foreground = Ui.B(Colors.White, 0.9);
            _capRight.Child = _capBars;
            width = _hovering ? 280 : 200;
        }
        else if (s.ShowKeepAwakeInCapsule && _ctx.KeepAwake.IsActive)
        {
            _capLeft.Child = Ui.Icon(Ui.GCoffee, 13, Ui.Yellow);
            _capTitle.Text = "";
            _capRight.Child = Ui.Text(_ctx.KeepAwake.RemainingShort, 12.5, FontWeights.SemiBold, 1, Ui.Yellow, true);
            width = 170;
        }
        else if (s.IdleContent == "agents" && AgentResting.Row(_ctx.Agents.Summary, s, _ctx.Settings.Agents.SeatOrder, out var key) is { } rest)
        {
            // The resting AI limits. Rebuilt only when what they show changes, so Rotate fades between agents.
            if (key != _restKey || _capLeft.Child is not FrameworkElement { Tag: "rest" })
            {
                _restKey = key;
                rest.Tag = "rest";
                _capLeft.Child = rest;
                rest.Opacity = 0;
                Fade(rest, 1, 300);
            }
            _capTitle.Text = "";
            _capRight.Child = null;
            _capLeft.Child.Measure(new Size(double.PositiveInfinity, ClosedHeight));
            width = Math.Max(120, _capLeft.Child.DesiredSize.Width + 36);
        }
        else if (s.IdleContent == "music" && _ctx.Media.Current is { } paused)
        {
            _capLeft.Child = Cover(paused, 20);
            _capTitle.Text = "";
            _capRight.Child = null;
            width = 140;
        }
        else
        {
            _capLeft.Child = null;
            _capTitle.Text = "";
            _capRight.Child = null;
            width = 140;
        }
        if (_capLeft.Child is FrameworkElement { Tag: not "rest" }) _restKey = null;
        var changed = Math.Abs(width - _capsuleWidth) > 0.5;
        _capsuleWidth = width;
        if (!animate) return;
        if (changed || Math.Abs(_surface.Width - CapsuleWidth()) > 0.5)
            _surface.BeginAnimation(WidthProperty, new DoubleAnimation(CapsuleWidth(), TimeSpan.FromMilliseconds(340))
            { EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut } });
    }

    public static FrameworkElement Cover(NowPlaying np, double size)
    {
        if (np.Artwork is null)
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Ui.B(Ui.Rose, 0.3), Child = Ui.Icon(Ui.GMusic, size * 0.5, Ui.Rose) };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = new ImageBrush(np.Artwork) { Stretch = Stretch.UniformToFill },
        };
    }

    private void ShowNotice(string glyph, Color tint, string title, string? detail, double? level, TimeSpan duration)
    {
        _notice = (glyph, tint, title, detail, level, DateTime.Now + duration);
        if (!IsVisible && !(OnTaskbar && _taskbarHidden)) Show();
        UpdateCapsule();
    }

    private void ShowAgentFinished(AgentFinished f)
    {
        var detail = $"{(f.Project.Length > 0 ? f.Project + " · " : "")}{Ui.Clock(f.Duration)}";
        ShowNotice(Ui.GCheck, Ui.Clay, "Claude finished", detail, null, TimeSpan.FromSeconds(6));
        System.Media.SystemSounds.Asterisk.Play();
    }

    // ---------- timers ----------

    private void FastTick()
    {
        // Caps Lock has no event to listen to; its toggle state is cheap to read.
        var caps = (GetKeyState(0x14 /* VK_CAPITAL */) & 1) != 0;
        if (_capsLock is bool was && was != caps && _ctx.Settings.Island.ShowCapsLock)
            ShowNotice(Ui.GKeyboard, caps ? Ui.Green : Colors.White, caps ? "Caps Lock on" : "Caps Lock off", null, null, TimeSpan.FromSeconds(1.4));
        _capsLock = caps;
        // Clicking the taskbar raises it above other topmost windows; win back the top quickly.
        if (OnTaskbar && _hwnd != IntPtr.Zero && IsVisible && ++_fastCount % 5 == 0)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (_open)
        {
            _current?.Tick();
            if (MouseClickedOutside()) Close(true);
            return;
        }
        if (_capRight.Child == _capBars)
            _capBars.Update(_ctx.Audio.OutputPeak, _ctx.Media.Current?.IsPlaying == true);
    }

    private void SlowTick()
    {
        if (_notice is { } n && n.Until <= DateTime.Now)
        {
            _notice = null;
            UpdateCapsule();
        }
        else if (!_open) UpdateCapsule();

        if (OnTaskbar) { RefreshAnchor(); Position(); }
        var hide = _ctx.Settings.Island.HideInFullscreen && !_open && Native.ForegroundIsFullscreen() && _notice is null;
        if (OnTaskbar && _taskbarHidden && !_open) hide = true;
        if (!_ctx.Settings.Island.Enabled || hide) { if (IsVisible) Hide(); }
        else if (!IsVisible) Show();
        // Stay above windows that went topmost after us.
        if (_hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    /// Whether a notice would be seen. On a taskbar that hides itself the island is out of sight most of the
    /// time, so Windows' own volume pop-up is better left alone.
    public bool CanShowNotices => !OnTaskbar || !TaskbarAutoHides();

    private static bool TaskbarAutoHides()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(4 /* ABM_GETSTATE */, ref data) & 1 /* ABS_AUTOHIDE */) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint message, ref APPBARDATA data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point p);

    /// On the taskbar the open panel lifts above the taskbar, away from the capsule the pointer opened it
    /// from. The strip below the panel, down to the taskbar's bottom edge and as wide as the panel, still
    /// counts as the island, so resting there or moving up into the panel doesn't close it.
    private bool PointerOnTaskbarBridge()
    {
        if (!OnTaskbar || !_open || !GetCursorPos(out var p)) return false;
        var topLeft = _surface.PointToScreen(new Point(0, 0));
        var bottomRight = _surface.PointToScreen(new Point(_surface.ActualWidth, _surface.ActualHeight));
        var windowBottom = PointToScreen(new Point(0, ActualHeight)).Y;
        return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= bottomRight.Y - 1 && p.Y <= windowBottom;
    }

    private bool MouseClickedOutside()
    {
        var pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (!pressed || !GetCursorPos(out var p)) return false;
        var topLeft = _surface.PointToScreen(new Point(0, 0));
        var bottomRight = _surface.PointToScreen(new Point(_surface.ActualWidth, _surface.ActualHeight));
        var inside = p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
        // Menus live in their own popup windows; a click in one is not a click outside.
        return !inside && !Menus.AnyOpen;
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action a)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => a();
            menu.Items.Add(item);
        }
        foreach (var id in _ctx.Settings.Island.Sections)
            if (Page(id) is { } p) Add(p.Title, () => Open(p.Id));
        menu.Items.Add(new Separator());
        Add("Settings…", _ctx.OpenSettings);
        Add(_open ? "Collapse" : "Open", () => Toggle(null));
        Add("Quit PC Essentials", _ctx.Quit);
        Menus.Track(menu);
        _surface.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
