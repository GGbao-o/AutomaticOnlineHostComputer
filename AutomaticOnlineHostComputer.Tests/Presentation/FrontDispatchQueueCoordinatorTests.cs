using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class FrontDispatchQueueCoordinatorTests
{
    [Fact]
    public void Pause_then_restart_moves_middle_task_to_tail_and_renumbers()
    {
        var queue = new FrontDispatchQueueCoordinator();
        var a = Row("A");
        var b = Row("B");
        var c = Row("C");

        Assert.True(queue.TryEnqueue(a, Wp("A"), out var aPosition, out _));
        Assert.True(queue.TryEnqueue(b, Wp("B"), out var bPosition, out _));
        Assert.True(queue.TryEnqueue(c, Wp("C"), out var cPosition, out _));
        Assert.Equal((1, 2, 3), (aPosition, bPosition, cPosition));

        Assert.Equal(
            FrontDispatchPauseOutcome.Removed,
            queue.TryPause(b, out var oldPosition, out var pausedSnapshot));
        Assert.Equal(2, oldPosition);
        Assert.Equal(new[] { "A", "C" }, PlateOrder(pausedSnapshot));

        Assert.True(queue.TryEnqueue(b, Wp("B"), out var newPosition, out var restartedSnapshot));
        Assert.Equal(3, newPosition);
        Assert.Equal(new[] { "A", "C", "B" }, PlateOrder(restartedSnapshot));
    }

    [Fact]
    public void Pausing_head_removes_blocker_and_promotes_remaining_tasks()
    {
        var queue = new FrontDispatchQueueCoordinator();
        var a = Row("A");
        var b = Row("B");
        queue.TryEnqueue(a, Wp("A"), out _, out _);
        queue.TryEnqueue(b, Wp("B"), out _, out _);

        Assert.Equal(FrontDispatchPauseOutcome.Removed, queue.TryPause(a, out var oldPosition, out var snapshot));
        Assert.Equal(1, oldPosition);
        Assert.Single(snapshot.Entries);
        Assert.Same(b, snapshot.Entries[0].Row);
        Assert.Equal(1, snapshot.Entries[0].Position);
        Assert.True(queue.TryPeek(out var head));
        Assert.Same(b, head!.Row);
    }

    [Fact]
    public void Dispatching_item_cannot_be_paused_or_enqueued_again()
    {
        var queue = new FrontDispatchQueueCoordinator();
        var a = Row("A");
        queue.TryEnqueue(a, Wp("A"), out _, out _);
        queue.TryPeek(out var expected);

        Assert.True(queue.TryBeginDispatch(expected!, out var dispatching, out var afterTake));
        Assert.Empty(afterTake.Entries);
        Assert.Same(expected, dispatching);
        Assert.Equal(
            FrontDispatchPauseOutcome.Dispatching,
            queue.TryPause(a, out var oldPosition, out var pauseSnapshot));
        Assert.Equal(0, oldPosition);
        Assert.Equal(afterTake.Version, pauseSnapshot.Version);
        Assert.False(queue.TryEnqueue(a, Wp("A2"), out _, out _));
    }

    [Fact]
    public void Failed_dispatch_requeues_the_same_item_at_head()
    {
        var queue = new FrontDispatchQueueCoordinator();
        var a = Row("A");
        var b = Row("B");
        queue.TryEnqueue(a, Wp("A"), out _, out _);
        queue.TryEnqueue(b, Wp("B"), out _, out _);
        queue.TryPeek(out var expected);
        queue.TryBeginDispatch(expected!, out var dispatching, out _);

        var snapshot = queue.RequeueHead(dispatching!);

        Assert.Equal(new[] { "A", "B" }, PlateOrder(snapshot));
        Assert.True(queue.TryPeek(out var restored));
        Assert.Same(dispatching, restored);
        Assert.Equal("A", restored!.Workpiece.PlateNo);
    }

    [Fact]
    public void Successful_dispatch_clears_in_progress_without_requeueing()
    {
        var queue = new FrontDispatchQueueCoordinator();
        var a = Row("A");
        queue.TryEnqueue(a, Wp("A"), out _, out _);
        queue.TryPeek(out var expected);
        queue.TryBeginDispatch(expected!, out var dispatching, out _);

        var snapshot = queue.CompleteDispatch(dispatching!);

        Assert.Empty(snapshot.Entries);
        Assert.Equal(FrontDispatchPauseOutcome.NotQueued, queue.TryPause(a, out _, out _));
        Assert.True(queue.TryEnqueue(a, Wp("A-new"), out var position, out _));
        Assert.Equal(1, position);
    }

    [Fact]
    public void Mutation_snapshots_have_monotonically_increasing_versions()
    {
        var queue = new FrontDispatchQueueCoordinator();
        var a = Row("A");
        var b = Row("B");

        queue.TryEnqueue(a, Wp("A"), out _, out var first);
        queue.TryEnqueue(b, Wp("B"), out _, out var second);
        queue.TryPause(a, out _, out var third);

        Assert.True(first.Version < second.Version);
        Assert.True(second.Version < third.Version);
    }

    [Fact]
    public async Task Pause_and_begin_dispatch_race_has_exactly_one_owner()
    {
        for (int i = 0; i < 100; i++)
        {
            var queue = new FrontDispatchQueueCoordinator();
            var row = Row($"A{i}");
            queue.TryEnqueue(row, Wp(row.PlateNo), out _, out _);
            queue.TryPeek(out var expected);

            using var start = new ManualResetEventSlim(false);
            FrontDispatchPauseOutcome pauseOutcome = FrontDispatchPauseOutcome.NotQueued;
            bool beganDispatch = false;
            var pauseTask = Task.Run(() =>
            {
                start.Wait();
                pauseOutcome = queue.TryPause(row, out _, out _);
            });
            var dispatchTask = Task.Run(() =>
            {
                start.Wait();
                beganDispatch = queue.TryBeginDispatch(expected!, out _, out _);
            });

            start.Set();
            await Task.WhenAll(pauseTask, dispatchTask);

            if (beganDispatch)
                Assert.Equal(FrontDispatchPauseOutcome.Dispatching, pauseOutcome);
            else
                Assert.Equal(FrontDispatchPauseOutcome.Removed, pauseOutcome);
        }
    }

    private static string[] PlateOrder(FrontDispatchQueueCoordinator.Snapshot snapshot) =>
        snapshot.Entries.Select(x => x.Row.PlateNo).ToArray();

    private static TaskRowViewModel Row(string plateNo) => new() { PlateNo = plateNo };

    private static WorkpieceCache Wp(string plateNo) => new()
    {
        PlateNo = plateNo,
        Sequence = plateNo,
        Diameter = 160,
        Length = 600
    };
}
