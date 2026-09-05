using System.Runtime.Versioning;
using System.Text;

namespace GitAlert.Platform.Linux;

/// <summary>The desktop entry that starts GitAlert at login, and what it says.</summary>
public static class XdgAutostart
{
    public const string FileName = "gitalert.desktop";

    /// <summary>
    /// A desktop entry with the startup switch, so GitAlert stays in the tray without opening a
    /// window. The executable is quoted and escaped the way the Desktop Entry specification wants.
    /// </summary>
    public static string DesktopEntry(string executable) =>
        $"""
         [Desktop Entry]
         Type=Application
         Name=GitAlert
         Comment=GitHub alerts in the tray
         Exec={Quote(executable)} {IStartupRegistrar.LaunchArgument}
         Icon=gitalert
         Terminal=false
         X-GNOME-Autostart-enabled=true

         """;

    /// <summary>
    /// A quoted Exec argument: backslashes, quotes, dollars and backticks are the characters the
    /// specification reserves inside quotes.
    /// </summary>
    public static string Quote(string argument)
    {
        var builder = new StringBuilder("\"");

        foreach (var c in argument)
        {
            if (c is '"' or '\\' or '$' or '`')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.Append('"').ToString();
    }
}

/// <summary>Writes and removes the entry under the user's own <c>~/.config/autostart</c>.</summary>
[SupportedOSPlatform("linux")]
public sealed class XdgAutostartRegistrar : IStartupRegistrar
{
    private static string Directory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var config = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;

            return Path.Combine(config, "autostart");
        }
    }

    private static string EntryPath => Path.Combine(Directory, XdgAutostart.FileName);

    public bool IsEnabled => File.Exists(EntryPath);

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(EntryPath, XdgAutostart.DesktopEntry(Executable()));
            }
            else if (File.Exists(EntryPath))
            {
                File.Delete(EntryPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The AppImage itself when running from one - the process path would be inside the mounted
    /// image, which is gone by the next login - and the executable otherwise.
    /// </summary>
    private static string Executable() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image
            ? image
            : Environment.ProcessPath ?? throw new InvalidOperationException("The running executable's path is unknown.");
}
