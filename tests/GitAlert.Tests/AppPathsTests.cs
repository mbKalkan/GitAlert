using System.IO;
using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Where the data folder goes on each platform. Windows users keep the folder they have; the
/// other two get the folder their platform reserves for a per-user application.
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void Windows_keeps_the_roaming_appdata_folder_an_upgrade_already_has()
    {
        var directory = AppPaths.Locate(windows: true, macOS: false, @"C:\Users\mert\AppData\Roaming", @"C:\Users\mert", null);

        Assert.Equal(Path.Combine(@"C:\Users\mert\AppData\Roaming", "GitAlert"), directory);
    }

    [Fact]
    public void MacOS_uses_application_support_rather_than_the_dot_config_folder_dotnet_would_pick()
    {
        var directory = AppPaths.Locate(windows: false, macOS: true, "/Users/mert/.config", "/Users/mert", null);

        Assert.Equal(Path.Combine("/Users/mert", "Library", "Application Support", "GitAlert"), directory);
    }

    [Fact]
    public void Linux_honours_the_xdg_config_home_and_falls_back_to_dot_config()
    {
        Assert.Equal(
            Path.Combine("/tmp/cfg", "GitAlert"),
            AppPaths.Locate(windows: false, macOS: false, "/home/mert/.config", "/home/mert", "/tmp/cfg"));

        Assert.Equal(
            Path.Combine("/home/mert", ".config", "GitAlert"),
            AppPaths.Locate(windows: false, macOS: false, "/home/mert/.config", "/home/mert", "  "));
    }
}
