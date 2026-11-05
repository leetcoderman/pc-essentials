using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PCEssentials.UI;

/// Palette, fonts and small building blocks shared by the island, the widget and Settings.
/// Colours follow the Mac island (NotchTopBarPalette.swift).
public static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static readonly Color Clay = C("#D97757"), Amber = C("#FFB224"), Rose = C("#FF375F"), Purple = C("#BF5AF2"),
        Red = C("#FF453A"), Tan = C("#D4A373"), Yellow = C("#FFD60A"), Green = C("#30D158"), Blue = C("#0A84FF"),
        Orange = C("#FF9F0A"), Gray = C("#98989D"), AwakeOn = C("#FF546B"), Codex = C("#7D99FF"), Mint = C("#63E6E2");

    // Segoe Fluent Icons code points.
    public const string GSettings = "", GPower = "", GPlay = "", GPause = "", GPrev = "",
        GNext = "", GVolume = "", GMute = "", GMic = "", GMicOff = "", GCalendar = "",
        GClipboard = "", GNote = "", GEye = "", GMoon = "", GMusic = "", GMixer = "",
        GSystem = "", GRobot = "", GSearch = "", GDelete = "", GCopy = "", GAdd = "",
        GClose = "", GPin = "", GUnpin = "", GLeft = "", GRight = "", GRefresh = "",
        GSpeaker = "", GShuffle = "", GImage = "", GFolder = "", GText = "", GTimer = "",
        GCpu = "", GMemory = "", GNetwork = "", GDisk = "", GGpu = "", GDown = "",
        GUp = "", GCheck = "", GReset = "", GCoffee = "", GKeyboard = "", GChevronDown = "";

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush B(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public static SolidColorBrush White(double opacity) => B(Colors.White, opacity);

    public static TextBlock Text(string text, double size = 12, FontWeight? weight = null, double opacity = 0.9,
        Color? color = null, bool mono = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = mono ? Mono : Font,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = B(color ?? Colors.White, opacity),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Icon(string glyph, double size, Color? color = null, double opacity = 1) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        Foreground = B(color ?? Colors.White, opacity),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// Rounded surface used for cards (radius 18, 6.5% white, as NotchControlSurface).
    public static Border Card(UIElement child, double padding = 12, double radius = 18) => new()
    {
        Background = White(0.065),
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(padding),
        Child = child,
    };

    public static Pressable IconButton(string glyph, string tip, Action onClick, Color? color = null, double size = 28, double glyphSize = 12)
    {
        var p = new Pressable
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(9),
            Child = Icon(glyph, glyphSize, color ?? Colors.White, color is null ? 0.7 : 1),
            ToolTip = tip,
        };
        p.Click += onClick;
        return p;
    }

    public static Pressable PillButton(string text, Action onClick, bool prominent = false, Color? tint = null)
    {
        var p = new Pressable
        {
            Height = 28,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(14),
            Background = prominent ? B(tint ?? Colors.White, tint is null ? 0.14 : 0.24) : White(0.06),
            Child = Text(text, 12, FontWeights.SemiBold, 0.9, tint),
        };
        p.Click += onClick;
        return p;
    }

    public static FrameworkElement Meter(double fraction, Color tint, double width = double.NaN, double height = 5)
    {
        var grid = new Grid { Height = height, Width = width, ClipToBounds = true };
        grid.Children.Add(new Border { Background = White(0.14), CornerRadius = new CornerRadius(height / 2) });
        var fill = new Border
        {
            Background = B(tint, 0.9),
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        grid.Children.Add(fill);
        void Size() => fill.Width = Math.Max(0, Math.Min(1, fraction)) * grid.ActualWidth;
        grid.SizeChanged += (_, _) => Size();
        return grid;
    }

    /// Empty state: a soft tile with a glyph and one line of text.
    public static FrameworkElement Empty(string glyph, string message, string? detail = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Background = White(0.05),
            Child = Icon(glyph, 24, Colors.White, 0.6), Margin = new Thickness(0, 0, 0, 10),
        });
        var t = Text(message, 12.5, FontWeights.SemiBold, 0.8);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(t);
        if (detail is not null)
        {
            var d = Text(detail, 11.5, null, 0.5);
            d.HorizontalAlignment = HorizontalAlignment.Center;
            d.TextWrapping = TextWrapping.Wrap;
            d.TextAlignment = TextAlignment.Center;
            d.MaxWidth = 340;
            d.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(d);
        }
        return stack;
    }

    public static Grid Columns(params (UIElement Element, GridLength Width)[] cols)
    {
        var g = new Grid();
        foreach (var (el, w) in cols)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            Grid.SetColumn(el, g.ColumnDefinitions.Count - 1);
            g.Children.Add(el);
        }
        return g;
    }

    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => new(1, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);

    public static UIElement Spacer(double w = 0, double h = 0) => new Border { Width = w, Height = h };

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        PanningMode = PanningMode.VerticalOnly,
    };

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    /// "$6.94", "$44.10", then "$115", "$1,954": cents only below $100, as the Mac app shows amounts.
    public static string Money(double usd) => usd >= 100 ? $"${usd:N0}" : $"${usd:0.00}";

    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.0}B",
        >= 1_000_000 => $"{n / 1e6:0.0}M",
        >= 1_000 => $"{n / 1e3:0}K",
        _ => n.ToString(),
    };

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}

/// A borderless button: 9% white wash on hover, slight scale on press, like NotchButtonStyle.
public class Pressable : Border
{
    public event Action? Click;
    public event Action? RightClick;
    public Brush? HoverBackground { get; set; } = Ui.White(0.09);
    private Brush? _rest;
    private bool _down;
    private readonly ScaleTransform _scale = new(1, 1);

    public Pressable()
    {
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _scale;
        Background ??= Brushes.Transparent;
        MouseEnter += (_, _) =>
        {
            _rest = Background;
            if (HoverBackground is not null && (Background is not SolidColorBrush s || s.Color.A < 40)) Background = HoverBackground;
        };
        MouseLeave += (_, _) =>
        {
            if (_rest is not null) Background = _rest;
            _down = false;
            AnimateScale(1);
        };
        MouseLeftButtonDown += (_, e) => { _down = true; AnimateScale(0.965); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            AnimateScale(1);
            if (!_down) return;
            _down = false;
            e.Handled = true;
            Click?.Invoke();
        };
        MouseRightButtonUp += (_, e) =>
        {
            if (RightClick is null) return;
            e.Handled = true;
            RightClick();
        };
    }

    /// Changes the resting background without fighting the hover wash.
    public void SetRestBackground(Brush b)
    {
        _rest = b;
        Background = b;
    }

    private void AnimateScale(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}

/// Draggable level slider (horizontal or vertical) with a rounded fill, like NotchLevelSlider.
public class LevelSlider : Grid
{
    private readonly Border _track, _fill;
    private readonly bool _vertical;
    private double _value;
    public event Action<double>? ValueChanged;
    public bool IsDragging { get; private set; }

    public LevelSlider(Color tint, bool vertical = false, double thickness = 28)
    {
        _vertical = vertical;
        if (vertical) Width = thickness; else Height = thickness;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        _track = new Border { Background = Ui.White(0.08), CornerRadius = new CornerRadius(thickness / 3.5) };
        _fill = new Border
        {
            Background = Ui.B(tint, 0.85),
            CornerRadius = new CornerRadius(thickness / 3.5),
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
        };
        Children.Add(_track);
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) => { IsDragging = true; CaptureMouse(); SetFrom(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsDragging) SetFrom(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { IsDragging = false; ReleaseMouseCapture(); e.Handled = true; };
        MouseWheel += (_, e) => { SetValue(_value + Math.Sign(e.Delta) * 0.02, true); e.Handled = true; };
    }

    public void SetTint(Color tint) => _fill.Background = Ui.B(tint, 0.85);

    public double Value => _value;

    public void SetValue(double v, bool notify = false)
    {
        _value = Math.Clamp(v, 0, 1);
        Layout();
        if (notify) ValueChanged?.Invoke(_value);
    }

    private void SetFrom(Point p)
    {
        var v = _vertical ? 1 - p.Y / Math.Max(1, ActualHeight) : p.X / Math.Max(1, ActualWidth);
        SetValue(v, true);
    }

    private void Layout()
    {
        if (_vertical) _fill.Height = _value * ActualHeight;
        else _fill.Width = _value * ActualWidth;
    }
}

/// Small line chart for history graphs.
public class Sparkline : Canvas
{
    private readonly Polyline _line;
    private readonly Polygon _area;
    public double? Max { get; set; }

    public Sparkline(Color tint)
    {
        ClipToBounds = true;
        _area = new Polygon { Fill = Ui.B(tint, 0.18) };
        _line = new Polyline { Stroke = Ui.B(tint, 0.95), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        Children.Add(_area);
        Children.Add(_line);
    }

    public void Set(IReadOnlyList<double> values)
    {
        double w = ActualWidth, h = ActualHeight;
        if (values.Count < 2 || w <= 0 || h <= 0) return;
        var max = Max ?? Math.Max(1e-6, values.Max());
        var pts = new PointCollection();
        for (var i = 0; i < values.Count; i++)
            pts.Add(new Point(i * w / (values.Count - 1), h - Math.Min(1, values[i] / max) * (h - 2) - 1));
        _line.Points = pts;
        var area = new PointCollection(pts) { new Point(w, h), new Point(0, h) };
        _area.Points = area;
    }
}

/// Live equalizer bars driven by a 0..1 level.
public class EqBars : StackPanel
{
    private readonly List<Rectangle> _bars = new();
    private readonly double _max;
    private readonly Random _rng = new();

    public EqBars(int count, Color tint, double barWidth = 2, double maxHeight = 12, double gap = 2)
    {
        Orientation = Orientation.Horizontal;
        _max = maxHeight;
        Height = maxHeight;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < count; i++)
        {
            var r = new Rectangle
            {
                Width = barWidth, Height = 2, RadiusX = barWidth / 2, RadiusY = barWidth / 2, Fill = Ui.B(tint),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0),
            };
            _bars.Add(r);
            Children.Add(r);
        }
    }

    public void SetTint(Color c)
    {
        foreach (var b in _bars) b.Fill = Ui.B(c);
    }

    public void Update(double level, bool playing)
    {
        foreach (var b in _bars)
        {
            var target = playing ? Math.Max(2, _max * Math.Min(1, level * (0.55 + _rng.NextDouble() * 0.9) * 1.6)) : 2;
            b.Height += (target - b.Height) * 0.55;
        }
    }
}

/// Popup menus for pickers (devices, presets). Tracks whether one is open so the island does not
/// treat a click inside it as a click outside.
public static class Menus
{
    private static int _open;
    public static bool AnyOpen => _open > 0;

    public static void Track(ContextMenu menu)
    {
        menu.Opened += (_, _) => _open++;
        menu.Closed += (_, _) => _open = Math.Max(0, _open - 1);
    }

    /// Shows a picker under the target. Inside the island it is drawn in the page itself: the island re-asserts
    /// HWND_TOPMOST every second, which buries a ContextMenu's popup window behind it, and the island never
    /// activates, so a popup there is unreliable. Elsewhere it is a normal ContextMenu.
    public static void Show(UIElement target, IEnumerable<(string Text, bool Checked, Action Action)> items)
    {
        var list = items.ToList();
        if (target is FrameworkElement fe && Host(fe) is { } host)
        {
            ShowInline(host, fe, list);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (text, isChecked, action) in list)
        {
            var item = new MenuItem { Header = text, IsCheckable = false, IsChecked = isChecked };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Nothing to choose", IsEnabled = false });
        Track(menu);
        menu.IsOpen = true;
    }

    private static Island.IslandPage? Host(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is Island.IslandPage page) return page;
        return null;
    }

    private const double RowHeight = 32, MaxRows = 7;
    private static Action? _closeInline;

    private static void ShowInline(Grid host, FrameworkElement target, List<(string Text, bool Checked, Action Action)> items)
    {
        _closeInline?.Invoke();
        var layer = new Grid();
        Panel.SetZIndex(layer, 1000);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count));
        DependencyPropertyChangedEventHandler? hidden = null;
        void Close()
        {
            host.IsVisibleChanged -= hidden;
            host.Children.Remove(layer);
            _closeInline = null;
        }
        // Leaving the page (switching page, closing the island) drops the picker.
        hidden = (_, e) => { if (!(bool)e.NewValue) Close(); };
        host.IsVisibleChanged += hidden;
        _closeInline = Close;

        // Clicking anywhere else on the page dismisses the picker without doing anything else.
        var backdrop = new Border { Background = Brushes.Transparent };
        backdrop.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };
        backdrop.MouseRightButtonDown += (_, e) => { e.Handled = true; Close(); };
        layer.Children.Add(backdrop);

        var stack = new StackPanel { Margin = new Thickness(4) };
        foreach (var (text, isChecked, action) in items)
        {
            var row = Ui.Columns(
                (Ui.Icon(isChecked ? Ui.GCheck : "", 11, Ui.Purple), Ui.Px(22)),
                (Ui.Text(text, 12, isChecked ? FontWeights.SemiBold : FontWeights.Normal, isChecked ? 1 : 0.85), Ui.Star));
            var option = new Pressable { Height = RowHeight, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 10, 0), Child = row, ToolTip = text };
            var run = action;
            option.Click += () => { Close(); run(); };
            stack.Children.Add(option);
        }
        if (items.Count == 0)
            stack.Children.Add(new Border { Height = RowHeight, Padding = new Thickness(28, 0, 10, 0), Child = Ui.Text("Nothing to choose", 12, null, 0.5) });

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            BorderBrush = Ui.White(0.12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new ScrollViewer
            {
                Content = stack,
                MaxHeight = RowHeight * MaxRows + 8,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black },
        };
        // Swallow clicks on the card's padding so they don't reach the backdrop.
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;

        // Under the target (or above it when there's no room), the same width or wider, kept inside the page.
        var width = Math.Min(Math.Max(target.ActualWidth, 240), Math.Max(120, host.ActualWidth));
        card.Width = width;
        card.Measure(new Size(width, double.PositiveInfinity));
        var height = card.DesiredSize.Height;
        var at = target.TranslatePoint(new Point(0, 0), host);
        var left = Math.Clamp(at.X, 0, Math.Max(0, host.ActualWidth - width));
        var top = at.Y + target.ActualHeight + 4;
        if (top + height > host.ActualHeight && at.Y - 4 - height >= 0) top = at.Y - 4 - height;
        card.Margin = new Thickness(left, Math.Max(0, top), 0, 0);
        layer.Children.Add(card);

        host.Children.Add(layer);
        layer.Opacity = 0;
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }
}
