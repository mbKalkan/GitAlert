using GitAlert.Core;

namespace GitAlert.ViewModels;

/// <summary>
/// The 16 x 16 vector glyph for each <see cref="AlertKind"/>, as path data, and the palette key its
/// accent colour is read from. Nothing here knows how a path is drawn: the view parses and caches it,
/// which is what lets the same view models sit under WPF today and Avalonia tomorrow.
/// </summary>
public static class AlertGlyphs
{
    private static readonly Dictionary<AlertKind, string> Paths = new()
    {
        // Arrow rising out of a baseline.
        [AlertKind.Push] = "M8,1.6 L13.2,6.9 L10,6.9 L10,11 L6,11 L6,6.9 L2.8,6.9 Z M3,12.6 L13,12.6 L13,14.4 L3,14.4 Z",

        // Two branches merging into an arrow head.
        [AlertKind.PullRequest] =
            "M4.2,2 A1.9,1.9 0 1 1 4.2,5.8 A1.9,1.9 0 1 1 4.2,2 Z " +
            "M3.3,5.6 L5.1,5.6 L5.1,10.4 L3.3,10.4 Z " +
            "M4.2,10.2 A1.9,1.9 0 1 1 4.2,14 A1.9,1.9 0 1 1 4.2,10.2 Z " +
            "M11.8,2 A1.9,1.9 0 1 1 11.8,5.8 A1.9,1.9 0 1 1 11.8,2 Z " +
            "M10.9,5.6 L12.7,5.6 L12.7,10.6 L10.9,10.6 Z " +
            "M11.8,14.6 L9,10.8 L14.6,10.8 Z",

        // Ringed dot, the issue marker.
        [AlertKind.Issue] =
            "M8,1.4 A6.6,6.6 0 1 0 8,14.6 A6.6,6.6 0 1 0 8,1.4 Z " +
            "M8,3.2 A4.8,4.8 0 1 1 8,12.8 A4.8,4.8 0 1 1 8,3.2 Z " +
            "M8,5.7 A2.3,2.3 0 1 0 8,10.3 A2.3,2.3 0 1 0 8,5.7 Z",

        // Speech bubble with a tail.
        [AlertKind.Comment] =
            "M2.4,2.6 L13.6,2.6 A1,1 0 0 1 14.6,3.6 L14.6,10.4 A1,1 0 0 1 13.6,11.4 " +
            "L7.4,11.4 L4.2,14.2 L4.2,11.4 L2.4,11.4 A1,1 0 0 1 1.4,10.4 L1.4,3.6 A1,1 0 0 1 2.4,2.6 Z",

        // Check mark.
        [AlertKind.Review] = "M2.2,8.4 L3.9,6.6 L6.5,9.2 L12.1,3.4 L13.8,5.2 L6.5,12.7 Z",

        // Tag with an eyelet.
        [AlertKind.Release] =
            "M7.6,1.6 L14.4,8.4 A1.2,1.2 0 0 1 14.4,10.1 L10.1,14.4 A1.2,1.2 0 0 1 8.4,14.4 " +
            "L1.6,7.6 L1.6,2.8 A1.2,1.2 0 0 1 2.8,1.6 Z " +
            "M4.7,3.6 A1.3,1.3 0 1 0 4.7,6.2 A1.3,1.3 0 1 0 4.7,3.6 Z",

        // A branch splitting away from the trunk.
        [AlertKind.Branch] =
            "M4,1.6 A2,2 0 1 1 4,5.6 A2,2 0 1 1 4,1.6 Z " +
            "M3.1,5.4 L4.9,5.4 L4.9,10.6 L3.1,10.6 Z " +
            "M4,10.4 A2,2 0 1 1 4,14.4 A2,2 0 1 1 4,10.4 Z " +
            "M12,1.6 A2,2 0 1 1 12,5.6 A2,2 0 1 1 12,1.6 Z " +
            "M11.1,5.4 L12.9,5.4 L12.9,7 A3.6,3.6 0 0 1 9.3,10.6 L4.6,10.6 L4.6,8.8 L9.3,8.8 " +
            "A1.8,1.8 0 0 0 11.1,7 Z",

        // Play button inside a ring: a workflow run.
        [AlertKind.Workflow] =
            "M8,1.4 A6.6,6.6 0 1 0 8,14.6 A6.6,6.6 0 1 0 8,1.4 Z " +
            "M8,3.2 A4.8,4.8 0 1 1 8,12.8 A4.8,4.8 0 1 1 8,3.2 Z " +
            "M6.6,5.4 L11,8 L6.6,10.6 Z",

        // Five-pointed star.
        [AlertKind.Star] = "M8,1.3 L10.1,5.7 L14.9,6.4 L11.4,9.8 L12.3,14.6 L8,12.3 L3.7,14.6 L4.6,9.8 L1.1,6.4 L5.9,5.7 Z",

        // Fork: one node splitting into two.
        [AlertKind.Fork] =
            "M8,1.6 A2,2 0 1 1 8,5.6 A2,2 0 1 1 8,1.6 Z " +
            "M7.1,5.4 L8.9,5.4 L8.9,7.4 L7.1,7.4 Z " +
            "M3.6,7.4 L12.4,7.4 L12.4,9.2 L3.6,9.2 Z " +
            "M2.7,8.6 L4.5,8.6 L4.5,10.6 L2.7,10.6 Z " +
            "M11.5,8.6 L13.3,8.6 L13.3,10.6 L11.5,10.6 Z " +
            "M3.6,10.4 A2,2 0 1 1 3.6,14.4 A2,2 0 1 1 3.6,10.4 Z " +
            "M12.4,10.4 A2,2 0 1 1 12.4,14.4 A2,2 0 1 1 12.4,10.4 Z",

        // The at sign.
        [AlertKind.Mention] =
            "M8,1.3 A6.7,6.7 0 1 0 11.4,13.8 L10.5,12.3 A5,5 0 1 1 13,8 L13,8.9 " +
            "A1,1 0 0 1 11.2,9.5 L11.2,4.9 L9.5,4.9 L9.5,5.5 A3.2,3.2 0 1 0 9.7,10.8 " +
            "A2.6,2.6 0 0 0 14.7,9 L14.7,8 A6.7,6.7 0 0 0 8,1.3 Z " +
            "M8,6.4 A1.6,1.6 0 1 1 8,9.6 A1.6,1.6 0 1 1 8,6.4 Z",

        [AlertKind.Other] = "M8,4.4 A3.6,3.6 0 1 0 8,11.6 A3.6,3.6 0 1 0 8,4.4 Z",
    };

    /// <summary>
    /// Colours for a process with no palette loaded, such as the tests, keyed the way the palettes
    /// key their brushes. Chosen to stay legible on both the light and the dark surface.
    /// </summary>
    private static readonly Dictionary<string, string> FallbackColours = new(StringComparer.Ordinal)
    {
        ["KindPush"] = "#4C8DF6",
        ["KindPullRequest"] = "#A371F7",
        ["KindIssue"] = "#34A853",
        ["KindComment"] = "#8993A1",
        ["KindReview"] = "#34A853",
        ["KindRelease"] = "#DB6D28",
        ["KindBranch"] = "#589CF0",
        ["KindWorkflow"] = "#8993A1",
        ["KindStar"] = "#D4A022",
        ["KindFork"] = "#8993A1",
        ["KindMention"] = "#E066A6",
        ["KindOther"] = "#8993A1",
        ["SeveritySuccess"] = "#34A853",
        ["SeverityWarning"] = "#C7931F",
        ["SeverityError"] = "#E5534B",
    };

    public static string PathFor(AlertKind kind) =>
        Paths.TryGetValue(kind, out var path) ? path : Paths[AlertKind.Other];

    /// <summary>
    /// The palette key for a card's accent: the severity's when the event carries one, which is
    /// what makes a failed CI run read as red at a glance, and the kind's otherwise.
    /// </summary>
    public static string AccentKeyFor(AlertKind kind, AlertSeverity severity) =>
        severity switch
        {
            AlertSeverity.Success => "SeveritySuccess",
            AlertSeverity.Warning => "SeverityWarning",
            AlertSeverity.Error => "SeverityError",
            _ => "Kind" + kind,
        };

    /// <summary>The colour behind a palette key when no palette is loaded, as <c>#RRGGBB</c>.</summary>
    public static string FallbackColourFor(string key) =>
        FallbackColours.TryGetValue(key, out var colour) ? colour : FallbackColours["KindOther"];
}
