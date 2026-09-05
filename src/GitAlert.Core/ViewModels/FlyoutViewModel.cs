using System.Collections.ObjectModel;
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
    /// Persists the choices made in the list itself - the order of the projects, the sections they
    /// are grouped under, whether read alerts are hidden - so they survive a restart.
    /// </summary>
    void SaveListPreferences(ListPreferences preferences);

    /// <summary>
    /// Something in the list was read or cleared. The tray icon carries the same number and has
    /// no other way of learning it changed, so it would otherwise keep the old one until the
    /// next poll happened to redraw it.
    /// </summary>
    void UnreadChanged();
}

/// <summary>The choices made in the list itself, handed to the shell to save.</summary>
public sealed record ListPreferences(
    IReadOnlyList<string> ProjectOrder,
    IReadOnlyList<ProjectSection> Sections,
    bool UnreadOnly);

/// <summary>
/// Drives the tray flyout: the alert list, the filter chips and the connection status line.
/// Subscribes to <see cref="MonitorService"/> directly and marshals its background-thread events
/// onto the UI dispatcher.
/// </summary>
public sealed partial class FlyoutViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the relative timestamps are redrawn while the flyout is open.</summary>
    private static readonly TimeSpan AgeRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Commits per request. One screenful and a bit, so "load more" is rarely needed.</summary>
    private const int HistoryPageSize = 30;

    private readonly AlertStore _store;
    private readonly MonitorService _monitor;
    private readonly IShellCommands _shell;
    private readonly UiThread _ui;
    private readonly Timer _ageTimer;
    private readonly List<AlertViewModel> _all = [];

    [ObservableProperty]
    private string _statusText = "Starting…";

    [ObservableProperty]
    private ConnectionState _status = ConnectionState.NotConfigured;

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

    /// <summary>
    /// The user's sections, in the order they are shown. Kept for the life of the window like the
    /// project groups, so a fold or a half-typed name survives the poll that lands during it.
    /// </summary>
    private readonly List<ProjectSectionViewModel> _sections;

    /// <summary>Drives whether the cards name the account the alert arrived through.</summary>
    private bool _showAccounts;

    /// <summary>
    /// One group per project, kept for the life of the window rather than rebuilt.
    /// </summary>
    /// <remarks>
    /// These used to be created afresh on every filter change and every arriving alert, which
    /// meant a poll landing while you read a project silently discarded the commits you had
    /// asked it to load and folded the group shut again. Keeping the instance keeps what the
    /// user did to it.
    /// </remarks>
    private readonly Dictionary<string, ProjectGroupViewModel> _projects = new(StringComparer.OrdinalIgnoreCase);

    public FlyoutViewModel(AlertStore store, MonitorService monitor, IShellCommands shell, AppSettings settings)
    {
        _store = store;
        _monitor = monitor;
        _shell = shell;
        _ui = UiThread.Capture();

        _order = [.. settings.ProjectOrder];
        _sections = settings.Sections.Select(section => Wrap(section.Clone())).ToList();
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

        // Relative timestamps drift; refresh them while the flyout is on screen. The timer fires on
        // the pool, so the refresh is handed back to the UI thread.
        _ageTimer = new Timer(_ => _ui.Post(RefreshAges), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The filtered alerts, flat. The list on screen renders <see cref="Groups"/>.</summary>
    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    /// <summary>
    /// One group per watched project, in the order the list shows them: the loose projects, then
    /// section by section. Every project appears whether or not it has anything to show, so the
    /// list is the shape of what is being watched rather than one that rearranges itself as alerts
    /// arrive. A project under a folded section is here too; only <see cref="Rows"/> leaves it out.
    /// </summary>
    public ObservableCollection<ProjectGroupViewModel> Groups { get; } = [];

    /// <summary>
    /// What the list renders, top to bottom: the loose projects, then each section's header
    /// followed by its projects while it is unfolded. A project row is a
    /// <see cref="ProjectGroupViewModel"/>, a section header a <see cref="ProjectSectionViewModel"/>.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<FilterChipViewModel> Filters { get; }

    /// <summary>The selected alert's changes: the files under its card, the diff beside the list.</summary>
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
        _ageTimer.Change(AgeRefreshInterval, AgeRefreshInterval);
    }

    public void OnHidden() => _ageTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    [RelayCommand]
    private void Refresh()
    {
        StatusText = "Checking GitHub…";
        Status = ConnectionState.Connecting;
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
            Timestamp = commit.Date ?? DateTimeOffset.Now,
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
    /// Clicking a row unfolds the change under it rather than throwing the user at a browser.
    /// Opening on GitHub is still one click away, from the line under the open card.
    /// </summary>
    [RelayCommand]
    private async Task SelectAlertAsync(AlertViewModel? alert)
    {
        if (alert is null)
        {
            return;
        }

        MarkRead(alert);

        // The open card again: fold it, the way a second click on a project header folds the project.
        if (ReferenceEquals(alert, SelectedAlert))
        {
            await ClearSelectionAsync().ConfigureAwait(true);
            return;
        }

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

        RefreshCounts();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        _store.Clear();
        _store.Save();
        _all.Clear();
        _projects.Clear();
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

        // Reading is not rearranging: the row stays where it is, and only the numbers move.
        RefreshCounts();
    }

    /// <summary>
    /// Reads everything a project is showing, in one go. What it is showing, not everything it
    /// ever had: with a kind picked in the chips, the badge counts that kind alone, and the tick
    /// beside it clears the number it sits next to and nothing more.
    /// </summary>
    private void MarkProjectRead(ProjectGroupViewModel group) => MarkRead([group]);

    /// <summary>The tick on a section header: every project under it, in one go.</summary>
    private void MarkSectionRead(ProjectSectionViewModel section) =>
        MarkRead(Groups.Where(g => section.Contains(g.Repository)));

    private void MarkRead(IEnumerable<ProjectGroupViewModel> groups)
    {
        var unread = groups.SelectMany(g => g.Items).Where(a => !a.IsRead).Distinct().ToList();

        if (unread.Count == 0)
        {
            return;
        }

        foreach (var alert in unread)
        {
            alert.MarkRead();
        }

        _store.MarkRead(unread.Select(a => a.Model.Id));
        _store.Save();

        // As with a single row: nothing moves, only the numbers do.
        RefreshCounts();
    }

    private void OnAlertsReceived(object? sender, IReadOnlyList<Alert> alerts) =>
        _ui.Post(() =>
        {
            foreach (var alert in alerts)
            {
                _all.Insert(0, Create(alert));
            }

            TrimToStore();
            ApplyFilter();
        });

    private void OnStatusChanged(object? sender, MonitorStatus status) =>
        _ui.Post(() => ApplyStatus(status));

    private void ApplyStatus(MonitorStatus status)
    {
        StatusText = status.Message;

        Status = status.State;

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
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Filter == ActiveFilter;
        }

        Alerts.Clear();

        foreach (var alert in _all.Where(a => OfKind(a) && IsShown(a)))
        {
            Alerts.Add(alert);
        }

        RebuildGroups();
        RefreshCounts();

        IsEmpty = Groups.Count == 0;
        UpdateEmptyMessage(_monitor.Status);
    }

    /// <summary>
    /// Brings every number back in step with what is unread, without rearranging anything.
    /// </summary>
    /// <remarks>
    /// Four counters describe the same alerts: the filter chips, the badge on each project, the
    /// line in the header, and the tray icon drawn outside this window entirely. Each of them
    /// used to be recomputed by a different event, and reading an alert changes all four while
    /// rearranging none of them - so it went through the one path that recomputed nothing. That
    /// was reported three separate times, about three different counters, each fixed on its own
    /// while the next one was left. They are counted here, in one place, for that reason: the
    /// question "which of them did I remember" should not be askable.
    /// </remarks>
    private void RefreshCounts()
    {
        UnreadCount = _store.UnreadCount;

        // Each chip counts against the other axis rather than against the whole history, so it
        // says what picking it would leave.
        foreach (var chip in Filters)
        {
            chip.Count = _all.Count(a => !a.IsRead && (chip.Filter == AlertFilter.All || a.Group == chip.Filter));
        }

        foreach (var group in Groups)
        {
            group.Recount();
        }

        // A section's number is its projects' put together.
        foreach (var section in _sections)
        {
            section.UnreadCount = Groups.Where(g => section.Contains(g.Repository)).Sum(g => g.UnreadCount);
        }

        _shell.UnreadChanged();
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
    /// Moves a project one place up or down. The places are what is on screen - so a step always
    /// looks like one move even when projects between them are hidden - plus one empty place for
    /// each area with nothing in it. Within an area the two projects swap; at its edge the project
    /// crosses into the next area instead, keeping its place in the order, so the arrows alone can
    /// walk it into a section and out again. The result is written back as a total order laid out
    /// area by area, so every later move has a definite starting point.
    /// </summary>
    private void MoveProject(ProjectGroupViewModel group, int delta)
    {
        var slots = Slots();
        var from = slots.FindIndex(s => ReferenceEquals(s.Project, group));
        var to = from + delta;

        if (from < 0 || to < 0 || to >= slots.Count)
        {
            return;
        }

        var neighbour = slots[to];

        if (ReferenceEquals(neighbour.Section, slots[from].Section) && neighbour.Project is { } other)
        {
            Swap(group.Repository, other.Repository);
        }
        else
        {
            // With the order laid out area by area, the project already stands at the edge it is
            // crossing: joining the neighbouring area is the whole move.
            NormaliseOrder();
            Assign(group.Repository, neighbour.Section);

            // Walked into a folded section: unfold it, or the move would look like a disappearance.
            if (neighbour.Section is { IsExpanded: false } section)
            {
                section.IsExpanded = true;
            }
        }

        NormaliseOrder();
        Persist();
        ApplyFilter();
    }

    private void Swap(string first, string second)
    {
        var order = OrderForEditing();
        var a = order.FindIndex(r => string.Equals(r, first, StringComparison.OrdinalIgnoreCase));
        var b = order.FindIndex(r => string.Equals(r, second, StringComparison.OrdinalIgnoreCase));

        if (a < 0 || b < 0)
        {
            return;
        }

        (order[a], order[b]) = (order[b], order[a]);

        _order.Clear();
        _order.AddRange(order);
    }

    /// <summary>
    /// Puts one project directly above or below another, wherever the two currently sit, and in
    /// that project's section. This is what dropping a dragged header on a project does; the
    /// arrows still move a project one step at a time.
    /// </summary>
    public void PlaceProject(ProjectGroupViewModel moved, ProjectGroupViewModel target, bool above)
    {
        if (ReferenceEquals(moved, target))
        {
            return;
        }

        var order = OrderForEditing();
        var from = order.FindIndex(r => string.Equals(r, moved.Repository, StringComparison.OrdinalIgnoreCase));

        if (from < 0)
        {
            return;
        }

        order.RemoveAt(from);

        var to = order.FindIndex(r => string.Equals(r, target.Repository, StringComparison.OrdinalIgnoreCase));

        if (to < 0)
        {
            return;
        }

        order.Insert(above ? to : to + 1, moved.Repository);

        _order.Clear();
        _order.AddRange(order);
        Assign(moved.Repository, SectionOf(target.Repository));

        NormaliseOrder();
        Persist();
        ApplyFilter();
    }

    /// <summary>
    /// Drops a project on a section header: into the section, at its top, unfolding it if it was
    /// folded so the drop can be seen landing. Dropped just above the header instead, the project
    /// goes to the end of whatever is above the section - the previous one, or the loose projects.
    /// </summary>
    public void PlaceProject(ProjectGroupViewModel moved, ProjectSectionViewModel section, bool above)
    {
        var index = _sections.IndexOf(section);

        if (index < 0)
        {
            return;
        }

        // Every known project gets a rank first, so "first" and "last" below mean what they say.
        NormaliseOrder();

        var area = above
            ? index > 0 ? _sections[index - 1] : null
            : section;

        Assign(moved.Repository, area);

        // A rank only matters within an area: first of all makes it first in its new area, last of
        // all makes it last.
        _order.RemoveAll(r => string.Equals(r, moved.Repository, StringComparison.OrdinalIgnoreCase));

        if (above)
        {
            _order.Add(moved.Repository);
        }
        else
        {
            _order.Insert(0, moved.Repository);
            section.IsExpanded = true;
        }

        NormaliseOrder();
        Persist();
        ApplyFilter();
    }

    /// <summary>Takes down every insertion line and the fade on whatever was being dragged.</summary>
    public void ClearDragMarkers()
    {
        foreach (var group in _projects.Values)
        {
            group.DropMarker = DropMarker.None;
            group.IsBeingDragged = false;
        }

        foreach (var section in _sections)
        {
            section.DropMarker = DropMarker.None;
            section.IsBeingDragged = false;
        }
    }

    // ---- Sections ------------------------------------------------------------

    /// <summary>
    /// Adds a section at the end of the list and opens its name for typing. Projects get into it
    /// by being dragged onto its header, or walked in with the arrows.
    /// </summary>
    [RelayCommand]
    private void AddSection()
    {
        var section = Wrap(new ProjectSection());
        _sections.Add(section);

        Persist();
        ApplyFilter();

        section.Rename();
    }

    /// <summary>
    /// Unfolds every section and every project. Nothing is fetched for it: a project with no
    /// alerts opens onto its "load earlier commits" button, as it does when opened by hand.
    /// </summary>
    [RelayCommand]
    private void ExpandAll() => SetEverythingExpanded(true);

    /// <summary>Folds every project and every section, down to a list of headers.</summary>
    [RelayCommand]
    private void CollapseAll() => SetEverythingExpanded(false);

    private void SetEverythingExpanded(bool expanded)
    {
        foreach (var project in _projects.Values)
        {
            project.IsExpanded = expanded;
        }

        var foldsChanged = false;

        foreach (var section in _sections.Where(s => s.IsExpanded != expanded))
        {
            section.IsExpanded = expanded;
            foldsChanged = true;
        }

        // The sections' folds are saved; the projects' are not, as before.
        if (foldsChanged)
        {
            Persist();
        }

        ApplyFilter();
    }

    private ProjectSectionViewModel Wrap(ProjectSection model) =>
        new(model, OnSectionChanged, MoveSection, RemoveSection, MarkSectionRead);

    /// <summary>A fold or a new name: worth saving, and a fold changes which rows show.</summary>
    private void OnSectionChanged(ProjectSectionViewModel section)
    {
        Persist();
        ApplyFilter();
    }

    private void MoveSection(ProjectSectionViewModel section, int delta)
    {
        var from = _sections.IndexOf(section);
        var to = from + delta;

        if (from < 0 || to < 0 || to >= _sections.Count)
        {
            return;
        }

        (_sections[from], _sections[to]) = (_sections[to], _sections[from]);

        NormaliseOrder();
        Persist();
        ApplyFilter();
    }

    /// <summary>
    /// Puts one section directly above or below another, with its projects. This is what dropping
    /// a dragged section header does; the arrows still move a section one step at a time.
    /// </summary>
    public void PlaceSection(ProjectSectionViewModel moved, ProjectSectionViewModel target, bool above)
    {
        var from = _sections.IndexOf(moved);

        if (from < 0 || ReferenceEquals(moved, target) || !_sections.Contains(target))
        {
            return;
        }

        _sections.RemoveAt(from);

        var to = _sections.IndexOf(target) + (above ? 0 : 1);

        // Back where it came from: nothing to save and nothing to redraw.
        if (to == from)
        {
            _sections.Insert(from, moved);
            return;
        }

        _sections.Insert(to, moved);

        NormaliseOrder();
        Persist();
        ApplyFilter();
    }

    /// <summary>
    /// Dissolves a section. Its projects stay, loose, after the other loose ones - which is where
    /// the loose area ends - and in the order they had.
    /// </summary>
    private void RemoveSection(ProjectSectionViewModel section)
    {
        if (!_sections.Remove(section))
        {
            return;
        }

        NormaliseOrder();
        Persist();
        ApplyFilter();
    }

    /// <summary>The section a project is in, or null for a loose one.</summary>
    private ProjectSectionViewModel? SectionOf(string repository) =>
        _sections.FirstOrDefault(s => s.Contains(repository));

    /// <summary>Puts a project in a section, or out of every section when given none.</summary>
    private void Assign(string repository, ProjectSectionViewModel? section)
    {
        foreach (var other in _sections)
        {
            other.Remove(repository);
        }

        section?.Add(repository);
    }

    /// <summary>
    /// Lays the total order out the way the list shows it: the loose projects, then each section's
    /// in turn, nothing moving within an area. Done after every edit, so a project crossing into a
    /// neighbouring area needs no new place in the order - it is already at the edge.
    /// </summary>
    private void NormaliseOrder()
    {
        var ranked = OrderForEditing();

        List<string> order =
        [
            .. ranked.Where(r => SectionOf(r) is null),
            .. _sections.SelectMany(s => ranked.Where(r => ReferenceEquals(SectionOf(r), s))),
        ];

        _order.Clear();
        _order.AddRange(order);
    }

    /// <summary>
    /// One place a project can stand: an area - a section, or null for the loose projects - and
    /// the project standing there, or none for an empty area.
    /// </summary>
    private readonly record struct Slot(ProjectSectionViewModel? Section, ProjectGroupViewModel? Project);

    /// <summary>
    /// The places the arrows walk a project through, top to bottom: every project on screen, plus
    /// one empty place for each area with nothing in it, so a project can be walked into an empty
    /// section, or out of the first section when nothing is loose above it.
    /// </summary>
    private List<Slot> Slots()
    {
        var slots = new List<Slot>();
        var loose = Groups.Where(g => !g.IsInSection).ToList();

        // An empty loose area is only a place to go once there are sections to leave.
        if (loose.Count == 0 && _sections.Count > 0)
        {
            slots.Add(new Slot(null, null));
        }

        slots.AddRange(loose.Select(g => new Slot(null, g)));

        foreach (var section in _sections)
        {
            var projects = Groups.Where(g => section.Contains(g.Repository)).ToList();

            if (projects.Count == 0)
            {
                // Out of sight while showing unread only, so not a place to walk a project into.
                if (!UnreadOnly)
                {
                    slots.Add(new Slot(section, null));
                }

                continue;
            }

            slots.AddRange(projects.Select(g => new Slot(section, g)));
        }

        return slots;
    }

    private void Persist() =>
        _shell.SaveListPreferences(new ListPreferences(_order, _sections.Select(s => s.Model).ToList(), UnreadOnly));

    private void RebuildGroups()
    {
        var byRepository = Alerts
            .GroupBy(a => a.Repository, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Every project in view, laid out the way the list shows them: loose first, then section
        // by section.
        var inView = ProjectsInView();
        var groups = new List<ProjectGroupViewModel>();

        foreach (var section in (ProjectSectionViewModel?[])[null, .. _sections])
        {
            foreach (var repository in inView.Where(r => ReferenceEquals(SectionOf(r), section)))
            {
                var group = GroupFor(repository, byRepository.GetValueOrDefault(repository, []));
                group.IsInSection = section is not null;
                groups.Add(group);
            }
        }

        Sync(Groups, groups);
        PruneProjects();

        // What is on screen. A folded section keeps its projects out of the rows, and while
        // showing unread only a section with nothing to show stays out of the way, like its projects.
        var rows = new List<object>();
        rows.AddRange(groups.Where(g => !g.IsInSection));

        foreach (var section in _sections)
        {
            var projects = groups.Where(g => section.Contains(g.Repository)).ToList();
            section.ProjectCount = projects.Count;

            if (UnreadOnly && projects.Count == 0)
            {
                continue;
            }

            rows.Add(section);

            if (section.IsExpanded)
            {
                rows.AddRange(projects);
            }
        }

        Sync(Rows, rows);

        // The arrows: a project may walk anywhere along the places, a section anywhere among the sections.
        var slots = Slots();

        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Project is { } project)
            {
                project.CanMoveUp = i > 0;
                project.CanMoveDown = i < slots.Count - 1;
            }
        }

        for (var i = 0; i < _sections.Count; i++)
        {
            _sections[i].CanMoveUp = i > 0;
            _sections[i].CanMoveDown = i < _sections.Count - 1;
        }
    }

    /// <summary>
    /// The group for a project - the one from last time where there is one, with its alerts
    /// brought up to date.
    /// </summary>
    private ProjectGroupViewModel GroupFor(string repository, List<AlertViewModel> alerts)
    {
        var accountId = AccountIdFor(repository);

        // A project whose account changed has to start over: its history would be fetched
        // with a token that no longer reaches it.
        if (_projects.TryGetValue(repository, out var group)
            && !string.Equals(group.AccountId, accountId, StringComparison.Ordinal))
        {
            _projects.Remove(repository);
            group = null;
        }

        if (group is null)
        {
            group = new ProjectGroupViewModel(repository, accountId, LoadHistoryPageAsync, MoveProject, MarkProjectRead);
            _projects[repository] = group;

            group.SetAlerts(alerts);

            // First sight of a project: open when it has something to say, folded otherwise.
            // After that it is the user's own choice, held on the group itself.
            group.IsExpanded = group.Items.Count > 0;
        }
        else
        {
            group.SetAlerts(alerts);
        }

        return group;
    }

    /// <summary>
    /// Brings a collection to new contents with the fewest changes. What stays keeps its row
    /// container, and with it the focus, the hover, and the header still finishing the click that
    /// asked for this; clearing and refilling on every poll threw all of that away to arrive at
    /// what was already on screen.
    /// </summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
            {
                continue;
            }

            var current = target.IndexOf(desired[i]);

            if (current >= 0)
            {
                target.RemoveAt(current);
            }

            target.Insert(i, desired[i]);
        }
    }

    /// <summary>Forgets groups for projects GitAlert no longer knows about.</summary>
    private void PruneProjects()
    {
        var known = AllProjects().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stale in _projects.Keys.Where(r => !known.Contains(r)).ToList())
        {
            _projects.Remove(stale);
        }
    }

    /// <summary>
    /// The total order a move or a drop edits: every project in view plus the ones switched off in
    /// settings, which are out of sight but keep their place for when the tick comes back. Without
    /// them the first reorder wrote the list back without the hidden project, and it came back last.
    /// </summary>
    private List<string> OrderForEditing()
    {
        var hidden = _store.Hidden;

        return
        [
            .. AllProjects()
                .Concat(hidden)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Rank)
                .ThenBy(r => r, StringComparer.OrdinalIgnoreCase),
        ];
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

    /// <summary>
    /// The palette changed: every card's glyph colour comes from it, and the rows would otherwise
    /// keep the brushes they were built with until the next rebuild.
    /// </summary>
    public void RefreshAccents()
    {
        foreach (var alert in _all.Concat(Groups.SelectMany(g => g.Items)).Distinct())
        {
            alert.RefreshAccent();
        }
    }

    /// <summary>Re-reads the store after settings changed the history size or cleared it.</summary>
    public void Reload()
    {
        _all.Clear();
        _all.AddRange(_store.Snapshot.Select(Create));

        // The rows the groups hold are wrappers around models that have just been replaced.
        _projects.Clear();
        ApplyFilter();

        // The selected alert is a wrapper around a model that has just been replaced, so the
        // pane would otherwise keep a row that is no longer in the list.
        if (SelectedAlert is not null && !_all.Contains(SelectedAlert))
        {
            SelectedAlert = null;
            _ = Detail.ShowAsync(null);
        }
    }

    public void Dispose()
    {
        _monitor.AlertsReceived -= OnAlertsReceived;
        _monitor.StatusChanged -= OnStatusChanged;
        _ageTimer.Dispose();
        Detail.Dispose();
    }
}
