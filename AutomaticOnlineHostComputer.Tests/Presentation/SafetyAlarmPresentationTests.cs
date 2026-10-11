using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.Alarms;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class SafetyAlarmPresentationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Manipulator_alarm_uses_manipulator_identity_and_preserves_probe_evidence(int number)
    {
        var action = new FlowActionContext(Guid.NewGuid().ToString(), "取板", $"机械手{number}", "A/1", "来源", "目标", "缓存");
        using var context = FlowAlarmPresentationContext.Begin(action, null, "动平衡", 0, "取板");
        action.BeginStep(FlowActionStep.ProbeSourcePresence);
        FlowAlarmPresentationContext.RecordPickupProbe("第2位置：实际Z=105，X11=0，尚未持件");
        FlowAlarmPresentationContext.RecordFailure(new IOException("输入读取失败"));
        action.Discard("本趟已作废");
        var detail = context.Snapshot();
        Assert.Equal(number, detail.ManipulatorNo);
        string text = SafetyAlarmFormatter.Format(detail, null);
        Assert.Contains($"机械手{number}", text);
        Assert.DoesNotContain("0号天车", text);
        Assert.Contains("第2位置：实际Z=105", text);
        Assert.Contains("尚未持件", text);
        Assert.Equal(FlowActionStep.ProbeSourcePresence, detail.FailureAction!.Step);
    }

    [Fact]
    public void Failure_keeps_workpiece_and_step_when_business_memory_is_cleared()
    {
        var wp = new WorkpieceCache { PlateNo = "P001", Sequence = "A/2", Length = 1200.5, Diameter = 280.2 };
        var action = Action();
        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(100, 200, 300), "斜床夹紧后退磁");
        using var context = FlowAlarmPresentationContext.Begin(action, wp, "2号线", 4, "斜床上料");
        FlowAlarmPresentationContext.RecordFailure(new InvalidOperationException("X7未确认"));
        wp = default;
        action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "异常收尾");
        FlowAlarmPresentationContext.RecordCleanup("设备清零成功", "Idle，工件已清", "来源缓存未改动");

        var detail = context.Snapshot();
        Assert.Equal("P001", detail.Workpiece!.PlateNo);
        Assert.Equal("A/2", detail.Workpiece.Sequence);
        Assert.Equal(1200.5, detail.Workpiece.Length);
        Assert.Equal(280.2, detail.Workpiece.Diameter);
        Assert.Equal(FlowActionStep.MagnetOffSent, detail.FailureAction!.Step);
        Assert.Equal(300, detail.FailureAction.TargetPosition.Z);
        Assert.Equal("X7未确认", detail.Reason);
    }

    [Fact]
    public void Unknown_and_failed_clear_are_explicit_and_never_reported_as_success()
    {
        using var context = FlowAlarmPresentationContext.Begin(Action(), null, "研磨", 5, "研磨机下料");
        FlowAlarmPresentationContext.RecordCleanup("设备侧清零失败：通信断开", "Idle，工件已清", "FIFO未改动");
        string text = SafetyAlarmFormatter.Format(context.Snapshot(), null);
        Assert.Contains("设备侧清零失败：通信断开", text);
        Assert.Contains("版号：未知", text);
        Assert.Contains("X6充磁反馈：未知", text);
        Assert.DoesNotContain("PLC下发数据已清", text);
        Assert.DoesNotContain("X6充磁反馈：0", text);
    }

    [Fact]
    public void Scope_is_isolated_and_only_reads_the_action()
    {
        var action = Action();
        action.RegisterLock("RearCrane");
        var before = action.Snapshot();
        using (var context = FlowAlarmPresentationContext.Begin(action, null, "2号线", 4, "斜床下料"))
        {
            Assert.Same(context, FlowAlarmPresentationContext.Current);
            FlowAlarmPresentationContext.RecordFailure(new Exception("读失败"));
            FlowAlarmPresentationContext.RecordCleanup("失败", "Idle", "未改动");
        }
        Assert.Null(FlowAlarmPresentationContext.Current);
        var after = action.Snapshot();
        Assert.Equal(before.Step, after.Step);
        Assert.Equal(before.Disposition, after.Disposition);
        Assert.Equal(before.CommandState, after.CommandState);
        Assert.Equal(before.HeldLocks, after.HeldLocks);
    }

    [Fact]
    public void Notification_failure_cannot_escape_into_business_code()
    {
        using var context = FlowAlarmPresentationContext.Begin(Action(), null, "2号线", 4, "斜床上料");
        context.Changed += () => throw new Exception("UI已关闭");
        FlowAlarmPresentationContext.RecordFailure(new Exception("运动失败"));
        FlowAlarmPresentationContext.RecordCleanup("清零失败", "Idle", "未改动");
        Assert.Equal("运动失败", context.Snapshot().Reason);
    }

    [Fact]
    public void Refresh_shows_new_sample_without_rewriting_original_failure()
    {
        using var context = FlowAlarmPresentationContext.Begin(Action(), null, "2号线", 4, "斜床上料");
        FlowAlarmPresentationContext.RecordFailure(new Exception("首次失败"));
        var initial = context.Snapshot();
        var sample = CraneAlarmSnapshot.Unknown("读取超时");
        string text = SafetyAlarmFormatter.Format(initial, sample);
        Assert.Contains("首次失败", text);
        Assert.Contains("读取超时", text);
        Assert.Equal(initial.OccurredAtUtc, context.Snapshot().OccurredAtUtc);
    }

    private static FlowActionContext Action() => new("alarm-test", "2号线后天车上料", "4号天车", "P001/A/2", "ST016", "ST607", "ST016");
}
