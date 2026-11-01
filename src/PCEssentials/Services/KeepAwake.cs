using System.Windows.Threading;
using PCEssentials.Core;

namespace PCEssentials.Services;

/// Keeps the PC (and optionally the display) awake, indefinitely or for a set time.
/// SetThreadExecutionState is per thread, so every call happens on the UI thread.
public sealed class KeepAwake
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };

    public bool IsActive { get; private set; }
    public DateTime? Until { get; private set; }
    public bool KeepDisplayOn { get; private set; } = true;
    public event Action? Changed;

    public static readonly (string Label, TimeSpan? Duration)[] Presets =
    [
        ("15 min", TimeSpan.FromMinutes(15)), ("30 min", TimeSpan.FromMinutes(30)), ("1 hour", TimeSpan.FromHours(1)),
        ("2 hours", TimeSpan.FromHours(2)), ("4 hours", TimeSpan.FromHours(4)), ("Indefinitely", null),
    ];

    public KeepAwake()
    {
        _timer.Tick += (_, _) =>
        {
            if (Until is { } until && DateTime.Now >= until) Stop();
            else Changed?.Invoke();
        };
    }

    public void Start(TimeSpan? duration)
    {
        IsActive = true;
        Until = duration is { } d ? DateTime.Now + d : null;
        Apply();
        _timer.Start();
        Changed?.Invoke();
    }

    public void Stop()
    {
        IsActive = false;
        Until = null;
        _timer.Stop();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (IsActive) Stop();
        else Start(null);
    }

    public void SetKeepDisplayOn(bool value)
    {
        KeepDisplayOn = value;
        if (IsActive) Apply();
        Changed?.Invoke();
    }

    private void Apply()
    {
        var flags = Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED;
        if (KeepDisplayOn) flags |= Native.ES_DISPLAY_REQUIRED;
        Native.SetThreadExecutionState(flags);
    }

    /// "45m", "1h35" or "∞", the way the island's capsule shows it.
    public string RemainingShort
    {
        get
        {
            if (!IsActive) return "";
            if (Until is not { } until) return "∞";
            var left = until - DateTime.Now;
            if (left.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
            return $"{(int)left.TotalHours}h{left.Minutes:00}";
        }
    }
}
