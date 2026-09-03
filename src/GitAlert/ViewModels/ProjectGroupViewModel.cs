using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitAlert.ViewModels;

/// <summary>A page of rows for a project, and whether asking again would return more.</summary>
public sealed record GroupPage(IReadOnlyList<AlertViewModel> Items, bool HasMore);

/// <summary>
/// One watched repository as a collapsible section. Every project gets one whether or not it has
/// anything to show, so the list is the shape of what is being watched rather than a shape that
/// changes under you as alerts arrive.
/// </summary>
public sealed partial class ProjectGroupViewModel : ObservableObject
{
    /// <summary>Fetches a page of history for this project. Null while showing alerts.</summary>
    private readonly Func<ProjectGroupViewModel, int, Task<GroupPage>>? _loadPage;

    /// <summary>Told which way the project should move, so the list can reorder and remember it.</summary>
    private readonly Action<ProjectGroupViewModel, int>? _move;

    private int _page;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>False at the top and bottom of the list, so the arrows can grey out there.</summary>
    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canLoadMore;

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

    /// <summary>True when this section fills itself on demand rather than being handed its rows.</summary>
    public bool IsLazy => _loadPage is not null;

    /// <summary>
    /// A count is only honest once there is something to count. On an unopened history section a
    /// zero would read as "no commits" when it means "nobody has looked yet".
    /// </summary>
    public bool ShowCount => !IsLazy || IsLoaded;

    /// <summary>Fills the group from an already-known set of alerts.</summary>
    public void SetItems(IEnumerable<AlertViewModel> items)
    {
        Items.Clear();

        foreach (var item in Order(items))
        {
            Items.Add(item);
        }

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

        // History is fetched the moment a project is opened and not before: expanding is the
        // only reliable signal that someone actually wants to read it.
        if (IsExpanded && _loadPage is not null && !IsLoaded)
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

            foreach (var item in page.Items)
            {
                Items.Add(item);
            }

            _page++;
            CanLoadMore = page.HasMore;
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(ShowCount));

            if (Items.Count == 0)
            {
                Message = "No commits here yet.";
            }

            OnPropertyChanged(nameof(CountText));
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

    /// <summary>Forgets what was fetched, so the next expansion starts over.</summary>
    public void Reset()
    {
        Items.Clear();
        _page = 0;
        CanLoadMore = false;
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(ShowCount));
        Message = null;
        UnreadCount = 0;
        OnPropertyChanged(nameof(CountText));
    }

    private static IEnumerable<AlertViewModel> Order(IEnumerable<AlertViewModel> items) =>
        items.OrderByDescending(i => i.Model.Timestamp);
}
