using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCEssentials.Services;

namespace PCEssentials.UI.Island;

/// System monitor: CPU, GPU, memory, network and disk cards with live values and history.
public sealed class SystemPage : IslandPage
{
    private sealed class Card
    {
        public readonly TextBlock Value = Ui.Text("--", 22, FontWeights.Medium, 0.95);
        public readonly TextBlock Detail = Ui.Text("", 11, FontWeights.Medium, 0.55);
        public readonly Border MeterHost = new() { Height = 5, Margin = new Thickness(0, 8, 0, 0) };
        public readonly Sparkline? Spark;
        public readonly Border Root;

        public Card(string label, string glyph, Color tint, Sparkline? spark = null)
        {
            Spark = spark;
            Value.FontFamily = Ui.Display;
            Typography.SetNumeralAlignment(Value, FontNumeralAlignment.Tabular);
            var head = Ui.Columns((Ui.Icon(glyph, 11, tint), Ui.Px(18)), (Ui.Text(label, 10.5, FontWeights.SemiBold, 0.55), Ui.Star));
            var stack = new StackPanel();
            stack.Children.Add(head);
            Value.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(Value);
            stack.Children.Add(Detail);
            if (spark is not null)
            {
                spark.Height = 26;
                spark.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(spark);
            }
            else stack.Children.Add(MeterHost);
            Root = Ui.Card(stack, 12, 18);
            Root.Margin = new Thickness(0, 0, 10, 10);
        }

        public void Meter(double fraction, Color tint) =>
            MeterHost.Child = Ui.Meter(fraction, fraction >= 0.85 ? Ui.Orange : tint, double.NaN, 5);
    }

    private readonly Card _cpu = new("CPU", Ui.GCpu, Ui.Blue);
    private readonly Card _gpu = new("GPU", Ui.GGpu, Ui.Green);
    private readonly Card _ram = new("MEMORY", Ui.GMemory, Ui.Purple);
    private readonly Card _down = new("DOWNLOAD", Ui.GDown, Ui.Green, new Sparkline(Ui.Green));
    private readonly Card _up = new("UPLOAD", Ui.GUp, Ui.Orange, new Sparkline(Ui.Orange));
    private readonly Card _disk = new("DISK", Ui.GDisk, Ui.Tan);
    private readonly TextBlock _hint = Ui.Text("", 10.5, null, 0.45);
    private int _tick;

    public SystemPage(IslandContext ctx) : base(ctx)
    {
        var grid = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(0, 0, -10, 0) };
        foreach (var c in new[] { _cpu, _gpu, _ram, _down, _up, _disk }) grid.Children.Add(c.Root);
        var stack = new StackPanel();
        stack.Children.Add(grid);
        _hint.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_hint);
        if (ctx.TakeScreenshot is { } takeScreenshot)
        {
            var shot = Ui.PillButton("Take a screenshot", () => { Ctx.Collapse(); takeScreenshot(); });
            shot.HorizontalAlignment = HorizontalAlignment.Left;
            shot.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(shot);
        }
        Children.Add(stack);
    }

    public override string Id => "system";
    public override string Title => "System";
    public override string Glyph => Ui.GSystem;
    public override Color Tint => (Ctx.Monitor.Current.CpuLoad ?? 0) > 80 ? Ui.Orange : Ui.Blue;

    public override void OnShown() => Render();

    public override void Tick()
    {
        if (++_tick % 6 == 0) Render();
    }

    private void Render()
    {
        var s = Ctx.Monitor.Current;
        var f = Ctx.Settings.UseFahrenheit;
        _cpu.Value.Text = Format.Percent(s.CpuLoad);
        _cpu.Detail.Text = s.CpuTemp is null ? "temp needs admin" : $"{Format.Temp(s.CpuTemp, f)}" + (s.CpuPower is double w ? $" · {w:0} W" : "");
        _cpu.Meter((s.CpuLoad ?? 0) / 100, Ui.Blue);
        _cpu.Root.ToolTip = s.CpuName;

        _gpu.Value.Text = Format.Percent(s.GpuLoad);
        var vram = s.GpuMemTotalMb is double total && total > 0 ? $" · {s.GpuMemUsedMb / 1024:0.0}/{total / 1024:0} GB" : "";
        _gpu.Detail.Text = Format.Temp(s.GpuTemp, f) + vram;
        _gpu.Meter((s.GpuLoad ?? 0) / 100, Ui.Green);
        _gpu.Root.ToolTip = s.GpuName;

        _ram.Value.Text = $"{s.RamUsedGb:0.0} GB";
        _ram.Detail.Text = $"{Format.Percent(s.RamLoad)} of {s.RamTotalGb:0} GB";
        _ram.Meter(s.RamLoad / 100, Ui.Purple);

        _down.Value.Text = Format.Speed(s.NetDownBps);
        _down.Detail.Text = $"{Format.Bytes(s.SessionDownBytes)} this session";
        _down.Spark?.Set(Ctx.Monitor.NetDownHistory);
        _up.Value.Text = Format.Speed(s.NetUpBps);
        _up.Detail.Text = $"{Format.Bytes(s.SessionUpBytes)} this session";
        _up.Spark?.Set(Ctx.Monitor.NetUpHistory);

        if (s.Disks.Count > 0)
        {
            var main = s.Disks.FirstOrDefault(d => d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase));
            if (main.Name is null) main = s.Disks[0];
            _disk.Value.Text = $"{main.FreeGb:0} GB";
            _disk.Detail.Text = $"free on {main.Name} of {main.TotalGb:0} GB";
            _disk.Meter(1 - main.FreeGb / Math.Max(1, main.TotalGb), Ui.Tan);
            _disk.Root.ToolTip = string.Join("\n", s.Disks.Select(d => $"{d.Name}  {d.FreeGb:0} GB free of {d.TotalGb:0} GB"));
        }

        _hint.Text = s.CpuTemp is null
            ? HardwareMonitor.IsAdmin
                ? "CPU temperature needs the PawnIO driver (winget install namazso.PawnIO), then restart PC Essentials."
                : "CPU temperature needs PC Essentials to run as administrator (Settings → General) and the PawnIO driver."
            : "";
    }
}
