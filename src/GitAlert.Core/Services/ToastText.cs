using GitAlert.Core;

namespace GitAlert.Services;

/// <summary>
/// The words on a toast. A Windows balloon holds a title of 63 characters and 255 of text, so a
/// toast is written to fit: the repository gives way before the headline does, the first line of
/// what was said comes along when there is room, and a batch lists what arrived one line each
/// rather than naming repositories alone.
/// </summary>
public static class ToastText
{
    /// <summary>What <c>Shell_NotifyIcon</c> allows for <c>szInfoTitle</c>, without the terminator.</summary>
    public const int MaxTitleLength = 63;

    /// <summary>What <c>Shell_NotifyIcon</c> allows for <c>szInfo</c>, without the terminator.</summary>
    public const int MaxBodyLength = 255;

    /// <summary>How many alerts a batch names before it counts the rest.</summary>
    public const int MaxLines = 3;

    /// <param name="displayName">How a repository or board key reads to the user: a board by its title.</param>
    public static (string Title, string Body) Compose(IReadOnlyList<Alert> alerts, Func<string, string> displayName) =>
        alerts.Count == 1
            ? Single(alerts[0], displayName(alerts[0].Repository))
            : Batch(alerts, displayName);

    private static (string Title, string Body) Single(Alert alert, string source)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(alert.Detail))
        {
            lines.Add(alert.Detail.Trim());
        }

        // A comment's detail is its own first line already.
        if (PlainText.FirstLine(alert.Body) is { } said && !Repeats(said, alert.Detail))
        {
            lines.Add(said);
        }

        if (lines.Count == 0)
        {
            lines.Add(alert.Actor is not null ? $"by {alert.Actor}" : alert.Title);
        }

        return (FitTitle(source, alert.Title), Cut(string.Join('\n', lines), MaxBodyLength));
    }

    private static (string Title, string Body) Batch(IReadOnlyList<Alert> alerts, Func<string, string> displayName)
    {
        var shown = alerts.Take(MaxLines).ToList();
        var rest = alerts.Count - shown.Count;
        var tail = rest > 0 ? $"+{rest} more" : null;

        // Every line gets the same share of the room, after the tail and the line breaks take theirs.
        var room = MaxBodyLength - (tail is null ? 0 : tail.Length + 1) - (shown.Count - 1);
        var each = Math.Max(24, room / shown.Count);

        var lines = shown
            .Select(alert => Cut(Line(alert, displayName(alert.Repository)), each))
            .ToList();

        if (tail is not null)
        {
            lines.Add(tail);
        }

        return ($"{alerts.Count} new alerts", string.Join('\n', lines));
    }

    private static string Line(Alert alert, string source) =>
        string.IsNullOrWhiteSpace(alert.Detail)
            ? $"{source}: {alert.Title}"
            : $"{source}: {alert.Title} · {alert.Detail.Trim()}";

    /// <summary>
    /// "source - headline" when it fits, the repository without its owner when that is what it
    /// takes, and the headline alone when even that is too long: the headline is the news.
    /// </summary>
    private static string FitTitle(string source, string headline)
    {
        var full = $"{source} - {headline}";

        if (full.Length <= MaxTitleLength)
        {
            return full;
        }

        var slash = source.LastIndexOf('/');

        if (slash >= 0)
        {
            var shorter = $"{source[(slash + 1)..].Trim()} - {headline}";

            if (shorter.Length <= MaxTitleLength)
            {
                return shorter;
            }
        }

        return Cut(headline, MaxTitleLength);
    }

    private static bool Repeats(string line, string? detail) =>
        !string.IsNullOrWhiteSpace(detail)
        && line.StartsWith(detail.Trim().TrimEnd('…'), StringComparison.Ordinal);

    private static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
