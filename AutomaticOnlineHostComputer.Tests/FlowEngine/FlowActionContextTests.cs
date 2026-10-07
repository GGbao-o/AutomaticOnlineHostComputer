using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class FlowActionContextTests
{
    [Fact]
    public void Sent_command_cannot_be_downgraded_to_wait_retry()
    {
        var action = CreateAction();
        action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(1, 2, 0), "XY到来源位");
        action.MarkCommandSent();

        Assert.Throws<InvalidOperationException>(() => action.WaitRetry("通信断开"));
    }

    [Fact]
    public void Snapshot_keeps_actual_identity_feedback_and_held_locks()
    {
        var action = CreateAction("E26072224/8");
        action.RegisterLock("RearCrane");
        action.RegisterLock("ZoneMT");
        action.RecordFeedback(new FlowActionPosition(10, 20, 30), x11: true, magnetOn: true, "X11确认持件");

        FlowActionSnapshot snapshot = action.Snapshot();

        Assert.Equal("E26072224/8", snapshot.WorkpieceIdentity);
        Assert.Equal(30, snapshot.LastKnownPosition.Z);
        Assert.True(snapshot.X11);
        Assert.True(snapshot.MagnetOn);
        Assert.Contains("RearCrane", snapshot.HeldLocks);
        Assert.Contains("ZoneMT", snapshot.HeldLocks);
    }

    [Fact]
    public void Pause_manual_keeps_confirmed_carrier_ownership()
    {
        var action = CreateAction();
        action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1");

        action.PauseForManualResolution("XY响应未知");

        Assert.Equal(FlowWorkpieceOwnership.OnCarrier, action.Ownership);
        Assert.Equal(FlowActionDisposition.PauseOnCarrier, action.Disposition);
    }

    [Fact]
    public void Pause_manual_keeps_source_reservation_as_source_specific_conclusion()
    {
        var action = CreateAction();

        action.PauseForManualResolution("X11读取响应未知");

        Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, action.Ownership);
        Assert.Equal(FlowActionDisposition.PauseSourceReserved, action.Disposition);
        Assert.True(action.ManualConfirmationRequired);
    }

    [Fact]
    public void Pause_manual_persists_snapshot_until_the_action_is_explicitly_settled()
    {
        const string operationId = "manual-registry-test";
        FlowActionManualRegistry.Remove(operationId);
        var action = new FlowActionContext(operationId, "测试研磨上料", "研磨天车", "E26072224/8", "ST709", "ST704", "ST709");
        action.RegisterLock("GrindingCrane");
        action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(10, 20, 0), "去研磨机");
        action.MarkCommandSent();
        action.MarkCommandResponseUnknown("通讯中断，目标位置未知");
        action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1");
        action.PauseForManualResolution("响应未知");

        Assert.True(FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot paused));
        Assert.Equal(FlowActionDisposition.PauseOnCarrier, paused.Disposition);
        Assert.Contains("GrindingCrane", paused.HeldLocks);

        Assert.True(FlowActionManualRegistry.Remove(operationId));
        Assert.False(FlowActionManualRegistry.TryGet(operationId, out _));
    }

    private static FlowActionContext CreateAction(string identity = "E26072224/8") =>
        new("test-action", "测试后天车上料", "2号天车", identity, "ST105", "ST108", "ST105");
}
