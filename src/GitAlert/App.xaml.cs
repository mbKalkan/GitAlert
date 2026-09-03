using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.Services;

namespace GitAlert;

/// <summary>
/// Composition root and process lifetime. GitAlert has no main window: the tray icon is the
/// application, so the shutdown mode is explicit and everything hangs off <see cref="TrayApplication"/>.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\GitAlert.SingleInstance";
    private const string ActivationEventName = @"Local\GitAlert.Activate";

    /// <summary>Used by the build to regenerate <c>Resources/app.ico</c> from the vector artwork.</summary>
    private const string ExportIconSwitch = "--export-icon";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationListener;

    private MonitorService? _monitor;
    private TrayApplication? _shell;
    private AlertStore? _alerts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryExportIcon(e.Args))
        {
            Shutdown(0);
            return;
        }

        if (!ClaimSingleInstance())
        {
            // Another copy is already in the tray; ask it to show itself and step aside.
            SignalRunningInstance();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        AppPaths.EnsureCreated();

        var settingsStore = new SettingsStore();
        var tokenStore = new SecureTokenStore();
        var settings = settingsStore.Load();

        // An install that predates multi-account support keeps working: its single token becomes
        // one account and every repository is attached to it.
        if (SettingsMigration.Apply(settings, tokenStore))
        {
            settingsStore.Save(settings);
        }

        ThemeService.Apply(settings.Theme);

        _alerts = new AlertStore { MaxHistory = settings.MaxHistory };
        _alerts.Load();

        // A repository removed while GitAlert was not running leaves its alerts behind in the
        // history file. Reconcile before anything has a chance to count them or list the project.
        if (_alerts.RemoveUnwatched(settings.Repositories.Where(r => r.Enabled).Select(r => r.FullName)) > 0)
        {
            _alerts.Save();
        }

        _monitor = new MonitorService(_alerts, new StateStore());
        _monitor.Configure(settings, tokenStore.ReadAll(settings.Accounts.Select(a => a.Id)));

        _shell = new TrayApplication(settingsStore, tokenStore, _alerts, _monitor, settings);

        StartActivationListener();

        _monitor.Start();

        // A first run has nothing to show, so take the user straight to setup - unless Windows
        // started us at sign-in, where popping a window would be rude.
        var launchedAtLogon = e.Args.Contains(StartupManager.StartupArgument, StringComparer.OrdinalIgnoreCase);

        if (!launchedAtLogon && (settings.Accounts.Count == 0 || settings.Repositories.Count == 0))
        {
            _shell.PromptForSetup();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationListener?.Cancel();
        _shell?.Dispose();

        if (_monitor is not null)
        {
            _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _alerts?.Save();

        _activationEvent?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Renders the application icon from <see cref="IconArtwork"/> and exits. Keeping this in the
    /// app means the icon file and the on-screen artwork can never drift apart.
    /// </summary>
    private static bool TryExportIcon(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(ExportIconSwitch, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length)
        {
            return false;
        }

        IconFactory.WriteApplicationIcon(args[index + 1]);
        return true;
    }

    private bool ClaimSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        return isFirst;
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is shutting down; nothing to activate.
        }
    }

    /// <summary>Watches for a second launch and opens the flyout when one happens.</summary>
    private void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationListener = new CancellationTokenSource();

        var token = _activationListener.Token;
        var handle = _activationEvent;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (handle.WaitOne(TimeSpan.FromMilliseconds(500)) && !token.IsCancellationRequested)
                {
                    // Show the window, not the setup prompt: whoever launched GitAlert again
                    // wants to see their alerts, and is already past being told to add an account.
                    Dispatcher.InvokeAsync(() => _shell?.ShowFlyout());
                }
            }
        })
        {
            IsBackground = true,
            Name = "GitAlert.ActivationListener",
        };

        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);

        // A failed poll or a rendering hiccup must not take the tray icon down with it.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log(exception);
        }
    }

    /// <summary>Past this the error log is rolled, so a repeating fault cannot fill the disk.</summary>
    private const long MaxLogBytes = 1024 * 1024;

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
