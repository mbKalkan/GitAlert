using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GitAlert.Platform;

/// <summary>
/// Places the flyout beside the tray icon: in the nearest corner of the monitor's work area, so it
/// never covers the taskbar and always lands on the display the user clicked on.
/// </summary>
public static class FlyoutPositioner
{
    private const double Margin = 12d;

    public static void PositionNear(Window window, Point screenPoint)
    {
        var work = WorkAreaFor(screenPoint);
        var scale = ScaleFor(window);

        // Work area comes back in physical pixels; WPF positions windows in DIPs.
        var left = work.Left / scale.X;
        var top = work.Top / scale.Y;
        var right = work.Right / scale.X;
        var bottom = work.Bottom / scale.Y;

        var width = window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        var anchorX = screenPoint.X / scale.X;
        var anchorY = screenPoint.Y / scale.Y;

        // Snap to the horizontal edge the tray icon sits closest to.
        var x = anchorX - (right + left) / 2 >= 0
            ? right - width - Margin
            : left + Margin;

        // The taskbar is usually at the bottom, but honour a top or side taskbar too.
        var y = anchorY - (bottom + top) / 2 >= 0
            ? bottom - height - Margin
            : top + Margin;

        window.Left = Math.Clamp(x, left + Margin, Math.Max(left + Margin, right - width - Margin));
        window.Top = Math.Clamp(y, top + Margin, Math.Max(top + Margin, bottom - height - Margin));
    }

    private static NativeMethods.RECT WorkAreaFor(Point screenPoint)
    {
        var point = new NativeMethods.POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        // Fall back to the primary monitor's work area, converted back to physical pixels.
        var fallback = SystemParameters.WorkArea;
        return new NativeMethods.RECT
        {
            Left = (int)fallback.Left,
            Top = (int)fallback.Top,
            Right = (int)fallback.Right,
            Bottom = (int)fallback.Bottom,
        };
    }

    private static Vector ScaleFor(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource { CompositionTarget: { } target })
        {
            var matrix = target.TransformToDevice;
            return new Vector(matrix.M11, matrix.M22);
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        return new Vector(dpi.DpiScaleX, dpi.DpiScaleY);
    }
}
