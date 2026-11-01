using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCEssentials.Services;

namespace PCEssentials.UI.Island;

/// Clipboard history: search, then click an entry to paste it into the app you were using.
public sealed class ClipboardPage : IslandPage
{
    private readonly TextBox _search = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
        FontSize = 12.5, FontFamily = Ui.Font, VerticalContentAlignment = VerticalAlignment.Center, CaretBrush = Brushes.White,
        Padding = new Thickness(0), MinHeight = 0,
    };
    private readonly TextBlock _placeholder = Ui.Text("Search clipboard", 12.5, null, 0.35);
    private readonly StackPanel _list = new();

    public ClipboardPage(IslandContext ctx) : base(ctx)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var searchGrid = new Grid();
        searchGrid.Children.Add(_placeholder);
        searchGrid.Children.Add(_search);
        _placeholder.IsHitTestVisible = false;
        var searchBox = new Border
        {
            Background = Ui.White(0.07), CornerRadius = new CornerRadius(10), Height = 32, Padding = new Thickness(10, 0, 10, 0),
            Child = Ui.Columns((Ui.Icon(Ui.GSearch, 12, Colors.White, 0.5), Ui.Px(22)), (searchGrid, Ui.Star)),
        };
        _search.PreviewMouseDown += (_, _) => ctx.AllowKeyboard(true);
        _search.TextChanged += (_, _) => { _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Filtered().FirstOrDefault() is { } first) { Paste(first); e.Handled = true; }
        };
        var clear = Ui.IconButton(Ui.GDelete, "Clear history (keeps pinned)", ctx.Clipboard.ClearUnpinned, null, 32, 13);
        var top = Ui.Columns((searchBox, Ui.Star), (Ui.Spacer(6), Ui.Auto), (clear, Ui.Auto));
        top.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(top);
        var scroll = Ui.Scroll(_list);
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
        ctx.Clipboard.Changed += () => { if (IsVisible) Render(); };
    }

    public override string Id => "clipboard";
    public override string Title => "Clipboard";
    public override string Glyph => Ui.GClipboard;
    public override Color Tint => Ui.Tan;

    public override void OnShown() => Render();

    public override void OnHidden()
    {
        _search.Text = "";
        Ctx.AllowKeyboard(false);
    }

    private IEnumerable<ClipItem> Filtered()
    {
        var q = _search.Text.Trim();
        var items = Ctx.Clipboard.Items.OrderByDescending(i => i.Pinned);
        return q.Length == 0 ? items : items.Where(i => i.Preview.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (i.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private void Render()
    {
        _list.Children.Clear();
        var items = Filtered().Take(60).ToList();
        if (items.Count == 0)
        {
            _list.Children.Add(new Border
            {
                Height = 240,
                Child = _search.Text.Length > 0 ? Ui.Empty(Ui.GSearch, "No matches") : Ui.Empty(Ui.GClipboard, "Nothing copied yet", "Text, images and files you copy show up here. Click one to paste it."),
            });
            return;
        }
        var index = 0;
        foreach (var item in items) _list.Children.Add(Card(item, ++index));
    }

    private FrameworkElement Card(ClipItem item, int index)
    {
        FrameworkElement lead = item.Kind switch
        {
            ClipKind.Image when item.ImagePath is { } path && File.Exists(path) => Thumb(path),
            ClipKind.Files => Ui.Icon(Ui.GFolder, 15, Ui.Tan),
            _ => Ui.Icon(Ui.GText, 14, Colors.White, 0.5),
        };
        var leadBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = Ui.White(0.05), Child = lead, ClipToBounds = true };
        var preview = Ui.Text(item.Preview, 12.5, FontWeights.Medium, 0.92);
        preview.TextWrapping = TextWrapping.NoWrap;
        var meta = Ui.Text($"{(item.SourceApp is { } app ? app + " · " : "")}{Ui.Ago(item.Copied)}" + (index <= 9 ? $"  ·  #{index}" : ""), 10.5, null, 0.42);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        text.Children.Add(preview);
        text.Children.Add(meta);

        var pin = Ui.IconButton(item.Pinned ? Ui.GUnpin : Ui.GPin, item.Pinned ? "Unpin" : "Pin", () => Ctx.Clipboard.TogglePin(item), item.Pinned ? Ui.Tan : null, 26, 11);
        var del = Ui.IconButton(Ui.GClose, "Remove", () => Ctx.Clipboard.Delete(item), null, 26, 10);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = item.Pinned ? 1 : 0 };
        actions.Children.Add(pin);
        actions.Children.Add(del);

        var card = new Pressable
        {
            Height = 52, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 0, 6, 0), Margin = new Thickness(0, 0, 0, 6),
            Background = Ui.White(0.045), ToolTip = item.Kind == ClipKind.Text ? Truncate(item.Text, 600) : item.Preview,
            Child = Ui.Columns((leadBox, Ui.Auto), (text, Ui.Star), (actions, Ui.Auto)),
        };
        card.MouseEnter += (_, _) => actions.Opacity = 1;
        card.MouseLeave += (_, _) => actions.Opacity = item.Pinned ? 1 : 0;
        card.Click += () => Paste(item);
        return card;
    }

    private void Paste(ClipItem item)
    {
        Ctx.Collapse();
        // Give the app underneath a moment to regain focus before Ctrl+V lands.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) => { timer.Stop(); Ctx.Clipboard.Use(item, paste: true); };
        timer.Start();
    }

    private static FrameworkElement Thumb(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 72;
            img.UriSource = new Uri(path);
            img.EndInit();
            return new Image { Source = img, Stretch = Stretch.UniformToFill };
        }
        catch { return Ui.Icon(Ui.GImage, 14); }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
