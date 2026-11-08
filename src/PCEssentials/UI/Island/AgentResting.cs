using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PCEssentials.Core;
using PCEssentials.Services.Agents;

namespace PCEssentials.UI.Island;

/// Claude at rest: the limit window it shows.
public sealed record RestingMeter(LimitWindow Window, bool IsWeekly)
{
    public double UsedFraction => Math.Clamp(Window.UsedPercent / 100, 0, 1);
    public double RemainingFraction => 1 - UsedFraction;
}

/// The resting island's Claude limits, ported from the Mac app's NotchAgentSupport (resting meters):
/// which window each account rests on, and what each layout shows in which order.
public static class AgentResting
{
    public static readonly Color ResetTeal = Ui.C("#40C8E0");

    /// Green while there is room, yellow from 80% used, red from 95% (restingStatus).
    public static int Status(double usedFraction) => usedFraction >= 0.95 ? 2 : usedFraction >= 0.8 ? 1 : 0;

    /// White while the window has room, then the resting yellow and red (notchAgentRestingInk).
    public static Color Ink(double usedFraction, double roomOpacity = 0.92) => Status(usedFraction) switch
    {
        2 => Ui.Red,
        1 => Ui.Yellow,
        _ => Color.FromArgb((byte)(255 * roomOpacity), 255, 255, 255),
    };

    private static IEnumerable<(LimitWindow Window, bool Weekly)> PlanWide(AgentLimits l)
    {
        if (l.Session is { } s) yield return (s, false);
        if (l.Weekly is { } w) yield return (w, true);
    }

    /// The window an account rests on: the kind "Limit to show" picks, or its most used one.
    /// Of two windows as spent, the one renewing later stops you longer.
    private static (LimitWindow Window, bool Weekly)? RestingWindow(AgentLimits? limits, string focus)
    {
        if (limits is null) return null;
        var all = PlanWide(limits).ToList();
        if (all.Count == 0) return null;
        if (focus == "session" && limits.Session is { } s) return (s, false);
        if (focus == "weekly" && limits.Weekly is { } w) return (w, true);
        return all.OrderByDescending(x => x.Window.UsedPercent).ThenByDescending(x => x.Window.ResetsAt ?? DateTime.MinValue).First();
    }

    /// Every signed-in account reporting a window, in the agents card's order (the main profile's
    /// plan-wide limits when no account is known yet).
    private static List<RestingMeter> Meters(AgentSummary s, IslandSettings cfg, IList<string> seatOrder)
    {
        var list = new List<RestingMeter>();
        foreach (var seat in ClaudeAccounts.Ordered(s.Seats.Where(x => x.SignedIn), x => x.Email, seatOrder))
            if (RestingWindow(new AgentLimits(null, null, seat.Session, seat.Weekly), cfg.AgentsLimitFocus) is { } pick)
                list.Add(new RestingMeter(pick.Window, pick.Weekly));
        if (list.Count == 0 && RestingWindow(s.Limits, cfg.AgentsLimitFocus) is { } main) list.Add(new RestingMeter(main.Window, main.Weekly));
        return list;
    }

    /// What a layout shows, in its order: Single's one meter (the limit closest to stopping you),
    /// the first two for One per side, and for Rotate the tightest first, then the rest in order.
    public static List<RestingMeter> Sequence(AgentSummary s, IslandSettings cfg, IList<string> seatOrder)
    {
        var meters = Meters(s, cfg, seatOrder);
        switch (cfg.AgentsRestingLayout)
        {
            case "split":
                return meters.Take(2).ToList();
            case "cycle":
                if (meters.Count > 1)
                {
                    var tightest = meters.Select((m, i) => (m, i)).OrderByDescending(x => x.m.Window.UsedPercent).ThenBy(x => x.i).First().i;
                    var first = meters[tightest];
                    meters.RemoveAt(tightest);
                    meters.Insert(0, first);
                }
                return meters;
            default:
                // Across agents: the chosen kind when anyone reports it, else each one's binding window.
                var chosen = meters.Where(m => cfg.AgentsLimitFocus == "mostUsed" || m.IsWeekly == (cfg.AgentsLimitFocus == "weekly")).ToList();
                var pool = chosen.Count > 0 ? chosen : meters;
                return pool.OrderByDescending(m => m.Window.UsedPercent).Take(1).ToList();
        }
    }

    /// Claude's mark and today's spend stand in while no account reports a limit, once Claude has been used here.
    private static bool Fallback(AgentSummary s) => s.Installed || s.LastActivity is not null;

    /// Rotate's place in its sequence; every 4 seconds.
    public static int CycleIndex(int count) =>
        count <= 1 ? 0 : (int)(DateTime.UtcNow.Ticks / TimeSpan.FromSeconds(4).Ticks % count);

    /// When a limit renews: 24-hour time within the day or the next twelve hours, the weekday and
    /// time within the week, the date beyond. The short form keeps only the weekday.
    public static (string Full, string Short)? Reset(LimitWindow w)
    {
        if (w.ResetsAt is not { } r) return null;
        var now = DateTime.Now;
        var mark = w.Estimated ? "≈" : "";
        if (r.Date == now.Date || r - now < TimeSpan.FromHours(12))
        {
            var clock = mark + r.ToString("HH:mm");
            return (clock, clock);
        }
        var full = r - now < TimeSpan.FromDays(7) ? r.ToString("ddd HH:mm") : r.ToString("d MMM");
        return (mark + full, mark + (r - now < TimeSpan.FromDays(6) ? r.ToString("ddd") : full));
    }

    /// Mac AgentFormat.spendLevel colours: green under $100, yellow to $500, red beyond.
    public static Color SpendTint(double usd) => usd < 100 ? Ui.C("#5FD068") : usd < 500 ? Ui.C("#F2C94C") : Ui.Red;

    // ---------- drawing ----------

    /// The capsule's resting row (NotchAgentRestingRow): a 16 pt ring with the agent's mark, then the
    /// number and the time it renews on one line. One per side puts two of these side by side.
    public static FrameworkElement? Row(AgentSummary s, IslandSettings cfg, IList<string> seatOrder, out string key)
    {
        if (cfg.AgentsRestingLayout == "all") return AllRow(s, cfg, seatOrder, out key);
        var used = cfg.AgentsLimitDisplay == "used";
        var meters = Sequence(s, cfg, seatOrder);
        if (meters.Count == 0)
        {
            key = "fallback";
            if (!Fallback(s)) return null;
            var cost = s.Today.Cost;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border { Width = 16, Child = Glyph(11) });
            var t = Ui.Text(Ui.Money(cost), 12, FontWeights.SemiBold, 1, SpendTint(cost));
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            t.Margin = new Thickness(5, 0, 0, 0);
            row.Children.Add(t);
            return row;
        }
        if (cfg.AgentsRestingLayout == "split" && meters.Count > 1)
        {
            key = "split:" + string.Join("|", meters.Select(m => $"{m.Window.UsedPercent:0}{m.Window.ResetsAt}"));
            var both = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var first = One(meters[0], used);
            var second = One(meters[1], used);
            second.Margin = new Thickness(10, 0, 0, 0);
            both.Children.Add(first);
            both.Children.Add(second);
            return both;
        }
        var index = cfg.AgentsRestingLayout == "cycle" ? CycleIndex(meters.Count) : 0;
        var m = meters[index];
        key = $"one:{m.Window.UsedPercent:0}{m.Window.ResetsAt}{used}";
        return One(m, used);
    }

    /// Every signed-in account side by side, in the agents card's order.
    /// An account shows its window closest to running out: a ring in Claude's colour filled to the
    /// used share (yellow from 80%, red from 95%), the percent used, and under it its name and when that
    /// window renews, so the Claude rings can be told apart.
    private static FrameworkElement? AllRow(AgentSummary s, IslandSettings cfg, IList<string> seatOrder, out string key)
    {
        var items = new List<(RestingMeter Meter, string? Name, string Tip)>();
        foreach (var seat in ClaudeAccounts.Ordered(s.Seats.Where(x => x.SignedIn), x => x.Email, seatOrder))
        {
            if (Tightest(seat.Session, seat.Weekly) is not { } pick) continue;
            items.Add((new RestingMeter(pick.Window, pick.Weekly), seat.Email?.Split('@')[0], Tip(seat.Email, seat.Session, seat.Weekly)));
        }
        if (items.Count == 0)
        {
            key = "none";
            return null;
        }
        key = "all:" + string.Join("|", items.Select(i => $"{i.Name}{i.Meter.Window.UsedPercent:0}{i.Meter.Window.ResetsAt:HHmm}{i.Meter.IsWeekly}"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) row.Children.Add(new Border { Width = 1, Height = 18, Background = Ui.White(0.16), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Account(items[i].Meter, items[i].Name, items[i].Tip));
        }
        return row;
    }

    private static (LimitWindow Window, bool Weekly)? Tightest(LimitWindow? session, LimitWindow? weekly)
    {
        if (session is null && weekly is null) return null;
        if (weekly is not null && (session is null || weekly.UsedPercent > session.UsedPercent)) return (weekly, true);
        return (session!, false);
    }

    private static string Tip(string? who, LimitWindow? session, LimitWindow? weekly)
    {
        string Line(LimitWindow w) => $"{w.Label}: {w.UsedPercent:0}% used" + (w.ResetsAt is { } r ? $", resets {(w.Estimated ? "≈" : "")}{r:ddd HH:mm}" : "");
        var lines = new List<string> { "Claude" + (who is { Length: > 0 } ? " · " + who : "") };
        if (session is not null) lines.Add(Line(session));
        if (weekly is not null) lines.Add(Line(weekly));
        return string.Join("\n", lines);
    }

    private static FrameworkElement Account(RestingMeter m, string? name, string tip)
    {
        var gauge = new RestingGauge(m, true, 20, Ui.Clay);
        var pct = new TextBlock { FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Ui.B(Ink(m.UsedFraction, 0.95)), LineHeight = 13, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
        pct.Inlines.Add(new Run($"{m.Window.UsedPercent:0}"));
        pct.Inlines.Add(new Run("%") { FontSize = 9 });
        if (m.IsWeekly) pct.Inlines.Add(new Run(" wk") { FontSize = 8.5, Foreground = Ui.White(0.5) });
        var sub = new TextBlock { FontFamily = Ui.Font, FontSize = 9, FontWeight = FontWeights.SemiBold, LineHeight = 10, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        Typography.SetNumeralAlignment(sub, FontNumeralAlignment.Tabular);
        if (name is not null) sub.Inlines.Add(new Run(name + " ") { Foreground = Ui.White(0.55) });
        if (Reset(m.Window) is { } reset) sub.Inlines.Add(new Run(m.IsWeekly ? reset.Short : reset.Full) { Foreground = Ui.B(ResetTeal) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        text.Children.Add(pct);
        if (sub.Inlines.Count > 0) text.Children.Add(sub);
        var item = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, Background = Brushes.Transparent };
        item.Children.Add(gauge);
        item.Children.Add(text);
        return item;
    }

    private static FrameworkElement One(RestingMeter m, bool used)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new RestingGauge(m, used, 16));
        var fraction = used ? m.UsedFraction : m.RemainingFraction;
        var ink = Ui.B(Ink(m.UsedFraction));
        var number = new TextBlock { FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        number.Inlines.Add(new Run($"{Math.Round(fraction * 100):0}") { FontSize = 12, Foreground = ink });
        number.Inlines.Add(new Run("%") { FontSize = 12 * 0.7, Foreground = ink });
        row.Children.Add(number);
        if (Reset(m.Window) is { } reset)
        {
            var time = Ui.Text(reset.Full, 9, FontWeights.SemiBold, 1, ResetTeal);
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            time.Margin = new Thickness(4, 1, 0, 0);
            row.Children.Add(time);
        }
        var label = used ? "used" : "left";
        row.ToolTip = $"Claude · {(m.IsWeekly ? "Weekly" : "Session")}: {Math.Round(fraction * 100):0}% {label}" +
                      (m.Window.ResetsAt is { } r ? $", resets {r:ddd HH:mm}" : "");
        return row;
    }

    /// Claude's mark without its tile, as drawn inside the ring.
    public static TextBlock Glyph(double size) => new()
    {
        Text = "✻",
        FontFamily = new FontFamily("Segoe UI Symbol"),
        FontWeight = FontWeights.Bold,
        FontSize = size * 1.05,
        Foreground = Ui.B(Ui.Clay),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// Claude at rest: its mark inside the ring of the limit it shows, filled as the person reads
/// limits (used or left). Port of NotchAgentRestingGauge.
public sealed class RestingGauge : Grid
{
    private readonly RestingMeter _meter;
    private readonly bool _used;
    private readonly double _ring;
    private readonly Color? _fill;

    /// fill: the ring's colour while the window has room (Claude's colour); null keeps the
    /// Mac's white. Yellow and red always win once the window runs low.
    public RestingGauge(RestingMeter meter, bool used, double ring, Color? fill = null)
    {
        _meter = meter;
        _used = used;
        _ring = ring;
        _fill = fill;
        Width = ring;
        Height = ring;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(AgentResting.Glyph(ring * (ring < 22 ? 0.6 : 0.48)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var line = Math.Max(1.6, _ring * 0.114);
        var radius = (_ring - line) / 2;
        var center = new Point(_ring / 2, _ring / 2);
        var value = _used ? _meter.UsedFraction : _meter.RemainingFraction;
        if (!_used && _meter.RemainingFraction <= 0)
        {
            // Nothing left to fill: a faint red track still reads as empty, not as missing.
            dc.DrawEllipse(null, new Pen(Ui.B(Ui.Red, 0.35), line), center, radius, radius);
            return;
        }
        dc.DrawEllipse(null, new Pen(Ui.White(0.24), line), center, radius, radius);
        if (value <= 0) return;
        var color = _fill is { } f && AgentResting.Status(_meter.UsedFraction) == 0 ? f : AgentResting.Ink(_meter.UsedFraction, 1);
        var pen = new Pen(Ui.B(color), line) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        // From 12 o'clock, clockwise.
        var angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, pen, new PathGeometry([fig]));
    }
}
