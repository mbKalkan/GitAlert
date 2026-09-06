using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
using GitAlert.Platform;

namespace GitAlert.ViewModels;

/// <summary>
/// What the detail pane shows for an alert that has no diff - an issue, a comment, a release, a
/// run, a board card: where it came from, what happened, what was said, the fields that describe
/// it, and the way to it on GitHub.
/// </summary>
public sealed partial class AlertCardViewModel
{
    private AlertCardViewModel(Alert alert, string source, string sourceUrl, string sourceLabel, bool isBoard)
    {
        Source = source;
        SourceUrl = sourceUrl;
        SourceLabel = sourceLabel;
        Caption = isBoard ? source : string.Empty;
        Headline = alert.Title;
        Item = alert.Detail ?? string.Empty;
        Body = alert.Body;
        Note = alert.Note;
        Fields = alert.Fields ?? [];

        // A comment's detail is its own first line; printed above the whole text it would read twice.
        ShowsItem = Item.Length > 0 && !(Body is not null && Body.StartsWith(Item.TrimEnd('…'), StringComparison.Ordinal));

        var when = RelativeTime.Format(alert.Timestamp) is "now" ? "just now" : $"{RelativeTime.Format(alert.Timestamp)} ago";
        Meta = string.Join(" · ", new[] { source, alert.Actor is null ? null : $"by {alert.Actor}", when }.Where(part => part is not null));

        // A draft card has no page of its own, and its alert points at the board instead.
        ItemUrl = alert.Url is { } url && !string.Equals(url, sourceUrl, StringComparison.OrdinalIgnoreCase) ? url : null;
        ItemLabel = isBoard ? BoardItemLabel(Item) : LabelFor(alert.Kind);
    }

    /// <summary>The card behind a board alert, named the way the list names the board: <c>acme / Roadmap</c>.</summary>
    public static AlertCardViewModel ForBoard(Alert alert, string board, string boardUrl) =>
        new(alert, board, boardUrl, "Open board", isBoard: true);

    /// <summary>An alert from a repository that has no diff to show: an issue, a release, a run, a star.</summary>
    public static AlertCardViewModel ForRepository(Alert alert)
    {
        var repository = alert.Repository;
        var slash = repository.IndexOf('/');
        var source = slash > 0 ? $"{repository[..slash]} / {repository[(slash + 1)..]}" : repository;

        return new(alert, source, $"https://github.com/{repository}", "Open repository", isBoard: false);
    }

    /// <summary>Where the alert came from: <c>acme / api-gateway</c>, <c>acme / Roadmap</c>.</summary>
    public string Source { get; }

    public string SourceUrl { get; }

    /// <summary>The second button: "Open repository" or "Open board".</summary>
    public string SourceLabel { get; }

    /// <summary>The line under the open alert in the list. A board's card names the board; a repository's says nothing new.</summary>
    public string Caption { get; }

    /// <summary>Source, who and when, as one muted line: <c>acme / api-gateway · by deniz · 5 minutes ago</c>.</summary>
    public string Meta { get; }

    /// <summary>What happened: "Issue #77 opened", "Moved to In progress".</summary>
    public string Headline { get; }

    /// <summary>The thing itself: the issue's title, the card with its number.</summary>
    public string Item { get; }

    public bool ShowsItem { get; }

    /// <summary>Which field went from what to what, when the headline does not say it all.</summary>
    public string? Note { get; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>What was said, as plain text and cut short.</summary>
    public string? Body { get; }

    public bool HasBody => !string.IsNullOrEmpty(Body);

    public IReadOnlyList<AlertField> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    public string? ItemUrl { get; }

    public bool HasItemUrl => ItemUrl is not null;

    public string ItemLabel { get; }

    [RelayCommand]
    private void OpenItem() => Browser.Open(ItemUrl);

    [RelayCommand]
    private void OpenSource() => Browser.Open(SourceUrl);

    private static string BoardItemLabel(string item) =>
        item.StartsWith("PR #", StringComparison.Ordinal) ? "Open pull request"
        : item.StartsWith('#') ? "Open issue"
        : "Open item";

    private static string LabelFor(AlertKind kind) => kind switch
    {
        AlertKind.Issue => "Open issue",
        AlertKind.PullRequest => "Open pull request",
        AlertKind.Comment => "Open comment",
        AlertKind.Review => "Open review",
        AlertKind.Release => "Open release",
        AlertKind.Workflow => "Open run",
        AlertKind.Mention => "Open thread",
        _ => "Open on GitHub",
    };
}
