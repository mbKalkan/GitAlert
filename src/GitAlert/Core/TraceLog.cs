using System.IO;

namespace GitAlert.Core;

/// <summary>
/// Opt-in tracing for the handful of behaviours that can only be observed on a real desktop: which
/// messages the shell actually sends for a tray click, and whether a window truly reaches the
/// foreground. Both differ between Windows builds and neither can be reproduced in a test, so when
/// a report says "it opens and closes again" this is how the cause gets established rather than
/// guessed. Off unless <c>trace.on</c> sits beside the settings, which costs one file check at
/// startup and nothing afterwards.
/// </summary>
public static class TraceLog
{
    private static readonly bool Enabled = MarkerExists();

    private static readonly object Gate = new();

    /// <summary>Tracing writes a line per tray click, so it needs a ceiling of its own.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                AppPaths.Roll(AppPaths.TraceFile, MaxBytes);

                File.AppendAllText(
                    AppPaths.TraceFile,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Tracing must never be the reason the app misbehaves.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool MarkerExists()
    {
        try
        {
            return File.Exists(AppPaths.TraceMarker);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
