using System.Runtime.Versioning;
using System.Security;

namespace GitAlert.Platform.MacOS;

/// <summary>The launch agent that starts GitAlert at login: what it is called and what its file says.</summary>
public static class LaunchAgent
{
    public const string Label = "com.mbkalkan.gitalert";

    /// <summary>
    /// The property list launchd reads. The program runs at load with the startup switch, so it
    /// stays in the menu bar without opening a window.
    /// </summary>
    public static string Plist(string executable) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>Label</key>
             <string>{Label}</string>
             <key>ProgramArguments</key>
             <array>
                 <string>{SecurityElement.Escape(executable)}</string>
                 <string>{IStartupRegistrar.LaunchArgument}</string>
             </array>
             <key>RunAtLoad</key>
             <true/>
             <key>ProcessType</key>
             <string>Interactive</string>
         </dict>
         </plist>

         """;
}

/// <summary>Writes and removes the launch agent under the user's own <c>~/Library/LaunchAgents</c>.</summary>
[SupportedOSPlatform("macos")]
public sealed class LaunchAgentRegistrar : IStartupRegistrar
{
    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    private static string PlistPath => Path.Combine(Directory, LaunchAgent.Label + ".plist");

    public bool IsEnabled => File.Exists(PlistPath);

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                // Inside the app bundle this is Contents/MacOS/GitAlert, which launchd runs directly.
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The running executable's path is unknown.");

                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(PlistPath, LaunchAgent.Plist(executable));
            }
            else if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
