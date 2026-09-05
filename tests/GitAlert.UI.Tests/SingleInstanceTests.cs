using Xunit;

namespace GitAlert.UI.Tests;

/// <summary>
/// One GitAlert per session, whichever front end got there first. Every test uses names of its own,
/// so a GitAlert running on the machine that runs the tests is neither found nor woken.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void The_second_launch_finds_the_first_and_wakes_it_through_the_pipe()
    {
        var names = Fresh();

        using var first = SingleInstance.TryAcquire(names);

        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(names));

        using var woken = new ManualResetEventSlim();
        first.Activated += woken.Set;
        first.Listen();

        SingleInstance.SignalRunning(names);

        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)), "the first instance heard the knock on the pipe");
    }

    [Fact]
    public void The_wpf_build_wakes_it_the_way_it_always_has()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the WPF build's named event exists on Windows only");

        var names = Fresh();

        using var first = SingleInstance.TryAcquire(names);

        Assert.NotNull(first);

        using var woken = new ManualResetEventSlim();
        first.Activated += woken.Set;
        first.Listen();

        // What the WPF build does on its second launch: open the event by name and set it.
        Assert.True(EventWaitHandle.TryOpenExisting(names.Event!, out var handle), "the event is there to be opened");

        using (handle)
        {
            handle.Set();
        }

        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)), "the first instance heard the event");
    }

    [Fact]
    public void Signalling_with_nobody_listening_gives_up_quietly()
    {
        var names = Fresh();

        // Neither a pipe nor an event of these names exists: the knock times out and that is all.
        SingleInstance.SignalRunning(names);
    }

    /// <summary>
    /// macOS turns a pipe name into a Unix socket under $TMPDIR, which is already about sixty
    /// characters long there, and a socket path stops at 104. The names GitAlert ships must leave
    /// that room; the test names below are cut short for the same reason.
    /// </summary>
    [Fact]
    public void The_pipe_name_leaves_room_for_a_macOS_socket_path()
    {
        Assert.InRange(InstanceNames.Default.Pipe.Length, 1, MaxPipeName);
    }

    private const int MaxPipeName = 40;

    private static InstanceNames Fresh()
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        // Mutexes and events share one kernel namespace on Windows, so the event needs a name of
        // its own; pipes live in a namespace of their own.
        return new InstanceNames(
            $"GitAlert.Test.{id}",
            $"GitAlert.Test.{id}",
            OperatingSystem.IsWindows() ? $@"Local\GitAlert.Test.{id}.Activate" : null);
    }
}
