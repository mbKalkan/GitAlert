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

    /// <summary>
    /// The login of the account this alert arrived through. Shown on the card once more than one
    /// account is configured, so it is obvious which identity saw it.
    /// </summary>
    public string? Account { get; set; }

    /// <summary>
    /// Which configured account saw this, so the detail pane can borrow that account's token when
    /// it goes back to GitHub for the diff. Alerts stored before diffs existed leave this empty;
    /// <see cref="Id"/> still carries the account as a prefix in that case.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>The commit this alert is about, when it is about one.</summary>
    public string? DiffHead { get; init; }

    /// <summary>
    /// What <see cref="DiffHead"/> should be compared against. Null means the alert covers a
    /// single commit, which is its own diff.
    /// </summary>
    public string? DiffBase { get; init; }

    /// <summary>Set on pull request alerts, whose changed files come from a different endpoint.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>
    /// What this alert points at on GitHub, recovered from the URL or the id when the alert
    /// predates these fields.
    /// </summary>
    [JsonIgnore]
    public DiffTarget Diff => DiffTarget.For(this);

    /// <summary>Whether the detail pane has something to fetch changed files for.</summary>
    [JsonIgnore]
    public bool HasDiff => Diff.IsKnown;

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
