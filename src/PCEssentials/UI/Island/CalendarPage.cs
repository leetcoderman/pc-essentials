using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PCEssentials.Services;

namespace PCEssentials.UI.Island;

/// Calendar: a month grid on the left, the agenda for the chosen day and the days after it on the right.
public sealed class CalendarPage : IslandPage
{
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;
    private readonly TextBlock _monthTitle = Ui.Text("", 13, FontWeights.SemiBold);
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private readonly StackPanel _agenda = new();

    public CalendarPage(IslandContext ctx) : base(ctx)
    {
        var left = new StackPanel { Width = 236 };
        var header = Ui.Columns(
            (_monthTitle, Ui.Star),
            (Ui.IconButton(Ui.GLeft, "Previous month", () => { _month = _month.AddMonths(-1); Render(); }, null, 26, 10), Ui.Auto),
            (Ui.IconButton("", "Today", () => { _month = new(DateTime.Today.Year, DateTime.Today.Month, 1); _selected = DateTime.Today; Render(); }, null, 26, 10), Ui.Auto),
            (Ui.IconButton(Ui.GRight, "Next month", () => { _month = _month.AddMonths(1); Render(); }, null, 26, 10), Ui.Auto));
        header.Margin = new Thickness(0, 0, 0, 6);
        left.Children.Add(header);
        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var d in new[] { "M", "T", "W", "T", "F", "S", "S" })
        {
            var t = Ui.Text(d, 10, FontWeights.SemiBold, 0.4);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            weekdays.Children.Add(t);
        }
        left.Children.Add(weekdays);
        _days.Height = 222;
        left.Children.Add(_days);

        var right = Ui.Card(Ui.Scroll(_agenda), 14, 16);
        right.Margin = new Thickness(16, 0, 0, 0);
        Children.Add(Ui.Columns((left, Ui.Auto), (right, Ui.Star)));
        ctx.Calendar.Changed += () => { if (IsVisible) Render(); };
    }

    public override string Id => "calendar";
    public override string Title => "Calendar";
    public override string Glyph => Ui.GCalendar;
    public override Color Tint => Ui.Red;

    public override void OnShown() => Render();

    private void Render()
    {
        _monthTitle.Text = _month.ToString("MMMM yyyy");
        _days.Children.Clear();
        var offset = ((int)_month.DayOfWeek + 6) % 7; // Monday first
        var start = _month.AddDays(-offset);
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var inMonth = day.Month == _month.Month;
            var isToday = day == DateTime.Today;
            var isSelected = day == _selected;
            var events = Ctx.Calendar.On(day).Take(3).ToList();
            var number = Ui.Text(day.Day.ToString(), 11.5, isToday ? FontWeights.Bold : FontWeights.Medium,
                inMonth ? 0.9 : 0.3, isToday && !isSelected ? Ui.Red : (isSelected ? Colors.Black : null));
            number.HorizontalAlignment = HorizontalAlignment.Center;
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 4 };
            foreach (var e in events)
                dots.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Ui.B(e.Color), Margin = new Thickness(1, 0, 1, 0) });
            var cellStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            cellStack.Children.Add(number);
            cellStack.Children.Add(dots);
            var cell = new Pressable
            {
                Margin = new Thickness(1), CornerRadius = new CornerRadius(9), Child = cellStack,
                Background = isSelected ? Ui.B(isToday ? Ui.Red : Colors.White, isToday ? 1 : 0.9) : Brushes.Transparent,
            };
            if (isSelected && isToday) number.Foreground = Brushes.White;
            var captured = day;
            cell.Click += () => { _selected = captured; if (captured.Month != _month.Month) _month = new(captured.Year, captured.Month, 1); Render(); };
            _days.Children.Add(cell);
        }
        RenderAgenda();
    }

    private void RenderAgenda()
    {
        _agenda.Children.Clear();
        var cal = Ctx.Calendar;
        if (Ctx.Settings.Island.CalendarFeeds.Count == 0)
        {
            _agenda.Children.Add(new Border
            {
                Height = 250,
                Child = Ui.Empty(Ui.GCalendar, "No calendars yet",
                    "Add an iCal link in Settings → Island → Calendar. In Google Calendar it's under Settings → your calendar → \"Secret address in iCal format\"."),
            });
            return;
        }
        if (cal.LastError is { } err)
        {
            var e = Ui.Text(err, 11, null, 0.8, Ui.Orange);
            e.TextWrapping = TextWrapping.Wrap;
            e.Margin = new Thickness(0, 0, 0, 8);
            _agenda.Children.Add(e);
        }
        var any = false;
        for (var i = 0; i < 7; i++)
        {
            var day = _selected.AddDays(i);
            var events = cal.On(day).ToList();
            if (events.Count == 0 && i > 0) continue;
            any |= events.Count > 0;
            var title = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(1) ? "Tomorrow" : day.ToString("dddd d MMM");
            var h = Ui.Text(title, 12, FontWeights.SemiBold, 0.9);
            h.Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 6);
            _agenda.Children.Add(h);
            if (events.Count == 0) _agenda.Children.Add(Ui.Text("No events", 11.5, null, 0.4));
            foreach (var ev in events)
            {
                var ongoing = ev.Start <= DateTime.Now && ev.End > DateTime.Now;
                var when = ev.AllDay ? "All day" : $"{ev.Start:h:mm tt}";
                var row = Ui.Columns(
                    (new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = Ui.B(ev.Color), Margin = new Thickness(0, 2, 8, 2) }, Ui.Auto),
                    (StackOf(Ui.Text(ev.Title, 12, FontWeights.Medium, 0.95),
                             Ui.Text(ev.Location is { Length: > 0 } loc ? $"{when} · {loc}" : when, 10.5, null, ongoing ? 1 : 0.5, ongoing ? Ui.Mint : null)), Ui.Star));
                row.Margin = new Thickness(0, 0, 0, 6);
                _agenda.Children.Add(row);
            }
        }
        if (!any && cal.Events.Count == 0 && cal.LastError is null)
            _agenda.Children.Add(Ui.Text("Loading or nothing scheduled.", 11.5, null, 0.4));
    }

    private static StackPanel StackOf(params UIElement[] items)
    {
        var s = new StackPanel();
        foreach (var i in items) s.Children.Add(i);
        return s;
    }
}
