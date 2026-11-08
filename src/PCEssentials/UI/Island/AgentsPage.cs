using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using PCEssentials.Services.Agents;

namespace PCEssentials.UI.Island;

/// Claude, laid out like the Mac island's page: a card with a column per account (session and weekly
/// limits with their pace, this session's and this month's API value) and the sessions running in
/// the Claude app and in the terminal; Trend and Spend side by side as squares; then a year of
/// activity, one cell per day, like a GitHub contribution graph.
public sealed class AgentsPage : IslandPage
{
    private readonly StackPanel _content = new();
    private readonly List<(TextBlock Text, DateTime Since)> _clocks = new();

    public AgentsPage(IslandContext ctx) : base(ctx)
    {
        Children.Add(Ui.Scroll(_content));
        ctx.Agents.Updated += s => Dispatcher.BeginInvoke(() => { if (IsVisible) Render(s); });
    }

    public override string Id => "agents";
    public override string Title => "Agents";
    public override string Glyph => Ui.GRobot;
    public override Color Tint => Ui.Clay;

    /// Claude's asterisk on a tinted tile.
    public static FrameworkElement Mark(double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Background = Ui.B(Ui.Clay, 0.18),
        Child = AgentResting.Glyph(size * 0.62), Margin = new Thickness(0, 0, 2, 0),
    };

    public override void OnShown()
    {
        Ctx.Agents.Seats.RefreshSoon();
        Render(Ctx.Agents.Summary);
    }

    public override void Tick()
    {
        foreach (var (text, since) in _clocks) text.Text = Ui.Clock(DateTime.Now - since);
    }

    private void Render(AgentSummary s)
    {
        _content.Children.Clear();
        _clocks.Clear();
        if (!s.Installed && !s.Seats.Any(x => x.SignedIn) && s.LastActivity is null)
        {
            _content.Children.Add(new Border { Height = 280, Child = Ui.Empty(Ui.GRobot, "No Claude activity yet", "Claude Code and Claude app usage shows up here once you use them.") });
            return;
        }
        _content.Children.Add(ClaudeCard(s));
        _content.Children.Add(Squares(TrendCard(s), SpendCard(s)));
        _content.Children.Add(ActivityCard(s));
    }

    // ---------- shared pieces ----------

    private static Border Card(UIElement child, double padding = 16) => new()
    {
        Background = Ui.White(0.09),
        CornerRadius = new CornerRadius(22),
        Padding = new Thickness(padding),
        Margin = new Thickness(0, 0, 0, 10),
        Child = child,
    };

    /// Two cards side by side, each as tall as it is wide.
    private static Grid Squares(FrameworkElement left, FrameworkElement right)
    {
        left.Margin = new Thickness(0);
        right.Margin = new Thickness(0);
        const double gap = 10;
        var g = Ui.Columns((left, Ui.Star), (Ui.Spacer(gap), Ui.Px(gap)), (right, Ui.Star));
        g.Margin = new Thickness(0, 0, 0, 10);
        g.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            g.Height = Math.Max(0, (e.NewSize.Width - gap) / 2);
        };
        return g;
    }

    private static Border Chip(string text, Color tint) => new()
    {
        Background = Ui.B(tint, 0.22), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Ui.Text(text, 11.5, FontWeights.SemiBold, 1, tint),
    };

    private static TextBlock Money(double usd, double size = 14.5)
    {
        var t = Ui.Text(Ui.Money(usd), size, FontWeights.Bold, 1, AgentResting.SpendTint(usd));
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    private static string Whole(double usd) => usd >= 1000 ? $"${usd:N0}" : Ui.Money(usd);

    /// "Session ↻ 23:00 ........ 15%" over a bar with a white tick at how far through the window we are.
    private static FrameworkElement LimitRow(LimitWindow? w, TimeSpan length, double opacity, string label)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text(w?.Label ?? label, 12.5, FontWeights.SemiBold, opacity));
        if (w is not null && AgentResting.Reset(w) is { } reset)
        {
            var r = Ui.Text("↻ " + reset.Full, 11, FontWeights.SemiBold, 1, AgentResting.ResetTeal);
            r.Margin = new Thickness(7, 1, 0, 0);
            head.Children.Add(r);
        }
        var used = w?.UsedPercent ?? 0;
        var pct = Ui.Text($"{used:0}%", 14, FontWeights.Bold, 0.95, used >= 95 ? Ui.Red : used >= 80 ? Ui.Yellow : null);
        Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
        stack.Children.Add(Ui.Columns((head, Ui.Star), (pct, Ui.Auto)));

        var bar = new Grid { Height = 12, Margin = new Thickness(0, 5, 0, 0) };
        var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        track.Children.Add(new Border { Background = Ui.White(0.14), CornerRadius = new CornerRadius(4) });
        var fill = new Border { Background = Ui.B(used >= 95 ? Ui.Red : used >= 80 ? Ui.Yellow : Ui.Clay), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill);
        bar.Children.Add(track);
        // The pace marker: where usage would be if it were spread evenly over the window.
        double? pace = w?.ResetsAt is { } end ? Math.Clamp(1 - (end - DateTime.Now) / length, 0, 1) : null;
        var tick = new Rectangle { Width = 2, Height = 12, Fill = Brushes.White, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, Visibility = pace is null ? Visibility.Collapsed : Visibility.Visible };
        bar.Children.Add(tick);
        bar.SizeChanged += (_, _) =>
        {
            fill.Width = Math.Clamp(used / 100, 0, 1) * bar.ActualWidth;
            if (pace is double p) tick.Margin = new Thickness(Math.Max(0, p * bar.ActualWidth - 1), 0, 0, 0);
        };
        stack.Children.Add(bar);
        return stack;
    }

    private static FrameworkElement MoneyLine(string label, double usd, double opacity) =>
        Ui.Columns((Ui.Text(label, 13, FontWeights.SemiBold, opacity), Ui.Star), (Money(usd), Ui.Auto));

    private static FrameworkElement Header(FrameworkElement icon, string text, UIElement? right = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        icon.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(icon);
        var t = Ui.Text(text, 15, FontWeights.SemiBold, 0.95);
        t.Margin = new Thickness(9, 0, 0, 0);
        head.Children.Add(t);
        var g = Ui.Columns((head, Ui.Star), (right ?? Ui.Spacer(), Ui.Auto));
        g.Margin = new Thickness(0, 0, 0, 10);
        return g;
    }

    /// "$" in a thin ring, as the Mac page marks spending.
    private static FrameworkElement DollarMark()
    {
        var dollar = Ui.Text("$", 10, FontWeights.Bold, 0.8);
        dollar.HorizontalAlignment = HorizontalAlignment.Center;
        dollar.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = 17, Height = 17, CornerRadius = new CornerRadius(8.5), BorderBrush = Ui.White(0.6), BorderThickness = new Thickness(1.3), Child = dollar };
    }

    // ---------- Claude seats ----------

    private FrameworkElement ClaudeCard(AgentSummary s)
    {
        var stack = new StackPanel();
        var seats = s.Seats;
        var plan = seats.FirstOrDefault(x => x.IsDefault)?.PlanName ?? seats.FirstOrDefault(x => x.PlanName is not null)?.PlanName;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(AgentResting.Glyph(20));
        if (seats.Count <= 1)
        {
            var title = Ui.Text("Claude", 15, FontWeights.SemiBold, 0.95);
            title.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(title);
        }
        if (plan is not null)
        {
            var chip = Chip(plan, Ui.Clay);
            chip.Margin = new Thickness(10, 0, 0, 0);
            head.Children.Add(chip);
        }
        var add = Ui.PillButton("+ Add Team seat", () => { Ctx.Agents.Seats.AddSeat(); }, false);
        add.Height = 24;
        add.Padding = new Thickness(10, 0, 10, 0);
        stack.Children.Add(Ui.Columns((head, Ui.Star), (add, Ui.Auto)));

        // One column per account, divided by hairlines, in the order the person set.
        var shown = ClaudeAccounts.Ordered(seats, x => x.Email, Ctx.Settings.Agents.SeatOrder).Take(4).ToList();
        if (shown.Count > 0)
        {
            var grid = new Grid();
            for (var i = 0; i < shown.Count; i++)
            {
                if (i > 0)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var line = new Border { Width = 1, Background = Ui.White(0.12), Margin = new Thickness(12, 2, 12, 2) };
                    Grid.SetColumn(line, grid.ColumnDefinitions.Count - 1);
                    grid.Children.Add(line);
                }
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var col = SeatColumn(shown[i], shown.Count);
                if (shown[i].Email is { } email && shown.Count > 1) AddMoveMenu(col, email, i, shown);
                Grid.SetColumn(col, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(col);
            }
            stack.Children.Add(grid);
        }

        stack.Children.Add(new Border { Height = 1, Background = Ui.White(0.12), Margin = new Thickness(0, 14, 0, 12) });
        stack.Children.Add(Ui.Columns(
            (Place("IN CLAUDE APP", AgentResting.Glyph(11), s.Live.Where(l => l.Place == "app").ToList(), s), Ui.Star),
            (new Border { Width = 1, Background = Ui.White(0.12), Margin = new Thickness(12, 0, 12, 0) }, Ui.Auto),
            (Place("IN TERMINAL", Ui.Text(">_", 11, FontWeights.Bold, 0.6, null, true), s.Live.Where(l => l.Place == "terminal").ToList(), s), Ui.Star)));
        return Card(stack, 18);
    }

    /// Right-click a seat to move it a place left or right; the order is saved by email.
    private void AddMoveMenu(FrameworkElement col, string email, int index, List<SeatSummary> shown)
    {
        // Hit-testable across the whole column, not just its text.
        if (col is Panel { Background: null } panel) panel.Background = Brushes.Transparent;
        col.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var emails = shown.Select(x => x.Email).OfType<string>().ToList();
            void Move(int by)
            {
                Ctx.Settings.Agents.SeatOrder = ClaudeAccounts.Move(emails, email, by);
                Ctx.Settings.Save();
                Render(Ctx.Agents.Summary);
            }
            var items = new List<(string, bool, Action)>();
            if (index > 0) items.Add(("Move left", false, () => Move(-1)));
            if (index < shown.Count - 1) items.Add(("Move right", false, () => Move(1)));
            // Drawn in the page: a ContextMenu's popup falls behind the always-on-top island.
            Menus.Show(col, items);
        };
    }

    private FrameworkElement SeatColumn(SeatSummary seat, int count)
    {
        var stack = new StackPanel();
        var active = seat.IsDefault;
        var opacity = active ? 0.95 : 0.6;
        var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (active) who.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Ui.B(Ui.Clay), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "This PC's main Claude account" });
        var email = Ui.Text(seat.Email ?? seat.Id, count >= 3 ? 12.5 : 13.5, FontWeights.SemiBold, opacity);
        email.ToolTip = (seat.Email ?? "Not signed in") + (seat.OrgName is { } org ? $" · {org}" : "") + $"\n{seat.ConfigDir}";
        who.Children.Add(email);
        stack.Children.Add(who);

        if (!seat.SignedIn)
        {
            stack.Children.Add(Ui.Text("Not signed in yet", 12.5, null, 0.55));
            var sign = Ui.PillButton("Sign in…", () => Ctx.Agents.Seats.OpenSignIn(seat.ConfigDir), true, Ui.Clay);
            var remove = Ui.PillButton("Remove", () => Ctx.Agents.Seats.RemoveSeat(seat.ConfigDir));
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.ToolTip = "Moves this empty seat folder to the Recycle Bin";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(sign);
            buttons.Children.Add(remove);
            stack.Children.Add(buttons);
            return stack;
        }
        stack.Children.Add(LimitRow(seat.Session, TimeSpan.FromHours(5), opacity, "Session"));
        if (seat.Weekly is { } weekly) stack.Children.Add(LimitRow(weekly, TimeSpan.FromDays(7), opacity, "Weekly"));
        stack.Children.Add(MoneyLine("This session", seat.SessionCost, opacity));
        var month = MoneyLine("This month", seat.MonthCost, opacity);
        month.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(month);
        if (seat.Error is { } err)
        {
            var e = Ui.Text("⚠ limits not refreshed", 10.5, null, 0.9, Ui.Orange);
            e.ToolTip = err;
            e.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(e);
        }
        if (seat.SameAccountAs is { } other)
        {
            var d = Ui.Text("⚠ same account as another seat", 10.5, null, 0.9, Ui.Orange);
            d.ToolTip = $"{System.IO.Path.GetFileName(other)} is already signed in to {seat.Email}, so both show the same limits.\n" +
                $"To use another account here, run /login in Claude Code with CLAUDE_CONFIG_DIR={seat.ConfigDir}.";
            d.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(d);
        }
        return stack;
    }

    private FrameworkElement Place(string title, FrameworkElement mark, List<LiveSession> sessions, AgentSummary s)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        mark.Margin = new Thickness(0, 0, 7, 0);
        head.Children.Add(mark);
        head.Children.Add(Ui.Text(title, 11, FontWeights.SemiBold, 0.45));
        stack.Children.Add(head);
        if (sessions.Count == 0) stack.Children.Add(Ui.Text("No sessions running", 13, FontWeights.Medium, 0.55));
        foreach (var l in sessions.Take(3))
        {
            var clock = Ui.Text(Ui.Clock(DateTime.Now - l.Since), 12.5, FontWeights.SemiBold, 1, Ui.Clay, true);
            _clocks.Add((clock, l.Since));
            var seat = s.Seats.FirstOrDefault(x => x.Id == l.Seat);
            var name = Ui.Text(l.Project.Length > 0 ? l.Project : "Claude", 13, FontWeights.SemiBold, 0.95);
            if (s.Seats.Count > 1 && seat?.Email is { } mail) name.ToolTip = mail;
            var row = Ui.Columns((name, Ui.Star), (clock, Ui.Auto));
            row.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(row);
        }
        return stack;
    }

    // ---------- Trend ----------

    private string _trendPeriod = "7";

    private FrameworkElement TrendCard(AgentSummary s)
    {
        var dock = new DockPanel { LastChildFill = true };
        // Today / 7 days / 30 days, the chosen one a clay pill with dark text.
        var picker = new StackPanel { Orientation = Orientation.Horizontal };
        var pickerBox = new Border { Background = Ui.White(0.1), CornerRadius = new CornerRadius(13), Padding = new Thickness(2), Child = picker };
        foreach (var (id, label) in new[] { ("today", "Today"), ("7", "7d"), ("30", "30d") })
        {
            var on = _trendPeriod == id;
            var b = new Pressable
            {
                Height = 22, Padding = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(11),
                Background = on ? Ui.B(Ui.Clay) : Brushes.Transparent,
                Child = Ui.Text(label, 11, FontWeights.SemiBold, on ? 1 : 0.6, on ? Colors.Black : null),
            };
            var period = id;
            b.Click += () => { _trendPeriod = period; Render(Ctx.Agents.Summary); };
            picker.Children.Add(b);
        }
        var header = Header(Ui.Icon("", 14, Colors.White, 0.7), "Trend", pickerBox);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var buckets = _trendPeriod switch { "today" => s.Hourly, "30" => s.Daily30, _ => s.Daily };
        var totalText = Ui.Text(Whole(buckets.Sum()), 13, FontWeights.SemiBold, 0.6);
        totalText.Margin = new Thickness(0, -2, 0, 8);
        DockPanel.SetDock(totalText, Dock.Top);
        dock.Children.Add(totalText);

        var max = Math.Max(0.01, buckets.DefaultIfEmpty(0).Max());
        var bars = new UniformGrid { Rows = 1 };
        var dense = buckets.Count > 10;
        var columns = new List<(Border Bar, double Value)>();
        for (var i = 0; i < buckets.Count; i++)
        {
            var column = new DockPanel { Margin = new Thickness(dense ? 0.75 : 3, 0, dense ? 0.75 : 3, 0), LastChildFill = true };
            string label;
            if (_trendPeriod == "today") label = i % 6 == 0 ? $"{i}" : "";
            else
            {
                var day = DateTime.Today.AddDays(i - (buckets.Count - 1));
                label = dense ? (i % 7 == 1 ? day.Day.ToString() : "") : day.ToString("ddd")[..1];
            }
            var l = Ui.Text(label, 11, FontWeights.SemiBold, 0.45);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.TextTrimming = TextTrimming.None;
            DockPanel.SetDock(l, Dock.Bottom);
            column.Children.Add(l);
            var v = buckets[i];
            var bar = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6),
                Background = v > 0 ? Ui.B(Ui.Clay) : Ui.White(0.14), CornerRadius = new CornerRadius(dense ? 2 : 4),
                Height = 3, ToolTip = Ui.Money(v),
            };
            columns.Add((bar, v));
            column.Children.Add(bar);
            bars.Children.Add(column);
        }
        // Bars fill whatever height the square leaves them.
        bars.SizeChanged += (_, _) =>
        {
            var room = Math.Max(10, bars.ActualHeight - 26);
            foreach (var (bar, v) in columns) bar.Height = v > 0 ? Math.Max(2, v / max * room) : 3;
        };
        dock.Children.Add(bars);
        return Card(dock);
    }

    // ---------- Spend ----------

    private static FrameworkElement SpendCard(AgentSummary s)
    {
        var stack = new StackPanel();
        // How the last 30 days at API prices compare with what the plan costs each month.
        var monthly = s.Limits?.PlanMonthly ?? 0;
        var month30 = s.Month.Cost;
        UIElement chip = monthly > 0 && month30 > 0 ? Chip($"{month30 / monthly:0.0}×", Ui.Clay) : Ui.Spacer();
        if (chip is Border cb) cb.ToolTip = $"Last 30 days at API prices ({Ui.Money(month30)}) vs your plan's monthly price ({Ui.Money(monthly)})";
        stack.Children.Add(Header(DollarMark(), "Spend", chip));

        var total = s.AllTime.Cost;
        var amount = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        amount.Inlines.Add(new Run((s.AllTime.Unpriced > 0 ? "≥" : "") + Whole(total))
        { FontSize = 28, FontWeight = FontWeights.Bold, FontFamily = Ui.Display, Foreground = Ui.B(AgentResting.SpendTint(total)) });
        amount.Inlines.Add(new Run("  all time") { FontSize = 12, FontFamily = Ui.Font, Foreground = Ui.White(0.55) });
        amount.ToolTip = "API value of every Claude request on this PC and the synced computers";
        stack.Children.Add(amount);

        // Today, 7 days and 30 days in three columns.
        var periods = new Grid { Margin = new Thickness(0, 10, 0, 12) };
        var cells = new[] { ("Today", s.Today.Cost), ("7 days", s.Week.Cost), ("30 days", s.Month.Cost) };
        for (var i = 0; i < cells.Length; i++)
        {
            periods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new StackPanel();
            cell.Children.Add(Ui.Text(cells[i].Item1, 11, FontWeights.SemiBold, 0.45));
            cell.Children.Add(Money(cells[i].Item2, 13.5));
            Grid.SetColumn(cell, i);
            periods.Children.Add(cell);
        }
        stack.Children.Add(periods);

        var caption = Ui.Text("MODELS", 11, FontWeights.SemiBold, 0.45);
        caption.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(caption);
        var max = Math.Max(0.0001, s.AllTimeModels.Select(r => r.Cost).DefaultIfEmpty(0).Max());
        foreach (var r in s.AllTimeModels)
        {
            var value = Ui.Text(Whole(r.Cost), 12.5, FontWeights.SemiBold, 0.9);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            var row = Ui.Columns((Ui.Text(r.Name, 12.5, FontWeights.SemiBold, 0.95), Ui.Px(74)), (Bar(r.Cost / max), Ui.Star), (value, Ui.Px(50)));
            row.Margin = new Thickness(0, 0, 0, 5);
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    /// A thin rounded bar, as in the Mac page's model rows.
    private static FrameworkElement Bar(double fraction)
    {
        var grid = new Grid { Height = 7, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        grid.Children.Add(new Border { Background = Ui.White(0.1), CornerRadius = new CornerRadius(3.5) });
        var fill = new Border { Background = Ui.B(Ui.Clay), CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(fill);
        grid.SizeChanged += (_, _) => fill.Width = Math.Max(7, Math.Clamp(fraction, 0, 1) * grid.ActualWidth);
        return grid;
    }

    // ---------- Activity ----------

    /// GitHub's contribution greens, from the first level to the brightest.
    private static readonly Color[] Greens = [Ui.C("#0E4429"), Ui.C("#006D32"), Ui.C("#26A641"), Ui.C("#39D353")];

    private static Brush Level(int level) => level == 0 ? Ui.White(0.07) : Ui.B(Greens[level - 1]);

    /// A year of days like a GitHub contribution graph: a column per week (Sunday on top), month names
    /// along the top, Mon/Wed/Fri down the side, greener the more API value the day saw.
    private static FrameworkElement ActivityCard(AgentSummary s)
    {
        var stack = new StackPanel();
        var days = s.Activity;
        var total = days.Sum();
        var title = new TextBlock { Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Run(Whole(total)) { FontSize = 15, FontWeight = FontWeights.Bold, FontFamily = Ui.Display, Foreground = Ui.White(0.95) });
        title.Inlines.Add(new Run(" in the last year") { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.White(0.95) });
        stack.Children.Add(title);

        var nonZero = days.Where(v => v > 0).OrderBy(v => v).ToList();
        double Quantile(double q) => nonZero.Count == 0 ? 0 : nonZero[(int)Math.Min(nonZero.Count - 1, q * nonZero.Count)];
        var (q1, q2, q3) = (Quantile(0.25), Quantile(0.5), Quantile(0.75));
        int LevelOf(double v) => v <= 0 ? 0 : v <= q1 ? 1 : v <= q2 ? 2 : v <= q3 ? 3 : 4;

        const double labelWidth = 30;
        var weeks = (days.Count + 6) / 7;
        // Row 0: month names; rows 1-7: Sunday to Saturday. Column 0: day names.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        for (var c = 0; c < weeks; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        for (var r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            var cell = Math.Max(4, (e.NewSize.Width - labelWidth) / weeks);
            for (var r = 1; r <= 7; r++) grid.RowDefinitions[r].Height = new GridLength(cell);
        };

        foreach (var (row, name) in new[] { (2, "Mon"), (4, "Wed"), (6, "Fri") })
        {
            var t = Ui.Text(name, 10.5, FontWeights.Medium, 0.6);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(t, row);
            grid.Children.Add(t);
        }

        // A month's name over the first week that starts in it, unless the next name would crowd it.
        var first = s.ActivityFrom;
        var starts = Enumerable.Range(0, weeks).Where(c => c == 0 || first.AddDays(7 * c).Month != first.AddDays(7 * (c - 1)).Month).ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var c = starts[i];
            if (i + 1 < starts.Count && starts[i + 1] - c < 3) continue;
            var t = Ui.Text(first.AddDays(7 * c).ToString("MMM"), 10.5, FontWeights.Medium, 0.75);
            t.TextTrimming = TextTrimming.None;
            Grid.SetRow(t, 0);
            // A month starting in the last few weeks ends at the grid's right edge rather than being cut off.
            var span = Math.Min(4, weeks - c);
            if (span < 3)
            {
                t.HorizontalAlignment = HorizontalAlignment.Right;
                (c, span) = (weeks - 3, 3);
            }
            Grid.SetColumn(t, c + 1);
            Grid.SetColumnSpan(t, span);
            grid.Children.Add(t);
        }

        for (var i = 0; i < days.Count; i++)
        {
            var v = days[i];
            var isToday = i == days.Count - 1;
            var cell = new Border
            {
                Margin = new Thickness(1.2), CornerRadius = new CornerRadius(2),
                Background = Level(LevelOf(v)),
                BorderBrush = isToday ? Ui.White(0.85) : null, BorderThickness = new Thickness(isToday ? 1 : 0),
                ToolTip = $"{first.AddDays(i):ddd d MMM yyyy}: {Ui.Money(v)}",
            };
            Grid.SetColumn(cell, i / 7 + 1);
            Grid.SetRow(cell, i % 7 + 1);
            grid.Children.Add(cell);
        }
        stack.Children.Add(grid);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(Ui.Text("Less", 11, FontWeights.Medium, 0.55));
        for (var level = 0; level <= 4; level++)
            legend.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Level(level), Margin = new Thickness(level == 0 ? 6 : 3, 0, 0, 0) });
        var more = Ui.Text("More", 11, FontWeights.Medium, 0.55);
        more.Margin = new Thickness(6, 0, 0, 0);
        legend.Children.Add(more);
        var active = Ui.Text($"Active days {days.Count(v => v > 0)}", 11.5, FontWeights.Medium, 0.55);
        var foot = Ui.Columns((active, Ui.Star), (legend, Ui.Auto));
        foot.Margin = new Thickness(labelWidth, 10, 0, 0);
        stack.Children.Add(foot);
        return Card(stack);
    }
}
