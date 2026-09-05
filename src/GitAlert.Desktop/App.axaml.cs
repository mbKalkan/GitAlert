using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.Services;

namespace GitAlert;

/// <summary>
/// Composition root and process lifetime. GitAlert has no main window: the tray icon is the
/// application, so the shutdown mode is explicit and everything hangs off <see cref="TrayShell"/>.
/// </summary>
public partial class App : Application
{
    /// <summary>Past this the error log is rolled, so a repeating fault cannot fill the disk.</summary>
    private const long MaxLogBytes = 1024 * 1024;

    private MonitorService? _monitor;
    private TrayShell? _shell;
    private AlertStore? _alerts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        desktop.Exit += (_, _) => TearDown();

        try
        {
            Compose(desktop.Args ?? []);
        }
        catch (Exception ex)
        {
            // Without the shell there is no tray icon, no window and no way to quit. Left to the
            // handler above, a failure here would leave a process sitting invisible in the list.
            Log(ex);
            Console.Error.WriteLine($"GitAlert could not start: {ex.Message}. Details were written to {AppPaths.LogFile}.");
            desktop.Shutdown(1);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Wires the application together, in dependency order.</summary>
    private void Compose(string[] args)
    {
        AppPaths.EnsureCreated();

        var platform = Platforms.Create();
        var settingsStore = new SettingsStore();
        var tokenStore = platform.CreateSecretStore();
        var settings = settingsStore.Load();

        // An install that predates multi-account support keeps working: its single token becomes
        // one account and every repository is attached to it.
        if (SettingsMigration.Apply(settings, tokenStore))
        {
            settingsStore.Save(settings);
        }

        var theme = new ThemeService(this);
        theme.Apply(settings.Theme, settings.DarkPalette);

        _alerts = new AlertStore { MaxHistory = settings.MaxHistory };
        _alerts.Load();

        // A repository removed while GitAlert was not running leaves its alerts behind in the
        // history file. Reconcile before anything has a chance to count them or list the project.
        // A repository that is merely switched off keeps its history, out of sight: pausing is
        // not removing.
        if (_alerts.RemoveUnwatched(settings.Repositories.Select(r => r.FullName)) > 0)
        {
            _alerts.Save();
        }

        _alerts.Hide(settings.SwitchedOffRepositories);

        _monitor = new MonitorService(_alerts, new StateStore());
        _monitor.Configure(settings, tokenStore.ReadAll(settings.Accounts.Select(a => a.Id)));

        _shell = new TrayShell(settingsStore, tokenStore, _alerts, _monitor, settings, platform, theme);

        _monitor.Start();

        // A first run has nothing to show, so take the user straight to setup - unless the system
        // started us at sign-in, where popping a window would be rude.
        var launchedAtLogon = args.Contains(IStartupRegistrar.LaunchArgument, StringComparer.OrdinalIgnoreCase);

        if (!launchedAtLogon && (settings.Accounts.Count == 0 || settings.Repositories.Count == 0))
        {
            _shell.PromptForSetup();
        }
    }

    /// <summary>A second launch, or a click on a notification: bring the window up.</summary>
    public void ShowFlyout() => _shell?.ShowFlyout();

    private void TearDown()
    {
        _shell?.Dispose();

        if (_monitor is not null)
        {
            _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _alerts?.Save();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);

        // A failed poll or a rendering hiccup must not take the tray icon down with it.
        e.Handled = true;

        if (_shell is null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Unless there is no tray icon to keep: carrying on would leave an invisible process.
            desktop.Shutdown(1);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log(exception);
        }
    }

    private static void Log(Exception exception)
    {
        try
        {
            AppPaths.EnsureCreated();
            AppPaths.Roll(AppPaths.LogFile, MaxLogBytes);

            File.AppendAllText(
                AppPaths.LogFile,
                $"[{DateTimeOffset.Now:u}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // If even logging fails there is nowhere left to report it.
        }
    }
}
