using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.ViewModels;
using GitAlert.Views;

namespace GitAlert.Services;

/// <summary>
/// The application shell. It owns the tray icon and the two windows, wires the monitor's events to
/// what the user actually sees, and is the only place that knows the app has no main window.
/// </summary>
public sealed class TrayShell : IShellCommands, ISettingsHost, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly ISecretStore _tokenStore;
    private readonly AlertStore _alerts;
    private readonly MonitorService _monitor;
    private readonly IPlatform _platform;
    private readonly ThemeService _theme;

    private readonly ITrayHost _tray;
    private readonly TrayMenu _menu;
    private readonly FlyoutViewModel _flyoutViewModel;
    private readonly FlyoutWindow _flyout;

    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;

    /// <summary>Remembered so a click on the notification can open the thing it was about.</summary>
    private Alert? _lastToastAlert;

    private AppSettings _settings;
    private bool _disposed;

    public TrayShell(
        SettingsStore settingsStore,
        ISecretStore tokenStore,
        AlertStore alerts,
        MonitorService monitor,
        AppSettings settings,
        IPlatform platform,
        ThemeService theme)
    {
        _settingsStore = settingsStore;
        _tokenStore = tokenStore;
        _alerts = alerts;
        _monitor = monitor;
        _settings = settings;
        _platform = platform;
        _theme = theme;

        _tray = platform.CreateTray();
        _tray.Activated += OnTrayActivated;
        _tray.ContextMenuRequested += OnTrayContextMenu;
        _tray.NotificationClicked += OnNotificationClicked;

        _flyoutViewModel = new FlyoutViewModel(_alerts, _monitor, this, settings);
        _flyout = new FlyoutWindow(_flyoutViewModel, platform);
        _flyout.ApplyPreferences(settings);
        _flyout.PlacementChanged += OnPlacementChanged;

        _menu = new TrayMenu(platform,
        [
            new TrayMenu.Entry("Open GitAlert", OpenFlyoutAtTray, Bold: true),
            new TrayMenu.Entry("Check now", () => _monitor.RequestRefresh()),
            TrayMenu.Entry.Separator,
            new TrayMenu.Entry("Mark all read", () => _flyoutViewModel.MarkAllReadCommand.Execute(null)),
            new TrayMenu.Entry("Settings…", ShowSettings),
            TrayMenu.Entry.Separator,
            new TrayMenu.Entry("Quit", Quit),
        ]);

        _monitor.AlertsReceived += OnAlertsReceived;
        _monitor.StatusChanged += OnStatusChanged;
        _monitor.AccountResolved += OnAccountResolved;

        _theme.Applied += OnThemeApplied;

        UpdateTrayPresentation(_monitor.Status);
    }

    /// <summary>Opens settings straight away, for a first run with nothing configured.</summary>
    public void PromptForSetup()
    {
        ShowSettings();

        _tray.ShowNotification(
            "GitAlert is running",
            "Add a GitHub account, then the repositories you want to watch.",
            NotificationKind.Info,
            playSound: false);
    }

    // ---- IShellCommands ----------------------------------------------------

    public void ShowSettings()
    {
        _flyout.HideFlyout();

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            _platform.TakeForeground(_settingsWindow);
            return;
        }

        var viewModel = new SettingsViewModel(_settingsStore, _tokenStore, this, _platform.Startup);

        _settingsViewModel = viewModel;
        _settingsWindow = new SettingsWindow(viewModel, _platform, _theme);
        _settingsWindow.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _settingsViewModel = null;
            _settingsWindow = null;
        };

        _settingsWindow.Show();
        _settingsWindow.Activate();

        // Settings is usually opened from the tray menu, and a window opened while the shell owns
        // the foreground is allowed to appear but not to come forward. Same reason the flyout needs it.
        _platform.TakeForeground(_settingsWindow);
    }

    public void HideFlyout() => _flyout.HideFlyout();

    /// <summary>
    /// Brings the window up beside the tray icon. This is what launching GitAlert a second time
    /// does: the copy already running is asked to show itself.
    /// </summary>
    public void ShowFlyout() => OpenFlyoutAtTray();

    /// <summary>
    /// The order of the projects and whether read alerts are hidden are set in the list itself,
    /// so they are written back from there rather than through the settings window.
    /// </summary>
    public void SaveListPreferences(IReadOnlyList<string> projectOrder, bool unreadOnly)
    {
        _settings.ProjectOrder = [.. projectOrder];
        _settings.UnreadOnly = unreadOnly;
        _settingsStore.Save(_settings);
    }

    public void Quit()
    {
        _alerts.Save();

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>The list read or cleared something; the tray icon carries the same count.</summary>
    public void UnreadChanged() => UpdateTrayPresentation(_monitor.Status);

    // ---- ISettingsHost -----------------------------------------------------

    public void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens)
    {
        _settings = settings;

        _theme.Apply(settings.Theme, settings.DarkPalette);
        _alerts.MaxHistory = settings.MaxHistory;
        _flyout.ApplyPreferences(settings);
        _monitor.Configure(settings, tokens);

        // A repository that is no longer watched takes its alerts with it. One that is only
        // switched off keeps them in the history, out of sight until it is switched back on.
        if (_alerts.RemoveUnwatched(settings.Repositories.Select(r => r.FullName)) > 0)
        {
            _alerts.Save();
        }

        _alerts.Hide(settings.SwitchedOffRepositories);

        // And the list is rebuilt either way: it is the only thing that knows which projects exist.
        _flyoutViewModel.Reload();
        UpdateTrayPresentation(_monitor.Status);

        _monitor.RequestRefresh();
    }

    /// <summary>
    /// The window was moved, resized or pinned. Persisting it here rather than on every drag
    /// keeps settings.json quiet while still surviving a restart.
    /// </summary>
    private void OnPlacementChanged(object? sender, EventArgs e)
    {
        _flyout.CapturePreferences(_settings);
        _settingsStore.Save(_settings);
    }

    /// <summary>
    /// The monitor learned an account's login by using its token. Record it so the settings list
    /// shows a name rather than "Unverified account".
    /// </summary>
    private void OnAccountResolved(object? sender, AccountIdentity identity) =>
        Dispatcher.UIThread.Post(() =>
        {
            // An open settings window holds its own copy, loaded before the login was known.
            _settingsViewModel?.ApplyResolvedLogin(identity.AccountId, identity.Login);

            var account = _settings.FindAccount(identity.AccountId);

            if (account is null || string.Equals(account.Login, identity.Login, StringComparison.Ordinal))
            {
                return;
            }

            account.Login = identity.Login;
            _settingsStore.Save(_settings);
        });

    public void ResetMonitorState()
    {
        _monitor.ResetState();
        _monitor.RequestRefresh();
    }

    public void ClearHistory()
    {
        _alerts.Clear();
        _alerts.Save();
        _flyoutViewModel.Reload();
        UpdateTrayPresentation(_monitor.Status);
    }

    public void CloseSettings() => _settingsWindow?.Close();

    // ---- Tray interaction --------------------------------------------------

    private void OnTrayActivated(object? sender, ScreenPoint screenPoint) => _flyout.ToggleFromTray(screenPoint);

    private void OnTrayContextMenu(object? sender, ScreenPoint screenPoint)
    {
        _flyout.HideFlyout();
        _menu.ShowAt(screenPoint);
    }

    private void OpenFlyoutAtTray()
    {
        // Anchor on the far corner of the primary work area; close enough to the icon in the
        // common case, and always inside the screen.
        var work = _flyout.Screens.Primary?.WorkingArea ?? _flyout.Screens.All.FirstOrDefault()?.WorkingArea;
        var anchor = work is { } area ? new ScreenPoint(area.Right, area.Bottom) : new ScreenPoint(0, 0);

        _flyout.ShowAt(anchor);
    }

    // ---- Monitor events ----------------------------------------------------

    private void OnAlertsReceived(object? sender, IReadOnlyList<Alert> alerts) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTrayPresentation(_monitor.Status);

            if (_settings.ShowToasts && alerts.Count > 0)
            {
                ShowToast(alerts);
            }
        });

    private void OnStatusChanged(object? sender, MonitorStatus status) =>
        Dispatcher.UIThread.Post(() => UpdateTrayPresentation(status));

    private void ShowToast(IReadOnlyList<Alert> alerts)
    {
        var newest = alerts[0];
        _lastToastAlert = alerts.Count == 1 ? newest : null;

        var (title, body) = alerts.Count == 1
            ? (newest.ToastTitle, newest.ToastBody)
            : ($"{alerts.Count} new alerts", Summarise(alerts));

        var kind = alerts.Any(a => a.Severity == AlertSeverity.Error)
            ? NotificationKind.Warning
            : NotificationKind.Info;

        _tray.ShowNotification(title, body, kind, _settings.PlaySound);
    }

    private static string Summarise(IReadOnlyList<Alert> alerts)
    {
        var repositories = alerts
            .Select(a => a.Repository)
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var lead = alerts[0].Title;

        return repositories.Count switch
        {
            0 => lead,
            1 => $"{lead} — {repositories[0]}",
            _ => $"{lead} — {string.Join(", ", repositories)}",
        };
    }

    private void OnNotificationClicked(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_lastToastAlert is { Url: { } url } alert && Browser.Open(url))
            {
                _alerts.MarkRead(alert.Id);
                _alerts.Save();
                _flyoutViewModel.Reload();
                UpdateTrayPresentation(_monitor.Status);
                return;
            }

            OpenFlyoutAtTray();
        });

    private void UpdateTrayPresentation(MonitorStatus status)
    {
        var unread = _alerts.UnreadCount;

        var state = status.State switch
        {
            ConnectionState.Error => TrayState.Error,
            ConnectionState.Warning => TrayState.Warning,
            _ when unread > 0 => TrayState.Unread,
            _ => TrayState.Idle,
        };

        _tray.SetState(state, unread > 0);

        var headline = unread switch
        {
            0 => "GitAlert",
            1 => "GitAlert — 1 unread alert",
            _ => $"GitAlert — {unread} unread alerts",
        };

        _tray.Tooltip = $"{headline}\n{status.Message}";
    }

    // ---- Appearance --------------------------------------------------------

    private void OnThemeApplied(object? sender, EventArgs e)
    {
        // The tray glyph is drawn in the bar's contrast colour, so it has to be redrawn too.
        _tray.Refresh();

        // So do the glyphs on the cards: their colours come from the palette, and the rows keep
        // the brush they were built with until told otherwise.
        _flyoutViewModel.RefreshAccents();
        UpdateTrayPresentation(_monitor.Status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _theme.Applied -= OnThemeApplied;
        _monitor.AlertsReceived -= OnAlertsReceived;
        _monitor.StatusChanged -= OnStatusChanged;
        _monitor.AccountResolved -= OnAccountResolved;

        _flyout.PlacementChanged -= OnPlacementChanged;
        _flyout.CapturePreferences(_settings);
        _settingsStore.Save(_settings);

        _flyoutViewModel.Dispose();
        _flyout.CloseForGood();
        _settingsWindow?.Close();
        _menu.Dispose();
        _tray.Dispose();
    }
}
