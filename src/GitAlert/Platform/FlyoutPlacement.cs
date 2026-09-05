using Avalonia;
using GitAlert.Core;

namespace GitAlert.Platform;

/// <summary>
/// Where the flyout belongs beside a tray anchor on a screen: in the corner of the work area the
/// anchor is nearest to, a margin in from the edges, and never over the panel that holds the icon.
/// The same rule the Windows layer applies, written against Avalonia's screen geometry so macOS and
/// Linux share it.
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>In physical pixels, like everything Avalonia positions windows with.</summary>
    private const int Margin = 12;

    /// <summary>
    /// Snaps to the horizontal edge the anchor sits closest to, and to the top or the bottom the
    /// same way - or always to the top when the platform keeps its status items in a bar up there,
    /// whatever point was handed in.
    /// </summary>
    public static PixelPoint Beside(ScreenPoint anchor, PixelSize size, PixelRect work, bool alwaysTop = false)
    {
        var x = anchor.X - (work.X + work.Right) / 2 >= 0
            ? work.Right - size.Width - Margin
            : work.X + Margin;

        var y = alwaysTop || anchor.Y - (work.Y + work.Bottom) / 2 < 0
            ? work.Y + Margin
            : work.Bottom - size.Height - Margin;

        return new PixelPoint(
            Math.Clamp(x, work.X + Margin, Math.Max(work.X + Margin, work.Right - size.Width - Margin)),
            Math.Clamp(y, work.Y + Margin, Math.Max(work.Y + Margin, work.Bottom - size.Height - Margin)));
    }

    /// <summary>
    /// The corner of a work area a status item lives in when the platform cannot say where it was
    /// clicked: the top when a panel sits along the top of the screen, the bottom otherwise.
    /// </summary>
    public static ScreenPoint CornerOf(PixelRect work, PixelRect bounds) =>
        work.Y > bounds.Y
            ? new ScreenPoint(work.Right - 1, work.Y)
            : new ScreenPoint(work.Right - 1, work.Bottom - 1);
}
