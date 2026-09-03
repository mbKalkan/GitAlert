using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Configuration;
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

    /// <summary>
    /// Persists the choices made in the list itself - the order of the projects, how rows are
    /// sorted inside them, whether read alerts are hidden - so they survive a restart.
    /// </summary>
    void SaveListPreferences(IReadOnlyList<string> projectOrder, bool unreadOnly);

    /// <summary>
    /// Something in the list was read or cleared. The tray icon carries the same number and has
    /// no other way of learning it changed, so it would otherwise keep the old one until the
    /// next poll happened to redraw it.
    /// </summary>
    void UnreadChanged();
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

    /// <summary>Hide what has already been read. Only meaningful while showing alerts.</summary>
    [ObservableProperty]
    private bool _unreadOnly;

    /// <summary>The order the user put the projects in. Anything absent follows alphabetically.</summary>
    private readonly List<string> _order;

    /// <summary>Drives whether the cards name the account the alert arrived through.</summary>
    private bool _showAccounts;

    /// <summary>
    /// One section per project, kept for the life of the window rather than rebuilt.
    /// </summary>
    /// <remarks>
    /// These used to be created afresh on every filter change and every arriving alert, which
    /// meant a poll landing while you read a project silently discarded the commits you had
    /// asked it to load and folded the section shut again. Keeping the instance keeps what the
    /// user did to it.
    /// </remarks>
    private readonly Dictionary<string, ProjectGroupViewModel> _sections = new(StringComparer.OrdinalIgnoreCase);

    public FlyoutViewModel(AlertStore store, MonitorService monitor, IShellCommands shell, AppSettings settings)
    {
        _store = store;
        _monitor = monitor;
        _shell = shell;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _order = [.. settings.ProjectOrder];
        _unreadOnly = settings.UnreadOnly;

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
        RefreshChipCounts();
        RecountSections();
        _shell.UnreadChanged();
    }

    private void RecountSections()
    {
        foreach (var section in Groups)
        {
            section.Recount();
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        _store.Clear();
        _store.Save();
        _all.Clear();
        _sections.Clear();
        UnreadCount = 0;
        ApplyFilter();
        _shell.UnreadChanged();

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

        // The filter chips, the badge beside the project and the number on the tray icon are all
        // counting this same alert, and none of them is watching the row.
        RefreshChipCounts();
        _sections.GetValueOrDefault(alert.Repository)?.Recount();
        _shell.UnreadChanged();
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
        if (last is null)
        {
            LastUpdatedText = string.Empty;
            return;
        }

        // Format returns "now" for anything under a minute, and "updated now ago" is not English.
        var age = RelativeTime.Format(last.Value);
        LastUpdatedText = age == "now" ? "updated just now" : $"updated {age} ago";
    }

    private void UpdateEmptyMessage(MonitorStatus status) =>
        EmptyMessage = status.State switch
        {
            ConnectionState.NotConfigured => "Add your access token and a repository to get started.",
            ConnectionState.Error => status.Message,
            _ => ActiveFilter == AlertFilter.All
                ? "You are all caught up."
                : "Nothing here yet.",
        };

    private void ApplyFilter()
    {
        RefreshChipCounts();

        Alerts.Clear();

        foreach (var alert in _all.Where(a => OfKind(a) && IsShown(a)))
        {
            Alerts.Add(alert);
        }

        RebuildGroups();

        IsEmpty = Groups.Count == 0;
        UpdateEmptyMessage(_monitor.Status);
    }

    /// <summary>
    /// The number on each filter chip: what is unread in that category.
    /// </summary>
    /// <remarks>
    /// Counted here rather than inside the filter pass, because reading an alert changes these
    /// numbers without changing which alerts are shown. Left to the filter pass, a chip went on
    /// showing the count it had when the list was last rearranged, however many of those alerts
    /// you had since read.
    /// </remarks>
    private void RefreshChipCounts()
    {
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Filter == ActiveFilter;
            chip.Count = _all.Count(a => !a.IsRead && (chip.Filter == AlertFilter.All || a.Group == chip.Filter));
        }
    }

    private bool OfKind(AlertViewModel alert) => ActiveFilter == AlertFilter.All || alert.Group == ActiveFilter;

    private bool IsShown(AlertViewModel alert) => !UnreadOnly || !alert.IsRead;

    [RelayCommand]
    private void ToggleUnreadOnly()
    {
        UnreadOnly = !UnreadOnly;
        Persist();
        ApplyFilter();
    }

    /// <summary>
    /// Moves a project one place up or down. The step is taken against what is on screen, so it
    /// always looks like one move even when projects between them are hidden, and the result is
    /// written back as a total order so every later move has a definite starting point.
    /// </summary>
    private void MoveProject(ProjectGroupViewModel group, int delta)
    {
        var visible = Groups.Select(g => g.Repository).ToList();
        var from = visible.IndexOf(group.Repository);
        var to = from + delta;

        if (from < 0 || to < 0 || to >= visible.Count)
        {
            return;
        }

        var order = AllProjects();
        var a = order.FindIndex(r => string.Equals(r, visible[from], StringComparison.OrdinalIgnoreCase));
        var b = order.FindIndex(r => string.Equals(r, visible[to], StringComparison.OrdinalIgnoreCase));

        if (a < 0 || b < 0)
        {
            return;
        }

        (order[a], order[b]) = (order[b], order[a]);

        _order.Clear();
        _order.AddRange(order);

        Persist();
        ApplyFilter();
    }

    private void Persist() => _shell.SaveListPreferences(_order, UnreadOnly);

    private void RebuildGroups()
    {
        var byRepository = Alerts
            .GroupBy(a => a.Repository, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        Groups.Clear();

        foreach (var repository in ProjectsInView())
        {
            var accountId = AccountIdFor(repository);

            // A project whose account changed has to start over: its history would be fetched
            // with a token that no longer reaches it.
            if (_sections.TryGetValue(repository, out var group)
                && !string.Equals(group.AccountId, accountId, StringComparison.Ordinal))
            {
                _sections.Remove(repository);
                group = null;
            }

            if (group is null)
            {
                group = new ProjectGroupViewModel(repository, accountId, LoadHistoryPageAsync, MoveProject);
                _sections[repository] = group;

                group.SetAlerts(byRepository.GetValueOrDefault(repository, []));

                // First sight of a project: open when it has something to say, folded otherwise.
                // After that it is the user's own choice, held on the section itself.
                group.IsExpanded = group.Items.Count > 0;
            }
            else
            {
                group.SetAlerts(byRepository.GetValueOrDefault(repository, []));
            }

            Groups.Add(group);
        }

        PruneSections();

        for (var i = 0; i < Groups.Count; i++)
        {
            Groups[i].CanMoveUp = i > 0;
            Groups[i].CanMoveDown = i < Groups.Count - 1;
        }
    }

    /// <summary>Forgets sections for projects GitAlert no longer knows about.</summary>
    private void PruneSections()
    {
        var known = AllProjects().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stale in _sections.Keys.Where(r => !known.Contains(r)).ToList())
        {
            _sections.Remove(stale);
        }
    }

    /// <summary>Every project GitAlert knows about, in the user's order.</summary>
    private List<string> AllProjects()
    {
        var known = _monitor.Watched
            .Select(w => w.FullName)
            .Concat(_all.Select(a => a.Repository))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // The user's order first, then anything they have not placed, alphabetically.
        return
        [
            .. known
                .OrderBy(Rank)
                .ThenBy(r => r, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private int Rank(string repository)
    {
        var index = _order.FindIndex(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>Which projects the list actually shows.</summary>
    private List<string> ProjectsInView()
    {
        IEnumerable<string> projects = AllProjects();

        // Every project stays reachable, because the list is also where you go looking for what
        // happened before GitAlert was watching. Asking for unread only turns it back into a
        // list of what needs attention, and a project with nothing unread drops out of it.
        if (UnreadOnly)
        {
            var withUnread = Alerts
                .Select(a => a.Repository)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            projects = projects.Where(withUnread.Contains);
        }

        return [.. projects];
    }

    private string? AccountIdFor(string repository) =>
        _monitor.Watched
            .FirstOrDefault(w => string.Equals(w.FullName, repository, StringComparison.OrdinalIgnoreCase))
            ?.AccountId
        ?? AccountIdOfAlertsIn(repository);

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

        // The rows the sections hold are wrappers around models that have just been replaced.
        _sections.Clear();
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
