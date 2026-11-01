using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace PCEssentials.UI.Island;

/// Microphone: a big mute toggle (every microphone at once), the input level and the input device.
public sealed class MicrophonePage : IslandPage
{
    private readonly Pressable _tile = new() { Width = 150, Height = 150, CornerRadius = new CornerRadius(16) };
    private readonly TextBlock _glyph = Ui.Icon(Ui.GMic, 30);
    private readonly TextBlock _state = Ui.Text("", 13, FontWeights.SemiBold);
    private readonly TextBlock _device = Ui.Text("", 10.5, null, 0.5);
    private readonly LevelSlider _level = new(Ui.Green, false, 28);
    private readonly TextBlock _levelText = Ui.Text("", 11.5, FontWeights.SemiBold, 0.7, null, true);
    private readonly LevelSlider _meter = new(Ui.Green, false, 6) { IsHitTestVisible = false };
    private readonly TextBlock _deviceButtonText = Ui.Text("", 12, FontWeights.Medium, 0.9);
    private double _peak;

    public MicrophonePage(IslandContext ctx) : base(ctx)
    {
        var tileStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _glyph.Margin = new Thickness(0, 0, 0, 10);
        _state.HorizontalAlignment = HorizontalAlignment.Center;
        _device.HorizontalAlignment = HorizontalAlignment.Center;
        _device.MaxWidth = 130;
        tileStack.Children.Add(_glyph);
        tileStack.Children.Add(_state);
        tileStack.Children.Add(_device);
        _tile.Child = tileStack;
        _tile.VerticalAlignment = VerticalAlignment.Top;
        _tile.Click += () => { ctx.Audio.ToggleMic(); Render(); };

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Caption("INPUT LEVEL"));
        _level.ValueChanged += v => ctx.Audio.InputVolume = (float)v;
        _levelText.TextAlignment = TextAlignment.Right;
        right.Children.Add(Ui.Columns((_level, Ui.Star), (_levelText, Ui.Px(46))));
        right.Children.Add(Caption("LIVE SIGNAL", 14));
        right.Children.Add(_meter);
        right.Children.Add(Caption("DEVICE", 14));
        Pressable deviceButton = null!;
        var deviceRow = Ui.Columns((Ui.Icon(Ui.GMic, 12, Colors.White, 0.6), Ui.Px(22)), (_deviceButtonText, Ui.Star), (Ui.Icon(Ui.GChevronDown, 9, Colors.White, 0.5), Ui.Auto));
        deviceButton = new Pressable { Height = 34, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 10, 0), Background = Ui.White(0.08), Child = deviceRow };
        deviceButton.Click += () => Menus.Show(deviceButton, ctx.Audio.Devices(DataFlow.Capture)
            .Select(d => (d.Name, d.IsDefault, (Action)(() => { ctx.Audio.SetDefault(d.Id); Render(); }))));
        right.Children.Add(deviceButton);
        var note = Ui.Text("Mute applies to every microphone. Right-click the mic icon in the bar to toggle it from anywhere in the island.", 11, null, 0.45);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 12, 0, 0);
        right.Children.Add(note);

        Children.Add(Ui.Columns((_tile, Ui.Px(150)), (right, Ui.Star)));
    }

    public override string Id => "microphone";
    public override string Title => "Microphone";
    public override string Glyph => Ui.GMic;
    public override Color Tint => Ctx.Audio.InputMuted ? Ui.Red : Ui.Green;

    private static TextBlock Caption(string text, double top = 0)
    {
        var t = Ui.Text(text, 10, FontWeights.SemiBold, 0.45);
        t.Margin = new Thickness(2, top, 0, 8);
        return t;
    }

    public override void OnShown()
    {
        Ctx.Audio.Refresh();
        Render();
    }

    public override void Tick()
    {
        var a = Ctx.Audio;
        if (!_level.IsDragging) _level.SetValue(a.InputVolume);
        _levelText.Text = a.HasInput ? $"{a.InputVolume * 100:0}%" : "--";
        _peak = Math.Max(a.InputMuted ? 0 : a.InputPeak, _peak * 0.8);
        _meter.SetValue(_peak);
    }

    private void Render()
    {
        var a = Ctx.Audio;
        var muted = a.InputMuted;
        var tint = muted ? Ui.Red : Ui.Green;
        _tile.SetRestBackground(Ui.B(tint, 0.16));
        _glyph.Text = muted ? Ui.GMicOff : Ui.GMic;
        _glyph.Foreground = Ui.B(tint);
        _state.Text = !a.HasInput ? "No microphone" : muted ? "Muted" : "Live";
        _state.Foreground = Ui.B(tint);
        _device.Text = a.InputName;
        _deviceButtonText.Text = a.InputName;
        _level.SetTint(tint);
        _meter.SetTint(tint);
        Tick();
    }
}
