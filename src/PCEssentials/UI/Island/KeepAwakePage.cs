using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCEssentials.Services;

namespace PCEssentials.UI.Island;

/// Keep awake: one big toggle, how long to stay awake, and whether the display stays on too.
public sealed class KeepAwakePage : IslandPage
{
    private readonly Pressable _tile = new() { Width = 150, Height = 150, CornerRadius = new CornerRadius(16) };
    private readonly TextBlock _glyph = Ui.Icon(Ui.GCoffee, 30);
    private readonly TextBlock _state = Ui.Text("", 13, FontWeights.SemiBold);
    private readonly TextBlock _detail = Ui.Text("", 10.5, null, 0.5);
    private readonly WrapPanel _presets = new();
    private readonly Pressable _display = new() { Height = 40, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _displayCheck = Ui.Icon(Ui.GCheck, 12);

    public KeepAwakePage(IslandContext ctx) : base(ctx)
    {
        var tileStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _glyph.Margin = new Thickness(0, 0, 0, 10);
        _state.HorizontalAlignment = HorizontalAlignment.Center;
        _detail.HorizontalAlignment = HorizontalAlignment.Center;
        tileStack.Children.Add(_glyph);
        tileStack.Children.Add(_state);
        tileStack.Children.Add(_detail);
        _tile.Child = tileStack;
        _tile.Click += ctx.KeepAwake.Toggle;
        _tile.VerticalAlignment = VerticalAlignment.Top;

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Caption("STAY AWAKE FOR"));
        right.Children.Add(_presets);
        right.Children.Add(Caption("OPTIONS"));
        var displayRow = Ui.Columns(
            (Ui.Icon(Ui.GSystem, 14, Colors.White, 0.7), Ui.Auto), (Ui.Spacer(10), Ui.Auto),
            (Ui.Text("Keep the display on too", 12.5, FontWeights.Medium), Ui.Star), (_displayCheck, Ui.Auto));
        _display.Child = displayRow;
        _display.Background = Ui.White(0.06);
        _display.Click += () => ctx.KeepAwake.SetKeepDisplayOn(!ctx.KeepAwake.KeepDisplayOn);
        right.Children.Add(_display);
        var note = Ui.Text("Right-click the keep awake icon in the bar to switch it on or off without opening this page.", 11, null, 0.45);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 12, 0, 0);
        right.Children.Add(note);

        Children.Add(Ui.Columns((_tile, Ui.Px(150)), (right, Ui.Star)));
        ctx.KeepAwake.Changed += Render;
        Render();
    }

    public override string Id => "keepAwake";
    public override string Title => "Keep Awake";
    public override string Glyph => Ui.GCoffee;
    public override Color Tint => Ctx.KeepAwake.IsActive ? Ui.AwakeOn : Ui.Amber;

    private static TextBlock Caption(string text)
    {
        var t = Ui.Text(text, 10, FontWeights.SemiBold, 0.45);
        t.Margin = new Thickness(2, 0, 0, 8);
        return t;
    }

    private void Render()
    {
        var k = Ctx.KeepAwake;
        var tint = k.IsActive ? Ui.AwakeOn : Ui.Amber;
        _tile.SetRestBackground(Ui.B(tint, 0.16));
        _glyph.Text = k.IsActive ? Ui.GEye : Ui.GCoffee;
        _glyph.Foreground = Ui.B(tint);
        _state.Text = k.IsActive ? "Staying awake" : "Off";
        _state.Foreground = Ui.B(tint);
        _detail.Text = !k.IsActive ? "Click to stay awake" : k.Until is { } u ? $"until {u:h:mm tt}" : "until you turn it off";
        _displayCheck.Visibility = k.KeepDisplayOn ? Visibility.Visible : Visibility.Hidden;

        _presets.Children.Clear();
        foreach (var (label, duration) in KeepAwake.Presets)
        {
            var b = Ui.PillButton(label, () => Ctx.KeepAwake.Start(duration));
            b.Margin = new Thickness(0, 0, 6, 8);
            _presets.Children.Add(b);
        }
        _presets.Margin = new Thickness(0, 0, 0, 8);
    }
}
