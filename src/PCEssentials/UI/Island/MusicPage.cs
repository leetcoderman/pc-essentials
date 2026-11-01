using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using PCEssentials.Services;

namespace PCEssentials.UI.Island;

/// Now playing: artwork, title, timeline, transport controls, output device and volume.
public sealed class MusicPage : IslandPage
{
    private readonly Grid _playing = new();
    private readonly FrameworkElement _idle;
    private readonly Border _cover = new() { Width = 84, Height = 84, CornerRadius = new CornerRadius(16), Background = Ui.White(0.06) };
    private readonly TextBlock _title = Ui.Text("", 15, FontWeights.SemiBold, 0.95);
    private readonly TextBlock _artist = Ui.Text("", 13, null, 0.62);
    private readonly TextBlock _app = Ui.Text("", 11, null, 0.4);
    private readonly EqBars _bars = new(4, Colors.White, 3, 18, 3);
    private readonly TextBlock _elapsed = Ui.Text("0:00", 11, FontWeights.Medium, 0.55, null, true);
    private readonly TextBlock _remaining = Ui.Text("0:00", 11, FontWeights.Medium, 0.55, null, true);
    private readonly LevelSlider _timeline = new(Colors.White, false, 6);
    private readonly TextBlock _playGlyph = Ui.Icon(Ui.GPlay, 20);
    private readonly LevelSlider _volume = new(Colors.White, false, 6);
    private readonly TextBlock _volumeText = Ui.Text("", 11, FontWeights.Medium, 0.55, null, true);
    private readonly Pressable _shuffle;

    public MusicPage(IslandContext ctx) : base(ctx)
    {
        _idle = Ui.Empty(Ui.GMusic, "Nothing playing", "Play something in Spotify, a browser or Media Player and it shows up here.");
        _bars.Opacity = 0.5;

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        info.Children.Add(_title);
        info.Children.Add(_artist);
        _app.Margin = new Thickness(0, 4, 0, 0);
        info.Children.Add(_app);
        var head = Ui.Columns((_cover, Ui.Px(84)), (info, Ui.Star), (_bars, Ui.Auto));

        _timeline.ValueChanged += v =>
        {
            if (Ctx.Media.Current is { } np && np.Duration > TimeSpan.Zero) Ctx.Media.Seek(np.Duration * v);
        };
        var timeline = Ui.Columns((_elapsed, Ui.Px(44)), (_timeline, Ui.Star), (_remaining, Ui.Px(44)));
        _remaining.TextAlignment = TextAlignment.Right;
        _remaining.HorizontalAlignment = HorizontalAlignment.Right;
        timeline.Margin = new Thickness(0, 18, 0, 10);

        _shuffle = Ui.IconButton(Ui.GShuffle, "Shuffle", ctx.Media.ToggleShuffle, null, 40, 15);
        var play = new Pressable { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Child = _playGlyph, Background = Ui.White(0.1) };
        play.Click += ctx.Media.PlayPause;
        Pressable output = null!;
        output = Ui.IconButton(Ui.GSpeaker, "Audio output", () => Menus.Show(output,
            ctx.Audio.Devices(DataFlow.Render).Select(d => (d.Name, d.IsDefault, (Action)(() => ctx.Audio.SetDefault(d.Id))))), null, 40, 15);
        var transport = new UniformGrid5(_shuffle,
            Ui.IconButton(Ui.GPrev, "Previous", ctx.Media.Previous, null, 44, 18), play,
            Ui.IconButton(Ui.GNext, "Next", ctx.Media.Next, null, 44, 18), output);

        _volume.ValueChanged += v => Ctx.Audio.OutputVolume = (float)v;
        var volume = Ui.Columns((Ui.Icon(Ui.GVolume, 13, Colors.White, 0.55), Ui.Px(28)), (_volume, Ui.Star), (_volumeText, Ui.Px(44)));
        _volumeText.TextAlignment = TextAlignment.Right;
        volume.Margin = new Thickness(0, 14, 0, 0);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(timeline);
        stack.Children.Add(transport);
        stack.Children.Add(volume);
        _playing.Children.Add(stack);
        Children.Add(_playing);
        Children.Add(_idle);
        ctx.Media.Changed += Render;
    }

    public override string Id => "music";
    public override string Title => "Now Playing";
    public override string Glyph => Ui.GMusic;
    public override Color Tint => Ui.Rose;

    public override void OnShown() => Render();

    public override void Tick()
    {
        var np = Ctx.Media.Current;
        _bars.Update(Ctx.Audio.OutputPeak, np?.IsPlaying == true);
        if (np is null) return;
        var pos = np.LivePosition;
        _elapsed.Text = Ui.Clock(pos);
        _remaining.Text = np.Duration > TimeSpan.Zero ? "-" + Ui.Clock(np.Duration - pos) : "";
        if (!_timeline.IsDragging && np.Duration > TimeSpan.Zero) _timeline.SetValue(pos / np.Duration);
        if (!_volume.IsDragging) _volume.SetValue(Ctx.Audio.OutputVolume);
        _volumeText.Text = $"{Ctx.Audio.OutputVolume * 100:0}%";
    }

    private void Render()
    {
        var np = Ctx.Media.Current;
        _playing.Visibility = np is null ? Visibility.Collapsed : Visibility.Visible;
        _idle.Visibility = np is null ? Visibility.Visible : Visibility.Collapsed;
        if (np is null) return;
        _title.Text = np.Title.Length > 0 ? np.Title : "Unknown title";
        _artist.Text = np.Artist;
        _app.Text = MediaService.AppName(np.AppId);
        _cover.Background = np.Artwork is null ? Ui.B(Ui.Rose, 0.25) : new ImageBrush(np.Artwork) { Stretch = Stretch.UniformToFill };
        _cover.Child = np.Artwork is null ? Ui.Icon(Ui.GMusic, 30, Ui.Rose) : null;
        _cover.RenderTransformOrigin = new Point(0.5, 0.5);
        _cover.RenderTransform = new ScaleTransform(np.IsPlaying ? 1 : 0.94, np.IsPlaying ? 1 : 0.94);
        _playGlyph.Text = np.IsPlaying ? Ui.GPause : Ui.GPlay;
        _shuffle.Opacity = np.Shuffle is null ? 0.3 : 1;
        ((TextBlock)_shuffle.Child).Foreground = Ui.B(np.Shuffle == true ? Ui.Rose : Colors.White, np.Shuffle == true ? 1 : 0.7);
        Tick();
    }

    /// Five equal slots for the transport row.
    private sealed class UniformGrid5 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid5(params UIElement[] items)
        {
            Columns = 5;
            foreach (var i in items)
            {
                if (i is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Center;
                Children.Add(i);
            }
        }
    }
}
