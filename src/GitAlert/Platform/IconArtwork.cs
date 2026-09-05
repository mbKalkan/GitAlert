using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitAlert.Core;

namespace GitAlert.Platform;

/// <summary>
/// GitAlert's mark, defined once as vector geometry and reused everywhere: the tray icon at
/// whatever size the shell asks for, and the application icon written into <c>app.ico</c>.
/// Drawing rather than shipping bitmaps keeps every size crisp on high-DPI displays.
/// </summary>
public static class IconArtwork
{
    /// <summary>The artwork is authored in a 24 x 24 space and scaled at draw time.</summary>
    private const double DesignSize = 24d;

    private static readonly Geometry BellBody = Geometry.Parse(
        "M12,3.2 C8.3,3.2 5.4,6.1 5.4,9.8 L5.4,14.6 L3.6,17.2 " +
        "C3.25,17.7 3.6,18.35 4.2,18.35 L19.8,18.35 " +
        "C20.4,18.35 20.75,17.7 20.4,17.2 L18.6,14.6 L18.6,9.8 " +
        "C18.6,6.1 15.7,3.2 12,3.2 Z");

    private static readonly Geometry BellKnob = new EllipseGeometry(new Point(12, 2.6), 1.5, 1.5);

    private static readonly Geometry BellClapper = Geometry.Parse(
        "M9.5,19.6 L14.5,19.6 C14.5,20.98 13.38,22.1 12,22.1 C10.62,22.1 9.5,20.98 9.5,19.6 Z");

    private static readonly Point BadgeCentre = new(18.1, 5.9);

    private static readonly Geometry Bell = BuildBell(punchBadge: false);

    private static readonly Geometry BellWithBadgeCutout = BuildBell(punchBadge: true);

    private static Geometry BuildBell(bool punchBadge)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(BellBody);
        group.Children.Add(BellKnob);
        group.Children.Add(BellClapper);

        Geometry bell = group;

        if (punchBadge)
        {
            // Cut a ring out of the bell so the badge reads clearly even at 16 px.
            var ring = new EllipseGeometry(BadgeCentre, 4.5, 4.5);
            bell = Geometry.Combine(bell, ring, GeometryCombineMode.Exclude, null);
        }

        bell.Freeze();
        return bell;
    }

    /// <summary>
    /// The bell on its own, in the 24 x 24 design space, so XAML can reuse the mark for the
    /// flyout header and the settings window without a second copy of the artwork.
    /// </summary>
    public static Geometry BellGeometry => Bell;

    /// <summary>
    /// The notification-area icon: a flat silhouette that inherits the taskbar's contrast colour,
    /// with an optional badge for unread alerts.
    /// </summary>
    public static void DrawTrayIcon(DrawingContext context, double size, Color foreground, Color? badge)
    {
        var scale = size / DesignSize;
        context.PushTransform(new ScaleTransform(scale, scale));

        var brush = new SolidColorBrush(foreground);
        brush.Freeze();

        context.DrawGeometry(brush, null, badge is null ? Bell : BellWithBadgeCutout);

        if (badge is { } colour)
        {
            var badgeBrush = new SolidColorBrush(colour);
            badgeBrush.Freeze();
            context.DrawEllipse(badgeBrush, null, BadgeCentre, 3.3, 3.3);
        }

        context.Pop();
    }

    /// <summary>
    /// The application icon: the same bell on a rounded, dark tile so it stands out in the Start
    /// menu, the taskbar and the installer.
    /// </summary>
    public static void DrawAppIcon(DrawingContext context, double size)
    {
        var scale = size / DesignSize;

        var background = new LinearGradientBrush(
            Color.FromRgb(0x24, 0x2B, 0x35),
            Color.FromRgb(0x14, 0x18, 0x1E),
            new Point(0, 0),
            new Point(1, 1));
        background.Freeze();

        // The tile bleeds slightly past the design box so the corners are not clipped.
        var radius = size * 0.22;
        context.DrawRoundedRectangle(background, null, new Rect(0, 0, size, size), radius, radius);

        context.PushTransform(new ScaleTransform(scale, scale));
        context.PushTransform(new TranslateTransform(0, 0.4));

        // Slightly inset so the bell does not touch the tile edge.
        context.PushTransform(new ScaleTransform(0.78, 0.78, 12, 12));

        var bellBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF6, 0xFC));
        bellBrush.Freeze();
        context.DrawGeometry(bellBrush, null, BellWithBadgeCutout);

        var badgeBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        badgeBrush.Freeze();
        context.DrawEllipse(badgeBrush, null, BadgeCentre, 3.3, 3.3);

        context.Pop();
        context.Pop();
        context.Pop();
    }

    // ---- Pixels for the shell ------------------------------------------------

    /// <summary>The tray icon as premultiplied BGRA pixels, the shape <see cref="IconFactory"/> takes.</summary>
    public static byte[] RenderTrayIcon(int size, Rgb foreground, Rgb? badge) =>
        Render(size, context => DrawTrayIcon(context, size, ToColor(foreground), badge is { } b ? ToColor(b) : null));

    /// <summary>The application icon at one size, for the frames of <c>app.ico</c>.</summary>
    public static byte[] RenderAppIcon(int size) => Render(size, context => DrawAppIcon(context, size));

    private static Color ToColor(Rgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);

    /// <summary>Renders the artwork to premultiplied BGRA pixels, top-down. Needs an STA thread.</summary>
    private static byte[] Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            draw(context);
        }

        // 96 DPI keeps one design unit equal to one device pixel, so `size` is exact.
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        target.CopyPixels(pixels, stride, 0);

        return pixels;
    }
}
