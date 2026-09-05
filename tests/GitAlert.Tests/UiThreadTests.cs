using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The view models hand background work to the UI thread through this and nothing else, which is
/// what let them move from WPF to Avalonia without knowing either dispatcher.
/// </summary>
public class UiThreadTests
{
    [Fact]
    public void Without_a_context_the_work_runs_inline()
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var ran = false;

            UiThread.Capture().Post(() => ran = true);

            Assert.True(ran);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    [Fact]
    public void With_a_context_the_work_is_queued_there_rather_than_run_inline()
    {
        var context = new RecordingContext();
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var ui = UiThread.Capture();

            // The thread that posts later - a timer, a poll - has no context of its own.
            SynchronizationContext.SetSynchronizationContext(null);

            var ran = false;
            ui.Post(() => ran = true);

            Assert.False(ran);
            var queued = Assert.Single(context.Queued);

            queued();

            Assert.True(ran);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public List<Action> Queued { get; } = [];

        public override void Post(SendOrPostCallback d, object? state) => Queued.Add(() => d(state));
    }
}
