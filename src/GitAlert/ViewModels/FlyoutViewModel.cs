using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
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

        _all.AddRange(_store.Snapshot.Select(a => new AlertViewModel(a)));
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

    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    public ObservableCollection<FilterChipViewModel> Filters { get; }

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
    private void OpenAlert(AlertViewModel? alert)
    {
        if (alert is null)
        {
            return;
        }

        MarkRead(alert);

        if (Browser.Open(alert.Url))
        {
            _shell.HideFlyout();
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
    private void ClearHistory()
    {
        _store.Clear();
        _store.Save();
        _all.Clear();
        UnreadCount = 0;
        ApplyFilter();
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
                _all.Insert(0, new AlertViewModel(alert));
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
            _ => ActiveFilter == AlertFilter.All
                ? "You are all caught up."
                : "Nothing here yet.",
        };

    private void ApplyFilter()
    {
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Filter == ActiveFilter;
            chip.Count = chip.Filter == AlertFilter.All
                ? _all.Count(a => !a.IsRead)
                : _all.Count(a => !a.IsRead && a.Group == chip.Filter);
        }

        Alerts.Clear();

        foreach (var alert in _all.Where(a => ActiveFilter == AlertFilter.All || a.Group == ActiveFilter))
        {
            Alerts.Add(alert);
        }

        IsEmpty = Alerts.Count == 0;
        UpdateEmptyMessage(_monitor.Status);
    }

    /// <summary>Keeps the in-memory list aligned with the trimmed, persisted history.</summary>
    private void TrimToStore()
    {
        var kept = _store.Snapshot.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        _all.RemoveAll(a => !kept.Contains(a.Model.Id));
    }

    private void RefreshAges()
    {
        foreach (var alert in Alerts)
        {
            alert.RefreshAge();
        }

        UpdateLastUpdated();
    }

    /// <summary>Re-reads the store after settings changed the history size or cleared it.</summary>
    public void Reload()
    {
        _all.Clear();
        _all.AddRange(_store.Snapshot.Select(a => new AlertViewModel(a)));
        UnreadCount = _store.UnreadCount;
        ApplyFilter();
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
    }
}
