using System.IO.Pipes;

namespace GitAlert;

/// <summary>
/// One GitAlert per session. The first process holds a named mutex and listens on a named pipe; a
/// second launch finds the mutex taken, knocks on the pipe so the first shows its flyout, and quits.
/// Both primitives exist on every platform .NET runs on, unlike the event handle the WPF app used.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "GitAlert.Desktop.SingleInstance";
    private const string PipeName = "GitAlert.Desktop.Activate";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopping = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Another launch asked this one to show itself. Raised on a pool thread.</summary>
    public event Action? Activated;

    /// <summary>The instance when this process is the first one; null when another already runs.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

        if (isFirst)
        {
            return new SingleInstance(mutex);
        }

        // Not ours to release: ReleaseMutex on a mutex this process never acquired throws.
        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the running instance to show itself. Quietly gives up if it is on its way out.</summary>
    public static void SignalRunning()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The other instance is shutting down or not listening yet; nothing to activate.
        }
    }

    /// <summary>Starts answering knocks on the pipe.</summary>
    public void Listen()
    {
        var token = _stopping.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    Activated?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A client that connected and vanished; listen again.
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
