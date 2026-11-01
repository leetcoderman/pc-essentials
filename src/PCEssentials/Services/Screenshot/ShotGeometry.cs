namespace PCEssentials.Services.Screenshot;

/// A rectangle in physical screen pixels (virtual-screen coordinates, so it can be negative).
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// The rectangle spanned by two corners, in either order. Both corners are inside it.
    public static PxRect FromCorners(int x1, int y1, int x2, int y2)
    {
        var (l, r) = x1 <= x2 ? (x1, x2) : (x2, x1);
        var (t, b) = y1 <= y2 ? (y1, y2) : (y2, y1);
        return new PxRect(l, t, r - l + 1, b - t + 1);
    }

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PxRect Intersect(PxRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? default : new PxRect(l, t, r - l, b - t);
    }
}

/// What part of the selection the pointer is over.
public enum ShotHandle { None, Inside, N, S, E, W, NE, NW, SE, SW }

/// Selection maths for the capture overlay: hit testing, moving and resizing by handles, and where
/// Lightshot's two toolbars go. Everything is in physical pixels.
public static class ShotGeometry
{
    /// Which handle (or the inside) is under the point. Handles sit on the edges and corners and
    /// reach <paramref name="grip"/> pixels either side of them.
    public static ShotHandle HitTest(PxRect sel, double x, double y, double grip)
    {
        if (sel.IsEmpty) return ShotHandle.None;
        double l = sel.X, t = sel.Y, r = sel.Right, b = sel.Bottom;
        if (x < l - grip || x > r + grip || y < t - grip || y > b + grip) return ShotHandle.None;
        bool nearL = Math.Abs(x - l) <= grip, nearR = Math.Abs(x - r) <= grip;
        bool nearT = Math.Abs(y - t) <= grip, nearB = Math.Abs(y - b) <= grip;
        if (nearT && nearL) return ShotHandle.NW;
        if (nearT && nearR) return ShotHandle.NE;
        if (nearB && nearL) return ShotHandle.SW;
        if (nearB && nearR) return ShotHandle.SE;
        if (nearT) return ShotHandle.N;
        if (nearB) return ShotHandle.S;
        if (nearL) return ShotHandle.W;
        if (nearR) return ShotHandle.E;
        return sel.Contains(x, y) ? ShotHandle.Inside : ShotHandle.None;
    }

    /// The selection after dragging <paramref name="handle"/> by (dx, dy) from <paramref name="start"/>.
    /// Dragging an edge past the opposite one flips the selection, as Lightshot does. Inside moves the
    /// whole selection, kept within <paramref name="bounds"/>.
    public static PxRect Drag(PxRect start, ShotHandle handle, int dx, int dy, PxRect bounds)
    {
        if (handle == ShotHandle.Inside) return Move(start, dx, dy, bounds);
        int l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        if (handle is ShotHandle.W or ShotHandle.NW or ShotHandle.SW) l += dx;
        if (handle is ShotHandle.E or ShotHandle.NE or ShotHandle.SE) r += dx;
        if (handle is ShotHandle.N or ShotHandle.NW or ShotHandle.NE) t += dy;
        if (handle is ShotHandle.S or ShotHandle.SW or ShotHandle.SE) b += dy;
        if (l > r) (l, r) = (r, l);
        if (t > b) (t, b) = (b, t);
        l = Math.Max(l, bounds.X);
        t = Math.Max(t, bounds.Y);
        r = Math.Min(r, bounds.Right);
        b = Math.Min(b, bounds.Bottom);
        return new PxRect(l, t, Math.Max(1, r - l), Math.Max(1, b - t));
    }

    /// Moves the selection by (dx, dy) without leaving <paramref name="bounds"/>.
    public static PxRect Move(PxRect sel, int dx, int dy, PxRect bounds)
    {
        var x = Math.Clamp(sel.X + dx, bounds.X, Math.Max(bounds.X, bounds.Right - sel.Width));
        var y = Math.Clamp(sel.Y + dy, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - sel.Height));
        return sel with { X = x, Y = y };
    }

    /// Where Lightshot puts its toolbars: the tools run down the right side of the selection, bottom
    /// aligned, and the actions run along its bottom edge, right aligned. A toolbar that doesn't fit
    /// outside on the monitor goes to the opposite side, and inside the selection if neither fits.
    /// Returns the top-left corners.
    public static ((int X, int Y) Tools, (int X, int Y) Actions) PlaceToolbars(
        PxRect sel, PxRect monitor, (int W, int H) tools, (int W, int H) actions, int gap)
    {
        // Actions bar: below, else above, else inside the bottom edge.
        int ay;
        if (sel.Bottom + gap + actions.H <= monitor.Bottom) ay = sel.Bottom + gap;
        else if (sel.Y - gap - actions.H >= monitor.Y) ay = sel.Y - gap - actions.H;
        else ay = sel.Bottom - gap - actions.H;
        var ax = Math.Clamp(sel.Right - actions.W, monitor.X, Math.Max(monitor.X, monitor.Right - actions.W));

        // Tools bar: right, else left, else inside the right edge; bottom aligned with the selection.
        int tx;
        if (sel.Right + gap + tools.W <= monitor.Right) tx = sel.Right + gap;
        else if (sel.X - gap - tools.W >= monitor.X) tx = sel.X - gap - tools.W;
        else tx = sel.Right - gap - tools.W;
        var ty = Math.Clamp(sel.Bottom - tools.H, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - tools.H));
        return ((tx, ty), (ax, ay));
    }

    /// Where the W×H label goes: just above the top-left corner, or inside it when that is off the monitor.
    public static (int X, int Y) PlaceSizeLabel(PxRect sel, PxRect monitor, (int W, int H) label, int gap)
    {
        var y = sel.Y - gap - label.H >= monitor.Y ? sel.Y - gap - label.H : sel.Y + gap;
        var x = Math.Clamp(sel.X, monitor.X, Math.Max(monitor.X, monitor.Right - label.W));
        return (x, y);
    }
}
