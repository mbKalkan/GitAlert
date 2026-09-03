using System.Diagnostics;

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
}
