using System.Net.Http;
using System.Windows.Media;
using Ical.Net;
using Ical.Net.DataTypes;
using PCEssentials.Core;

namespace PCEssentials.Services;

public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay, string? Location, Color Color);

/// Events from iCal feeds the user adds in Settings (e.g. Google Calendar's secret iCal address,
/// or an Outlook published calendar). Refreshed every 15 minutes.
public sealed class CalendarService : IDisposable
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0xFF, 0x9F, 0x0A),
        Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x37, 0x5F),
    ];

    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Timer _timer;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public List<CalendarEvent> Events { get; private set; } = [];
    public string? LastError { get; private set; }
    public event Action? Changed;

    public CalendarService(AppSettings settings)
    {
        _settings = settings;
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(15));
    }

    public async Task RefreshAsync()
    {
        var feeds = _settings.Island.CalendarFeeds.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        var events = new List<CalendarEvent>();
        string? error = null;
        var from = DateTime.Today.AddDays(-31);
        var to = DateTime.Today.AddDays(62);
        for (var i = 0; i < feeds.Count; i++)
        {
            try
            {
                var url = feeds[i].Trim();
                if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["webcal://".Length..];
                var text = await _http.GetStringAsync(url);
                var cal = Calendar.Load(text);
                if (cal is null) continue;
                var color = Palette[i % Palette.Length];
                foreach (var occ in cal.GetOccurrences(new CalDateTime(from)))
                {
                    var start = occ.Period.StartTime.AsUtc.ToLocalTime();
                    if (start > to) break;
                    if (occ.Source is not Ical.Net.CalendarComponents.CalendarEvent ev) continue;
                    var end = occ.Period.EffectiveEndTime?.AsUtc.ToLocalTime() ?? start.AddHours(1);
                    var allDay = !occ.Period.StartTime.HasTime;
                    if (allDay)
                    {
                        start = occ.Period.StartTime.Date.ToDateTime(TimeOnly.MinValue);
                        end = start.AddDays(Math.Max(1, (end.Date - start.Date).Days));
                    }
                    events.Add(new CalendarEvent(ev.Summary ?? "(No title)", start, end, allDay, ev.Location, color));
                }
            }
            catch (Exception ex)
            {
                error = $"Couldn't load calendar {i + 1}: {ex.Message}";
                Log.Error("calendar", ex);
            }
        }
        _ = _dispatcher.BeginInvoke(() =>
        {
            Events = events.OrderBy(e => e.Start).ToList();
            LastError = error;
            Changed?.Invoke();
        });
    }

    public IEnumerable<CalendarEvent> On(DateTime day) =>
        Events.Where(e => e.Start.Date <= day.Date && e.End > day.Date && (e.End.Date > day.Date || e.Start.Date == day.Date));

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
