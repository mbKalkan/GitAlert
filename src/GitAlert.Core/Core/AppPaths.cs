using System.IO;

namespace GitAlert.Core;

/// <summary>
/// Central place for every file GitAlert writes. Everything lives in the folder the platform keeps
/// for a per-user application - <c>%APPDATA%\GitAlert</c> on Windows - so nothing needs elevation.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Locate(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));

    /// <summary>
    /// Roaming AppData on Windows, Application Support on macOS, the XDG config home on Linux -
    /// the one folder each platform tells applications to keep their per-user files in.
    /// </summary>
    public static string Locate(bool windows, bool macOS, string applicationData, string home, string? xdgConfigHome)
    {
        if (windows)
        {
            return Path.Combine(applicationData, "GitAlert");
        }

        if (macOS)
        {
            return Path.Combine(home, "Library", "Application Support", "GitAlert");
        }

        var config = string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome;
        return Path.Combine(config, "GitAlert");
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string StateFile => Path.Combine(DataDirectory, "state.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");

    public static string LogFile => Path.Combine(DataDirectory, "gitalert.log");

    public static string TraceFile => Path.Combine(DataDirectory, "trace.log");

    /// <summary>Tracing is on only while this file exists, so it is a user's deliberate act.</summary>
    public static string TraceMarker => Path.Combine(DataDirectory, "trace.on");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);

    /// <summary>
    /// Rolls a log to <c>.1</c> once it passes <paramref name="limit"/> bytes, keeping one
    /// previous file and no more.
    /// </summary>
    /// <remarks>
    /// A fault that repeats on every poll writes an entry every couple of minutes for as long as
    /// the machine is on. Unbounded, that is a tray app quietly filling someone's disk.
    /// </remarks>
    public static void Roll(string path, long limit)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true } file && file.Length > limit)
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A log that cannot be rolled is not worth an error of its own.
        }
    }
}
