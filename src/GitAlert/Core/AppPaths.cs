using System.IO;

namespace GitAlert.Core;

/// <summary>
/// Central place for every file GitAlert writes. Everything lives under
/// <c>%APPDATA%\GitAlert</c> so the app stays fully per-user and needs no elevation.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitAlert");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string StateFile => Path.Combine(DataDirectory, "state.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");

    public static string LogFile => Path.Combine(DataDirectory, "gitalert.log");

    public static string TraceFile => Path.Combine(DataDirectory, "trace.log");

    /// <summary>Tracing is on only while this file exists, so it is a user's deliberate act.</summary>
    public static string TraceMarker => Path.Combine(DataDirectory, "trace.on");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
