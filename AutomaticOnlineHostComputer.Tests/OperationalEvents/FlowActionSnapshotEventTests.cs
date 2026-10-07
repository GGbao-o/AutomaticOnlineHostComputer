using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

/// <summary>防止最终安全异常退化为只有异常文本、丢失动作账本事实。</summary>
public sealed class FlowActionSnapshotEventTests
{
    [Fact]
    public void Physical_event_copies_action_snapshot_into_workpiece_business_and_lock_evidence()
    {
        var action = new FlowActionContext(
            "line2-rear-load-42", "2号线后天车上料", "4号天车", "E26072224/8", "M818", "ST021", "M818");
        action.RegisterLock("RearCrane#4");
        action.RegisterLock("ZoneMT");
        action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(-2000, 780, 0), "去ST021时Y轴未到位");
        action.MarkCommandSent();
        action.RecordFeedback(new FlowActionPosition(-2000, 330, 0), x11: true, magnetOn: true, "X已到位，Y仍在330mm");
        action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1，后天车持件");
        action.MarkCommandResponseUnknown("XY命令超时，Y轴最终位置未知");
        action.PauseForManualResolution("XY运动超时，等待人工确认现场位置");

        var reporter = new CapturingReporter();
        OperationalEventContextFactory.TryReportPhysical(
            reporter,
            "ENGINE_FINAL_FAILURE",
            CreateSite(),
            new OperationalPhysicalCycleTracker("flow-action-event"),
            new TimeoutException("Y轴未到位"),
            "后天车XY运动超时",
            usePlannedWorkpieceEvidence: true,
            actionSnapshot: action.Snapshot());

        OperationalEventContext context = Assert.Single(reporter.Contexts);
        Assert.Equal("M818", context.Evidence.Workpiece.Source.Value);
        Assert.Equal("ST021", context.Evidence.Workpiece.Target.Value);
        Assert.Contains("OnCarrier", context.Evidence.Workpiece.SoftwareOwner.Value);
        Assert.Contains("MoveXYToTarget", context.Evidence.BusinessState.LastSuccessfulCheckpoint.Value);
        Assert.Contains("PauseOnCarrier", context.Evidence.BusinessState.StateAfter.Value);
        Assert.Contains(context.Evidence.Locks.Items, x => x.Name == "RearCrane#4");
        Assert.Contains(context.Evidence.Locks.Items, x => x.Name == "ZoneMT");
        Assert.Contains(context.Evidence.BusinessState.PhysicalCommitments,
            x => x.Name == "动作账本步骤/命令" && x.State.Reason.Contains("ResponseUnknown", StringComparison.Ordinal));
    }

    private static OperationalEventContextFactory.OperationalPhysicalEventSite CreateSite() => new(
        Scope: "2号线后端", Engine: "2号线后端引擎", DeviceType: "天车", DeviceNo: "4", Station: "M818",
        ActionStage: "后天车动作最终异常", ActionId: "line2-rear-load-42", CorrelationKey: "test:42",
        Workpiece: new WorkpieceCache { PlateNo = "E26072224", Sequence = "8", Diameter = 164.26 },
        PlannedSource: "M818", PlannedTarget: "ST021", Owner: "4号天车", LastConfirmedLocation: "M818",
        LastSuccessfulCheckpoint: "取料确认", LockName: "RearCrane#4");

    private sealed class CapturingReporter : IOperationalEventReporter
    {
        public List<OperationalEventContext> Contexts { get; } = new();
        public void Report(OperationalEventContext context) => Contexts.Add(context);
        public void ObserveTransientFailure(OperationalFailureObservation observation) { }
        public void ObserveRecovery(OperationalRecoveryObservation observation) { }
        public void ObserveStage(OperationalStageObservation observation) { }
    }
}
