using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Platform;
using GitAlert.Services;

namespace GitAlert.ViewModels;

/// <summary>Actions the flyout needs from the shell that owns the tray icon.</summary>
public interface IShellCommands
{
    void ShowSettings();

    void HideFlyout();

    void Quit();
}

/// <summary>
/// Drives the tray flyout: the alert list, the filter chips and the connection status line.
/// Subscribes to <see cref="MonitorService"/> directly and marshals its background-thread events
/// onto the UI dispatcher.
/// </summary>
public sealed partial class FlyoutViewModel : ObservableObject, IDisposable
{
    private static readonly SolidColorBrush ConnectedBrush = Frozen(0x34, 0xA8, 0x53);
    private static readonly SolidColorBrush WorkingBrush = Frozen(0x58, 0x9C, 0xF0);
    private static readonly SolidColorBrush WarningBrush = Frozen(0xC7, 0x93, 0x1F);
    private static readonly SolidColorBrush ErrorBrush = Frozen(0xE5, 0x53, 0x4B);
    private static readonly SolidColorBrush IdleBrush = Frozen(0x89, 0x93, 0xA1);

    /// <summary>Commits per request. One screenful and a bit, so "load more" is rarely needed.</summary>
    private const int HistoryPageSize = 30;

    private readonly AlertStore _store;
    private readonly MonitorService _monitor;
    private readonly IShellCommands _shell;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ageTimer;
    private readonly List<AlertViewModel> _all = [];

    [ObservableProperty]
    private string _statusText = "Starting…";

    [ObservableProperty]
    private Brush _statusBrush = IdleBrush;

    [ObservableProperty]
    private string _lastUpdatedText = string.Empty;

    [ObservableProperty]
    private string _rateLimitText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyMessage = "You are all caught up.";

    [ObservableProperty]
    private AlertFilter _activeFilter = AlertFilter.All;

    /// <summary>
    /// The alert the detail pane is showing. Selecting rather than opening the browser is the
    /// whole point of the detail pane: the change can be read without leaving the window.
    /// </summary>
    [ObservableProperty]
    private AlertViewModel? _selectedAlert;

    /// <summary>Which repository the list is narrowed to, or null for every project.</summary>
    [ObservableProperty]
    private string? _activeProject;

    /// <summary>
    /// True while the left pane is showing a repository's commit history rather than the alerts
    /// GitAlert happened to catch. Alerts only start the day you point GitAlert at a repository;
    /// the history was always there.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlertMode))]
    private bool _isHistoryMode;

    private GroupSortOption _selectedSort = null!;

    /// <summary>Drives whether the cards name the account the alert arrived through.</summary>
    private bool _showAccounts;

    /// <summary>
    /// Repositories the user has folded away. Held separately from the groups because the groups
    /// are rebuilt on every filter change and would otherwise spring open again.
    /// </summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    public FlyoutViewModel(AlertStore store, MonitorService monitor, IShellCommands shell)
    {
        _store = store;
        _monitor = monitor;
        _shell = shell;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Filters =
        [
            new FilterChipViewModel(AlertFilter.All, "All") { IsSelected = true },
            new FilterChipViewModel(AlertFilter.Push, "Push"),
            new FilterChipViewModel(AlertFilter.PullRequests, "PRs"),
            new FilterChipViewModel(AlertFilter.Issues, "Issues"),
            new FilterChipViewModel(AlertFilter.Ci, "CI"),
            new FilterChipViewModel(AlertFilter.More, "More"),
        ];

        Detail = new AlertDetailViewModel(monitor);
        _selectedSort = SortOptions[0];

        _all.AddRange(_store.Snapshot.Select(Create));
        _unreadCount = _store.UnreadCount;
        ApplyFilter();

        _monitor.AlertsReceived += OnAlertsReceived;
        _monitor.StatusChanged += OnStatusChanged;
        ApplyStatus(_monitor.Status);

        // Relative timestamps drift; refresh them while the flyout is on screen.
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _ageTimer.Tick += (_, _) => RefreshAges();
    }

    /// <summary>The filtered alerts, flat. The list on screen renders <see cref="Groups"/>.</summary>
    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    /// <summary>
    /// One section per watched project, in both modes. Every project appears whether or not it
    /// has anything to show, so the list is the shape of what is being watched rather than one
    /// that rearranges itself as alerts arrive.
    /// </summary>
    public ObservableCollection<ProjectGroupViewModel> Groups { get; } = [];

    public ObservableCollection<FilterChipViewModel> Filters { get; }

    /// <summary>One chip per repository that has alerts, plus the chip that clears the filter.</summary>
    public ObservableCollection<ProjectChipViewModel> Projects { get; } = [];

    public bool IsAlertMode => !IsHistoryMode;

    public IReadOnlyList<GroupSortOption> SortOptions { get; } =
    [
        new(GroupSort.Newest, "Newest first"),
        new(GroupSort.Oldest, "Oldest first"),
        new(GroupSort.UnreadFirst, "Unread first"),
    ];

    /// <summary>How rows are ordered inside each project.</summary>
    public GroupSortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || !SetProperty(ref _selectedSort, value))
            {
                return;
            }

            foreach (var group in Groups)
            {
                group.ApplySort(value.Sort);
            }
        }
    }

    /// <summary>
    /// Whether narrowing by project is worth offering. With a single repository watched, the
    /// control would be a menu with one entry that does nothing.
    /// </summary>
    public bool HasSeveralProjects => Projects.Count > 2;

    /// <summary>
    /// The project the list is narrowed to, as a picker rather than a row of chips: the chips
    /// grew a row per axis and buried the alerts under their own controls.
    /// </summary>
    public ProjectChipViewModel? SelectedProject
    {
        get => Projects.FirstOrDefault(
            p => string.Equals(p.Repository, ActiveProject, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && !ReferenceEquals(value, SelectedProject))
            {
                SelectProject(value);
            }
        }
    }

    /// <summary>The right-hand pane: the selected alert and the files it changed.</summary>
    public AlertDetailViewModel Detail { get; }

    public bool HasUnread => UnreadCount > 0;

    public string UnreadText => UnreadCount switch
    {
        0 => "No unread alerts",
        1 => "1 unread alert",
        _ => $"{UnreadCount} unread alerts",
    };

    /// <summary>Called when the flyout becomes visible.</summary>
    public void OnShown()
    {
        RefreshAges();
        UpdateLastUpdated();
        _ageTimer.Start();
    }

    public void OnHidden() => _ageTimer.Stop();

    [RelayCommand]
    private void Refresh()
    {
        StatusText = "Checking GitHub…";
        StatusBrush = WorkingBrush;
        _monitor.RequestRefresh();
    }

    [RelayCommand]
    private void SelectFilter(FilterChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        ActiveFilter = chip.Filter;
        ApplyFilter();
    }

    [RelayCommand]
    private void SelectProject(ProjectChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        ActiveProject = chip.Repository;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ShowAlertsAsync()
    {
        IsHistoryMode = false;
        await ClearSelectionAsync().ConfigureAwait(true);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        IsHistoryMode = true;
        await ClearSelectionAsync().ConfigureAwait(true);
        ApplyFilter();
    }

    /// <summary>
    /// Reads one page of a project's history. Handed to each group so it can fill itself when
    /// someone opens it, rather than the whole list costing a request per project up front.
    /// </summary>
    private async Task<GroupPage> LoadHistoryPageAsync(ProjectGroupViewModel group, int page)
    {
        if (!RepoRef.TryParse(group.Repository, out var repo))
        {
            throw new InvalidOperationException($"Cannot work out which repository {group.Repository} refers to.");
        }

        var client = _monitor.ClientFor(group.AccountId);

        if (client is null)
        {
            throw new InvalidOperationException("No configured account can reach this repository.");
        }

        var login = _monitor.Watched
            .FirstOrDefault(w => string.Equals(w.FullName, group.Repository, StringComparison.OrdinalIgnoreCase))
            ?.Login;

        try
        {
            var commits = await client.GetCommitHistoryAsync(repo, page, HistoryPageSize).ConfigureAwait(true);

            var items = commits
                .Select(c => FromCommit(c, group.Repository, group.AccountId!, login))
                .ToList();

            return new GroupPage(items, commits.Count == HistoryPageSize);
        }
        catch (GitHubException ex)
        {
            throw new InvalidOperationException(ex.UserMessage, ex);
        }
    }

    /// <summary>
    /// Dresses a commit as an alert. The list rows, the selection and the diff pane all already
    /// know how to show one, so history costs a translation rather than a parallel world.
    /// </summary>
    private AlertViewModel FromCommit(GhCommit commit, string repository, string accountId, string? login)
    {
        var message = commit.Commit?.Message ?? string.Empty;
        var newline = message.IndexOfAny(['\r', '\n']);
        var summary = newline < 0 ? message : message[..newline];

        var alert = new Alert
        {
            // Shares the identity a push alert for this commit would have, so the diff the detail
            // pane already fetched is reused rather than requested again.
            Id = $"{accountId}|commit:{commit.Sha}",
            Kind = AlertKind.Push,

            // Shaped like a push alert on purpose: the headline is the boilerplate and the
            // message is the detail, which is what makes the row lead with the message.
            Title = $"Commit {Abbreviate(commit.Sha)}",
            Detail = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
            Repository = repository,
            Account = login,
            AccountId = accountId,
            Actor = commit.Author?.Login ?? commit.Commit?.Author?.Name,
            Url = commit.HtmlUrl,
            Timestamp = commit.Commit?.Author?.Date ?? DateTimeOffset.Now,
            DiffHead = commit.Sha,

            // Nothing in history is news, so none of it wears an unread dot.
            IsRead = true,
        };

        return new AlertViewModel(alert) { ShowAccount = _showAccounts };
    }

    private static string Abbreviate(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>Falls back to whichever account produced alerts for a repository.</summary>
    private string? AccountIdOfAlertsIn(string repository) =>
        _all.FirstOrDefault(a => string.Equals(a.Repository, repository, StringComparison.OrdinalIgnoreCase))
            ?.Model.AccountId;

    private async Task ClearSelectionAsync()
    {
        if (SelectedAlert is { } previous)
        {
            previous.IsSelected = false;
        }

        SelectedAlert = null;
        await Detail.ShowAsync(null).ConfigureAwait(true);
    }

    /// <summary>
    /// Clicking a row shows the change in the detail pane rather than throwing the user at a
    /// browser. Opening on GitHub is still one click away, from the detail pane's own header.
    /// </summary>
    [RelayCommand]
    private async Task SelectAlertAsync(AlertViewModel? alert)
    {
        if (alert is null)
        {
            return;
        }

        MarkRead(alert);

        if (SelectedAlert is { } previous)
        {
            previous.IsSelected = false;
        }

        alert.IsSelected = true;
        SelectedAlert = alert;

        await Detail.ShowAsync(alert).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenAlert(AlertViewModel? alert)
    {
        if (alert is not null)
        {
            MarkRead(alert);
            Browser.Open(alert.Url);
        }
    }

    [RelayCommand]
    private void MarkAllRead()
    {
        _store.MarkAllRead();
        _store.Save();

        foreach (var alert in _all)
        {
            alert.IsRead = true;
        }

        UnreadCount = 0;
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        _store.Clear();
        _store.Save();
        _all.Clear();
        UnreadCount = 0;
        ApplyFilter();

        SelectedAlert = null;
        await Detail.ShowAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void Quit() => _shell.Quit();

    private void MarkRead(AlertViewModel alert)
    {
        if (alert.IsRead)
        {
            return;
        }

        alert.MarkRead();
        _store.MarkRead(alert.Model.Id);
        _store.Save();
        UnreadCount = Math.Max(0, UnreadCount - 1);
    }

    private void OnAlertsReceived(object? sender, IReadOnlyList<Alert> alerts) =>
        _dispatcher.InvokeAsync(() =>
        {
            foreach (var alert in alerts)
            {
                _all.Insert(0, Create(alert));
            }

            TrimToStore();
            UnreadCount = _store.UnreadCount;
            ApplyFilter();
        });

    private void OnStatusChanged(object? sender, MonitorStatus status) =>
        _dispatcher.InvokeAsync(() => ApplyStatus(status));

    private void ApplyStatus(MonitorStatus status)
    {
        StatusText = status.Message;

        StatusBrush = status.State switch
        {
            ConnectionState.Connected => ConnectedBrush,
            ConnectionState.Connecting => WorkingBrush,
            ConnectionState.Warning => WarningBrush,
            ConnectionState.Error => ErrorBrush,
            _ => IdleBrush,
        };

        if (_showAccounts != status.AccountCount > 1)
        {
            _showAccounts = status.AccountCount > 1;

            foreach (var alert in _all)
            {
                alert.ShowAccount = _showAccounts;
            }
        }

        RateLimitText = status.RateLimit.IsKnown
            ? $"{status.RateLimit.Remaining}/{status.RateLimit.Limit} API calls left this hour"
            : string.Empty;

        UpdateLastUpdated();
        UpdateEmptyMessage(status);
    }

    private void UpdateLastUpdated()
    {
        var last = _monitor.Status.LastSuccess;
        LastUpdatedText = last is null ? string.Empty : $"updated {RelativeTime.Format(last.Value)} ago";
    }

    private void UpdateEmptyMessage(MonitorStatus status) =>
        EmptyMessage = status.State switch
        {
            ConnectionState.NotConfigured => "Add your access token and a repository to get started.",
            ConnectionState.Error => status.Message,
            _ when ActiveProject is not null => $"Nothing from {ShortName(ActiveProject)} yet.",
            _ => ActiveFilter == AlertFilter.All
                ? "You are all caught up."
                : "Nothing here yet.",
        };

    private void ApplyFilter()
    {
        // Chip counts describe what picking that chip would leave, so each counts against the
        // other axis rather than against the whole history.
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Filter == ActiveFilter;
            chip.Count = _all.Count(a => !a.IsRead && InProject(a) && (chip.Filter == AlertFilter.All || a.Group == chip.Filter));
        }

        RebuildProjects();

        Alerts.Clear();

        foreach (var alert in _all.Where(a => InProject(a) && OfKind(a)))
        {
            Alerts.Add(alert);
        }

        RebuildGroups();

        IsEmpty = Groups.Count == 0;
        UpdateEmptyMessage(_monitor.Status);
    }

    private bool OfKind(AlertViewModel alert) => ActiveFilter == AlertFilter.All || alert.Group == ActiveFilter;

    private bool InProject(AlertViewModel alert) =>
        ActiveProject is null || string.Equals(alert.Repository, ActiveProject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps a chip per repository that has alerts. The chips are only rebuilt when that set
    /// actually changes, so clicking one does not make the whole row flicker.
    /// </summary>
    private void RebuildProjects()
    {
        // Everything watched, not just what has produced an alert: a repository nobody has
        // pushed to yet still has a history worth reading.
        var repositories = _monitor.Watched
            .Select(w => w.FullName)
            .Concat(_all.Select(a => a.Repository))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = Projects.Skip(1).Select(p => p.Repository!).ToList();

        if (!current.SequenceEqual(repositories, StringComparer.OrdinalIgnoreCase))
        {
            Projects.Clear();
            Projects.Add(new ProjectChipViewModel(null, "All projects"));

            foreach (var repository in repositories)
            {
                Projects.Add(new ProjectChipViewModel(repository, ShortName(repository)));
            }

            OnPropertyChanged(nameof(HasSeveralProjects));
        }

        OnPropertyChanged(nameof(SelectedProject));

        // A project the user had narrowed to can disappear when history is trimmed or cleared.
        if (ActiveProject is not null && !repositories.Contains(ActiveProject, StringComparer.OrdinalIgnoreCase))
        {
            ActiveProject = null;
        }

        foreach (var chip in Projects)
        {
            chip.IsSelected = string.Equals(chip.Repository, ActiveProject, StringComparison.OrdinalIgnoreCase);
            chip.Summary = chip.Repository ?? "Every watched repository";
            chip.Count = _all.Count(a =>
                !a.IsRead
                && OfKind(a)
                && (chip.Repository is null
                    || string.Equals(a.Repository, chip.Repository, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void RebuildGroups()
    {
        // Remember which sections were open, so a filter change does not fold everything shut.
        foreach (var existing in Groups)
        {
            if (!existing.IsExpanded)
            {
                _collapsed.Add(existing.Repository);
            }
            else
            {
                _collapsed.Remove(existing.Repository);
            }
        }

        Groups.Clear();

        var byRepository = Alerts
            .GroupBy(a => a.Repository, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var repository in ProjectsInView())
        {
            var group = new ProjectGroupViewModel(
                repository,
                AccountIdFor(repository),
                SelectedSort.Sort,
                IsHistoryMode ? LoadHistoryPageAsync : null);

            if (!IsHistoryMode)
            {
                group.SetItems(byRepository.GetValueOrDefault(repository, []));
            }

            // A section with nothing in it opens to nothing, so it starts folded. Otherwise the
            // user's own last choice wins, and a first sight of the project is open.
            group.IsExpanded = !_collapsed.Contains(repository)
                               && (IsHistoryMode ? Groups.Count == 0 && OnlyOneProject() : group.Items.Count > 0);

            Groups.Add(group);
        }

        // Opening history on a single project should not need a click to get going.
        foreach (var group in Groups.Where(g => g.IsExpanded && IsHistoryMode && !g.IsLoaded))
        {
            _ = group.LoadMoreCommand.ExecuteAsync(null);
        }
    }

    private bool OnlyOneProject() => ProjectsInView().Count == 1;

    /// <summary>Which projects the list is showing: everything watched, or just the chosen one.</summary>
    private List<string> ProjectsInView()
    {
        var watched = _monitor.Watched
            .Select(w => w.FullName)
            .Concat(_all.Select(a => a.Repository))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (ActiveProject is not null)
        {
            watched = watched.Where(r => string.Equals(r, ActiveProject, StringComparison.OrdinalIgnoreCase));
        }

        return [.. watched.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)];
    }

    private string? AccountIdFor(string repository) =>
        _monitor.Watched
            .FirstOrDefault(w => string.Equals(w.FullName, repository, StringComparison.OrdinalIgnoreCase))
            ?.AccountId
        ?? AccountIdOfAlertsIn(repository);

    private static string ShortName(string repository)
    {
        var cut = repository.IndexOf('/');
        return cut > 0 ? repository[(cut + 1)..] : repository;
    }

    /// <summary>Keeps the in-memory list aligned with the trimmed, persisted history.</summary>
    private void TrimToStore()
    {
        var kept = _store.Snapshot.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        _all.RemoveAll(a => !kept.Contains(a.Model.Id));
    }

    private void RefreshAges()
    {
        foreach (var alert in Groups.SelectMany(g => g.Items))
        {
            alert.RefreshAge();
        }

        UpdateLastUpdated();
    }

    private AlertViewModel Create(Alert alert) => new(alert) { ShowAccount = _showAccounts };

    /// <summary>Re-reads the store after settings changed the history size or cleared it.</summary>
    public void Reload()
    {
        _all.Clear();
        _all.AddRange(_store.Snapshot.Select(Create));
        UnreadCount = _store.UnreadCount;
        ApplyFilter();

        // The selected alert is a wrapper around a model that has just been replaced, so the
        // pane would otherwise keep a row that is no longer in the list.
        if (SelectedAlert is not null && !_all.Contains(SelectedAlert))
        {
            SelectedAlert = null;
            _ = Detail.ShowAsync(null);
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _monitor.AlertsReceived -= OnAlertsReceived;
        _monitor.StatusChanged -= OnStatusChanged;
        _ageTimer.Stop();
        Detail.Dispose();
    }
}
