using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using GitAlert.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace GitAlert.Desktop.Tests;

/// <summary>
/// The real <see cref="App"/>, on the headless platform with Skia drawing on, so the windows lay
/// out and paint exactly as they do on screen. The app composes nothing here: without a desktop
/// lifetime it only loads its styles.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
