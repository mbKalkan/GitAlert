namespace GitAlert.Core;

/// <summary>
/// GitAlert's mark as path data, authored once in a 24 x 24 space. Every front end draws it with
/// its own geometry type: the tray icon at whatever size the shell asks for, the header of the
/// flyout, and the application icon.
/// </summary>
public static class BellArtwork
{
    /// <summary>The artwork is authored in this square and scaled at draw time.</summary>
    public const double DesignSize = 24d;

    public const string Body =
        "M12,3.2 C8.3,3.2 5.4,6.1 5.4,9.8 L5.4,14.6 L3.6,17.2 " +
        "C3.25,17.7 3.6,18.35 4.2,18.35 L19.8,18.35 " +
        "C20.4,18.35 20.75,17.7 20.4,17.2 L18.6,14.6 L18.6,9.8 " +
        "C18.6,6.1 15.7,3.2 12,3.2 Z";

    public const string Clapper = "M9.5,19.6 L14.5,19.6 C14.5,20.98 13.38,22.1 12,22.1 C10.62,22.1 9.5,20.98 9.5,19.6 Z";

    /// <summary>The knob on top of the bell is a circle: centre and radius.</summary>
    public const double KnobX = 12;
    public const double KnobY = 2.6;
    public const double KnobRadius = 1.5;

    /// <summary>The unread badge sits over the bell's shoulder.</summary>
    public const double BadgeX = 18.1;
    public const double BadgeY = 5.9;
    public const double BadgeRadius = 3.3;

    /// <summary>A ring this wide is cut out of the bell so the badge reads clearly even at 16 px.</summary>
    public const double BadgeCutoutRadius = 4.5;

    /// <summary>The application icon: bell colour, badge colour and the tile's gradient.</summary>
    public static readonly Rgb AppBell = new(0xF0, 0xF6, 0xFC);
    public static readonly Rgb AppBadge = new(0x3F, 0xB9, 0x50);
    public static readonly Rgb AppTileTop = new(0x24, 0x2B, 0x35);
    public static readonly Rgb AppTileBottom = new(0x14, 0x18, 0x1E);
}
