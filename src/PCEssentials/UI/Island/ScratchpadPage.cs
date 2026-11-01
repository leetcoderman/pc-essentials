using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCEssentials.UI.Island;

/// Scratchpad: autosaved notes in tabs. This page takes keyboard focus while it is open.
public sealed class ScratchpadPage : IslandPage
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
        CaretBrush = Brushes.White, FontFamily = Ui.Font, FontSize = 13.5, Padding = new Thickness(4),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, SelectionBrush = Ui.B(Ui.Yellow, 0.5),
    };
    private readonly TextBlock _placeholder = Ui.Text("Start typing…", 13.5, null, 0.35);
    private readonly TextBlock _status = Ui.Text("", 10.5, null, 0.4);
    private bool _loading;

    public ScratchpadPage(IslandContext ctx) : base(ctx)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Ui.IconButton(Ui.GAdd, "New note", () => { ctx.Scratchpad.Add(); Load(); }, null, 28, 12));
        actions.Children.Add(Ui.IconButton(Ui.GCopy, "Copy all", () => { if (_editor.Text.Length > 0) Clipboard.SetText(_editor.Text); Flash("Copied"); }, null, 28, 12));
        actions.Children.Add(Ui.IconButton(Ui.GDelete, "Delete this note", () => { ctx.Scratchpad.RemoveCurrent(); Load(); }, null, 28, 12));
        var top = Ui.Columns((Ui.Scroll(_tabs), Ui.Star), (_status, Ui.Auto), (Ui.Spacer(8), Ui.Auto), (actions, Ui.Auto));
        top.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(top);

        var editorGrid = new Grid();
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _placeholder.Margin = new Thickness(6, 5, 0, 0);
        _placeholder.IsHitTestVisible = false;
        editorGrid.Children.Add(_editor);
        editorGrid.Children.Add(_placeholder);
        var surface = new Border { Background = Ui.White(0.06), CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Child = editorGrid };
        Grid.SetRow(surface, 1);
        Children.Add(surface);

        _editor.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_loading) return;
            ctx.Scratchpad.Edited(_editor.Text);
            _status.Text = $"{_editor.Text.Length:N0} chars · saved";
        };
        _editor.PreviewMouseDown += (_, _) => ctx.AllowKeyboard(true);
        Load();
    }

    public override string Id => "scratchpad";
    public override string Title => "Scratchpad";
    public override string Glyph => Ui.GNote;
    public override Color Tint => Ui.Yellow;
    public override bool WantsKeyboard => true;

    public override void OnShown()
    {
        Load();
        Dispatcher.BeginInvoke(() => { _editor.Focus(); _editor.CaretIndex = _editor.Text.Length; },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public override void OnHidden() => Ctx.Scratchpad.SaveNow();

    private void Load()
    {
        var pad = Ctx.Scratchpad;
        _loading = true;
        _editor.Text = pad.Current.Text;
        _loading = false;
        _tabs.Children.Clear();
        for (var i = 0; i < pad.Pages.Count; i++)
        {
            var selected = i == pad.Selected;
            var title = pad.Pages[i].Text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? pad.Pages[i].Title;
            if (title.Length > 18) title = title[..18] + "…";
            if (title.Length == 0) title = pad.Pages[i].Title;
            var tab = new Pressable
            {
                Height = 28, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 4, 0),
                Background = selected ? Ui.B(Ui.Yellow, 0.2) : Ui.White(0.04),
                Child = Ui.Text(title, 11.5, FontWeights.SemiBold, selected ? 1 : 0.6, selected ? Ui.Yellow : null),
            };
            var index = i;
            tab.Click += () => { pad.SaveNow(); pad.Selected = index; Load(); _editor.Focus(); };
            _tabs.Children.Add(tab);
        }
        _status.Text = "";
    }

    private void Flash(string text) => _status.Text = text;
}
