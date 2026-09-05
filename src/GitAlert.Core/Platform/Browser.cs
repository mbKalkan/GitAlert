using System.Diagnostics;
using System.IO;

namespace GitAlert.Platform;

/// <summary>Opens links in whatever the user has set as their default browser.</summary>
public static class Browser
{
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            // No default browser, or the shell refused; nothing useful to do but carry on.
            return false;
        }
    }

    /// <summary>Reveals a local folder in File Explorer.</summary>
    public static bool OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
