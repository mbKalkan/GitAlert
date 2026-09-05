using System.IO.Pipes;
using System.Runtime.Versioning;

namespace GitAlert;

/// <summary>The kernel object names one GitAlert uses to find another.</summary>
/// <param name="Mutex">Held by the running instance.</param>
/// <param name="Pipe">Where the running instance listens for a knock.</param>
/// <param name="Event">The event the WPF build sets and waits on. Windows only; null elsewhere.</param>
public sealed record InstanceNames(string Mutex, string Pipe, string? Event)
{
    /// <summary>
    /// The names the WPF build has used since 1.0, so the two front ends cannot run side by side
    /// while both exist. Two of them would poll the same repositories with two copies of the
    /// settings and take turns overwriting each other's settings and history files. The second one
    /// launched, whichever it is, wakes the first and steps aside. <c>Local\</c> scopes a Windows
    /// name to the session; other systems get a plain name.
    /// </summary>
    public static InstanceNames Default { get; } = OperatingSystem.IsWindows()
        ? new(@"Local\GitAlert.SingleInstance", "GitAlert.Activate", @"Local\GitAlert.Activate")
        : new("GitAlert.SingleInstance", "GitAlert.Activate", null);
}

/// <summary>
/// One GitAlert per session. The first process holds a named mutex and listens on a named pipe; a
/// second launch finds the mutex taken, knocks on the pipe so the first shows its flyout, and quits.
/// On Windows it also speaks the WPF build's language, a named event, in both directions.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly InstanceNames _names;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopping = new();
    private EventWaitHandle? _event;

    private SingleInstance(InstanceNames names, Mutex mutex)
    {
        _names = names;
        _mutex = mutex;
    }

    /// <summary>Another launch asked this one to show itself. Raised on a pool thread.</summary>
    public event Action? Activated;

    /// <summary>The instance when this process is the first one; null when another already runs.</summary>
    public static SingleInstance? TryAcquire() => TryAcquire(InstanceNames.Default);

    public static SingleInstance? TryAcquire(InstanceNames names)
    {
        var mutex = new Mutex(initiallyOwned: true, names.Mutex, out var isFirst);

        if (isFirst)
        {
            return new SingleInstance(names, mutex);
        }

        // Not ours to release: ReleaseMutex on a mutex this process never acquired throws.
        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the running instance to show itself, whichever front end it is.</summary>
    public static void SignalRunning() => SignalRunning(InstanceNames.Default);

    public static void SignalRunning(InstanceNames names)
    {
        if (KnockOnPipe(names.Pipe))
        {
            return;
        }

        if (names.Event is { } eventName && OperatingSystem.IsWindows())
        {
            SetEvent(eventName);
        }
    }

    /// <summary>Starts answering knocks on the pipe and, on Windows, the WPF build's event.</summary>
    public void Listen()
    {
        var token = _stopping.Token;

        _ = Task.Run(() => ListenOnPipeAsync(token), token);

        if (_names.Event is { } eventName && OperatingSystem.IsWindows())
        {
            ListenOnEvent(eventName, token);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _event?.Dispose();
    }

    private static bool KnockOnPipe(string pipe)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Nobody listens on this side: the WPF build, or an instance on its way out.
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetEvent(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is shutting down; nothing to activate.
        }
    }

    private async Task ListenOnPipeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            try
            {
                server = new NamedPipeServerStream(
                    _names.Pipe,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
                // The name is still held, by an instance on its way out. Wait rather than spin.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // The client connected and was gone again before the pipe caught up, which is
                    // exactly what a knock looks like: the launcher has nothing to say and exits.
                    // Swallowing this lost every knock that landed in that window.
                }
            }

            Activated?.Invoke();
        }
    }

    [SupportedOSPlatform("windows")]
    private void ListenOnEvent(string name, CancellationToken token)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _event = handle;

        var thread = new Thread(() =>
        {
            try
            {
                WaitHandle[] handles = [handle, token.WaitHandle];

                while (!token.IsCancellationRequested && WaitHandle.WaitAny(handles) == 0)
                {
                    Activated?.Invoke();
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed under us on the way out; there is nothing left to wake.
            }
        })
        {
            IsBackground = true,
            Name = "GitAlert.ActivationListener",
        };

        thread.Start();
    }
}
