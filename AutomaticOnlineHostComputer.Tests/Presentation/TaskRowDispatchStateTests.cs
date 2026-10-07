using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class TaskRowDispatchStateTests
{
    [Theory]
    [InlineData(TaskDispatchState.NotQueued, "启动", true, "—", false)]
    [InlineData(TaskDispatchState.Queued, "暂停", true, "2", true)]
    [InlineData(TaskDispatchState.Dispatching, "派发中", false, "—", true)]
    [InlineData(TaskDispatchState.EnteredLine, "已入线", false, "—", true)]
    public void Dispatch_state_controls_action_order_and_command(
        TaskDispatchState state,
        string action,
        bool enabled,
        string order,
        bool running)
    {
        var row = new TaskRowViewModel();

        Apply(row, state);

        Assert.Equal(state, row.DispatchState);
        Assert.Equal(action, row.ActionText);
        Assert.Equal(enabled, row.CanToggleRun);
        Assert.Equal(enabled, row.ToggleRunCommand.CanExecute(null));
        Assert.Equal(order, row.QueueOrderText);
        Assert.Equal(running, row.IsRunning);
    }

    [Fact]
    public void RequestStart_only_notifies_owner_and_does_not_preemptively_change_state()
    {
        var row = new TaskRowViewModel();
        bool requested = false;
        row.OnStartRequested = _ => requested = true;

        row.RequestStart();

        Assert.True(requested);
        Assert.Equal(TaskDispatchState.NotQueued, row.DispatchState);
        Assert.False(row.IsRunning);
    }

    [Fact]
    public void Toggle_queued_task_requests_pause_without_preemptively_changing_state()
    {
        var row = new TaskRowViewModel();
        bool requested = false;
        row.OnPauseRequested = _ => requested = true;
        row.MarkQueued(1);

        row.ToggleRunCommand.Execute(null);

        Assert.True(requested);
        Assert.Equal(TaskDispatchState.Queued, row.DispatchState);
        Assert.Equal("1", row.QueueOrderText);
    }

    [Fact]
    public void Queue_position_can_be_refreshed_without_changing_dispatch_state()
    {
        var row = new TaskRowViewModel();
        row.MarkQueued(3);

        row.SetQueuePosition(1);

        Assert.Equal(TaskDispatchState.Queued, row.DispatchState);
        Assert.Equal("1", row.QueueOrderText);
    }

    private static void Apply(TaskRowViewModel row, TaskDispatchState state)
    {
        switch (state)
        {
            case TaskDispatchState.NotQueued:
                row.MarkPaused();
                break;
            case TaskDispatchState.Queued:
                row.MarkQueued(2);
                break;
            case TaskDispatchState.Dispatching:
                row.MarkQueued(1);
                row.MarkDispatching();
                break;
            case TaskDispatchState.EnteredLine:
                row.MarkQueued(1);
                row.MarkDispatching();
                row.MarkEnteredLine();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }
}
