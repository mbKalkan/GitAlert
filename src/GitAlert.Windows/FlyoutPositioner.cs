using System.Runtime.InteropServices;
using GitAlert.Core;

namespace GitAlert.Platform;

/// <summary>
/// Places the flyout beside the tray icon: in the nearest corner of the monitor's work area, so it
/// never covers the taskbar and always lands on the display the user clicked on.
/// </summary>
public static class FlyoutPositioner
{
    private const double Margin = 12d;

    /// <summary>
    /// Where a window of the given size belongs, in the front end's device-independent units.
    /// The work area comes from Windows in physical pixels, hence the scale.
    /// </summary>
    public static (double Left, double Top) Place(ScreenPoint anchor, double width, double height, double scaleX, double scaleY)
    {
        var work = WorkAreaFor(anchor);

        var left = work.Left / scaleX;
        var top = work.Top / scaleY;
        var right = work.Right / scaleX;
        var bottom = work.Bottom / scaleY;

        var anchorX = anchor.X / scaleX;
        var anchorY = anchor.Y / scaleY;

        // Snap to the horizontal edge the tray icon sits closest to.
        var x = anchorX - (right + left) / 2 >= 0
            ? right - width - Margin
            : left + Margin;

        // The taskbar is usually at the bottom, but honour a top or side taskbar too.
        var y = anchorY - (bottom + top) / 2 >= 0
            ? bottom - height - Margin
            : top + Margin;

        return (
            Math.Clamp(x, left + Margin, Math.Max(left + Margin, right - width - Margin)),
            Math.Clamp(y, top + Margin, Math.Max(top + Margin, bottom - height - Margin)));
    }

    /// <summary>The work area of the monitor under a point, in physical pixels.</summary>
    public static NativeMethods.RECT WorkAreaFor(ScreenPoint screenPoint)
    {
        var point = new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        // Fall back to the primary monitor's work area.
        var fallback = default(NativeMethods.RECT);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref fallback, 0);
        return fallback;
    }
}
