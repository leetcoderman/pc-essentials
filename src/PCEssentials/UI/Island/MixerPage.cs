using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using PCEssentials.Services;

namespace PCEssentials.UI.Island;

/// Volume mixer: output and microphone master faders, then one fader per app playing sound.
public sealed class MixerPage : IslandPage
{
    private readonly LevelSlider _outFader = new(Colors.White, true, 40);
    private readonly LevelSlider _inFader = new(Ui.Green, true, 40);
    private readonly TextBlock _outText = Ui.Text("", 11, FontWeights.SemiBold, 0.8, null, true);
    private readonly TextBlock _inText = Ui.Text("", 11, FontWeights.SemiBold, 0.8, null, true);
    private readonly TextBlock _outGlyph = Ui.Icon(Ui.GVolume, 13);
    private readonly TextBlock _inGlyph = Ui.Icon(Ui.GMic, 13);
    private readonly StackPanel _apps = new();
    private readonly TextBlock _deviceName = Ui.Text("", 12, FontWeights.SemiBold, 0.9);
    private readonly List<(AppAudio App, LevelSlider Slider, TextBlock Percent, TextBlock MuteGlyph)> _rows = new();
    private string _appKeys = "";
    private int _tick;

    public MixerPage(IslandContext ctx) : base(ctx)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Pressable device = null!;
        var deviceRow = new StackPanel { Orientation = Orientation.Horizontal };
        deviceRow.Children.Add(Ui.Icon(Ui.GSpeaker, 13, Ui.Purple));
        _deviceName.Margin = new Thickness(8, 0, 6, 0);
        _deviceName.MaxWidth = 380;
        deviceRow.Children.Add(_deviceName);
        deviceRow.Children.Add(Ui.Icon(Ui.GChevronDown, 9, Colors.White, 0.5));
        device = new Pressable { Height = 28, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(9), Child = deviceRow, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        device.Click += () => Menus.Show(device, ctx.Audio.Devices(DataFlow.Render)
            .Select(d => (d.Name, d.IsDefault, (Action)(() => { ctx.Audio.SetDefault(d.Id); Refresh(true); }))));
        Children.Add(device);

        var masters = new StackPanel { Orientation = Orientation.Horizontal };
        masters.Children.Add(Master("Output", _outFader, _outText, _outGlyph, () => ctx.Audio.OutputMuted = !ctx.Audio.OutputMuted));
        masters.Children.Add(Master("Mic", _inFader, _inText, _inGlyph, ctx.Audio.ToggleMic));
        _outFader.ValueChanged += v => ctx.Audio.OutputVolume = (float)v;
        _inFader.ValueChanged += v => ctx.Audio.InputVolume = (float)v;

        var divider = new Border { Width = 1, Background = Ui.White(0.12), Margin = new Thickness(6, 4, 14, 4) };
        var body = Ui.Columns((masters, Ui.Auto), (divider, Ui.Auto), (Ui.Scroll(_apps), Ui.Star));
        Grid.SetRow(body, 1);
        Children.Add(body);
    }

    public override string Id => "mixer";
    public override string Title => "Mixer";
    public override string Glyph => Ui.GMixer;
    public override Color Tint => Ui.Purple;

    private static FrameworkElement Master(string label, LevelSlider fader, TextBlock percent, TextBlock glyph, Action toggleMute)
    {
        var dock = new DockPanel { Width = 64, Margin = new Thickness(0, 0, 8, 0) };
        var name = Ui.Text(label, 10.5, FontWeights.SemiBold, 0.5);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        DockPanel.SetDock(name, Dock.Top);
        dock.Children.Add(name);
        var mute = new Pressable { Width = 28, Height = 24, CornerRadius = new CornerRadius(8), Child = glyph, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Mute" };
        mute.Click += toggleMute;
        DockPanel.SetDock(mute, Dock.Bottom);
        dock.Children.Add(mute);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        percent.Margin = new Thickness(0, 4, 0, 2);
        DockPanel.SetDock(percent, Dock.Bottom);
        dock.Children.Add(percent);
        fader.Margin = new Thickness(0, 6, 0, 0);
        fader.HorizontalAlignment = HorizontalAlignment.Center;
        dock.Children.Add(fader);
        return dock;
    }

    public override void OnShown()
    {
        Ctx.Audio.Refresh();
        Refresh(true);
    }

    public override void Tick()
    {
        // Masters every tick; the app list every ~2 seconds (apps come and go).
        UpdateMasters();
        if (++_tick % 25 == 0) Refresh(false);
        foreach (var r in _rows)
        {
            if (r.Slider.IsDragging) continue;
            r.Slider.SetValue(r.App.Volume);
            r.Percent.Text = $"{r.App.Volume * 100:0}%";
            r.MuteGlyph.Text = r.App.Muted ? Ui.GMute : Ui.GVolume;
            r.MuteGlyph.Foreground = Ui.B(r.App.Muted ? Ui.Red : Colors.White, r.App.Muted ? 1 : 0.55);
        }
    }

    private void UpdateMasters()
    {
        var a = Ctx.Audio;
        _deviceName.Text = a.OutputName;
        if (!_outFader.IsDragging) _outFader.SetValue(a.OutputVolume);
        if (!_inFader.IsDragging) _inFader.SetValue(a.InputVolume);
        _outText.Text = $"{a.OutputVolume * 100:0}%";
        _inText.Text = a.HasInput ? $"{a.InputVolume * 100:0}%" : "--";
        _outGlyph.Text = a.OutputMuted ? Ui.GMute : Ui.GVolume;
        _outGlyph.Foreground = Ui.B(a.OutputMuted ? Ui.Red : Colors.White, a.OutputMuted ? 1 : 0.7);
        _inGlyph.Text = a.InputMuted ? Ui.GMicOff : Ui.GMic;
        _inGlyph.Foreground = Ui.B(a.InputMuted ? Ui.Red : Ui.Green);
        _inFader.SetTint(a.InputMuted ? Ui.Red : Ui.Green);
    }

    private void Refresh(bool force)
    {
        UpdateMasters();
        if (_rows.Any(r => r.Slider.IsDragging)) return;
        var apps = Ctx.Audio.Apps();
        var keys = AudioSessionKeys.Signature(apps.Select(a => (a.Key, a.Sessions.Count, a.Active)));
        if (!force && keys == _appKeys) return;
        _appKeys = keys;
        _apps.Children.Clear();
        _rows.Clear();
        if (apps.Count == 0)
        {
            _apps.Children.Add(new Border { Height = 230, Child = Ui.Empty(Ui.GMixer, "No apps are playing sound") });
            return;
        }
        foreach (var app in apps)
        {
            var icon = app.Icon is null
                ? (FrameworkElement)Ui.Icon(app.Key == AudioSessionKeys.System ? Ui.GSystem : Ui.GVolume, 14, Colors.White, 0.6)
                : new Image { Source = app.Icon, Width = 20, Height = 20 };
            var slider = new LevelSlider(Ui.Purple, false, 24) { Margin = new Thickness(0, 0, 8, 0) };
            var percent = Ui.Text("", 11, FontWeights.SemiBold, 0.7, null, true);
            percent.TextAlignment = TextAlignment.Right;
            var muteGlyph = Ui.Icon(Ui.GVolume, 12);
            var mute = new Pressable { Width = 28, Height = 28, CornerRadius = new CornerRadius(9), Child = muteGlyph, ToolTip = "Mute" };
            var captured = app;
            mute.Click += () => AudioService.SetAppMute(captured, !captured.Muted);
            slider.ValueChanged += v => AudioService.SetAppVolume(captured, (float)v);
            var name = Ui.Text(app.Name, 12, FontWeights.Medium, app.Active ? 0.9 : 0.5);
            name.ToolTip = app.Devices.Count == 0 ? app.Name : app.Name + "\n" + string.Join(", ", app.Devices);
            var row = Ui.Columns(
                (icon, Ui.Px(24)), (Ui.Spacer(8), Ui.Auto), (name, Ui.Px(110)), (Ui.Spacer(8), Ui.Auto),
                (slider, Ui.Star), (percent, Ui.Px(38)), (Ui.Spacer(4), Ui.Auto), (mute, Ui.Auto));
            row.Height = 40;
            row.Margin = new Thickness(0, 0, 0, 4);
            _apps.Children.Add(row);
            _rows.Add((app, slider, percent, muteGlyph));
        }
        Tick();
    }
}
