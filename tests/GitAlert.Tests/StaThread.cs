using Xunit.Sdk;

namespace GitAlert.Tests;

/// <summary>
/// Runs a piece of test work on its own thread and rethrows whatever it threw.
/// </summary>
/// <remarks>
/// The thread is an STA one on Windows, where these view model tests grew up beside WPF, so the
/// run stays the same there. macOS and Linux have no COM apartments and refuse the call, which
/// is what took the first three-platform CI run down.
/// </remarks>
internal static class StaThread
{
    public static void Run(Action work)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new XunitException(failure.ToString());
        }
    }
}
