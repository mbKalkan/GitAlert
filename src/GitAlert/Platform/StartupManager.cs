using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace GitAlert.Platform;

/// <summary>
/// Registers GitAlert under the per-user <c>Run</c> key. Per-user means no elevation prompt and no
/// impact on anyone else signing in to the machine.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GitAlert";

    /// <summary>Passed on a startup launch so the app knows to stay in the tray, silently.</summary>
    public const string StartupArgument = "--startup";

    public static string ExecutablePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.ChangeExtension(typeof(StartupManager).Assembly.Location, ".exe");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Contains("GitAlert", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Returns true when the change was applied.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\" {StartupArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception)
        {
            // A managed device can lock the Run key; the caller reports this to the user.
            return false;
        }
    }
}
