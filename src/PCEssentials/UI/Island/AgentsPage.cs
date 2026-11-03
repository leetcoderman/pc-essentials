using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using PCEssentials.Services.Agents;

namespace PCEssentials.UI.Island;

/// AI agents, laid out like the Mac island's page: Claude with a column per Team seat (session
/// limit with its pace, this session's and this month's API value) and the sessions running in the
/// Claude app and in the terminal; Codex beside Spending; then the trend and top projects.
public sealed class AgentsPage : IslandPage
{
    private readonly StackPanel _content = new();
    private readonly List<(TextBlock Text, DateTime Since)> _clocks = new();

    public AgentsPage(IslandContext ctx) : base(ctx)
    {
        Children.Add(Ui.Scroll(_content));
        ctx.Agents.Updated += s => Dispatcher.BeginInvoke(() => { if (IsVisible) Render(s); });
        ctx.Agents.Resets.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Render(Ctx.Agents.Summary); });
    }

    public override string Id => "agents";
    public override string Title => "Agents";
    public override string Glyph => Ui.GRobot;
    public override Color Tint => Ui.Clay;

    public static Color TintOf(AgentProvider p) => p == AgentProvider.Claude ? Ui.Clay : Ui.Codex;
    public static string NameOf(AgentProvider p) => p == AgentProvider.Claude ? "Claude" : "Codex";

    /// The agent's mark: Claude's asterisk or Codex's prompt, on a tinted tile.
    public static FrameworkElement Mark(AgentProvider p, double size)
    {
        var glyph = AgentResting.Glyph(p, size * 0.62);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Background = Ui.B(TintOf(p), 0.18),
            Child = glyph, Margin = new Thickness(0, 0, 2, 0),
        };
    }

    public override void OnShown()
    {
        Ctx.Agents.Seats.RefreshSoon();
        Ctx.Agents.Resets.RefreshIfStale();
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
        var hasClaude = s.Installed.Contains(AgentProvider.Claude) || s.Seats.Any(x => x.SignedIn);
        var hasCodex = s.Installed.Contains(AgentProvider.Codex) || s.Limits.ContainsKey(AgentProvider.Codex);
        if (!hasClaude && !hasCodex)
        {
            _content.Children.Add(new Border { Height = 280, Child = Ui.Empty(Ui.GRobot, "No agents yet", "Claude Code and Codex activity shows up here once you use them on this PC.") });
            return;
        }
        if (hasClaude) _content.Children.Add(ClaudeCard(s));
        _content.Children.Add(hasCodex ? Row(CodexCard(s), SpendingCard(s)) : SpendingCard(s));
        _content.Children.Add(Row(ProjectsCard(s), TrendCard(s)));
        _content.Children.Add(hasCodex ? Row(ResetsCard(), ActivityCard(s)) : ActivityCard(s));
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

    private static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        left.Margin = new Thickness(0);
        right.Margin = new Thickness(0);
        var g = Ui.Columns((left, Ui.Star), (Ui.Spacer(10), Ui.Auto), (right, Ui.Star));
        g.Margin = new Thickness(0, 0, 0, 10);
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

    /// "Session ↻ 23:00 ........ 15%" over a bar with a white tick at how far through the window we are.
    private static FrameworkElement LimitRow(LimitWindow? w, Color tint, TimeSpan length, double opacity, string label)
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
        var fill = new Border { Background = Ui.B(used >= 95 ? Ui.Red : used >= 80 ? Ui.Yellow : tint), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
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

    // ---------- Claude ----------

    private FrameworkElement ClaudeCard(AgentSummary s)
    {
        var stack = new StackPanel();
        var seats = s.Seats.Count > 0 ? s.Seats : [];
        var plan = seats.FirstOrDefault(x => x.IsDefault)?.PlanName ?? seats.FirstOrDefault(x => x.PlanName is not null)?.PlanName;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(AgentResting.Glyph(AgentProvider.Claude, 20));
        if (plan is not null)
        {
            var chip = Chip(plan, Ui.Clay);
            chip.Margin = new Thickness(10, 0, 0, 0);
            head.Children.Add(chip);
        }
        if (seats.Count <= 1)
        {
            var title = Ui.Text("Claude", 15, FontWeights.SemiBold, 0.95);
            title.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Insert(1, title);
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
        var live = s.Live.Where(l => l.Provider == AgentProvider.Claude).ToList();
        stack.Children.Add(Ui.Columns(
            (Place("IN CLAUDE APP", AgentResting.Glyph(AgentProvider.Claude, 11), live.Where(l => l.Place == "app").ToList(), s), Ui.Star),
            (new Border { Width = 1, Background = Ui.White(0.12), Margin = new Thickness(12, 0, 12, 0) }, Ui.Auto),
            (Place("IN TERMINAL", Ui.Text(">_", 11, FontWeights.Bold, 0.6, null, true), live.Where(l => l.Place == "terminal").ToList(), s), Ui.Star)));
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
        stack.Children.Add(LimitRow(seat.Session, Ui.Clay, TimeSpan.FromHours(5), opacity, "Session"));
        if (seat.Weekly is { } weekly) stack.Children.Add(LimitRow(weekly, Ui.Clay, TimeSpan.FromDays(7), opacity, "Weekly"));
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
            var clock = Ui.Text(Ui.Clock(DateTime.Now - l.Since), 12.5, FontWeights.SemiBold, 1, TintOf(l.Provider), true);
            _clocks.Add((clock, l.Since));
            var seat = s.Seats.FirstOrDefault(x => x.Id == l.Seat);
            var name = Ui.Text(l.Project.Length > 0 ? l.Project : NameOf(l.Provider), 13, FontWeights.SemiBold, 0.95);
            if (s.Seats.Count > 1 && seat?.Email is { } mail) name.ToolTip = mail;
            var row = Ui.Columns((name, Ui.Star), (clock, Ui.Auto));
            row.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(row);
        }
        return stack;
    }

    // ---------- Codex ----------

    private FrameworkElement CodexCard(AgentSummary s)
    {
        var stack = new StackPanel();
        s.Limits.TryGetValue(AgentProvider.Codex, out var limits);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Mark(AgentProvider.Codex, 22));
        var title = Ui.Text("Codex", 15, FontWeights.SemiBold, 0.95);
        title.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(title);
        var top = Ui.Columns((head, Ui.Star), ((UIElement?)(limits?.PlanName is { Length: > 0 } plan ? Chip(plan, Ui.Codex) : null) ?? Ui.Spacer(), Ui.Auto));
        top.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(top);
        if (limits is null || (limits.Session is null && limits.Weekly is null))
            stack.Children.Add(Ui.Text("Limits appear after your next Codex turn.", 12, null, 0.55));
        else
        {
            if (limits.Session is not null) stack.Children.Add(LimitRow(limits.Session, Ui.Codex, TimeSpan.FromHours(5), 0.95, "Session"));
            if (limits.Weekly is not null) stack.Children.Add(LimitRow(limits.Weekly with { Label = "Week" }, Ui.Codex, TimeSpan.FromDays(7), 0.95, "Week"));
        }
        stack.Children.Add(new Border { Height = 1, Background = Ui.White(0.12), Margin = new Thickness(0, 2, 0, 12) });
        var live = s.Live.Where(l => l.Provider == AgentProvider.Codex).ToList();
        stack.Children.Add(Place("IN CODEX APP", Mark(AgentProvider.Codex, 14), live.Where(l => l.Place == "app").ToList(), s));
        if (live.Any(l => l.Place == "terminal"))
        {
            var t = Place("IN TERMINAL", Ui.Text(">_", 11, FontWeights.Bold, 0.6, null, true), live.Where(l => l.Place == "terminal").ToList(), s);
            t.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(t);
        }
        return Card(stack);
    }

    // ---------- Spending ----------

    private FrameworkElement SpendingCard(AgentSummary s)
    {
        var stack = new StackPanel();
        var total = s.AllTime.Values.Sum(t => t.Cost);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderBrush = Ui.White(0.6), BorderThickness = new Thickness(1.4),
            Child = Ui.Text("$", 11, FontWeights.Bold, 0.8),
        });
        ((TextBlock)((Border)head.Children[0]).Child).HorizontalAlignment = HorizontalAlignment.Center;
        var title = Ui.Text("Spending", 15, FontWeights.SemiBold, 0.95);
        title.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(title);
        // How the last 30 days at API prices compare with what the plans cost each month.
        var monthly = s.Limits.Values.Sum(l => l.PlanMonthly ?? 0) + s.Seats.Where(x => !x.IsDefault && x.SignedIn).Sum(_ => 0.0);
        var month30 = s.Month.Values.Sum(t => t.Cost);
        UIElement chip = monthly > 0 && month30 > 0 ? Chip($"{month30 / monthly:0.0}×", Ui.Codex) : Ui.Spacer();
        if (chip is Border cb) cb.ToolTip = $"Last 30 days at API prices ({Ui.Money(month30)}) vs your plans' monthly price ({Ui.Money(monthly)})";
        var top = Ui.Columns((head, Ui.Star), (chip, Ui.Auto));
        top.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(top);

        var amount = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom };
        var unpriced = s.AllTime.Values.Sum(t => t.Unpriced) > 0;
        amount.Inlines.Add(new Run((unpriced ? "≥" : "") + (total >= 1000 ? $"${total:N0}" : Ui.Money(total)))
        { FontSize = 30, FontWeight = FontWeights.Bold, FontFamily = Ui.Display, Foreground = Ui.B(AgentResting.SpendTint(total)) });
        amount.Inlines.Add(new Run("  all time · all agents") { FontSize = 12, FontFamily = Ui.Font, Foreground = Ui.White(0.55) });
        stack.Children.Add(amount);

        var split = new Grid { Height = 7, Margin = new Thickness(0, 10, 0, 12), ClipToBounds = true };
        var col = 0;
        foreach (var (p, t) in s.AllTime.OrderByDescending(kv => kv.Value.Cost))
        {
            if (total <= 0) break;
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, t.Cost / total), GridUnitType.Star) });
            var b = new Border { Background = Ui.B(TintOf(p)), Margin = new Thickness(col == 0 ? 0 : 2, 0, 0, 0), CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(b, col++);
            split.Children.Add(b);
        }
        if (col == 0) split.Children.Add(new Border { Background = Ui.White(0.1), CornerRadius = new CornerRadius(3) });
        stack.Children.Add(split);

        var caption = Ui.Text("MODELS", 11, FontWeights.SemiBold, 0.45);
        caption.Margin = new Thickness(0, 2, 0, 6);
        stack.Children.Add(caption);
        var max = Math.Max(0.0001, s.AllTimeModels.Select(r => r.Cost).DefaultIfEmpty(0).Max());
        foreach (var r in s.AllTimeModels)
        {
            var value = Money(r.Cost, 13);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.Foreground = Ui.White(0.9);
            var row = Ui.Columns((Ui.Text(r.Name, 13, FontWeights.SemiBold, 0.95), Ui.Px(88)), (Bar(r.Cost / max, TintOf(r.Provider)), Ui.Star), (value, Ui.Px(52)));
            row.Margin = new Thickness(0, 0, 0, 5);
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    private static void Unused(AgentSummary s, StackPanel stack, double month30)
    {
        var legend = new WrapPanel();
        foreach (var (p, t) in s.AllTime.OrderByDescending(kv => kv.Value.Cost))
        {
            legend.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Ui.B(TintOf(p)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var lt = Ui.Text($"{NameOf(p)} {Ui.Money(t.Cost)}", 11.5, FontWeights.Medium, 0.65);
            lt.Margin = new Thickness(0, 0, 12, 0);
            legend.Children.Add(lt);
        }
        stack.Children.Add(legend);
        s.Today.TryGetValue(AgentProvider.Claude, out var tc);
        s.Today.TryGetValue(AgentProvider.Codex, out var tx);
        var today = Ui.Text($"Today {Ui.Money((tc?.Cost ?? 0) + (tx?.Cost ?? 0))} · 30 days {Ui.Money(month30)}", 11.5, null, 0.5);
        today.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(today);
    }

    // ---------- Trend and rankings ----------

    private static FrameworkElement Header(string glyph, string text, UIElement? right = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Icon(glyph, 14, Colors.White, 0.7));
        var t = Ui.Text(text, 15, FontWeights.SemiBold, 0.95);
        t.Margin = new Thickness(9, 0, 0, 0);
        head.Children.Add(t);
        var g = Ui.Columns((head, Ui.Star), (right ?? Ui.Spacer(), Ui.Auto));
        g.Margin = new Thickness(0, 0, 0, 10);
        return g;
    }

    /// A thin rounded bar, as in the Mac page's model rows.
    private static FrameworkElement Bar(double fraction, Color tint)
    {
        var grid = new Grid { Height = 7, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { Background = Ui.White(0.1), CornerRadius = new CornerRadius(3.5) });
        var fill = new Border { Background = Ui.B(tint), CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(fill);
        grid.SizeChanged += (_, _) => fill.Width = Math.Max(7, Math.Clamp(fraction, 0, 1) * grid.ActualWidth);
        grid.MinWidth = 30;
        return grid;
    }

    private static FrameworkElement ProjectsCard(AgentSummary s)
    {
        var stack = new StackPanel();
        stack.Children.Add(Header(Ui.GFolder, "Projects"));
        if (s.TopProjects.Count == 0) stack.Children.Add(Ui.Text("Nothing yet", 12.5, null, 0.45));
        var max = Math.Max(0.0001, s.TopProjects.Select(r => r.Cost).DefaultIfEmpty(0).Max());
        foreach (var r in s.TopProjects)
        {
            // A white pill whose length is the project's share, then its value in grey.
            var pill = new Grid { Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
            var fill = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 7 };
            pill.Children.Add(fill);
            var share = r.Cost / max;
            pill.SizeChanged += (_, _) => fill.Width = Math.Max(7, share * pill.ActualWidth);
            var value = Ui.Text(r.Cost > 0 ? Ui.Money(r.Cost) : Ui.Tokens(r.Tokens), 13, FontWeights.SemiBold, 0.55);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            var row = Ui.Columns((Ui.Text(r.Name, 13.5, FontWeights.SemiBold, 0.95), Ui.Px(110)), (pill, Ui.Star), (value, Ui.Px(64)));
            row.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    private bool _confirmingReset;

    /// Codex's banked resets: how many the account holds, when the next expires, and a use that asks first.
    private FrameworkElement ResetsCard()
    {
        var resets = Ctx.Agents.Resets;
        var summary = resets.Summary;
        var stack = new DockPanel { LastChildFill = false };
        UIElement? chip = summary is { Available: > 0 } ? Chip(summary.Available.ToString(), Ui.Codex) : null;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Mark(AgentProvider.Codex, 22));
        var title = Ui.Text("Resets", 15, FontWeights.SemiBold, 0.95);
        title.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(title);
        var top = Ui.Columns((head, Ui.Star), (chip ?? Ui.Spacer(), Ui.Auto));
        top.Margin = new Thickness(0, 0, 0, 10);
        top.ToolTip = "A reset renews the Codex session and weekly limits at once. Codex checks your resets with its own sign-in, which PC Essentials never reads.";
        DockPanel.SetDock(top, Dock.Top);
        stack.Children.Add(top);

        TextBlock Line(string text, Color? tint = null, double opacity = 0.6)
        {
            var t = Ui.Text(text, 13, FontWeights.Medium, tint is null ? opacity : 1, tint);
            t.TextWrapping = TextWrapping.Wrap;
            DockPanel.SetDock(t, Dock.Top);
            return t;
        }
        Pressable Pill(string text, Action click, bool prominent = true, string? glyph = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph is not null) { var g = Ui.Icon(glyph, 11, Ui.Codex); g.Margin = new Thickness(0, 0, 6, 0); row.Children.Add(g); }
            row.Children.Add(Ui.Text(text, 13, FontWeights.SemiBold, 1, prominent ? Ui.Codex : null));
            var p = new Pressable { Height = 30, Padding = new Thickness(12, 0, 14, 0), CornerRadius = new CornerRadius(15), Background = Ui.B(prominent ? Ui.Codex : Colors.White, prominent ? 0.2 : 0.08), Child = row, Margin = new Thickness(0, 0, 6, 0) };
            p.Click += click;
            return p;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var finished = resets.Finished is { } f && DateTime.Now - f.At < TimeSpan.FromSeconds(60) ? f : ((CodexResetService.Outcome, DateTime)?)null;

        if (resets.Redeeming) stack.Children.Add(Line("Resetting…"));
        else if (_confirmingReset && summary is { Available: > 0 })
        {
            stack.Children.Add(Line("Reset the session and weekly limits now?", null, 0.9));
            buttons.Children.Add(Pill("Cancel", () => { _confirmingReset = false; Render(Ctx.Agents.Summary); }, false));
            buttons.Children.Add(Pill("Reset", () => { _confirmingReset = false; resets.Redeem(); Render(Ctx.Agents.Summary); }));
        }
        else if (finished is { } done)
        {
            stack.Children.Add(done.Item1 switch
            {
                CodexResetService.Outcome.Reset => Line("✓ Limits reset", Ui.Green),
                CodexResetService.Outcome.NothingToReset => Line("Your usage doesn't need a reset yet"),
                CodexResetService.Outcome.AlreadyRedeemed => Line("That reset was already used"),
                CodexResetService.Outcome.NoCredit => Line("No resets available"),
                _ => Line("Couldn't use the reset", Ui.Orange),
            });
            if (done.Item1 == CodexResetService.Outcome.Failed && summary is { Available: > 0 })
                buttons.Children.Add(Pill("Use a reset", () => { _confirmingReset = true; Render(Ctx.Agents.Summary); }, true, ""));
        }
        else if (summary is null && resets.Failure is { } failure)
        {
            stack.Children.Add(Line(failure switch
            {
                "missing" => "Needs the Codex app or CLI",
                "signIn" => "Sign in to Codex with a plan to see resets",
                "outdated" => "Update Codex to use resets here",
                _ => "Couldn't check resets",
            }));
            buttons.Children.Add(Pill("Try again", () => resets.RefreshIfStale(), false));
        }
        else if (summary is null) stack.Children.Add(Line("Checking…"));
        else if (summary.Available > 0)
        {
            var soon = summary.NextExpiry is { } e && e - DateTime.Now < TimeSpan.FromDays(1);
            if (summary.NextExpiry is { } exp) stack.Children.Add(Line("Next expires " + Relative(exp), soon ? Ui.Orange : null));
            buttons.Children.Add(Pill("Use a reset", () => { _confirmingReset = true; Render(Ctx.Agents.Summary); }, true, ""));
        }
        else stack.Children.Add(Line("No resets available"));
        if (buttons.Children.Count > 0) stack.Children.Add(buttons);
        return Card(stack);
    }

    private static string Relative(DateTime when)
    {
        var d = when - DateTime.Now;
        if (d.TotalHours < 1) return $"in {Math.Max(1, (int)d.TotalMinutes)} minutes";
        if (d.TotalHours < 24) return $"in {(int)d.TotalHours} hours";
        var days = (int)Math.Round(d.TotalDays);
        return days == 1 ? "tomorrow" : $"in {days} days";
    }

    private string _trendPeriod = "7";

    private FrameworkElement TrendCard(AgentSummary s)
    {
        var stack = new StackPanel();
        // Today / 7 days / 30 days, the chosen one a clay pill with dark text.
        var picker = new StackPanel { Orientation = Orientation.Horizontal };
        var pickerBox = new Border { Background = Ui.White(0.1), CornerRadius = new CornerRadius(13), Padding = new Thickness(2), Child = picker };
        foreach (var (id, label) in new[] { ("today", "Today"), ("7", "7 days"), ("30", "30 days") })
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
        stack.Children.Add(Header("", "Trend", pickerBox));

        var buckets = _trendPeriod switch { "today" => s.Hourly, "30" => s.Daily30, _ => s.Daily };
        var total = buckets.Sum(d => d.Values.Sum());
        var totalText = Ui.Text(total >= 1000 ? $"${total:N0}" : Ui.Money(total), 13, FontWeights.SemiBold, 0.6);
        totalText.Margin = new Thickness(0, -2, 0, 6);
        stack.Children.Add(totalText);

        var max = Math.Max(0.01, buckets.Select(d => d.Values.Sum()).DefaultIfEmpty(0).Max());
        var bars = new UniformGrid { Rows = 1, Height = 110 };
        var dense = buckets.Count > 10;
        for (var i = 0; i < buckets.Count; i++)
        {
            var column = new DockPanel { Margin = new Thickness(dense ? 1 : 4, 0, dense ? 1 : 4, 0), LastChildFill = true };
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
            var stackBars = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
            var costs = buckets[i];
            foreach (var p in new[] { AgentProvider.Codex, AgentProvider.Claude })
            {
                if (!costs.TryGetValue(p, out var c) || c <= 0) continue;
                stackBars.Children.Add(new Border { Height = Math.Max(2, c / max * 86), Background = Ui.B(TintOf(p)), CornerRadius = new CornerRadius(dense ? 2 : 4), Margin = new Thickness(0, 1, 0, 0) });
            }
            if (stackBars.Children.Count == 0)
                stackBars.Children.Add(new Border { Height = 3, Background = Ui.White(0.14), CornerRadius = new CornerRadius(1.5) });
            stackBars.ToolTip = Ui.Money(costs.Values.Sum());
            column.Children.Add(stackBars);
            bars.Children.Add(column);
        }
        stack.Children.Add(bars);
        return Card(stack);
    }

    /// 13 weeks of activity: one cell per day, a column per week, brighter the more API value it saw.
    private static FrameworkElement ActivityCard(AgentSummary s)
    {
        var stack = new StackPanel();
        stack.Children.Add(Header("", "Activity"));
        var days = s.Heatmap;
        var nonZero = days.Where(v => v > 0).OrderBy(v => v).ToList();
        double Quantile(double q) => nonZero.Count == 0 ? 0 : nonZero[(int)Math.Min(nonZero.Count - 1, q * nonZero.Count)];
        var q1 = Quantile(0.25);
        var q2 = Quantile(0.5);
        var q3 = Quantile(0.75);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        // Weeks start on Sunday; the first column may begin part-way through its week.
        var first = DateTime.Today.AddDays(-(days.Count - 1));
        var lead = (int)first.DayOfWeek;
        var weeks = (lead + days.Count + 6) / 7;
        for (var c = 0; c < weeks; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        for (var r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        for (var i = 0; i < days.Count; i++)
        {
            var slot = lead + i;
            var v = days[i];
            var level = v <= 0 ? 0 : v <= q1 ? 1 : v <= q2 ? 2 : v <= q3 ? 3 : 4;
            var isToday = i == days.Count - 1;
            var cell = new Border
            {
                Margin = new Thickness(2.5), CornerRadius = new CornerRadius(5),
                Background = Ui.White(level switch { 0 => 0.08, 1 => 0.3, 2 => 0.5, 3 => 0.72, _ => 0.92 }),
                BorderBrush = isToday ? Ui.White(0.85) : null, BorderThickness = new Thickness(isToday ? 1.5 : 0),
                ToolTip = $"{first.AddDays(i):ddd d MMM}: {Ui.Money(v)}",
            };
            Grid.SetColumn(cell, slot / 7);
            Grid.SetRow(cell, slot % 7);
            grid.Children.Add(cell);
        }
        stack.Children.Add(grid);
        var total = days.Sum();
        var foot = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
        foot.Inlines.Add(new Run(total >= 1000 ? $"${total:N0}" : Ui.Money(total)) { FontSize = 17, FontWeight = FontWeights.Bold, FontFamily = Ui.Display, Foreground = Ui.White(0.95) });
        foot.Inlines.Add(new Run("  13 weeks") { FontSize = 12.5, Foreground = Ui.White(0.5) });
        var active = Ui.Text($"Active days {days.Count(v => v > 0)}", 12.5, FontWeights.Medium, 0.5);
        active.VerticalAlignment = VerticalAlignment.Bottom;
        stack.Children.Add(Ui.Columns((foot, Ui.Star), (active, Ui.Auto)));
        return Card(stack);
    }
}
