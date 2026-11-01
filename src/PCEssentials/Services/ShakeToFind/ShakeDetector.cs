namespace PCEssentials.Services.ShakeToFind;

/// How eager shake detection is. Distances are in logical pixels (96 dpi), times in milliseconds.
public sealed record ShakeProfile(int Reversals, double MinTravel, double MinSpeed, double WindowMs)
{
    /// A stroke must turn back by this much before it counts as a reversal, so jitter doesn't flip direction.
    public double Hysteresis { get; init; } = 6;
    /// A single stroke slower than this is a deliberate move, not a shake.
    public double MaxStrokeMs { get; init; } = 350;
    /// The pointer resting this long breaks a shake.
    public double MaxGapMs { get; init; } = 150;
    /// How long the effect stays on after the last qualifying stroke.
    public double HoldMs { get; init; } = 300;

    public static ShakeProfile For(string sensitivity) => sensitivity switch
    {
        "low" => new(5, 60, 0.9, 750),
        "high" => new(3, 25, 0.4, 550),
        _ => new(4, 40, 0.6, 650),
    };
}

/// Spots a "shake to find" gesture in a stream of pointer positions: several quick left-right reversals,
/// each stroke long and fast enough and mostly horizontal, inside a short window. Pure logic, fed one
/// sample at a time, so it can run inside a mouse hook and be tested with made-up positions.
public sealed class ShakeDetector
{
    private ShakeProfile _p;
    private double _scale = 1;
    private bool _has;
    private double _lastT, _lastX;
    private int _dir;
    private double _startT, _startX, _extremeT, _extremeX, _minY, _maxY;
    private readonly Queue<double> _reversals = new();

    public ShakeDetector(ShakeProfile profile) => _p = profile;

    /// Physical pixels per logical pixel, so thresholds feel the same at every display scale.
    public double PixelScale { get => _scale; set { _scale = value > 0 ? value : 1; Reset(); } }

    public ShakeProfile Profile { get => _p; set { _p = value; Reset(); } }

    /// The effect is on until this time (ms); earlier or equal means off.
    public double ActiveUntil { get; private set; } = double.NegativeInfinity;

    public bool IsActive(double now) => now < ActiveUntil;

    /// Forgets the movement so far (a button went down, the feature was paused). A running effect fades as usual.
    public void Reset()
    {
        _has = false;
        _dir = 0;
        _reversals.Clear();
    }

    /// Feeds one pointer position. Returns true when this sample starts the effect.
    public bool Feed(double t, double x, double y)
    {
        if (!_has || t - _lastT > _p.MaxGapMs)
        {
            _has = true;
            _dir = 0;
            _reversals.Clear();
            Begin(t, x, y);
            _lastT = t;
            _lastX = x;
            return false;
        }
        _lastT = t;
        _lastX = x;
        var hysteresis = _p.Hysteresis * _scale;

        if (_dir == 0)
        {
            Track(y);
            if (Math.Abs(x - _startX) < hysteresis) return false;
            _dir = Math.Sign(x - _startX);
            _extremeX = x;
            _extremeT = t;
            return false;
        }

        if ((x - _extremeX) * _dir >= 0)
        {
            _extremeX = x;
            _extremeT = t;
            Track(y);
            return false;
        }
        Track(y);
        if (Math.Abs(x - _extremeX) < hysteresis) return false;

        // Turned back: judge the stroke that just ended at the extreme point.
        var started = EndStroke();
        _dir = -_dir;
        Begin(_extremeT, _extremeX, y);
        _extremeX = x;
        _extremeT = t;
        return started;
    }

    private void Begin(double t, double x, double y)
    {
        _startT = t;
        _startX = x;
        _extremeT = t;
        _extremeX = x;
        _minY = _maxY = y;
    }

    private void Track(double y)
    {
        if (y < _minY) _minY = y;
        if (y > _maxY) _maxY = y;
    }

    private bool EndStroke()
    {
        var travel = Math.Abs(_extremeX - _startX);
        var ms = Math.Max(1, _extremeT - _startT);
        var vertical = _maxY - _minY;
        var good = travel >= _p.MinTravel * _scale
                   && travel / ms >= _p.MinSpeed * _scale
                   && ms <= _p.MaxStrokeMs
                   && vertical <= travel;
        var t = _extremeT;
        if (!good)
        {
            _reversals.Clear();
            return false;
        }
        var wasActive = IsActive(t);
        _reversals.Enqueue(t);
        while (_reversals.Count > 0 && t - _reversals.Peek() > _p.WindowMs) _reversals.Dequeue();
        if (wasActive)
        {
            ActiveUntil = _lastT + _p.HoldMs;
            return false;
        }
        if (_reversals.Count < _p.Reversals) return false;
        ActiveUntil = _lastT + _p.HoldMs;
        return true;
    }
}

/// The pointer's size over time: eases up toward the maximum while shaking, and back to 1 once it stops.
public sealed class ShakeGrowth
{
    /// Time constants (ms) of the ease. Growing is a little slower, so it swells with the shake.
    public double GrowMs { get; init; } = 160;
    public double ShrinkMs { get; init; } = 110;

    public double Scale { get; private set; } = 1;

    /// True once it has shrunk back to normal size.
    public bool AtRest => Scale <= 1;

    public double Step(double dtMs, bool active, double max)
    {
        var target = active ? Math.Max(1, max) : 1;
        var tau = target > Scale ? GrowMs : ShrinkMs;
        Scale += (target - Scale) * (1 - Math.Exp(-Math.Max(0, dtMs) / tau));
        if (!active && Scale < 1.01) Scale = 1;
        return Scale;
    }
}
