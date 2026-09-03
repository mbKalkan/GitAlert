using System.Text.Json.Serialization;

namespace GitAlert.Core;

/// <summary>
/// One thing that happened, already translated from its GitHub shape into something
/// a human can read. Alerts are immutable apart from <see cref="IsRead"/>.
/// </summary>
public sealed class Alert
{
    /// <summary>
    /// Stable identity used for de-duplication across polls and restarts, e.g. <c>event:1234567</c>.
    /// </summary>
    public required string Id { get; init; }

    public required AlertKind Kind { get; init; }

    /// <summary>Headline, e.g. "3 new commits on main".</summary>
    public required string Title { get; init; }

    /// <summary>Supporting line, e.g. the first commit message or the pull request title.</summary>
    public string? Detail { get; init; }

    public required string Repository { get; init; }

    public string? Actor { get; init; }

    public string? Url { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public bool IsRead { get; set; }

    /// <summary>
    /// Severity hint that colours the card accent - a failed CI run is red, a passing one green.
    /// </summary>
    public AlertSeverity Severity { get; init; } = AlertSeverity.Normal;

    [JsonIgnore]
    public string ToastTitle => $"{Repository} - {Title}";

    [JsonIgnore]
    public string ToastBody =>
        !string.IsNullOrWhiteSpace(Detail) ? Detail! :
        Actor is not null ? $"by {Actor}" :
        Title;
}

public enum AlertSeverity
{
    Normal,
    Success,
    Warning,
    Error,
}
