using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class RearLoadX11FinalEventTests
{
    [Fact]
    public void Final_report_contains_x11_recovery_locks_pause_and_workpiece_once()
    {
        var reporter = new CapturingReporter();
        OperationalPhysicalCycleTracker tracker = CreateThreeZeroTracker(safeZ: true);
        var finalization = new OperationalEventContextFactory.RearLoadX11Finalization(
            RecoveryStepState.Succeeded,
            "退磁方法成功返回",
            RecoveryStepState.Succeeded,
            "Z回原点成功",
            RecoveryStepState.Succeeded,
            "X退避成功",
            ZoneTsHeldBeforeCleanup: true,
            ZoneTsReleasedAfterward: true,
            ZoneMtHeldBeforeCleanup: true,
            ZoneMtReleasedAfterward: true,
            RearCraneHeldBeforeCleanup: true,
            RearCraneReleasedAfterward: true,
            CacheRestored: true,
            RearEnginePaused: true,
            SafetyAlarmCallbackCompleted: true,
            ResultSummary: "退磁成功；Z已回原点；X退避成功；三把锁已释放");

        OperationalEventContextFactory.TryReportRearLoadX11FinalFailure(
            reporter,
            CreateSite(),
            tracker,
            finalization,
            new InvalidOperationException("取料失败: 3次充磁后X11仍=0"));

        OperationalEventContext context = Assert.Single(reporter.Contexts);
        Assert.Equal("CRANE_X11_NOT_CONFIRMED", context.EventCode);
        Assert.Equal("1号线", context.Scope);
        Assert.Equal("后端引擎", context.Engine);
        Assert.Equal("ST711", context.Station);
        Assert.True(context.BusinessPaused.HasValue && context.BusinessPaused.Value);
        Assert.Equal("P-001", context.Evidence.Workpiece.PlateNo.Value);
        Assert.Equal("S-01", context.Evidence.Workpiece.Sequence.Value);
        Assert.Equal(0, context.Evidence.MotionAndMagnet.X11LastValue.Value);
        Assert.Equal(3, context.Evidence.MotionAndMagnet.X11Attempts.Value);
        Assert.True(context.Evidence.MotionAndMagnet.ZMayStillBeLow.HasValue);
        Assert.False(context.Evidence.MotionAndMagnet.ZMayStillBeLow.Value);
        Assert.True(context.Evidence.Recovery.Completed.HasValue && context.Evidence.Recovery.Completed.Value);
        Assert.Equal(3, context.Evidence.Recovery.Steps.Count);
        Assert.All(context.Evidence.Recovery.Steps, step => Assert.Equal(RecoveryStepState.Succeeded, step.State));
        Assert.Equal(3, context.Evidence.Locks.Items.Count);
        Assert.All(context.Evidence.Locks.Items, item =>
            Assert.True(item.ReleasedAfterward.HasValue && item.ReleasedAfterward.Value));
        Assert.Contains("三把锁已释放", context.DetailMessage, StringComparison.Ordinal);
        Assert.Equal(PhysicalConclusionCode.ManualConfirmationRequired, context.PhysicalConclusion.Code);
    }

    [Fact]
    public void Failed_recovery_is_recorded_without_claiming_physical_safety()
    {
        var reporter = new CapturingReporter();
        OperationalPhysicalCycleTracker tracker = CreateThreeZeroTracker(safeZ: false);
        var finalization = new OperationalEventContextFactory.RearLoadX11Finalization(
            RecoveryStepState.Failed,
            "退磁调用异常，结果未知",
            RecoveryStepState.Failed,
            "Z回原点失败",
            RecoveryStepState.Skipped,
            "Z可能低位，禁止X退避",
            true, true, true, true, true, true,
            CacheRestored: true,
            RearEnginePaused: true,
            SafetyAlarmCallbackCompleted: true,
            ResultSummary: "退磁未知；Z回原点失败；未执行X退避；锁已释放");

        OperationalEventContextFactory.TryReportRearLoadX11FinalFailure(
            reporter, CreateSite(), tracker, finalization, new Exception("恢复失败"));

        OperationalEventContext context = Assert.Single(reporter.Contexts);
        Assert.True(context.Evidence.Recovery.Completed.HasValue);
        Assert.False(context.Evidence.Recovery.Completed.Value);
        Assert.Equal(RecoveryStepState.Failed, context.Evidence.Recovery.Steps[0].State);
        Assert.Equal(RecoveryStepState.Failed, context.Evidence.Recovery.Steps[1].State);
        Assert.Equal(RecoveryStepState.Skipped, context.Evidence.Recovery.Steps[2].State);
        Assert.Equal(PhysicalConclusionCode.ManualConfirmationRequired, context.PhysicalConclusion.Code);
        Assert.Contains("现场仍需人工确认", context.PhysicalConclusion.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Reporter_failure_is_isolated_from_finally()
    {
        OperationalPhysicalCycleTracker tracker = CreateThreeZeroTracker(safeZ: true);
        var finalization = new OperationalEventContextFactory.RearLoadX11Finalization(
            RecoveryStepState.Succeeded, "退磁成功",
            RecoveryStepState.Succeeded, "Z安全",
            RecoveryStepState.Skipped, "普通斜床无需退避",
            true, true, true, true, true, true,
            true, true, true, "收尾完成");

        Exception? escaped = Record.Exception(() =>
            OperationalEventContextFactory.TryReportRearLoadX11FinalFailure(
                new ThrowingReporter(), CreateSite(), tracker, finalization, null));

        Assert.Null(escaped);
    }

    private static OperationalPhysicalCycleTracker CreateThreeZeroTracker(bool safeZ)
    {
        var tracker = new OperationalPhysicalCycleTracker("rear-load-x11-test");
        tracker.BeginZDown(500);
        tracker.CompleteZDown();
        tracker.BeginMagnetOn();
        tracker.CompleteMagnetOn();
        tracker.BeginX11Stage("充磁后持件确认");
        for (int index = 0; index < 3; index++)
        {
            tracker.BeginX11Attempt();
            tracker.CompleteX11(false, DateTime.UtcNow.AddSeconds(index));
        }
        tracker.BeginMagnetOff();
        if (safeZ)
        {
            tracker.CompleteMagnetOff();
            tracker.BeginSafeZReturn(0, "开始回原点");
            tracker.ConfirmSafeZ(0, 5, "Z回原点成功");
        }
        else
        {
            tracker.FailMagnetOff();
            tracker.MarkZUnknown("Z回原点失败");
        }
        return tracker;
    }

    private static OperationalEventContextFactory.OperationalPhysicalEventSite CreateSite() => new(
        Scope: "1号线",
        Engine: "后端引擎",
        DeviceType: "后天车",
        DeviceNo: "2",
        Station: "ST711",
        ActionStage: "ST711三次X11均未确认持件（最终收尾）",
        ActionId: "rear-load-action-1",
        CorrelationKey: "rear-load-action-1:rear-load",
        Workpiece: new WorkpieceCache
        {
            PlateNo = "P-001",
            Sequence = "S-01",
            Diameter = 600,
            Length = 1200
        },
        PlannedSource: "ST711",
        PlannedTarget: "ST108",
        Owner: "1号线后天车",
        LastConfirmedLocation: "ST711",
        LastSuccessfulCheckpoint: "X11第三次读取为0",
        LockName: "ZoneMT/ZoneTS/后天车锁");

    private sealed class CapturingReporter : IOperationalEventReporter
    {
        public List<OperationalEventContext> Contexts { get; } = new();
        public void Report(OperationalEventContext context) => Contexts.Add(context);
        public void ObserveTransientFailure(OperationalFailureObservation observation) { }
        public void ObserveRecovery(OperationalRecoveryObservation observation) { }
        public void ObserveStage(OperationalStageObservation observation) { }
    }

    private sealed class ThrowingReporter : IOperationalEventReporter
    {
        public void Report(OperationalEventContext context) => throw new InvalidOperationException("monitor failed");
        public void ObserveTransientFailure(OperationalFailureObservation observation) { }
        public void ObserveRecovery(OperationalRecoveryObservation observation) { }
        public void ObserveStage(OperationalStageObservation observation) { }
    }
}
