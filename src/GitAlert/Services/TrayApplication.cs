using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.ViewModels;
using GitAlert.Views;
using Microsoft.Win32;

namespace GitAlert.Services;

/// <summary>
/// The application shell. It owns the tray icon and the two windows, wires the monitor's events to
/// what the user actually sees, and is the only place that knows the app has no main window.
/// </summary>
public sealed class TrayApplication : IShellCommands, ISettingsHost, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly SecureTokenStore _tokenStore;
    private readonly AlertStore _alerts;
    private readonly MonitorService _monitor;
    private readonly Dispatcher _dispatcher;

    private readonly TrayIcon _tray;
    private readonly FlyoutViewModel _flyoutViewModel;
    private readonly FlyoutWindow _flyout;

    private SettingsWindow? _settingsWindow;
    private ContextMenu? _menu;

    /// <summary>Remembered so a click on the toast can open the thing it was about.</summary>
    private Alert? _lastToastAlert;

    private AppSettings _settings;
    private bool _disposed;

    public TrayApplication(
        SettingsStore settingsStore,
        SecureTokenStore tokenStore,
        AlertStore alerts,
        MonitorService monitor,
        AppSettings settings)
    {
        _settingsStore = settingsStore;
        _tokenStore = tokenStore;
        _alerts = alerts;
        _monitor = monitor;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _tray = new TrayIcon();
        _tray.Activated += OnTrayActivated;
        _tray.ContextMenuRequested += OnTrayContextMenu;
        _tray.BalloonClicked += OnBalloonClicked;

        _flyoutViewModel = new FlyoutViewModel(_alerts, _monitor, this, settings);
        _flyout = new FlyoutWindow(_flyoutViewModel);
        _flyout.ApplyPreferences(settings);
        _flyout.PlacementChanged += OnPlacementChanged;

        // Create the window handle without showing anything: the context menu needs a real
        // foreground window to dismiss itself against, even before the flyout is first opened.
        new WindowInteropHelper(_flyout).EnsureHandle();

        _monitor.AlertsReceived += OnAlertsReceived;
        _monitor.StatusChanged += OnStatusChanged;
        _monitor.AccountResolved += OnAccountResolved;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ThemeService.Applied += OnThemeApplied;

        UpdateTrayPresentation(_monitor.Status);
    }

    /// <summary>Opens settings straight away, for a first run with nothing configured.</summary>
    public void PromptForSetup()
    {
        ShowSettings();

        _tray.ShowBalloon(
            "GitAlert is running",
            "Add a GitHub account, then the repositories you want to watch.",
            BalloonIcon.Info,
            playSound: false);
    }

    // ---- IShellCommands ----------------------------------------------------

    public void ShowSettings()
    {
        _flyout.HideFlyout();

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            BringToFront(_settingsWindow);
            return;
        }

        var viewModel = new SettingsViewModel(_settingsStore, _tokenStore, this);

        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _settingsWindow = null;
        };

        _settingsWindow.Show();
        _settingsWindow.Activate();
        BringToFront(_settingsWindow);
    }

    /// <summary>
    /// Settings is usually opened from the tray menu, and a window opened while Explorer owns the
    /// foreground is allowed to appear but not to come forward. Same reason the flyout needs it.
    /// </summary>
    private static void BringToFront(Window window) =>
        NativeMethods.ForceForeground(new WindowInteropHelper(window).Handle);

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
        Application.Current.Shutdown();
    }

    // ---- ISettingsHost -----------------------------------------------------

    public void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens)
    {
        _settings = settings;

        ThemeService.Apply(settings.Theme);
        _alerts.MaxHistory = settings.MaxHistory;
        _flyout.ApplyPreferences(settings);
        _monitor.Configure(settings, tokens);
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
    /// shows a name rather than "Unverified account", which matters most right after the
    /// single-token settings file has been migrated.
    /// </summary>
    private void OnAccountResolved(object? sender, AccountIdentity identity) =>
        _dispatcher.InvokeAsync(() =>
        {
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

    private void OnTrayActivated(object? sender, Point screenPoint) => _flyout.ToggleFromTray(screenPoint);

    private void OnTrayContextMenu(object? sender, Point screenPoint)
    {
        _flyout.HideFlyout();

        _menu ??= BuildMenu();

        // Without a foreground window the menu would not close when the user clicks elsewhere.
        if (PresentationSource.FromVisual(_flyout) is HwndSource source)
        {
            NativeMethods.SetForegroundWindow(source.Handle);
        }

        var scale = VisualTreeHelper.GetDpi(_flyout);

        _menu.Placement = PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = screenPoint.X / scale.DpiScaleX;
        _menu.VerticalOffset = screenPoint.Y / scale.DpiScaleY;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)Application.Current.Resources["TrayMenu"],
        };

        var itemStyle = (Style)Application.Current.Resources["TrayMenuItem"];
        var separatorStyle = (Style)Application.Current.Resources["TrayMenuSeparator"];

        MenuItem Item(string header, Action action, bool bold = false)
        {
            var item = new MenuItem { Header = header, Style = itemStyle };

            if (bold)
            {
                item.FontWeight = FontWeights.SemiBold;
            }

            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Open GitAlert", OpenFlyoutAtTray, bold: true));
        menu.Items.Add(Item("Check now", () => _monitor.RequestRefresh()));
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(Item("Mark all read", () => _flyoutViewModel.MarkAllReadCommand.Execute(null)));
        menu.Items.Add(Item("Settings…", ShowSettings));
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(Item("Quit", Quit));

        return menu;
    }

    private void OpenFlyoutAtTray()
    {
        // Anchor on the far corner of the primary work area; close enough to the icon in the
        // common case, and always inside the screen.
        var work = SystemParameters.WorkArea;
        var scale = VisualTreeHelper.GetDpi(_flyout);

        _flyout.ShowAt(new Point(work.Right * scale.DpiScaleX, work.Bottom * scale.DpiScaleY));
        BringToFront(_flyout);
    }

    // ---- Monitor events ----------------------------------------------------

    private void OnAlertsReceived(object? sender, IReadOnlyList<Alert> alerts) =>
        _dispatcher.InvokeAsync(() =>
        {
            UpdateTrayPresentation(_monitor.Status);

            if (_settings.ShowToasts && alerts.Count > 0)
            {
                ShowToast(alerts);
            }
        });

    private void OnStatusChanged(object? sender, MonitorStatus status) =>
        _dispatcher.InvokeAsync(() => UpdateTrayPresentation(status));

    private void ShowToast(IReadOnlyList<Alert> alerts)
    {
        var newest = alerts[0];
        _lastToastAlert = alerts.Count == 1 ? newest : null;

        var (title, body) = alerts.Count == 1
            ? (newest.ToastTitle, newest.ToastBody)
            : ($"{alerts.Count} new alerts", Summarise(alerts));

        var icon = alerts.Any(a => a.Severity == AlertSeverity.Error)
            ? BalloonIcon.Warning
            : BalloonIcon.Info;

        _tray.ShowBalloon(title, body, icon, _settings.PlaySound);
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

    private void OnBalloonClicked(object? sender, EventArgs e) =>
        _dispatcher.InvokeAsync(() =>
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

    // ---- Windows appearance ------------------------------------------------

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            ThemeService.Reapply();
            SystemTheme.Raise();
        });
    }

    private void OnThemeApplied(object? sender, EventArgs e)
    {
        // The tray glyph is drawn in the taskbar's contrast colour, so it has to be redrawn too.
        _tray.Refresh();
        UpdateTrayPresentation(_monitor.Status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ThemeService.Applied -= OnThemeApplied;
        _monitor.AlertsReceived -= OnAlertsReceived;
        _monitor.StatusChanged -= OnStatusChanged;
        _monitor.AccountResolved -= OnAccountResolved;

        _flyout.PlacementChanged -= OnPlacementChanged;
        _flyout.CapturePreferences(_settings);
        _settingsStore.Save(_settings);

        _flyoutViewModel.Dispose();
        _flyout.CloseForGood();
        _settingsWindow?.Close();
        _tray.Dispose();
    }
}
