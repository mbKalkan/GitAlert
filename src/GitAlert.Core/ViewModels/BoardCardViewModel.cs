using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
using GitAlert.Platform;

namespace GitAlert.ViewModels;

/// <summary>
/// What the detail pane shows for a board alert, since a card has no diff: the board, what
/// happened to the card, the card's fields as they stood, and the way to the issue behind it.
/// </summary>
public sealed partial class BoardCardViewModel
{
    public BoardCardViewModel(Alert alert, string board, string boardUrl)
    {
        Board = board;
        BoardUrl = boardUrl;
        Headline = alert.Title;
        Item = alert.Detail ?? string.Empty;
        Note = alert.Note;
        When = RelativeTime.Format(alert.Timestamp) is "now" ? "just now" : $"{RelativeTime.Format(alert.Timestamp)} ago";
        Fields = alert.Fields ?? [];

        // A draft has no page of its own, and its alert points at the board instead.
        ItemUrl = alert.Url is { } url && !string.Equals(url, boardUrl, StringComparison.OrdinalIgnoreCase) ? url : null;

        ItemLabel = Item.StartsWith("PR #", StringComparison.Ordinal) ? "Open pull request"
            : Item.StartsWith('#') ? "Open issue"
            : "Open item";
    }

    /// <summary>The board, the way the list names it: <c>acme / Roadmap</c>.</summary>
    public string Board { get; }

    public string BoardUrl { get; }

    /// <summary>What happened: "Moved to In progress", "Added to Roadmap".</summary>
    public string Headline { get; }

    /// <summary>The card itself: the issue or pull request with its number, or a draft's title.</summary>
    public string Item { get; }

    /// <summary>Which field went from what to what, when the headline does not say it all.</summary>
    public string? Note { get; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public string When { get; }

    public IReadOnlyList<AlertField> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    public string? ItemUrl { get; }

    public bool HasItemUrl => ItemUrl is not null;

    public string ItemLabel { get; }

    [RelayCommand]
    private void OpenItem() => Browser.Open(ItemUrl);

    [RelayCommand]
    private void OpenBoard() => Browser.Open(BoardUrl);
}
