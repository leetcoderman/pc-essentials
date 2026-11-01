using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCEssentials.Core;
using PCEssentials.Services.Screenshot;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace PCEssentials.UI.Screenshot;

/// The Lightshot-style capture screen. The frozen screenshot is shown dimmed on one window per
/// monitor (so each draws at its own DPI); dragging selects an area, which can then be moved and
/// resized by its handles and drawn on. All positions are physical virtual-screen pixels, read
/// with GetCursorPos, so a selection can span monitors with different scaling.
public sealed class ShotOverlay
{
    private sealed class Screen
    {
        public required Window Window { get; init; }
        public required Surface Surface { get; init; }
        public required Canvas Controls { get; init; }
        public required PxRect Bounds { get; init; }
        public double Scale => VisualTreeHelper.GetDpi(Surface).DpiScaleX;
        public IntPtr Handle => new WindowInteropHelper(Window).Handle;
    }

    /// Draws the overlay's picture for one monitor.
    private sealed class Surface(ShotOverlay owner) : FrameworkElement
    {
        public Screen? Screen { get; set; }
        protected override void OnRender(DrawingContext dc)
        {
            if (Screen is not null) owner.Render(dc, Screen);
        }
    }

    private enum Drag { None, Selecting, Handle, Drawing }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    /// Line widths (DIPs) and text size per tool, kept for the session like Lightshot does.
    private static readonly Dictionary<ShotTool, double> Widths = new()
    {
        [ShotTool.Pen] = 3, [ShotTool.Line] = 3, [ShotTool.Arrow] = 3, [ShotTool.Rectangle] = 3, [ShotTool.Circle] = 3,
        [ShotTool.Marker] = 16, [ShotTool.Text] = 18, [ShotTool.Blur] = 4,
    };

    private static readonly Brush Dim = Ui.B(Colors.Black, 0.45);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly ScreenshotService _service;
    private readonly AppSettings _settings;
    private readonly ShotFrame _frame;
    private readonly List<Screen> _screens = new();
    private readonly ShotShapes _shapes = new();
    private readonly Dictionary<ShotShape, (PxRect Rect, BitmapSource Image)> _blurCache = new();

    private PxRect? _sel;
    private ShotTool _tool = ShotTool.None;
    private Drag _drag;
    private ShotHandle _handle;
    private (int X, int Y) _dragStart;
    private PxRect _selStart;
    private ShotShape? _current;
    private (int X, int Y) _cursor;
    private bool _closed;

    private TextBox? _textBox;
    private Screen? _textScreen;
    private (int X, int Y) _textAt;

    private string? _hint;
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    private readonly Border _toolsBar, _actionsBar;
    private readonly Dictionary<ShotTool, Pressable> _toolButtons = new();
    private readonly Border _swatch = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Ui.White(0.6) };

    public event Action? Closed;

    public ShotOverlay(ScreenshotService service, AppSettings settings, ShotFrame frame)
    {
        _service = service;
        _settings = settings;
        _frame = frame;
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _hint = null; Invalidate(); };
        _toolsBar = BuildToolsBar();
        _actionsBar = BuildActionsBar();
        UpdateSwatch();

        if (settings.Screenshot.KeepSelection && TryParseRect(settings.Screenshot.LastSelection, out var last))
        {
            var kept = last.Intersect(frame.Bounds);
            if (!kept.IsEmpty) _sel = kept;
        }
    }

    // ---------------- Windows ----------------

    public void Show()
    {
        GetCursorPos(out var p);
        _cursor = (p.X, p.Y);
        foreach (var (bounds, _) in _frame.Monitors)
        {
            var surface = new Surface(this);
            var controls = new Canvas();
            var root = new Grid();
            root.Children.Add(surface);
            root.Children.Add(controls);
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                Background = Brushes.Black,
                Title = "PC Essentials screenshot",
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 10, Height = 10,
                Content = root,
                Cursor = Cursors.Cross,
                FontFamily = Ui.Font,
                UseLayoutRounding = true,
            };
            RenderOptions.SetBitmapScalingMode(surface, BitmapScalingMode.NearestNeighbor);
            var screen = new Screen { Window = w, Surface = surface, Controls = controls, Bounds = bounds };
            surface.Screen = screen;
            w.SourceInitialized += (_, _) =>
            {
                var hwnd = screen.Handle;
                Native.MakeToolWindow(hwnd, noActivate: false);
                // Move onto the monitor first so WPF picks up its DPI, then size it exactly to the monitor.
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, bounds.X + 8, bounds.Y + 8, 64, 64, Native.SWP_NOACTIVATE);
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
            };
            w.Loaded += (_, _) =>
                Native.SetWindowPos(screen.Handle, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
            w.DpiChanged += (_, _) => { PlaceToolbars(); Invalidate(); };
            w.MouseLeftButtonDown += (_, e) => OnDown(screen, e);
            w.MouseMove += (_, _) => OnMove();
            w.MouseLeftButtonUp += (_, _) => OnUp(screen);
            w.MouseRightButtonUp += (_, e) => { e.Handled = true; OnRightClick(); };
            w.MouseWheel += (_, e) => OnWheel(e.Delta);
            w.KeyDown += (_, e) => OnKey(e);
            w.Deactivated += (_, _) => CheckFocusSoon();
            _screens.Add(screen);
            w.Show();
        }
        var under = ScreenAt(_cursor.X, _cursor.Y);
        Activate(under);
        if (_sel is not null) PlaceToolbars();
        Log.Info($"screenshot overlay on {_screens.Count} monitor(s), {_frame.Bounds.Width}x{_frame.Bounds.Height}");
    }

    private static void Activate(Screen s)
    {
        s.Window.Activate();
        if (Native.GetForegroundWindow() == s.Handle) return;
        // Windows only lets the app that had the last input take the foreground; an unassigned key press counts.
        Native.SendKeyCombo(0xE8);
        Native.SetForegroundWindow(s.Handle);
        s.Window.Activate();
    }

    /// Another app took focus (Alt+Tab, a notification clicked): cancel, as Lightshot does.
    /// Our own dialogs (save, print, colour) are owned by an overlay window and don't count.
    private void CheckFocusSoon()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_closed || _modal) return;
            var fg = Native.GetForegroundWindow();
            if (_screens.Any(s => s.Handle == fg)) return;
            Native.GetWindowThreadProcessId(fg, out var pid);
            if (pid == Environment.ProcessId) return;
            Close();
        };
        timer.Start();
    }

    private bool _modal;

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _hintTimer.Stop();
        if (_sel is { } sel)
        {
            _settings.Screenshot.LastSelection = $"{sel.X},{sel.Y},{sel.Width},{sel.Height}";
            _settings.Save();
        }
        foreach (var s in _screens) s.Window.Close();
        _screens.Clear();
        Closed?.Invoke();
    }

    private Screen ScreenAt(double x, double y)
    {
        var m = _frame.MonitorAt(x, y);
        return _screens.FirstOrDefault(s => s.Bounds == m) ?? _screens[0];
    }

    private void Invalidate()
    {
        foreach (var s in _screens) s.Surface.InvalidateVisual();
    }

    private static (int X, int Y) Pointer()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    // ---------------- Mouse ----------------

    private double Grip(Screen s) => 6 * s.Scale;

    private void OnDown(Screen screen, MouseButtonEventArgs e)
    {
        var p = Pointer();
        _cursor = p;
        if (_textBox is not null)
        {
            CommitText();
            if (_tool != ShotTool.Text) return;
        }

        if (e.ClickCount == 2 && _tool == ShotTool.None && _shapes.Count == 0)
        {
            SelectMonitor(false);
            return;
        }

        if (_sel is not { } sel)
        {
            Begin(screen, Drag.Selecting, p);
            return;
        }

        var hit = ShotGeometry.HitTest(sel, p.X, p.Y, Grip(screen));
        if (hit == ShotHandle.Inside && _tool == ShotTool.Text)
        {
            StartText(screen, p);
            return;
        }
        if (hit == ShotHandle.Inside && _tool != ShotTool.None)
        {
            _current = new ShotShape
            {
                Tool = _tool,
                Color = _tool == ShotTool.Marker ? (_settings.Screenshot.Color & 0x00FFFFFF) | 0x66000000 : _settings.Screenshot.Color,
                Width = Widths[_tool] * screen.Scale,
            };
            _current.Points.Add(p);
            _current.Points.Add(p);
            Begin(screen, Drag.Drawing, p);
            return;
        }
        if (hit != ShotHandle.None)
        {
            _handle = hit;
            _selStart = sel;
            Begin(screen, Drag.Handle, p);
            return;
        }
        // Outside the selection: start over, unless that would throw away drawings.
        if (_shapes.Count == 0)
        {
            _sel = null;
            Begin(screen, Drag.Selecting, p);
        }
    }

    private void Begin(Screen screen, Drag drag, (int X, int Y) p)
    {
        _drag = drag;
        _dragStart = p;
        screen.Window.CaptureMouse();
        if (drag is Drag.Selecting or Drag.Handle) HideToolbars();
        Invalidate();
    }

    private void OnMove()
    {
        var p = Pointer();
        if (p == _cursor && _drag == Drag.None) return;
        _cursor = p;
        switch (_drag)
        {
            case Drag.Selecting:
                if (Math.Abs(p.X - _dragStart.X) > 1 || Math.Abs(p.Y - _dragStart.Y) > 1)
                    _sel = PxRect.FromCorners(_dragStart.X, _dragStart.Y, p.X, p.Y).Intersect(_frame.Bounds);
                break;
            case Drag.Handle:
                _sel = ShotGeometry.Drag(_selStart, _handle, p.X - _dragStart.X, p.Y - _dragStart.Y, _frame.Bounds);
                break;
            case Drag.Drawing when _current is not null:
                if (_current.Tool is ShotTool.Pen or ShotTool.Marker)
                {
                    if (_current.End != p) _current.Points.Add(p);
                }
                else _current.Points[^1] = p;
                break;
        }
        UpdateCursor();
        Invalidate();
    }

    private void OnUp(Screen screen)
    {
        foreach (var s in _screens) if (s.Window.IsMouseCaptured) s.Window.ReleaseMouseCapture();
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.Drawing && _current is not null)
        {
            _shapes.Add(_current);
            _current = null;
        }
        if (drag == Drag.Selecting && _sel is { IsEmpty: true }) _sel = null;
        if (drag is Drag.Selecting or Drag.Handle && _sel is not null) PlaceToolbars();
        Invalidate();
    }

    /// Right click clears the selection, or cancels when there is none.
    private void OnRightClick()
    {
        if (_drag != Drag.None) return;
        if (_sel is not null && _shapes.Count == 0)
        {
            _sel = null;
            HideToolbars();
            Invalidate();
        }
        else if (_sel is null) Close();
    }

    private void OnWheel(int delta)
    {
        if (_tool == ShotTool.None) return;
        var step = Math.Sign(delta);
        var (min, max) = _tool == ShotTool.Text ? (8.0, 96.0) : (1.0, 60.0);
        Widths[_tool] = Math.Clamp(Widths[_tool] + step * (_tool is ShotTool.Text or ShotTool.Marker ? 2 : 1), min, max);
        ShowHint(_tool == ShotTool.Text ? $"Text size {Widths[_tool]:0}" : $"Size {Widths[_tool]:0}");
        if (_textBox is not null && _textScreen is not null && _tool == ShotTool.Text)
            _textBox.FontSize = Widths[ShotTool.Text];
    }

    private void ShowHint(string text)
    {
        _hint = text;
        _hintTimer.Stop();
        _hintTimer.Start();
        Invalidate();
    }

    private void UpdateCursor()
    {
        Cursor c = Cursors.Cross;
        if (_drag == Drag.Handle) c = CursorFor(_handle);
        else if (_drag == Drag.None && _sel is { } sel)
        {
            var screen = ScreenAt(_cursor.X, _cursor.Y);
            var hit = ShotGeometry.HitTest(sel, _cursor.X, _cursor.Y, Grip(screen));
            c = hit == ShotHandle.Inside
                ? _tool switch { ShotTool.None => Cursors.SizeAll, ShotTool.Text => Cursors.IBeam, _ => Cursors.Cross }
                : CursorFor(hit);
        }
        foreach (var s in _screens) if (s.Window.Cursor != c) s.Window.Cursor = c;
    }

    private static Cursor CursorFor(ShotHandle h) => h switch
    {
        ShotHandle.N or ShotHandle.S => Cursors.SizeNS,
        ShotHandle.E or ShotHandle.W => Cursors.SizeWE,
        ShotHandle.NW or ShotHandle.SE => Cursors.SizeNWSE,
        ShotHandle.NE or ShotHandle.SW => Cursors.SizeNESW,
        ShotHandle.Inside => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    /// Ctrl+A or a double click: the monitor under the pointer; Ctrl+A again takes every monitor.
    private void SelectMonitor(bool allOnRepeat)
    {
        var m = _frame.MonitorAt(_cursor.X, _cursor.Y);
        _sel = allOnRepeat && _sel == m ? _frame.Bounds : m;
        PlaceToolbars();
        Invalidate();
    }

    // ---------------- Keyboard ----------------

    private void OnKey(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_textBox is not null) CancelText();
            else Close();
            return;
        }
        if (_textBox is not null) return; // typing
        e.Handled = true;
        switch (e.Key)
        {
            case Key.A when ctrl: SelectMonitor(true); break;
            case Key.C when ctrl: Output(Copy); break;
            case Key.S when ctrl: Output(Save); break;
            case Key.P when ctrl: Output(Print); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when _sel is { } sel && _drag == Drag.None:
                var step = ctrl ? 10 : 1;
                int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                int dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                // Arrows move the selection; Shift+arrows resize it from the bottom-right corner.
                _sel = shift ? ShotGeometry.Drag(sel, ShotHandle.SE, dx, dy, _frame.Bounds) : ShotGeometry.Move(sel, dx, dy, _frame.Bounds);
                PlaceToolbars();
                Invalidate();
                break;
            default: e.Handled = false; break;
        }
    }

    private void Undo()
    {
        if (_shapes.Undo()) Invalidate();
    }

    // ---------------- Text ----------------

    private void StartText(Screen screen, (int X, int Y) p)
    {
        var s = screen.Scale;
        var box = new TextBox
        {
            AcceptsReturn = true,
            MinWidth = 24,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = Widths[ShotTool.Text],
            Foreground = new SolidColorBrush(ToColor(_settings.Screenshot.Color)),
            CaretBrush = new SolidColorBrush(ToColor(_settings.Screenshot.Color)),
            Background = Brushes.Transparent,
            Template = PlainTextBoxTemplate(),
        };
        Canvas.SetLeft(box, (p.X - screen.Bounds.X) / s - 1);
        Canvas.SetTop(box, (p.Y - screen.Bounds.Y) / s - 1);
        screen.Controls.Children.Add(box);
        _textBox = box;
        _textScreen = screen;
        _textAt = p;
        box.Loaded += (_, _) => box.Focus();
    }

    /// A bare text box: a dashed outline and the text, no Fluent padding, so the text lands where it was typed.
    private static ControlTemplate PlainTextBoxTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Ui.White(0.7));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        border.AppendChild(host);
        t.VisualTree = border;
        return t;
    }

    private void CommitText()
    {
        if (_textBox is null || _textScreen is null) return;
        var s = _textScreen.Scale;
        var shape = new ShotShape
        {
            Tool = ShotTool.Text,
            Color = _settings.Screenshot.Color,
            FontSize = _textBox.FontSize * s,
            Text = _textBox.Text,
        };
        shape.Points.Add(_textAt);
        _shapes.Add(shape);
        CancelText();
    }

    private void CancelText()
    {
        if (_textBox is null) return;
        (_textBox.Parent as Panel)?.Children.Remove(_textBox);
        _textBox = null;
        var screen = _textScreen;
        _textScreen = null;
        if (screen is not null) Activate(screen);
        Invalidate();
    }

    // ---------------- Toolbars ----------------

    private Border BuildToolsBar()
    {
        var stack = new StackPanel();
        void AddTool(ShotTool tool, UIElement icon, string tip)
        {
            var b = Button(icon, tip, () => SelectTool(tool));
            _toolButtons[tool] = b;
            stack.Children.Add(b);
        }
        AddTool(ShotTool.Pen, Ui.Icon("", 14), "Pen");
        AddTool(ShotTool.Line, PathIcon("M3,13 L13,3", false), "Line");
        AddTool(ShotTool.Arrow, PathIcon("M3,13 L12,4 M6,3.5 L12.5,3.5 L12.5,10", false), "Arrow");
        AddTool(ShotTool.Rectangle, PathIcon("M2.5,4.5 H13.5 V11.5 H2.5 Z", false), "Rectangle");
        AddTool(ShotTool.Circle, new Shapes.Ellipse { Width = 13, Height = 11, Stroke = Ui.White(0.85), StrokeThickness = 1.5 }, "Circle");
        AddTool(ShotTool.Marker, Ui.Icon("", 14), "Marker");
        AddTool(ShotTool.Text, Ui.Icon(Ui.GText, 14), "Text");
        AddTool(ShotTool.Blur, PathIcon("M2,2 H6 V6 H2 Z M10,2 H14 V6 H10 Z M6,6 H10 V10 H6 Z M2,10 H6 V14 H2 Z M10,10 H14 V14 H10 Z", true), "Blur");
        stack.Children.Add(Button(_swatch, "Color", PickColor));
        stack.Children.Add(Button(Ui.Icon("", 14), "Undo (Ctrl+Z)", Undo));
        return Bar(stack);
    }

    private Border BuildActionsBar()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(Button(Ui.Icon(Ui.GImage, 14), "Open in Paint", () => Output(OpenInPaint)));
        stack.Children.Add(Button(Ui.Icon("", 14), "Print (Ctrl+P)", () => Output(Print)));
        stack.Children.Add(Button(Ui.Icon(Ui.GCopy, 14), "Copy (Ctrl+C)", () => Output(Copy)));
        stack.Children.Add(Button(Ui.Icon("", 14), "Save (Ctrl+S)", () => Output(Save)));
        stack.Children.Add(new Border { Width = 1, Margin = new Thickness(3, 6, 3, 6), Background = Ui.White(0.15) });
        stack.Children.Add(Button(Ui.Icon(Ui.GClose, 12), "Close (Esc)", Close));
        return Bar(stack);
    }

    private static Border Bar(UIElement content) => new()
    {
        Child = content,
        Background = Ui.B(Ui.C("#202020"), 0.97),
        BorderBrush = Ui.White(0.14),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(3),
        Cursor = Cursors.Arrow,
        Visibility = Visibility.Collapsed,
    };

    private static Pressable Button(UIElement icon, string tip, Action click)
    {
        var p = new Pressable
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Child = icon, ToolTip = tip,
            Margin = new Thickness(1),
        };
        if (icon is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        p.Click += click;
        return p;
    }

    private static Shapes.Path PathIcon(string data, bool fill) => new()
    {
        Data = Geometry.Parse(data),
        Width = 16, Height = 16,
        Stroke = fill ? null : Ui.White(0.85),
        Fill = fill ? Ui.White(0.8) : null,
        StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    private void SelectTool(ShotTool tool)
    {
        if (_textBox is not null) CommitText();
        _tool = _tool == tool ? ShotTool.None : tool;
        foreach (var (t, b) in _toolButtons)
            b.SetRestBackground(t == _tool ? Ui.B(Ui.Blue, 0.55) : Brushes.Transparent);
        UpdateCursor();
    }

    private void PickColor()
    {
        var screen = _screens.FirstOrDefault(s => s.Controls.Children.Contains(_toolsBar)) ?? _screens[0];
        var c = ToColor(_settings.Screenshot.Color);
        using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) };
        _modal = true;
        try
        {
            if (dialog.ShowDialog(new Owner(screen.Handle)) != Forms.DialogResult.OK) return;
        }
        finally
        {
            _modal = false;
            Activate(screen);
        }
        _settings.Screenshot.Color = 0xFF000000 | (uint)(dialog.Color.R << 16 | dialog.Color.G << 8 | dialog.Color.B);
        _settings.Save();
        UpdateSwatch();
    }

    private sealed class Owner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }

    private void UpdateSwatch() => _swatch.Background = new SolidColorBrush(ToColor(_settings.Screenshot.Color));

    private void HideToolbars()
    {
        _toolsBar.Visibility = Visibility.Collapsed;
        _actionsBar.Visibility = Visibility.Collapsed;
    }

    /// Puts both toolbars on the monitor holding the selection's bottom-right corner.
    private void PlaceToolbars()
    {
        if (_sel is not { } sel || _closed) { HideToolbars(); return; }
        var screen = ScreenAt(sel.Right - 1, sel.Bottom - 1);
        var s = screen.Scale;
        foreach (var bar in new[] { _toolsBar, _actionsBar })
        {
            if (bar.Parent is Panel old && old != screen.Controls) old.Children.Remove(bar);
            if (bar.Parent is null) screen.Controls.Children.Add(bar);
            bar.Visibility = Visibility.Visible;
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        (int, int) Px(Border b) => ((int)Math.Ceiling(b.DesiredSize.Width * s), (int)Math.Ceiling(b.DesiredSize.Height * s));
        var (tools, actions) = ShotGeometry.PlaceToolbars(sel, screen.Bounds, Px(_toolsBar), Px(_actionsBar), (int)(6 * s));
        Canvas.SetLeft(_toolsBar, (tools.X - screen.Bounds.X) / s);
        Canvas.SetTop(_toolsBar, (tools.Y - screen.Bounds.Y) / s);
        Canvas.SetLeft(_actionsBar, (actions.X - screen.Bounds.X) / s);
        Canvas.SetTop(_actionsBar, (actions.Y - screen.Bounds.Y) / s);
    }

    // ---------------- Output ----------------

    private void Output(Func<BitmapSource, bool> action)
    {
        if (_textBox is not null) CommitText();
        if (_sel is not { } sel || _drag != Drag.None) return;
        var image = RenderSelection(sel);
        _modal = true;
        bool done;
        try { done = action(image); }
        finally { _modal = false; }
        if (done) Close();
        else if (!_closed) Activate(ScreenAt(_cursor.X, _cursor.Y));
    }

    private bool Copy(BitmapSource image)
    {
        Close();
        _service.Copy(image);
        return true;
    }

    private bool Save(BitmapSource image) =>
        _service.SaveWithDialog(image, _screens.FirstOrDefault(s => s.Controls.Children.Contains(_actionsBar))?.Window ?? _screens[0].Window);

    private bool Print(BitmapSource image)
    {
        // The print dialog isn't owned by the overlay, so step out of its way first.
        foreach (var s in _screens) s.Window.Topmost = false;
        foreach (var s in _screens) s.Window.Hide();
        var printed = _service.Print(image);
        if (printed) return true;
        foreach (var s in _screens) { s.Window.Show(); s.Window.Topmost = true; }
        return false;
    }

    private bool OpenInPaint(BitmapSource image)
    {
        Close();
        _service.OpenInPaint(image);
        return true;
    }

    /// The selection with its drawings, at full resolution.
    private BitmapSource RenderSelection(PxRect sel)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-sel.X, -sel.Y));
            dc.DrawImage(_frame.Image, ToRect(_frame.Bounds));
            foreach (var shape in _shapes.Items) DrawShape(dc, shape);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(sel.Width, sel.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ---------------- Drawing ----------------

    private void Render(DrawingContext dc, Screen screen)
    {
        var s = screen.Scale;
        var m = screen.Bounds;
        dc.PushTransform(new ScaleTransform(1 / s, 1 / s));
        dc.PushTransform(new TranslateTransform(-m.X, -m.Y));

        dc.DrawImage(_frame.Image, ToRect(_frame.Bounds));
        Geometry dim = new RectangleGeometry(ToRect(m));
        if (_sel is { } sel) dim = new CombinedGeometry(GeometryCombineMode.Exclude, dim, new RectangleGeometry(ToRect(sel)));
        dc.DrawGeometry(Dim, null, dim);

        if (_sel is { } area)
        {
            dc.PushClip(new RectangleGeometry(ToRect(area)));
            foreach (var shape in _shapes.Items) DrawShape(dc, shape);
            if (_current is not null) DrawShape(dc, _current);
            dc.Pop();
            DrawSelection(dc, area, s, m);
        }

        var selecting = _sel is null || _drag is Drag.Selecting || (_drag == Drag.Handle && _handle != ShotHandle.Inside);
        if (selecting && m.Contains(_cursor.X, _cursor.Y))
        {
            if (_sel is null) DrawCrosshair(dc, m, s);
            if (_settings.Screenshot.Magnifier) DrawMagnifier(dc, m, s);
        }
        if (_hint is not null && m.Contains(_cursor.X, _cursor.Y))
            DrawLabel(dc, _hint, _cursor.X + 16 * s, _cursor.Y + 16 * s, s);

        dc.Pop();
        dc.Pop();
    }

    private void DrawSelection(DrawingContext dc, PxRect sel, double s, PxRect m)
    {
        var rect = ToRect(sel);
        var line = Math.Max(1, Math.Round(s));
        dc.DrawRectangle(null, new Pen(Brushes.Black, line), rect);
        dc.DrawRectangle(null, new Pen(Brushes.White, line) { DashStyle = new DashStyle([4, 4], 0) }, rect);

        if (_drag != Drag.Drawing)
        {
            var size = Math.Round(7 * s);
            foreach (var (x, y) in new[]
            {
                (rect.Left, rect.Top), (rect.Left + rect.Width / 2, rect.Top), (rect.Right, rect.Top),
                (rect.Left, rect.Top + rect.Height / 2), (rect.Right, rect.Top + rect.Height / 2),
                (rect.Left, rect.Bottom), (rect.Left + rect.Width / 2, rect.Bottom), (rect.Right, rect.Bottom),
            })
            {
                if (!m.Contains(x - 1, y - 1) && !m.Contains(x, y)) continue;
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, line), new Rect(x - size / 2, y - size / 2, size, size));
            }
        }

        // W×H above the top-left corner, on the monitor that holds that corner.
        if (!m.Contains(sel.X, sel.Y)) return;
        var text = Label($"{sel.Width} × {sel.Height}", s);
        var (lw, lh) = ((int)(text.Width + 12 * s), (int)(text.Height + 6 * s));
        var (lx, ly) = ShotGeometry.PlaceSizeLabel(sel, m, (lw, lh), (int)(4 * s));
        dc.DrawRoundedRectangle(Ui.B(Colors.Black, 0.75), null, new Rect(lx, ly, lw, lh), 4 * s, 4 * s);
        dc.DrawText(text, new Point(lx + 6 * s, ly + 3 * s));
    }

    private void DrawCrosshair(DrawingContext dc, PxRect m, double s)
    {
        var pen = new Pen(Ui.White(0.35), Math.Max(1, Math.Round(s)));
        double x = _cursor.X + 0.5, y = _cursor.Y + 0.5;
        dc.DrawLine(pen, new Point(m.X, y), new Point(m.Right, y));
        dc.DrawLine(pen, new Point(x, m.Y), new Point(x, m.Bottom));
    }

    /// A zoomed view of the pixels around the pointer, with its position and colour, like a loupe.
    private void DrawMagnifier(DrawingContext dc, PxRect m, double s)
    {
        const int span = 15;
        var size = Math.Round(120 * s);
        var gap = 20 * s;
        double x = _cursor.X + gap, y = _cursor.Y + gap;
        var info = Label($"{_cursor.X - m.X}, {_cursor.Y - m.Y}   {_frame.HexAt(_cursor.X, _cursor.Y)}", s, 11);
        var total = size + info.Height + 8 * s;
        if (x + size > m.Right) x = _cursor.X - gap - size;
        if (y + total > m.Bottom) y = _cursor.Y - gap - total;

        var view = new Rect(_cursor.X - span / 2 - _frame.Bounds.X, _cursor.Y - span / 2 - _frame.Bounds.Y, span, span);
        var brush = new ImageBrush(_frame.Image) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = view, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        var box = new Rect(x, y, size, size);
        dc.DrawRectangle(Brushes.Black, null, new Rect(x - s, y - s, size + 2 * s, total + 2 * s));
        dc.DrawRectangle(brush, null, box);
        var cell = size / span;
        var center = new Rect(x + cell * (span / 2), y + cell * (span / 2), cell, cell);
        var thin = new Pen(Ui.B(Ui.Blue, 0.6), Math.Max(1, s));
        dc.DrawLine(thin, new Point(x, center.Top + cell / 2), new Point(center.Left, center.Top + cell / 2));
        dc.DrawLine(thin, new Point(center.Right, center.Top + cell / 2), new Point(x + size, center.Top + cell / 2));
        dc.DrawLine(thin, new Point(center.Left + cell / 2, y), new Point(center.Left + cell / 2, center.Top));
        dc.DrawLine(thin, new Point(center.Left + cell / 2, center.Bottom), new Point(center.Left + cell / 2, y + size));
        dc.DrawRectangle(null, new Pen(Brushes.White, Math.Max(1, s)), center);
        dc.DrawRectangle(null, new Pen(Ui.White(0.5), Math.Max(1, s)), box);
        dc.DrawText(info, new Point(x + 4 * s, y + size + 4 * s));
    }

    private static void DrawLabel(DrawingContext dc, string text, double x, double y, double s)
    {
        var t = Label(text, s);
        dc.DrawRoundedRectangle(Ui.B(Colors.Black, 0.75), null, new Rect(x, y, t.Width + 12 * s, t.Height + 6 * s), 4 * s, 4 * s);
        dc.DrawText(t, new Point(x + 6 * s, y + 3 * s));
    }

    private static FormattedText Label(string text, double s, double size = 12) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size * s, Brushes.White, 1.0);

    private void DrawShape(DrawingContext dc, ShotShape shape)
    {
        var brush = new SolidColorBrush(ToColor(shape.Color));
        var pen = new Pen(brush, shape.Width)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        var a = new Point(shape.Start.X, shape.Start.Y);
        var b = new Point(shape.End.X, shape.End.Y);
        switch (shape.Tool)
        {
            case ShotTool.Pen or ShotTool.Marker:
                if (shape.Tool == ShotTool.Marker) pen.StartLineCap = pen.EndLineCap = PenLineCap.Square;
                if (shape.Points.Count == 1 || shape.Points.All(p => p == shape.Start))
                {
                    dc.DrawEllipse(brush, null, a, shape.Width / 2, shape.Width / 2);
                    break;
                }
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(a, false, false);
                    g.PolyLineTo(shape.Points.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, true);
                }
                dc.DrawGeometry(null, pen, geo);
                break;
            case ShotTool.Line:
                dc.DrawLine(pen, a, b);
                break;
            case ShotTool.Arrow:
                DrawArrow(dc, brush, pen, a, b, shape.Width);
                break;
            case ShotTool.Rectangle:
                dc.DrawRectangle(null, new Pen(brush, shape.Width) { LineJoin = PenLineJoin.Miter }, ToRect(shape.Bounds));
                break;
            case ShotTool.Circle:
                var r = ToRect(shape.Bounds);
                dc.DrawEllipse(null, pen, new Point(r.Left + r.Width / 2, r.Top + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            case ShotTool.Text:
                var t = new FormattedText(shape.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextFace, Math.Max(1, shape.FontSize), brush, 1.0);
                dc.DrawText(t, new Point(a.X + 1, a.Y + 1));
                break;
            case ShotTool.Blur:
                if (Blurred(shape) is { } blur) dc.DrawImage(blur.Image, ToRect(blur.Rect));
                break;
        }
    }

    private static void DrawArrow(DrawingContext dc, Brush brush, Pen pen, Point a, Point b, double width)
    {
        var v = b - a;
        if (v.Length < 1) return;
        v.Normalize();
        var head = Math.Max(10, width * 4);
        var baseCenter = b - v * head;
        var n = new Vector(-v.Y, v.X) * head * 0.5;
        dc.DrawLine(pen, a, baseCenter + v * Math.Min(head * 0.3, (b - a).Length / 2));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(b, true, true);
            g.LineTo(baseCenter + n, true, true);
            g.LineTo(baseCenter - n, true, true);
        }
        dc.DrawGeometry(brush, new Pen(brush, 1) { LineJoin = PenLineJoin.Round }, tri);
    }

    private (PxRect Rect, BitmapSource Image)? Blurred(ShotShape shape)
    {
        var rect = shape.Bounds.Intersect(_frame.Bounds);
        if (rect.IsEmpty) return null;
        if (_blurCache.TryGetValue(shape, out var cached) && cached.Rect == rect) return cached;
        var region = rect with { X = rect.X - _frame.Bounds.X, Y = rect.Y - _frame.Bounds.Y };
        var px = ShotImage.Pixelate(_frame.Pixels, _frame.Stride, region, Math.Max(4, (int)(shape.Width * 3)));
        var img = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgr32, null, px, rect.Width * 4);
        img.Freeze();
        _blurCache[shape] = (rect, img);
        return (rect, img);
    }

    private static Rect ToRect(PxRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static bool TryParseRect(string text, out PxRect rect)
    {
        rect = default;
        var parts = text.Split(',');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _))) return false;
        var v = parts.Select(int.Parse).ToArray();
        rect = new PxRect(v[0], v[1], v[2], v[3]);
        return !rect.IsEmpty;
    }
}
