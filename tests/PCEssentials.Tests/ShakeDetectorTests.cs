using PCEssentials.Services.ShakeToFind;
using Xunit;

namespace PCEssentials.Tests;

public class ShakeDetectorTests
{
    private static readonly ShakeProfile Normal = ShakeProfile.For("normal");

    /// Moves the pointer through the given x turning points in a straight line, sampling every 8 ms.
    /// Returns the time of the first sample that started the effect, or null.
    private static double? Drive(ShakeDetector d, ref double t, double y, double msPerStroke, params double[] xs)
    {
        double? started = null;
        if (d.Feed(t, xs[0], y)) started = t;
        for (var i = 1; i < xs.Length; i++)
        {
            var steps = Math.Max(1, (int)(msPerStroke / 8));
            for (var s = 1; s <= steps; s++)
            {
                t += msPerStroke / steps;
                var x = xs[i - 1] + (xs[i] - xs[i - 1]) * s / steps;
                if (d.Feed(t, x, y) && started is null) started = t;
            }
        }
        return started;
    }

    private static double[] Shake(int strokes, double from = 500, double width = 150)
    {
        var xs = new double[strokes + 1];
        for (var i = 0; i <= strokes; i++) xs[i] = i % 2 == 0 ? from : from + width;
        return xs;
    }

    [Fact]
    public void QuickShakeStartsTheEffect()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        var started = Drive(d, ref t, 300, 100, Shake(6));
        Assert.NotNull(started);
        Assert.True(d.IsActive(t));
    }

    [Fact]
    public void NeedsEnoughReversals()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        // Three strokes give only two reversals.
        Assert.Null(Drive(d, ref t, 300, 100, Shake(3)));
        Assert.False(d.IsActive(t));
    }

    [Fact]
    public void SmallJitterDoesNotTrigger()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        Assert.Null(Drive(d, ref t, 300, 40, Shake(30, width: 12)));
    }

    [Fact]
    public void SlowBackAndForthDoesNotTrigger()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        // 150 px every 400 ms is reading-pace movement.
        Assert.Null(Drive(d, ref t, 300, 400, Shake(10)));
    }

    [Fact]
    public void VerticalShakeDoesNotTrigger()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        double? started = null;
        if (d.Feed(t, 500, 300)) started = t;
        for (var stroke = 0; stroke < 8; stroke++)
        {
            for (var s = 1; s <= 12; s++)
            {
                t += 8;
                var y = stroke % 2 == 0 ? 300 + 200.0 * s / 12 : 500 - 200.0 * s / 12;
                var x = 500 + (stroke % 2 == 0 ? 50.0 * s / 12 : 50 - 50.0 * s / 12);
                if (d.Feed(t, x, y)) started ??= t;
            }
        }
        Assert.Null(started);
    }

    [Fact]
    public void PausesBetweenStrokesBreakTheShake()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        double? started = null;
        for (var i = 0; i < 6; i++)
        {
            started ??= Drive(d, ref t, 300, 100, i % 2 == 0 ? 500 : 650, i % 2 == 0 ? 650 : 500);
            t += 300; // rest between strokes
        }
        Assert.Null(started);
    }

    [Fact]
    public void FadesShortlyAfterShakingStops()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        Drive(d, ref t, 300, 100, Shake(6));
        Assert.True(d.IsActive(t));
        Assert.False(d.IsActive(t + Normal.HoldMs + 1));
    }

    [Fact]
    public void KeepsGoingWhileShakingContinues()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        Drive(d, ref t, 300, 100, Shake(40));
        Assert.True(d.IsActive(t));
    }

    [Fact]
    public void ThresholdsScaleWithDisplay()
    {
        // 50 px strokes are a shake at 100% but too short at 200%.
        var at100 = new ShakeDetector(Normal);
        double t = 0;
        Assert.NotNull(Drive(at100, ref t, 300, 60, Shake(8, width: 50)));

        var at200 = new ShakeDetector(Normal) { PixelScale = 2 };
        t = 0;
        Assert.Null(Drive(at200, ref t, 300, 60, Shake(8, width: 50)));
    }

    [Fact]
    public void ResetForgetsReversals()
    {
        var d = new ShakeDetector(Normal);
        double t = 0;
        Drive(d, ref t, 300, 100, Shake(3));
        d.Reset();
        // Two more strokes would have completed the shake without the reset.
        Assert.Null(Drive(d, ref t, 300, 100, 500, 650, 500));
    }

    [Fact]
    public void SensitivityChangesHowMuchShakingIsNeeded()
    {
        double t = 0;
        Assert.NotNull(Drive(new ShakeDetector(ShakeProfile.For("high")), ref t, 300, 90, Shake(4, width: 40)));
        t = 0;
        Assert.Null(Drive(new ShakeDetector(ShakeProfile.For("low")), ref t, 300, 90, Shake(4, width: 40)));
    }

    [Fact]
    public void GrowthEasesUpAndBackDown()
    {
        var g = new ShakeGrowth();
        var last = 1.0;
        for (var i = 0; i < 30; i++)
        {
            var s = g.Step(16, true, 4);
            Assert.True(s >= last);
            last = s;
        }
        Assert.InRange(last, 3.8, 4.0);
        for (var i = 0; i < 60 && !g.AtRest; i++)
        {
            var s = g.Step(16, false, 4);
            Assert.True(s <= last);
            last = s;
        }
        Assert.True(g.AtRest);
        Assert.Equal(1, g.Scale);
    }

    [Fact]
    public void GrowthIsGradualNotAJump()
    {
        var g = new ShakeGrowth();
        Assert.InRange(g.Step(16, true, 4), 1.05, 1.6);
    }
}
