using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitAlert.ViewModels;

/// <summary>A page of rows for a project, and whether asking again would return more.</summary>
public sealed record GroupPage(IReadOnlyList<AlertViewModel> Items, bool HasMore);

/// <summary>The insertion line shown on a project while another is dragged over it.</summary>
public enum DropMarker
{
    None,
    Above,
    Below,
}

/// <summary>
/// One watched repository as a collapsible section, holding everything known about it: the alerts
/// GitAlert caught while running and the commits it can still fetch from before that. They are one
/// timeline, not two tabs - a push alert and its commit are the same event and share an identity,
/// so merging them collapses the duplicate rather than showing it twice.
/// </summary>
public sealed partial class ProjectGroupViewModel : ObservableObject
{
    /// <summary>Fetches a page of history for this project. Null while showing alerts.</summary>
    private readonly Func<ProjectGroupViewModel, int, Task<GroupPage>>? _loadPage;

    /// <summary>Told which way the project should move, so the list can reorder and remember it.</summary>
    private readonly Action<ProjectGroupViewModel, int>? _move;

    private int _page;

    /// <summary>Alerts handed in from the store, which carry the read state.</summary>
    private readonly List<AlertViewModel> _alerts = [];

    /// <summary>Commits fetched from the repository's history.</summary>
    private readonly List<AlertViewModel> _commits = [];

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Where a dragged project would land relative to this one, while one hovers over it.</summary>
    [ObservableProperty]
    private DropMarker _dropMarker;

    /// <summary>True for the project in the air, so its header can fade until it lands.</summary>
    [ObservableProperty]
    private bool _isBeingDragged;

    /// <summary>False at the top and bottom of the list, so the arrows can grey out there.</summary>
    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canLoadMore = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public ProjectGroupViewModel(
        string repository,
        string? accountId,
        Func<ProjectGroupViewModel, int, Task<GroupPage>>? loadPage = null,
        Action<ProjectGroupViewModel, int>? move = null)
    {
        Repository = repository;
        AccountId = accountId;
        _loadPage = loadPage;
        _move = move;

        var cut = repository.IndexOf('/');
        Owner = cut > 0 ? repository[..cut] : string.Empty;
        Name = cut > 0 ? repository[(cut + 1)..] : repository;
    }

    public string Repository { get; }

    public string? AccountId { get; }

    public string Owner { get; }

    public string Name { get; }

    /// <summary>The owner with its slash, dimmed in front of the name the way GitHub writes it.</summary>
    public string OwnerPrefix => Owner.Length == 0 ? string.Empty : $"{Owner}/";

    public ObservableCollection<AlertViewModel> Items { get; } = [];

    public bool HasUnread => UnreadCount > 0;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Unread if there is any, otherwise how much is in the group at all.</summary>
    public string CountText => UnreadCount > 0 ? UnreadCount.ToString() : Items.Count.ToString();

    /// <summary>True once history has been fetched, so expanding again does not refetch.</summary>
    public bool IsLoaded => _page > 0;

    /// <summary>
    /// A count is only honest once there is something to count. A zero on a project nobody has
    /// opened would read as "nothing here" when it means "nobody has looked yet".
    /// </summary>
    public bool ShowCount => Items.Count > 0;

    /// <summary>
    /// Reaching further back is always worth offering until a short page proves there is no more.
    /// </summary>
    public string LoadMoreLabel => IsLoaded ? "Load more" : "Load earlier commits";

    /// <summary>Hands the group the alerts already known for this project.</summary>
    public void SetAlerts(IEnumerable<AlertViewModel> alerts)
    {
        _alerts.Clear();
        _alerts.AddRange(alerts);
        Merge();
    }

    /// <summary>
    /// Rebuilds the single timeline from both sources. The stored alert wins a tie because it is
    /// the one carrying the read state and the selection; the fetched commit is the same event
    /// seen from the other side.
    /// </summary>
    private void Merge()
    {
        var merged = _alerts
            .Concat(_commits)
            .GroupBy(i => i.Model.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(i => i.Model.Timestamp)
            .ToList();

        // A poll that brought nothing for this project still lands here. Clearing and refilling
        // regardless would throw away every row container the list had built, and take the
        // user's scroll position with them, to arrive at exactly what was already on screen.
        if (!Items.SequenceEqual(merged))
        {
            Items.Clear();

            foreach (var item in merged)
            {
                Items.Add(item);
            }
        }

        UnreadCount = Items.Count(i => !i.IsRead);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowCount));
    }

    /// <summary>
    /// Recounts what is unread here.
    /// </summary>
    /// <remarks>
    /// Reading an alert happens on the row, which the group knows nothing about - it counts when
    /// it builds its list and never again. That left the badge beside a project showing the
    /// number of unread alerts it had when you opened it, however many of them you then read.
    /// </remarks>
    public void Recount()
    {
        UnreadCount = Items.Count(i => !i.IsRead);
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private void MoveUp() => _move?.Invoke(this, -1);

    [RelayCommand]
    private void MoveDown() => _move?.Invoke(this, 1);

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;

        // Commits are fetched when someone opens a project and not before. A project that came
        // open because it had alerts waits for the button instead, so simply showing the list
        // never costs a request per project.
        if (IsExpanded && _loadPage is not null && !IsLoaded && _alerts.Count == 0)
        {
            await LoadMoreAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_loadPage is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        Message = null;

        try
        {
            var page = await _loadPage(this, _page + 1).ConfigureAwait(true);

            _commits.AddRange(page.Items);
            _page++;
            CanLoadMore = page.HasMore;

            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(LoadMoreLabel));

            Merge();

            if (Items.Count == 0)
            {
                Message = "Nothing here yet.";
            }
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            CanLoadMore = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Forgets what was fetched, so the next request starts over.</summary>
    public void Reset()
    {
        _commits.Clear();
        _page = 0;
        CanLoadMore = true;
        Message = null;

        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(LoadMoreLabel));

        Merge();
    }
}
