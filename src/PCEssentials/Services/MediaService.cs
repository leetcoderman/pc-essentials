using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCEssentials.Core;
using Windows.Media.Control;

namespace PCEssentials.Services;

public sealed record NowPlaying(
    string Title, string Artist, string AppId, bool IsPlaying,
    TimeSpan Position, TimeSpan Duration, DateTime PositionAt, BitmapImage? Artwork,
    bool CanSkipNext, bool CanSkipPrevious, bool? Shuffle)
{
    /// Playback position extrapolated to now while playing.
    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var p = Position + (DateTime.Now - PositionAt);
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }
}

/// Now playing from any app that reports to Windows' media controls (Spotify, browsers, Media Player...).
public sealed class MediaService
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _artKey;
    private BitmapImage? _art;

    public NowPlaying? Current { get; private set; }
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(Attach);
            _manager.SessionsChanged += (_, _) => _dispatcher.BeginInvoke(Attach);
            Attach();
        }
        catch (Exception ex) { Log.Error("media start", ex); }
    }

    private void Attach()
    {
        var next = PickSession();
        if (!ReferenceEquals(next, _session))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnChanged;
                _session.PlaybackInfoChanged -= OnChanged;
                _session.TimelinePropertiesChanged -= OnChanged;
            }
            _session = next;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnChanged;
                _session.PlaybackInfoChanged += OnChanged;
                _session.TimelinePropertiesChanged += OnChanged;
            }
        }
        _ = RefreshAsync();
    }

    /// Prefer a session that is actually playing over Windows' idea of the current one.
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        var sessions = _manager.GetSessions();
        return sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? _manager.GetCurrentSession()
            ?? sessions.FirstOrDefault();
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession s, object e) => _dispatcher.BeginInvoke(Attach);

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session is null)
        {
            Current = null;
            Changed?.Invoke();
            return;
        }
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var key = $"{session.SourceAppUserModelId}|{props.Title}|{props.Artist}";
            if (key != _artKey)
            {
                _artKey = key;
                _art = await LoadArtAsync(props.Thumbnail);
            }
            Current = new NowPlaying(
                props.Title ?? "", props.Artist ?? "", session.SourceAppUserModelId ?? "",
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.Position, timeline.EndTime - timeline.StartTime, timeline.LastUpdatedTime.LocalDateTime,
                _art, playback.Controls.IsNextEnabled, playback.Controls.IsPreviousEnabled, playback.IsShuffleActive);
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("media refresh", ex); }
    }

    private static async Task<BitmapImage?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        if (thumb is null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.DecodePixelWidth = 160;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public async void PlayPause() { if (_session is not null) await _session.TryTogglePlayPauseAsync(); }
    public async void Next() { if (_session is not null) await _session.TrySkipNextAsync(); }
    public async void Previous() { if (_session is not null) await _session.TrySkipPreviousAsync(); }
    public async void ToggleShuffle()
    {
        if (_session is null || Current?.Shuffle is not bool shuffle) return;
        await _session.TryChangeShuffleActiveAsync(!shuffle);
    }
    public async void Seek(TimeSpan position)
    {
        if (_session is not null) await _session.TryChangePlaybackPositionAsync(position.Ticks);
    }

    /// Friendly name for the app that is playing, from its AppUserModelID.
    public static string AppName(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        var name = appId;
        var bang = name.IndexOf('!');
        if (bang >= 0) name = name[..bang];
        name = Path.GetFileNameWithoutExtension(name);
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name switch
        {
            "chrome" => "Chrome", "msedge" => "Edge", "firefox" => "Firefox", "Spotify" or "SpotifyMusic" => "Spotify",
            "ZuneMusic" => "Media Player", _ => name,
        };
    }
}
