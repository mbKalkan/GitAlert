using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace GitAlert.Platform;

/// <summary>
/// Registers GitAlert under the per-user <c>Run</c> key. Per-user means no elevation prompt and no
/// impact on anyone else signing in to the machine.
/// </summary>
public sealed class StartupManager : IStartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GitAlert";

    public static string ExecutablePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.ChangeExtension(typeof(StartupManager).Assembly.Location, ".exe");

    public bool IsEnabled
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
    public bool SetEnabled(bool enabled)
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
                key.SetValue(ValueName, $"\"{ExecutablePath}\" {IStartupRegistrar.LaunchArgument}");
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
