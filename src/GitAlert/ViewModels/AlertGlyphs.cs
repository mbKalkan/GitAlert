using System.Windows;
using System.Windows.Media;
using GitAlert.Core;

namespace GitAlert.ViewModels;

/// <summary>
/// The 16 x 16 vector glyph and accent colour for each <see cref="AlertKind"/>. Colours are chosen
/// to stay legible on both the light and the dark surface, so the flyout needs no per-theme icon set.
/// </summary>
public static class AlertGlyphs
{
    private static readonly Dictionary<AlertKind, Geometry> Geometries = new()
    {
        // Arrow rising out of a baseline.
        [AlertKind.Push] = Freeze("M8,1.6 L13.2,6.9 L10,6.9 L10,11 L6,11 L6,6.9 L2.8,6.9 Z M3,12.6 L13,12.6 L13,14.4 L3,14.4 Z"),

        // Two branches merging into an arrow head.
        [AlertKind.PullRequest] = Freeze(
            "M4.2,2 A1.9,1.9 0 1 1 4.2,5.8 A1.9,1.9 0 1 1 4.2,2 Z " +
            "M3.3,5.6 L5.1,5.6 L5.1,10.4 L3.3,10.4 Z " +
            "M4.2,10.2 A1.9,1.9 0 1 1 4.2,14 A1.9,1.9 0 1 1 4.2,10.2 Z " +
            "M11.8,2 A1.9,1.9 0 1 1 11.8,5.8 A1.9,1.9 0 1 1 11.8,2 Z " +
            "M10.9,5.6 L12.7,5.6 L12.7,10.6 L10.9,10.6 Z " +
            "M11.8,14.6 L9,10.8 L14.6,10.8 Z"),

        // Ringed dot, the issue marker.
        [AlertKind.Issue] = Freeze(
            "M8,1.4 A6.6,6.6 0 1 0 8,14.6 A6.6,6.6 0 1 0 8,1.4 Z " +
            "M8,3.2 A4.8,4.8 0 1 1 8,12.8 A4.8,4.8 0 1 1 8,3.2 Z " +
            "M8,5.7 A2.3,2.3 0 1 0 8,10.3 A2.3,2.3 0 1 0 8,5.7 Z"),

        // Speech bubble with a tail.
        [AlertKind.Comment] = Freeze(
            "M2.4,2.6 L13.6,2.6 A1,1 0 0 1 14.6,3.6 L14.6,10.4 A1,1 0 0 1 13.6,11.4 " +
            "L7.4,11.4 L4.2,14.2 L4.2,11.4 L2.4,11.4 A1,1 0 0 1 1.4,10.4 L1.4,3.6 A1,1 0 0 1 2.4,2.6 Z"),

        // Check mark.
        [AlertKind.Review] = Freeze("M2.2,8.4 L3.9,6.6 L6.5,9.2 L12.1,3.4 L13.8,5.2 L6.5,12.7 Z"),

        // Tag with an eyelet.
        [AlertKind.Release] = Freeze(
            "M7.6,1.6 L14.4,8.4 A1.2,1.2 0 0 1 14.4,10.1 L10.1,14.4 A1.2,1.2 0 0 1 8.4,14.4 " +
            "L1.6,7.6 L1.6,2.8 A1.2,1.2 0 0 1 2.8,1.6 Z " +
            "M4.7,3.6 A1.3,1.3 0 1 0 4.7,6.2 A1.3,1.3 0 1 0 4.7,3.6 Z"),

        // A branch splitting away from the trunk.
        [AlertKind.Branch] = Freeze(
            "M4,1.6 A2,2 0 1 1 4,5.6 A2,2 0 1 1 4,1.6 Z " +
            "M3.1,5.4 L4.9,5.4 L4.9,10.6 L3.1,10.6 Z " +
            "M4,10.4 A2,2 0 1 1 4,14.4 A2,2 0 1 1 4,10.4 Z " +
            "M12,1.6 A2,2 0 1 1 12,5.6 A2,2 0 1 1 12,1.6 Z " +
            "M11.1,5.4 L12.9,5.4 L12.9,7 A3.6,3.6 0 0 1 9.3,10.6 L4.6,10.6 L4.6,8.8 L9.3,8.8 " +
            "A1.8,1.8 0 0 0 11.1,7 Z"),

        // Play button inside a ring: a workflow run.
        [AlertKind.Workflow] = Freeze(
            "M8,1.4 A6.6,6.6 0 1 0 8,14.6 A6.6,6.6 0 1 0 8,1.4 Z " +
            "M8,3.2 A4.8,4.8 0 1 1 8,12.8 A4.8,4.8 0 1 1 8,3.2 Z " +
            "M6.6,5.4 L11,8 L6.6,10.6 Z"),

        // Five-pointed star.
        [AlertKind.Star] = Freeze("M8,1.3 L10.1,5.7 L14.9,6.4 L11.4,9.8 L12.3,14.6 L8,12.3 L3.7,14.6 L4.6,9.8 L1.1,6.4 L5.9,5.7 Z"),

        // Fork: one node splitting into two.
        [AlertKind.Fork] = Freeze(
            "M8,1.6 A2,2 0 1 1 8,5.6 A2,2 0 1 1 8,1.6 Z " +
            "M7.1,5.4 L8.9,5.4 L8.9,7.4 L7.1,7.4 Z " +
            "M3.6,7.4 L12.4,7.4 L12.4,9.2 L3.6,9.2 Z " +
            "M2.7,8.6 L4.5,8.6 L4.5,10.6 L2.7,10.6 Z " +
            "M11.5,8.6 L13.3,8.6 L13.3,10.6 L11.5,10.6 Z " +
            "M3.6,10.4 A2,2 0 1 1 3.6,14.4 A2,2 0 1 1 3.6,10.4 Z " +
            "M12.4,10.4 A2,2 0 1 1 12.4,14.4 A2,2 0 1 1 12.4,10.4 Z"),

        // The at sign.
        [AlertKind.Mention] = Freeze(
            "M8,1.3 A6.7,6.7 0 1 0 11.4,13.8 L10.5,12.3 A5,5 0 1 1 13,8 L13,8.9 " +
            "A1,1 0 0 1 11.2,9.5 L11.2,4.9 L9.5,4.9 L9.5,5.5 A3.2,3.2 0 1 0 9.7,10.8 " +
            "A2.6,2.6 0 0 0 14.7,9 L14.7,8 A6.7,6.7 0 0 0 8,1.3 Z " +
            "M8,6.4 A1.6,1.6 0 1 1 8,9.6 A1.6,1.6 0 1 1 8,6.4 Z"),

        [AlertKind.Other] = Freeze("M8,4.4 A3.6,3.6 0 1 0 8,11.6 A3.6,3.6 0 1 0 8,4.4 Z"),
    };

    private static readonly Dictionary<AlertKind, Color> Accents = new()
    {
        [AlertKind.Push] = Color.FromRgb(0x4C, 0x8D, 0xF6),
        [AlertKind.PullRequest] = Color.FromRgb(0xA3, 0x71, 0xF7),
        [AlertKind.Issue] = Color.FromRgb(0x34, 0xA8, 0x53),
        [AlertKind.Comment] = Color.FromRgb(0x89, 0x93, 0xA1),
        [AlertKind.Review] = Color.FromRgb(0x34, 0xA8, 0x53),
        [AlertKind.Release] = Color.FromRgb(0xDB, 0x6D, 0x28),
        [AlertKind.Branch] = Color.FromRgb(0x58, 0x9C, 0xF0),
        [AlertKind.Workflow] = Color.FromRgb(0x89, 0x93, 0xA1),
        [AlertKind.Star] = Color.FromRgb(0xD4, 0xA0, 0x22),
        [AlertKind.Fork] = Color.FromRgb(0x89, 0x93, 0xA1),
        [AlertKind.Mention] = Color.FromRgb(0xE0, 0x66, 0xA6),
        [AlertKind.Other] = Color.FromRgb(0x89, 0x93, 0xA1),
    };

    private static readonly Dictionary<AlertSeverity, Color> SeverityAccents = new()
    {
        [AlertSeverity.Success] = Color.FromRgb(0x34, 0xA8, 0x53),
        [AlertSeverity.Warning] = Color.FromRgb(0xC7, 0x93, 0x1F),
        [AlertSeverity.Error] = Color.FromRgb(0xE5, 0x53, 0x4B),
    };

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = [];

    public static Geometry GlyphFor(AlertKind kind) =>
        Geometries.TryGetValue(kind, out var geometry) ? geometry : Geometries[AlertKind.Other];

    /// <summary>
    /// The accent for a card: severity wins when the event carries one, which is what makes a
    /// failed CI run read as red at a glance.
    /// </summary>
    /// <summary>The resource key a palette uses for this glyph: the severity's when it carries one, the kind's otherwise.</summary>
    private static string KeyFor(AlertKind kind, AlertSeverity severity) =>
        severity switch
        {
            AlertSeverity.Success => "SeveritySuccess",
            AlertSeverity.Warning => "SeverityWarning",
            AlertSeverity.Error => "SeverityError",
            _ => "Kind" + kind,
        };

    public static SolidColorBrush BrushFor(AlertKind kind, AlertSeverity severity)
    {
        // The palette decides, so the glyphs recolour with the theme. The table below is for a
        // process with no palette loaded, such as the tests.
        if (Application.Current?.TryFindResource(KeyFor(kind, severity)) is SolidColorBrush themed)
        {
            return themed;
        }

        var colour = SeverityAccents.TryGetValue(severity, out var bySeverity)
            ? bySeverity
            : Accents.TryGetValue(kind, out var byKind) ? byKind : Accents[AlertKind.Other];

        if (BrushCache.TryGetValue(colour, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        BrushCache[colour] = brush;
        return brush;
    }

    private static Geometry Freeze(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
}
