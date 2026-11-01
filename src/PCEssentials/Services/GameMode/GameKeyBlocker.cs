using System.Diagnostics;
using Microsoft.Win32;
using PCEssentials.Core;

namespace PCEssentials.Services.GameMode;

/// Game mode: swallows the Windows key while a game is in front. The foreground tracker works out
/// whether a game is active and caches it in a volatile flag; the shared keyboard hook only reads that flag.
public sealed class GameKeyBlocker : IDisposable
{
    private readonly AppSettings _settings;
    private readonly KeyboardHook _hook;
    private readonly ForegroundTracker _tracker = new();
    private readonly GameDetector _detector = new(new Win32ForegroundProbe());
    private readonly string _ownExe = Process.GetCurrentProcess().ProcessName + ".exe";
    private readonly Func<bool> _blockWin;
    private volatile bool _blocking;

    public GameKeyBlocker(AppSettings settings, KeyboardHook hook)
    {
        _settings = settings;
        _hook = hook;
        _blockWin = () => _blocking;
        _tracker.Changed += Evaluate;
        _settings.Changed += Apply;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Apply();
    }

    /// True while the Windows key is being swallowed.
    public bool IsBlocking => _blocking;
    public bool Paused { get; private set; }
    public GameDecision Current { get; private set; } = GameDecision.NotAGame;
    /// The last app other than PC Essentials that was in front, for "Add last active app" in Settings.
    public string? LastActiveExe { get; private set; }
    /// The last game seen running elevated while PC Essentials isn't. Its keys are hidden from our hook (UIPI).
    public string? ElevatedGame { get; private set; }

    /// Raised on the UI thread when the decision, the pause state or blocking changes.
    public event Action? StateChanged;
    /// Raised on the UI thread when blocking turns on or off.
    public event Action? BlockingChanged;

    /// Starts or stops tracking to match the settings. Safe to call repeatedly.
    public void Apply()
    {
        if (_settings.GameMode.Enabled)
        {
            if (!_tracker.Running)
            {
                _tracker.Start();
                _hook.BlockWin = _blockWin;
                Log.Info("game mode on");
            }
            Evaluate();
        }
        else if (_tracker.Running)
        {
            _tracker.Stop();
            _hook.BlockWin = null;
            Current = GameDecision.NotAGame;
            SetBlocking(false);
            Log.Info("game mode off");
            StateChanged?.Invoke();
        }
    }

    public void TogglePause()
    {
        Paused = !Paused;
        Log.Info($"game mode {(Paused ? "paused" : "resumed")}");
        Evaluate();
        StateChanged?.Invoke();
    }

    private void Evaluate()
    {
        if (!_settings.GameMode.Enabled) return;
        var hwnd = Native.GetForegroundWindow();
        // The foreground is briefly nothing while focus moves; keep the last answer until it settles.
        if (hwnd == IntPtr.Zero) return;
        var g = _settings.GameMode;
        GameDecision decision;
        try { decision = _detector.Evaluate(hwnd, new GameRules(g.Games, g.Excluded, g.AutoDetectFullscreen, HardwareMonitor.IsAdmin)); }
        catch (Exception ex) { Log.Error("game mode", ex); return; }

        if (decision.Exe is { } exe && !exe.Equals(_ownExe, StringComparison.OrdinalIgnoreCase)) LastActiveExe = exe;
        if (decision.ElevatedGame && ElevatedGame != decision.Exe)
        {
            ElevatedGame = decision.Exe;
            Log.Info($"game mode: {decision.Exe} runs as administrator; PC Essentials isn't, so it can't see that window's keys");
        }

        else if (decision.IsGame && !decision.ElevatedGame && ElevatedGame == decision.Exe) ElevatedGame = null;

        var changed = decision != Current;
        Current = decision;
        SetBlocking(decision.IsGame && !Paused);
        if (changed) StateChanged?.Invoke();
    }

    private void SetBlocking(bool on)
    {
        if (_blocking == on) return;
        _blocking = on;
        Log.Info(on ? $"game mode: blocking Win key ({Current.Exe}, {Current.Reason})" : "game mode: Win key unblocked");
        BlockingChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// Windows can drop low-level hooks across sleep, so install the shared hook again on resume.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _hook.Rearm();
            Evaluate();
        });
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _settings.Changed -= Apply;
        _tracker.Dispose();
        _hook.BlockWin = null;
        _blocking = false;
    }
}
