using Avalonia;
using Avalonia.Media;
using GitAlert.Core;
using SkiaSharp;

namespace GitAlert.Graphics;

/// <summary>
/// <see cref="BellArtwork"/> drawn two ways: as Avalonia geometry for the windows, and through
/// Skia straight to pixels for the tray icon and the application icon, where no visual tree exists.
/// </summary>
public static class Bell
{
    /// <summary>The bell in its 24 x 24 design space, for the flyout header and the empty state.</summary>
    public static Geometry Geometry { get; } = BuildGeometry();

    private static Geometry BuildGeometry()
    {
        var knob = BellArtwork.KnobRadius;

        var group = new GeometryGroup { FillRule = FillRule.NonZero };
        group.Children.Add(StreamGeometry.Parse(BellArtwork.Body));
        group.Children.Add(new EllipseGeometry(new Rect(BellArtwork.KnobX - knob, BellArtwork.KnobY - knob, knob * 2, knob * 2)));
        group.Children.Add(StreamGeometry.Parse(BellArtwork.Clapper));
        return group;
    }

    /// <summary>
    /// The notification-area icon: a flat silhouette in the taskbar's contrast colour, with an
    /// optional badge for unread alerts. Premultiplied BGRA, top-down, as the shell wants it.
    /// </summary>
    public static byte[] RenderTrayIcon(int size, Rgb foreground, Rgb? badge)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / (float)BellArtwork.DesignSize);

        DrawBell(canvas, foreground, badge);

        return bitmap.Bytes;
    }

    /// <summary>
    /// The application icon: the same bell on a rounded, dark tile so it stands out in the Start
    /// menu, the taskbar and the installer.
    /// </summary>
    public static byte[] RenderAppIcon(int size)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);

        using var tile = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(size, size),
                [ToSkia(BellArtwork.AppTileTop), ToSkia(BellArtwork.AppTileBottom)],
                SKShaderTileMode.Clamp),
        };

        var radius = size * 0.22f;
        canvas.DrawRoundRect(new SKRect(0, 0, size, size), radius, radius, tile);

        // Slightly inset so the bell does not touch the tile edge.
        canvas.Scale(size / (float)BellArtwork.DesignSize);
        canvas.Translate(0, 0.4f);
        canvas.Scale(0.78f, 0.78f, 12, 12);

        DrawBell(canvas, BellArtwork.AppBell, BellArtwork.AppBadge);

        return bitmap.Bytes;
    }

    /// <summary>Draws the bell in design units; a badge, when there is one, sits in a ring cut out of it.</summary>
    private static void DrawBell(SKCanvas canvas, Rgb foreground, Rgb? badge)
    {
        using var path = BuildPath();
        using var paint = new SKPaint { IsAntialias = true, Color = ToSkia(foreground) };

        canvas.DrawPath(path, paint);

        if (badge is not { } colour)
        {
            return;
        }

        var x = (float)BellArtwork.BadgeX;
        var y = (float)BellArtwork.BadgeY;

        using var cutout = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
        canvas.DrawCircle(x, y, (float)BellArtwork.BadgeCutoutRadius, cutout);

        using var badgePaint = new SKPaint { IsAntialias = true, Color = ToSkia(colour) };
        canvas.DrawCircle(x, y, (float)BellArtwork.BadgeRadius, badgePaint);
    }

    private static SKPath BuildPath()
    {
        var path = SKPath.ParseSvgPathData(BellArtwork.Body) ?? new SKPath();
        path.FillType = SKPathFillType.Winding;
        path.AddCircle((float)BellArtwork.KnobX, (float)BellArtwork.KnobY, (float)BellArtwork.KnobRadius);

        using var clapper = SKPath.ParseSvgPathData(BellArtwork.Clapper);
        if (clapper is not null)
        {
            path.AddPath(clapper);
        }

        return path;
    }

    private static SKColor ToSkia(Rgb rgb) => new(rgb.R, rgb.G, rgb.B);
}
