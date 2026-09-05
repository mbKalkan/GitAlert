using Avalonia;
using Avalonia.Controls;
using GitAlert.Graphics;
using GitAlert.Platform;

namespace GitAlert;

internal static class Program
{
    /// <summary>Used by the build to regenerate <c>app.ico</c> from the vector artwork.</summary>
    private const string ExportIconSwitch = "--export-icon";

    [STAThread]
    public static int Main(string[] args)
    {
        if (TryExportIcon(args))
        {
            return 0;
        }

        using var instance = SingleInstance.TryAcquire();

        if (instance is null)
        {
            // Another copy is already in the tray; ask it to show itself and step aside.
            SingleInstance.SignalRunning();
            return 0;
        }

        // Listening starts once the app is up (see ListenForActivation): a knock answered from a
        // pool thread before Avalonia has claimed the UI thread would claim it for that thread.
        s_instance = instance;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static SingleInstance? s_instance;

    /// <summary>
    /// Starts answering second launches. Called by the app once its shell exists; a knock that
    /// arrives earlier waits on the pipe for up to two seconds, which covers a slow start.
    /// </summary>
    internal static void ListenForActivation(Action onActivated)
    {
        if (s_instance is not { } instance)
        {
            return;
        }

        instance.Activated += onActivated;
        instance.Listen();
    }

    /// <summary>Also what the previewer and the headless render tool start from.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Renders the application icon from the vector artwork and exits, so the icon file and the
    /// on-screen artwork can never drift apart. Only Windows wants an .ico.
    /// </summary>
    private static bool TryExportIcon(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(ExportIconSwitch, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length || !OperatingSystem.IsWindows())
        {
            return false;
        }

        IconFactory.WriteApplicationIcon(args[index + 1], Bell.RenderAppIcon);
        return true;
    }
}
