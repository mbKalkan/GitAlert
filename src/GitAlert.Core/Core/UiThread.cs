namespace GitAlert.Core;

/// <summary>
/// Hands work from a background thread to the thread the view models belong to. WPF and Avalonia
/// both install a <see cref="SynchronizationContext"/> on their UI thread, and that is all the
/// view models need to know about either of them.
/// </summary>
public sealed class UiThread
{
    private readonly SynchronizationContext? _context;

    private UiThread(SynchronizationContext? context) => _context = context;

    /// <summary>
    /// Binds to the calling thread. A thread without a context - a test, a console - gets its work
    /// run inline, which keeps the view models usable without a message loop.
    /// </summary>
    public static UiThread Capture() => new(SynchronizationContext.Current);

    /// <summary>Queues the action on the UI thread; on a thread with a loop it never runs inline.</summary>
    public void Post(Action action)
    {
        if (_context is null)
        {
            action();
            return;
        }

        _context.Post(static state => ((Action)state!)(), action);
    }
}
