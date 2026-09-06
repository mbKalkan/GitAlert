using CommunityToolkit.Mvvm.ComponentModel;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;

namespace GitAlert.ViewModels;

/// <summary>A watched board as shown under its account in the settings list.</summary>
public sealed partial class BoardItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    public BoardItemViewModel(BoardSubscription subscription)
    {
        Owner = subscription.Owner;
        OwnerKind = subscription.OwnerKind;
        Number = subscription.Number;
        Title = subscription.Title;
        _isEnabled = subscription.Enabled;
    }

    public BoardItemViewModel(BoardRef board, string title)
    {
        Owner = board.Owner;
        OwnerKind = board.OwnerKind;
        Number = board.Number;
        Title = title;
        _isEnabled = true;
    }

    public string Owner { get; }

    public BoardOwnerKind OwnerKind { get; }

    public int Number { get; }

    public string Title { get; }

    public BoardRef Ref => new(Owner, OwnerKind, Number);

    /// <summary>The name the flyout groups this board's alerts under.</summary>
    public string Key => Ref.Key;

    public string Url => Ref.HtmlUrl;

    /// <summary>The owner shown in front of the title, dimmed, the way the flyout writes it.</summary>
    public string OwnerPrefix => $"{Owner} / ";

    /// <summary>The number and who owns it, since two boards may share a title.</summary>
    public string Tag => OwnerKind == BoardOwnerKind.Organization
        ? $"#{Number} · organisation"
        : $"#{Number} · user";

    /// <summary>The account id is supplied by the owning account when the settings are saved.</summary>
    public BoardSubscription ToSubscription(string accountId) => new()
    {
        AccountId = accountId,
        Owner = Owner,
        OwnerKind = OwnerKind,
        Number = Number,
        Title = Title,
        Enabled = IsEnabled,
    };
}

/// <summary>
/// A board the account's token can reach, offered as a checkbox rather than a link to paste.
/// Ticking it starts watching it under this account.
/// </summary>
public sealed partial class DiscoveredBoardViewModel : ObservableObject
{
    private readonly Action<DiscoveredBoardViewModel, bool> _watchChanged;

    [ObservableProperty]
    private bool _isWatched;

    public DiscoveredBoardViewModel(BoardRef board, GhProject project, Action<DiscoveredBoardViewModel, bool> watchChanged)
    {
        _watchChanged = watchChanged;

        Board = board;
        Title = string.IsNullOrWhiteSpace(project.Title) ? $"#{board.Number}" : project.Title;
        UpdatedAt = project.UpdatedAt;
        Description = project.ShortDescription;
    }

    public BoardRef Board { get; }

    public string Title { get; }

    public string Key => Board.Key;

    public string OwnerPrefix => $"{Board.Owner} / ";

    public string Tag => Board.OwnerKind == BoardOwnerKind.Organization
        ? $"#{Board.Number} · organisation"
        : $"#{Board.Number} · user";

    public DateTimeOffset UpdatedAt { get; }

    public string? Description { get; }

    public string Activity => UpdatedAt == default ? string.Empty : $"updated {RelativeTime.Format(UpdatedAt)} ago";

    partial void OnIsWatchedChanged(bool value) => _watchChanged(this, value);
}
