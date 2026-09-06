namespace GitAlert.Core;

/// <summary>
/// The categories GitAlert surfaces. They drive the flyout filter chips, the per-kind
/// accent colour and the user's mute preferences, so the set is deliberately small.
/// </summary>
public enum AlertKind
{
    Push,
    PullRequest,
    Issue,
    Comment,
    Review,
    Release,
    Branch,
    Workflow,
    Star,
    Fork,
    Mention,

    /// <summary>A card on a project board arrived, moved column, was archived or went away.</summary>
    Board,

    Other,
}

public static class AlertKindInfo
{
    public static string DisplayName(this AlertKind kind) => kind switch
    {
        AlertKind.Push => "Push",
        AlertKind.PullRequest => "Pull request",
        AlertKind.Issue => "Issue",
        AlertKind.Comment => "Comment",
        AlertKind.Review => "Review",
        AlertKind.Release => "Release",
        AlertKind.Branch => "Branch / tag",
        AlertKind.Workflow => "CI run",
        AlertKind.Star => "Star",
        AlertKind.Fork => "Fork",
        AlertKind.Mention => "Mention",
        AlertKind.Board => "Board change",
        _ => "Other",
    };
}
