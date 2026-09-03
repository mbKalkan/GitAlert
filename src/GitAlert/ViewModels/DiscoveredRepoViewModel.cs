using CommunityToolkit.Mvvm.ComponentModel;
using GitAlert.Core;
using GitAlert.GitHub;

namespace GitAlert.ViewModels;

/// <summary>How the discovered repository list is ordered.</summary>
public enum RepoSort
{
    RecentlyPushed,
    Name,
    Owner,
    Watched,
}

public sealed record RepoSortOption(RepoSort Sort, string Label);

/// <summary>
/// A repository the account's token can reach, offered as a checkbox rather than something to
/// type out. Ticking it starts watching it under this account.
/// </summary>
public sealed partial class DiscoveredRepoViewModel : ObservableObject
{
    private readonly Action<DiscoveredRepoViewModel, bool> _watchChanged;

    [ObservableProperty]
    private bool _isWatched;

    public DiscoveredRepoViewModel(GhRepository repository, Action<DiscoveredRepoViewModel, bool> watchChanged)
    {
        _watchChanged = watchChanged;

        FullName = repository.FullName;
        Name = repository.Name;
        Owner = repository.Owner?.Login ?? OwnerFrom(repository.FullName);
        IsPrivate = repository.IsPrivate;
        IsArchived = repository.Archived;
        IsFork = repository.IsFork;
        PushedAt = repository.PushedAt;
        Description = repository.Description;
    }

    public string FullName { get; }

    public string Name { get; }

    public string Owner { get; }

    public bool IsPrivate { get; }

    public bool IsArchived { get; }

    public bool IsFork { get; }

    public DateTimeOffset? PushedAt { get; }

    public string? Description { get; }

    /// <summary>The owner shown in front of the name, dimmed, the way GitHub writes it.</summary>
    public string OwnerPrefix => $"{Owner}/";

    public string Activity => PushedAt is { } pushed
        ? $"pushed {RelativeTime.Format(pushed)} ago"
        : "no commits yet";

    /// <summary>Everything the row says about itself, matched against the search box.</summary>
    public bool Matches(string term) =>
        FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    partial void OnIsWatchedChanged(bool value) => _watchChanged(this, value);

    private static string OwnerFrom(string fullName)
    {
        var cut = fullName.IndexOf('/');
        return cut > 0 ? fullName[..cut] : fullName;
    }
}
