using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using PCEssentials.Core;
using PCEssentials.Services.Launcher;

namespace PCEssentials.UI.Launcher;

/// The Spotlight-style search box: opens centred near the top of the screen the pointer is on, searches as
/// you type and closes when it loses focus. Enter opens, Ctrl+Enter shows in folder, Ctrl+Shift+Enter runs
/// as administrator, Ctrl+C copies the path or result, Esc clears and then closes.
public sealed class LauncherWindow : Window
{
    private const double PanelWidth = 680, RowHeight = 50;
    /// Shared columns: rows sit RowMargin in from the panel and pad RowPadding more, so their icons start
    /// at EdgeInset like the search glyph; text begins at EdgeInset + IconSize + IconGap everywhere.
    private const double RowMargin = 8, EdgeInset = 20, RowPadding = EdgeInset - RowMargin, IconSize = 28, IconGap = 12, RightInset = 22;

    private readonly LauncherService _service;
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _divider;
    private readonly StackPanel _footer, _footerRight;
    private readonly Border _root;
    private List<LauncherResult> _results = [];
    private int _selected;
    private int _version;
    private CancellationTokenSource? _filesCts;
    private string? _confirmId;
    private IntPtr _hwnd, _previous;
    private readonly bool _acrylic = Environment.OSVersion.Version.Build >= 22621;

    public LauncherWindow(LauncherService service)
    {
        _service = service;
        Title = "PC Essentials Search";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        FontFamily = Ui.Font;
        Background = Brushes.Transparent;
        // A glass frame over the whole window lets DWM draw the acrylic backdrop behind the content.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });

        var searchIcon = Ui.Icon(Ui.GSearch, 17, Colors.White, 0.55);
        _box = new TextBox
        {
            FontSize = 20, FontFamily = Ui.Display, Foreground = Ui.White(0.95), CaretBrush = Ui.White(0.9),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
            SelectionBrush = Ui.B(Ui.Blue, 0.6), Padding = new Thickness(0),
        };
        // The app's Fluent theme gives text boxes a rounded field and a focus underline; Spotlight's box is
        // bare text, so the template is just the text host.
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        template.VisualTree = host;
        _box.Template = template;
        _box.FocusVisualStyle = null;
        _box.VerticalAlignment = VerticalAlignment.Center;

        _placeholder = Ui.Text("Search apps, files, windows and settings", 20, null, 0.32);
        _placeholder.FontFamily = Ui.Display;
        _placeholder.IsHitTestVisible = false;
        // The text host insets its text by 2 px; the placeholder sits exactly where typed text will.
        _placeholder.Margin = new Thickness(2, 0, 0, 0);
        var boxGrid = new Grid();
        boxGrid.Children.Add(_placeholder);
        boxGrid.Children.Add(_box);
        // Column layout shared with the result rows: icons centred in a 28 px column at 20 px from the edge,
        // text starting at 60 px, 22 px clear on the right.
        var iconCell = new Grid { Width = IconSize, Margin = new Thickness(EdgeInset, 0, IconGap, 0) };
        iconCell.Children.Add(searchIcon);
        var searchRow = Ui.Columns((iconCell, Ui.Auto), (boxGrid, Ui.Star), (Ui.Spacer(RightInset), Ui.Auto));
        searchRow.Height = 60;

        _divider = new Border { Height = 1, Background = Ui.White(0.08), Visibility = Visibility.Collapsed };
        _scroll = new ScrollViewer
        {
            Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = RowHeight * 9 + 12,
            Padding = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed, Focusable = false,
        };
        _footer = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _footerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var footerRow = Ui.Columns((_footer, Ui.Star), (_footerRight, Ui.Auto));
        footerRow.Margin = new Thickness(EdgeInset, 0, RightInset, 0);
        var footerBar = new Border
        {
            Height = 34, Child = footerRow, Background = Ui.White(0.03), BorderBrush = Ui.White(0.07), BorderThickness = new Thickness(0, 1, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(searchRow);
        stack.Children.Add(_divider);
        stack.Children.Add(_scroll);
        stack.Children.Add(footerBar);
        _root = new Border
        {
            Child = stack,
            // Over acrylic a light tint is enough; without it (Windows 10) a solid dark panel stands in.
            Background = _acrylic ? Ui.B(Ui.C("#1C1C1E"), 0.45) : Ui.B(Ui.C("#1C1C1E"), 1),
            // Windows 11 draws its own hairline border around the rounded window; a square one here would
            // poke out at the corners.
            BorderBrush = Ui.White(0.10), BorderThickness = new Thickness(_acrylic ? 0 : 1),
        };
        Content = _root;

        _box.TextChanged += (_, _) => { _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Search(); };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (IsVisible) Close(false); };
        SourceInitialized += (_, _) => SetUpWindow();
        _service.Apps.Updated += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Search(); });
        UpdateFooter();
    }

    // ---------------- Showing and hiding ----------------

    public void Toggle()
    {
        if (IsVisible) Close(true);
        else Open();
    }

    public void Open()
    {
        _previous = Native.GetForegroundWindow();
        _service.Apps.RefreshIfStale();
        _confirmId = null;
        _box.Text = "";
        Search();
        PlaceOnPointerMonitor();
        Show();
        WindowList.ForceForeground(_hwnd);
        Activate();
        _box.Focus();
        Keyboard.Focus(_box);
        _root.Opacity = 0;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
    }

    /// Hides the box. restoreFocus gives focus back to the window that had it (Esc or the hotkey again);
    /// after opening something that app takes focus by itself.
    private void Close(bool restoreFocus)
    {
        _filesCts?.Cancel();
        Hide();
        if (restoreFocus && _previous != IntPtr.Zero) Native.SetForegroundWindow(_previous);
        _previous = IntPtr.Zero;
    }

    private void SetUpWindow()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd, false);
        if (HwndSource.FromHwnd(_hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        int dark = 1, round = 2 /* DWMWCP_ROUND */, backdrop = _acrylic ? 3 /* DWMSBT_TRANSIENTWINDOW (acrylic) */ : 1;
        DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4);
        DwmSetWindowAttribute(_hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref backdrop, 4);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point p);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    /// Centred on the monitor under the pointer, a fifth of the way down, like Spotlight.
    private void PlaceOnPointerMonitor()
    {
        GetCursorPos(out var pt);
        var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var work = Native.MonitorInfoFor(mon).rcWork;
        var scale = GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var widthPx = (int)(PanelWidth * scale);
        var x = work.Left + (work.Width - widthPx) / 2;
        var y = work.Top + (int)(work.Height * 0.2);
        if (_hwnd == IntPtr.Zero)
        {
            // First show: WPF positions in its own units, measured on the primary monitor's DPI.
            var primary = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left = x / primary;
            Top = y / primary;
        }
        else Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    // ---------------- Searching ----------------

    private async void Search()
    {
        var version = ++_version;
        var text = _box.Text;
        _confirmId = null;
        _filesCts?.Cancel();
        _filesCts = null;

        var instant = text.TrimStart().StartsWith('<') ? _service.WindowsOnly(text, _hwnd) : _service.Query(text, _hwnd);
        Render(instant, keepSelection: false);
        if (!_service.WantsFiles(text)) return;

        var cts = _filesCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token);
            var files = await _service.FilesAsync(text, cts.Token);
            if (version != _version || files.Count == 0) return;
            // Files join the ranked results; web search stays last.
            var web = instant.Where(r => r.Kind == "Web").ToList();
            var merged = instant.Except(web).Concat(files).OrderByDescending(r => r.Score)
                .Take(_service.MaxResults).Concat(web).ToList();
            Render(merged, keepSelection: true);
        }
        catch (OperationCanceledException) { }
    }

    private void Render(List<LauncherResult> results, bool keepSelection)
    {
        var selectedId = keepSelection && _selected < _results.Count ? _results[_selected].Id : null;
        _results = results;
        _selected = 0;
        if (selectedId is not null)
        {
            var i = _results.FindIndex(r => r.Id == selectedId);
            if (i >= 0) _selected = i;
        }
        _rows.Children.Clear();
        for (int i = 0; i < _results.Count; i++) _rows.Children.Add(Row(_results[i], i));
        var any = _results.Count > 0;
        _scroll.Visibility = _divider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Highlight();
        UpdateFooter();
    }

    private Border Row(LauncherResult r, int index)
    {
        FrameworkElement icon;
        if (r.Icon is not null) icon = new Image { Source = r.Icon, Width = 28, Height = 28 };
        else if (r.IconSource is not null)
        {
            var img = new Image { Width = 28, Height = 28 };
            var glyph = Ui.Icon(string.IsNullOrEmpty(r.Glyph) ? "" : r.Glyph, 18, Colors.White, 0.6);
            var holder = new Grid { Width = 28, Height = 28 };
            holder.Children.Add(glyph);
            holder.Children.Add(img);
            var source = r.IconSource;
            // Icons load just after the row appears, so typing never waits for the shell.
            Dispatcher.BeginInvoke(() =>
            {
                var image = ShellIcons.Get(source, 32);
                if (image is null) return;
                img.Source = image;
                glyph.Visibility = Visibility.Collapsed;
            }, DispatcherPriority.Background);
            icon = holder;
        }
        else
        {
            icon = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Ui.White(0.08),
                Child = Ui.Icon(r.Glyph, 15, r.Kind == "Calculator" ? Ui.Orange : Colors.White, 0.85),
            };
        }
        icon.Margin = new Thickness(0, 0, IconGap, 0);
        icon.Width = icon.Height = IconSize;

        var title = Ui.Text(r.Title, r.Kind == "Calculator" ? 17 : 14, FontWeights.SemiBold, 0.95);
        var confirming = _confirmId == r.Id;
        var subtitle = Ui.Text(confirming ? "Press Enter again to confirm · Esc to cancel" : r.Subtitle, 11.5, null, confirming ? 1 : 0.5,
            confirming ? Ui.Orange : null);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        if (subtitle.Text.Length > 0) texts.Children.Add(subtitle);
        var kind = Ui.Text(r.Kind, 11, null, 0.4);
        kind.Margin = new Thickness(12, 0, 0, 0);

        var row = new Border
        {
            Height = RowHeight, CornerRadius = new CornerRadius(8), Margin = new Thickness(RowMargin, 0, RowMargin, 0),
            Padding = new Thickness(RowPadding, 0, RightInset - RowMargin, 0),
            Child = Ui.Columns((icon, Ui.Auto), (texts, Ui.Star), (kind, Ui.Auto)), Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        row.MouseMove += (_, _) => { if (_selected != index) { _selected = index; Highlight(); UpdateFooter(); } };
        row.MouseLeftButtonUp += (_, _) => { _selected = index; Choose(ModeFromKeyboard()); };
        return row;
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Children.Count; i++)
            ((Border)_rows.Children[i]).Background = i == _selected ? Ui.White(0.11) : Brushes.Transparent;
        if (_selected < _rows.Children.Count) ((FrameworkElement)_rows.Children[_selected]).BringIntoView();
    }

    private void UpdateFooter()
    {
        var r = _selected < _results.Count ? _results[_selected] : null;
        _footer.Children.Clear();
        _footerRight.Children.Clear();
        if (r is null)
        {
            Hint(_footer, "?", "Files");
            Hint(_footer, "<", "Windows");
            Hint(_footer, "=", "Calculator");
        }
        else
        {
            Hint(_footer, "Enter", r.Kind == "Calculator" ? "Copy" : r.Kind == "Window" ? "Switch" : "Open");
            if (r.CanReveal) Hint(_footer, "Ctrl Enter", "Show in folder");
            if (r.CanRunAsAdmin) Hint(_footer, "Ctrl Shift Enter", "Run as admin");
            if (r.CopyText is not null && r.Kind != "Calculator") Hint(_footer, "Ctrl C", "Copy");
        }
        Hint(_footerRight, "Esc", "Close");
    }

    /// A key-cap chip followed by what the key does, e.g. [Ctrl] [Enter] Show in folder.
    private static void Hint(StackPanel panel, string keys, string label)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(panel.Children.Count == 0 ? 0 : 16, 0, 0, 0) };
        foreach (var key in keys.Split(' '))
        {
            var cap = Ui.Text(key, 10.5, FontWeights.SemiBold, 0.7);
            item.Children.Add(new Border
            {
                Child = cap, Background = Ui.White(0.09), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 3, 0), MinWidth = 18, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var text = Ui.Text(label, 11, null, 0.5);
        text.Margin = new Thickness(3, 0, 0, 0);
        item.Children.Add(text);
        panel.Children.Add(item);
    }

    // ---------------- Keys ----------------

    private static LaunchMode ModeFromKeyboard()
    {
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Shift)) return LaunchMode.Admin;
        if (m.HasFlag(ModifierKeys.Control)) return LaunchMode.Reveal;
        return LaunchMode.Open;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Tab when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                Move(1); e.Handled = true; break;
            case Key.Up:
            case Key.Tab:
                Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(5); e.Handled = true; break;
            case Key.PageUp: Move(-5); e.Handled = true; break;
            case Key.Enter:
                Choose(ModeFromKeyboard()); e.Handled = true; break;
            case Key.Escape:
                if (_confirmId is not null) { _confirmId = null; Render(_results, true); }
                else if (_box.Text.Length > 0) _box.Text = "";
                else Close(true);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && _box.SelectionLength == 0:
                if (_selected < _results.Count && _results[_selected].CopyText is { } copy)
                {
                    try { Clipboard.SetText(copy); } catch (COMException) { }
                    Close(true);
                    e.Handled = true;
                }
                break;
        }
    }

    private void Move(int delta)
    {
        if (_results.Count == 0) return;
        _confirmId = null;
        _selected = ((_selected + delta) % _results.Count + _results.Count) % _results.Count;
        Highlight();
        UpdateFooter();
    }

    private void Choose(LaunchMode mode)
    {
        if (_selected >= _results.Count) return;
        var r = _results[_selected];
        if (mode == LaunchMode.Reveal && !r.CanReveal) mode = LaunchMode.Open;
        if (mode == LaunchMode.Admin && !r.CanRunAsAdmin) mode = LaunchMode.Open;
        if (r.NeedsConfirm && _confirmId != r.Id)
        {
            _confirmId = r.Id;
            var keep = _selected;
            Render(_results, true);
            _selected = keep;
            Highlight();
            return;
        }
        Close(false);
        _service.Record(r);
        try { r.Run(mode); }
        catch (Exception ex) { Log.Error("launcher run", ex); }
    }
}
