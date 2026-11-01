using System.Windows;

namespace PCEssentials.UI;

public sealed partial class SettingsWindow
{
    // ---------------- Mouse ----------------

    private UIElement MouseTab()
    {
        var p = Panel();
        var m = _s.Mouse;
        p.Children.Add(Heading("Shake to find"));
        p.Children.Add(Check("Shake the pointer to make it bigger", m.ShakeToFind, v => { m.ShakeToFind = v; Changed(); }));
        p.Children.Add(Note("Move the mouse or swipe on the touchpad quickly left and right and the pointer grows so you can spot it, " +
                            "then shrinks back once you stop, like on a Mac. Dragging and clicking never trigger it. " +
                            "Your pointer size in Windows Settings is left as it is."));
        p.Children.Add(Labeled("Largest size (× normal)", NumberSlider(2, 6, m.ShakeMaxScale, 0.5, v => { m.ShakeMaxScale = v; Changed(); })));
        p.Children.Add(Labeled("Sensitivity", Choice(m.ShakeSensitivity,
            [("low", "Low (needs a long, fast shake)"), ("normal", "Normal"), ("high", "High (a short shake is enough)")], v => m.ShakeSensitivity = v)));
        return p;
    }
}
