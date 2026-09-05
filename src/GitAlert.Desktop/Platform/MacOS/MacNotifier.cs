using System.Runtime.Versioning;
using GitAlert.Core;

namespace GitAlert.Platform.MacOS;

/// <summary>
/// Notifications through AppleScript's <c>display notification</c>, which needs no bundle
/// registration and no entitlement. They land in Notification Centre under Script Editor's name
/// rather than GitAlert's; the user notification centre proper waits for a signed bundle.
/// </summary>
public static class MacNotifier
{
    [SupportedOSPlatform("macos")]
    public static void Show(string title, string message, NotificationKind kind, bool playSound)
    {
        var (code, _) = Tool.Run("osascript", ["-e", Script(title, message, playSound)], timeout: TimeSpan.FromSeconds(5));

        if (code != 0)
        {
            TraceLog.Write($"notification not shown: osascript exited with {code}");
        }
    }

    /// <summary>The script, with the two strings quoted the way AppleScript wants them.</summary>
    public static string Script(string title, string message, bool playSound)
    {
        var script = $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"";
        return playSound ? script + " sound name \"default\"" : script;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
