using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using PCEssentials.Core;

namespace PCEssentials.Services;

public sealed class AppAudio
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public required List<AudioSessionControl> Sessions { get; init; }
    /// Output devices the app's sessions are on.
    public List<string> Devices { get; init; } = new();
    // Read every tick; a session whose app just quit can throw, so each read is guarded.
    public float Volume => Sessions.Count == 0 ? 0 : Sessions.Max(s => Try(() => s.SimpleAudioVolume.Volume, 0f));
    public bool Muted => Sessions.All(s => Try(() => s.SimpleAudioVolume.Mute, false));
    public bool Active => Sessions.Any(s => Try(() => s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive, false));

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// Core Audio: master and microphone levels, per-app sessions and default device switching.
/// All calls are made on the UI thread.
public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new();
    private MMDevice? _output, _input;

    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// Master output level or mute changed (from any app, the volume keys, or this one). Raised on the UI thread.
    public event Action<float, bool>? OutputVolumeChanged;

    public AudioService() => Refresh();

    public void Refresh()
    {
        if (_output is not null) Safe(() => _output.AudioEndpointVolume.OnVolumeNotification -= OnVolume);
        try { _output = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { _output = null; }
        try { _input = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { _input = null; }
        if (_output is not null) Safe(() => _output.AudioEndpointVolume.OnVolumeNotification += OnVolume);
    }

    private void OnVolume(AudioVolumeNotificationData data) =>
        _dispatcher.BeginInvoke(() => OutputVolumeChanged?.Invoke(data.MasterVolume, data.Muted));

    public string OutputName => _output?.FriendlyName ?? "No output";
    public string InputName => _input?.FriendlyName ?? "No microphone";
    public bool HasInput => _input is not null;

    public float OutputVolume
    {
        get => Safe(() => _output?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0);
        set => Safe(() => { if (_output is not null) _output.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1); });
    }

    public bool OutputMuted
    {
        get => Safe(() => _output?.AudioEndpointVolume.Mute ?? false);
        set => Safe(() => { if (_output is not null) _output.AudioEndpointVolume.Mute = value; });
    }

    public float InputVolume
    {
        get => Safe(() => _input?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0);
        set => Safe(() => { if (_input is not null) _input.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1); });
    }

    public bool InputMuted
    {
        get => Safe(() => _input?.AudioEndpointVolume.Mute ?? false);
        set => Safe(() => { if (_input is not null) _input.AudioEndpointVolume.Mute = value; });
    }

    /// Mutes or unmutes every active microphone, not just the default one.
    public void ToggleMic()
    {
        var mute = !InputMuted;
        Safe(() =>
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                d.AudioEndpointVolume.Mute = mute;
        });
    }

    /// Peak level of what is playing right now, 0..1. Used for the live equalizer bars.
    public float OutputPeak => Safe(() => _output?.AudioMeterInformation.MasterPeakValue ?? 0);
    public float InputPeak => Safe(() => _input?.AudioMeterInformation.MasterPeakValue ?? 0);

    /// Every app with an audio session on any active output device, grouped by app, like the Windows volume mixer.
    public List<AppAudio> Apps()
    {
        var groups = new Dictionary<string, (string Name, ImageSource? Icon, List<AudioSessionControl> List, HashSet<string> Devices)>();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                SessionCollection sessions;
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    sessions = manager.Sessions;
                }
                catch (Exception ex) { Log.Error("audio sessions " + device.FriendlyName, ex); continue; }
                for (var i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                        var system = s.IsSystemSoundsSession;
                        var id = s.GetSessionIdentifier;
                        var pid = (int)s.GetProcessID;
                        if (!system && pid == 0) continue;
                        var path = system ? null : ProcessPath(pid);
                        var processName = path is null ? ProcessName(pid) : System.IO.Path.GetFileNameWithoutExtension(path);
                        var key = AudioSessionKeys.Key(system, id, processName);
                        if (key is null) continue;
                        if (!groups.TryGetValue(key, out var g))
                        {
                            var name = system ? "System sounds" : AudioSessionKeys.DisplayName(s.DisplayName, Description(path), processName, id);
                            groups[key] = g = (name, path is null ? null : Icon(path), new List<AudioSessionControl>(), new HashSet<string>());
                        }
                        g.List.Add(s);
                        g.Devices.Add(device.FriendlyName);
                    }
                    catch (Exception ex) { Log.Error("audio session", ex); }
                }
            }
        }
        catch (Exception ex) { Log.Error("audio apps", ex); }
        return groups
            .Select(kv => new AppAudio { Key = kv.Key, Name = kv.Value.Name, Icon = kv.Value.Icon, Sessions = kv.Value.List, Devices = kv.Value.Devices.ToList() })
            .OrderByDescending(a => a.Active).ThenBy(a => a.Key == AudioSessionKeys.System).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static void SetAppVolume(AppAudio app, float volume)
    {
        foreach (var s in app.Sessions) Safe(() => s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1));
    }

    public static void SetAppMute(AppAudio app, bool mute)
    {
        foreach (var s in app.Sessions) Safe(() => s.SimpleAudioVolume.Mute = mute);
    }

    public List<AudioDevice> Devices(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            var current = flow == DataFlow.Render ? _output?.ID : _input?.ID;
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == current));
        }
        catch (Exception ex) { Log.Error("audio devices", ex); }
        return list;
    }

    /// Makes a device the default for every role, like picking it in the Windows sound flyout.
    public void SetDefault(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                policy.SetDefaultEndpoint(deviceId, role);
            Refresh();
        }
        catch (Exception ex) { Log.Error("audio set default", ex); }
    }

    private readonly Dictionary<string, string?> _descriptionCache = new();

    private string? Description(string? path)
    {
        if (path is null) return null;
        if (_descriptionCache.TryGetValue(path, out var cached)) return cached;
        string? desc = null;
        try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
        return _descriptionCache[path] = desc;
    }

    /// Full exe path. Uses limited query rights, so it also works for protected and other users' processes.
    private static string? ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    private static string? ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return null; }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

    private ImageSource? Icon(string path)
    {
        if (_iconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
            }
        }
        catch { }
        return _iconCache[path] = img;
    }

    private static T Safe<T>(Func<T> f)
    {
        try { return f(); } catch { return default!; }
    }

    private static void Safe(Action f)
    {
        try { f(); } catch { }
    }

    public void Dispose() => _enumerator.Dispose();

    // Undocumented but long-stable interface Windows' own sound settings use to change the default device.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, bool def, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility(string id, bool visible);
    }
}
