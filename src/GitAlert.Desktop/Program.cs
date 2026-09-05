using Avalonia;
using Avalonia.Controls;
using GitAlert.Graphics;
using GitAlert.Platform;

namespace GitAlert;

internal static class Program
{
    /// <summary>Used by the build to regenerate <c>app.ico</c> from the vector artwork.</summary>
    private const string ExportIconSwitch = "--export-icon";

    /// <summary>Used by the macOS and Linux packaging to draw the icon files from the same artwork.</summary>
    private const string ExportPngSwitch = "--export-png";

    /// <summary>Every size the macOS iconset wants, which covers the Linux icon theme too.</summary>
    private static readonly int[] PngSizes = [16, 32, 64, 128, 256, 512, 1024];

    [STAThread]
    public static int Main(string[] args)
    {
        if (TryExportIcon(args) || TryExportPngs(args))
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

    /// <summary>
    /// Writes <c>gitalert-{size}.png</c> for every size into a folder and exits. Runs before
    /// Avalonia starts, so it needs no display: the packaging jobs run it on a build machine.
    /// </summary>
    private static bool TryExportPngs(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(ExportPngSwitch, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length)
        {
            return false;
        }

        var directory = args[index + 1];
        Directory.CreateDirectory(directory);

        foreach (var size in PngSizes)
        {
            File.WriteAllBytes(Path.Combine(directory, $"gitalert-{size}.png"), Bell.RenderAppIconPng(size));
        }

        return true;
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
