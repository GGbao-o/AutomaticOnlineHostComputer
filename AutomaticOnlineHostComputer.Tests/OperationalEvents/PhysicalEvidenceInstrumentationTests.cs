using System.Text.RegularExpressions;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class PhysicalEvidenceInstrumentationTests
{
    private static readonly IReadOnlyDictionary<string, (int MagnetOn, int X11, int MagnetOff)> Baseline =
        new Dictionary<string, (int, int, int)>(StringComparer.Ordinal)
        {
            ["Line1FrontFlowEngine.cs"] = (5, 5, 7),
            ["Line2FrontFlowEngine.cs"] = (5, 5, 7),
            ["Line1RearFlowEngine.cs"] = (4, 4, 7),
            ["Line2RearFlowEngine.cs"] = (4, 4, 7),
            ["BalancingFlowEngine.cs"] = (4, 4, 4),
            ["GrindingFlowEngine.cs"] = (4, 4, 4)
        };

    [Fact]
    public void Device_call_baseline_remains_26_magnet_on_26_x11_and_36_magnet_off()
    {
        int on = 0, x11 = 0, off = 0;
        foreach ((string file, (int expectedOn, int expectedX11, int expectedOff)) in Baseline)
        {
            string source = ReadFlowSource(file);
            int actualOn = Regex.Matches(source, @"\bMagnetOnAsync\(").Count;
            int actualX11 = Regex.Matches(source, @"\bReadXBitAsync\(63497,").Count;
            int actualOff = Regex.Matches(source, @"\bMagnetOffAsync\(").Count;
            Assert.Equal(expectedOn, actualOn);
            Assert.Equal(expectedX11, actualX11);
            Assert.Equal(expectedOff, actualOff);
            Assert.Equal(actualOn, Regex.Matches(source, @"CRANE_MAGNET_ON_RESPONSE_UNKNOWN").Count);
            Assert.Equal(actualX11, Regex.Matches(source, @"CRANE_X11_READ_FAILED").Count);
            Assert.Equal(actualOff, Regex.Matches(source, @"CRANE_MAGNET_OFF_FAILED").Count);
            on += actualOn;
            x11 += actualX11;
            off += actualOff;
        }

        Assert.Equal(26, on);
        Assert.Equal(26, x11);
        Assert.Equal(36, off);
    }

    [Fact]
    public void Tracker_counts_failed_attempt_but_preserves_last_successful_x11_value()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-A");
        tracker.BeginX11Attempt();
        tracker.CompleteX11(false, DateTime.UnixEpoch);
        tracker.BeginX11Attempt();
        tracker.FailX11();

        OperationalPhysicalCycleSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(2, snapshot.X11Attempts.Value);
        Assert.Equal(0, snapshot.X11LastValue.Value);
        Assert.False(snapshot.X11ReadValid.Value);
        Assert.Equal(DateTime.UnixEpoch, snapshot.X11ReadAtUtc.Value);
        Assert.Contains("X11成功读取为0", snapshot.LastSuccessfulCheckpoint.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Tracker_distinguishes_command_start_acknowledgement_failure_and_safe_z()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-B");
        tracker.BeginZDown();
        tracker.CompleteZDown();
        tracker.BeginMagnetOn();
        tracker.FailMagnetOn();
        tracker.BeginMagnetOff();
        tracker.CompleteMagnetOff();
        tracker.ConfirmSafeZ(120, 5, "回升命令成功返回");

        OperationalPhysicalCycleSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(DeviceCommandState.Acknowledged, snapshot.ZDownCommand.State);
        Assert.False(snapshot.ZMayStillBeLow.Value);
        Assert.Equal(DeviceCommandState.Unknown, snapshot.MagnetOnCommand.State);
        Assert.Equal(EvidenceAvailability.Unknown, snapshot.MagnetOnCommand.Availability);
        Assert.Equal(DeviceCommandState.Acknowledged, snapshot.MagnetOffCommand.State);
        Assert.True(snapshot.ZKnownSafe.Value);
        Assert.Equal(120, snapshot.SafeZTarget.Value);
        Assert.Contains("目标=120", snapshot.LastSuccessfulCheckpoint.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void New_cycle_has_no_evidence_from_previous_cycle()
    {
        var first = new OperationalPhysicalCycleTracker("cycle-1");
        first.BeginX11Attempt();
        first.CompleteX11(true, DateTime.UnixEpoch);
        first.BeginMagnetOn();
        first.CompleteMagnetOn();

        var second = new OperationalPhysicalCycleTracker("cycle-2");
        OperationalPhysicalCycleSnapshot snapshot = second.Snapshot();

        Assert.Equal("cycle-2", snapshot.CycleId);
        Assert.False(snapshot.X11LastValue.HasValue);
        Assert.False(snapshot.X11Attempts.HasValue);
        Assert.Equal(DeviceCommandState.NotSent, snapshot.MagnetOnCommand.State);
    }

    [Fact]
    public void Catalog_uses_truthful_unknown_wording_and_defines_unexpected_workpiece()
    {
        OperationalEventCatalog catalog = OperationalEventCatalog.Default;
        OperationalEventDefinition magnetOff = catalog.GetRequired("CRANE_MAGNET_OFF_FAILED");
        OperationalEventDefinition unexpected = catalog.GetRequired("CRANE_X11_UNEXPECTED_WORKPIECE");

        Assert.Contains("结果未知", magnetOff.Title, StringComparison.Ordinal);
        Assert.Contains("残留", unexpected.Title, StringComparison.Ordinal);
        Assert.Contains(unexpected.ForbiddenActions, text => text.Contains("本次计划工件", StringComparison.Ordinal));
    }

    [Fact]
    public void Instrumentation_contract_mentions_all_required_event_codes_and_fourteen_cycles()
    {
        string all = string.Join(Environment.NewLine, Baseline.Keys.Select(ReadFlowSource));

        Assert.Equal(14, Regex.Matches(all, @"new OperationalPhysicalCycleTracker\(").Count);
        Assert.Equal(26, Regex.Matches(all, @"CRANE_MAGNET_ON_RESPONSE_UNKNOWN").Count);
        Assert.Equal(26, Regex.Matches(all, @"CRANE_X11_READ_FAILED").Count);
        Assert.Equal(36, Regex.Matches(all, @"CRANE_MAGNET_OFF_FAILED").Count);
        Assert.Equal(14, Regex.Matches(all, @"CRANE_X11_NOT_CONFIRMED").Count);
        Assert.Equal(12, Regex.Matches(all, @"CRANE_X11_UNEXPECTED_WORKPIECE").Count);
        Assert.Equal(14, Regex.Matches(all, @"PHYSICAL_HANDOFF_NOT_CLOSED").Count);
    }

    [Fact]
    public void Monitoring_factory_remains_a_pure_copy_and_report_boundary()
    {
        string factory = ReadFlowSource("OperationalEventContextFactory.cs");
        Assert.DoesNotContain("ReadXBitAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOnAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOffAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveAbsoluteAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void Reporter_failure_cannot_replace_the_original_business_exception()
    {
        var reporter = new ThrowingReporter();
        var original = new InvalidOperationException("原业务异常");
        Exception? propagated = null;

        try
        {
            try
            {
                throw original;
            }
            catch (Exception ex)
            {
                OperationalEventContextFactory.TryReportPhysical(
                    reporter,
                    "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                    CreateSite(),
                    new OperationalPhysicalCycleTracker("cycle-report-failure"),
                    ex,
                    "测试Reporter异常隔离",
                    usePlannedWorkpieceEvidence: true);
                throw;
            }
        }
        catch (Exception ex)
        {
            propagated = ex;
        }

        Assert.Same(original, propagated);
        Assert.Equal(1, reporter.ReportAttempts);
    }

    [Fact]
    public void Reporter_failure_cannot_replace_operation_cancellation()
    {
        var reporter = new ThrowingReporter();
        var original = new OperationCanceledException("原取消异常");
        Exception? propagated = null;

        try
        {
            try
            {
                throw original;
            }
            catch (OperationCanceledException ex)
            {
                OperationalEventContextFactory.TryReportPhysical(
                    reporter,
                    "CRANE_X11_READ_FAILED",
                    CreateSite(),
                    new OperationalPhysicalCycleTracker("cycle-cancellation"),
                    ex,
                    "测试取消异常隔离",
                    usePlannedWorkpieceEvidence: true);
                throw;
            }
        }
        catch (Exception ex)
        {
            propagated = ex;
        }

        Assert.Same(original, propagated);
        Assert.Equal(1, reporter.ReportAttempts);
    }

    [Fact]
    public void Unexpected_x11_workpiece_keeps_identity_unknown_instead_of_binding_planned_workpiece()
    {
        var reporter = new CapturingReporter();
        var tracker = new OperationalPhysicalCycleTracker("cycle-residual");
        tracker.BeginX11Attempt();
        tracker.CompleteX11(true, DateTime.UnixEpoch);

        OperationalEventContextFactory.TryReportPhysical(
            reporter,
            "CRANE_X11_UNEXPECTED_WORKPIECE",
            CreateSite(),
            tracker,
            exception: null,
            detail: "取料前X11=1",
            usePlannedWorkpieceEvidence: false,
            holdingWorkpiece: true);

        OperationalEventContext context = Assert.Single(reporter.Contexts);
        Assert.Equal(PhysicalConclusionCode.WorkpieceConfirmedHeld, context.PhysicalConclusion.Code);
        Assert.False(context.Evidence.Workpiece.PlateNo.HasValue);
        Assert.False(context.Evidence.Workpiece.Sequence.HasValue);
        Assert.False(context.Evidence.Workpiece.Diameter.HasValue);
        Assert.False(context.Evidence.Workpiece.Source.HasValue);
        Assert.False(context.Evidence.Workpiece.Target.HasValue);
        Assert.True(context.Evidence.BusinessState.HoldingWorkpiece.Value);
        Assert.Equal(1, context.Evidence.MotionAndMagnet.X11LastValue.Value);
        Assert.True(context.Evidence.MotionAndMagnet.X11ReadValid.Value);
    }

    [Fact]
    public void Handoff_event_contains_each_physical_commitment_without_inventing_missing_results()
    {
        var reporter = new CapturingReporter();
        var tracker = new OperationalPhysicalCycleTracker("cycle-handoff");
        tracker.BeginMagnetOn();
        tracker.CompleteMagnetOn();
        tracker.BeginMagnetOff();
        tracker.CompleteMagnetOff();

        OperationalEventContextFactory.TryReportPhysical(
            reporter,
            "PHYSICAL_HANDOFF_NOT_CLOSED",
            CreateSite(),
            tracker,
            new InvalidOperationException("通知失败"),
            "已放料但下游通知失败",
            usePlannedWorkpieceEvidence: true,
            holdingWorkpiece: false,
            placed: true,
            cacheNotified: null,
            downstreamNotified: false,
            handoffClosed: false);

        OperationalEventContext context = Assert.Single(reporter.Contexts);
        IReadOnlyDictionary<string, EvidenceValue<bool>> commitments = context.Evidence.BusinessState.PhysicalCommitments
            .ToDictionary(item => item.Name, item => item.State, StringComparer.Ordinal);

        Assert.Equal(7, commitments.Count);
        Assert.True(commitments["充磁命令调用"].Value);
        Assert.False(commitments["X11确认持件"].Value);
        Assert.True(commitments["退磁命令返回"].Value);
        Assert.True(commitments["推定已放料"].Value);
        Assert.False(commitments["缓存通知"].HasValue);
        Assert.Equal(EvidenceAvailability.Unknown, commitments["缓存通知"].Availability);
        Assert.False(commitments["PLC/CNC通知"].Value);
        Assert.False(commitments["完整物理交接闭环"].Value);
        Assert.Equal(PhysicalConclusionCode.ManualConfirmationRequired, context.PhysicalConclusion.Code);
    }

    [Fact]
    public void Known_dimension_and_route_are_preserved_when_plate_identity_is_unavailable()
    {
        var reporter = new CapturingReporter();
        OperationalEventContextFactory.OperationalPhysicalEventSite site = CreateSite() with
        {
            Workpiece = new WorkpieceCache { Diameter = 680 },
            PlannedSource = "ST009",
            PlannedTarget = "ST010",
            LastConfirmedLocation = "ST009"
        };

        OperationalEventContextFactory.TryReportPhysical(
            reporter,
            "CRANE_X11_READ_FAILED",
            site,
            new OperationalPhysicalCycleTracker("cycle-known-diameter"),
            new InvalidOperationException("X11读取失败"),
            "M700来源已读取D100直径",
            usePlannedWorkpieceEvidence: true);

        WorkpieceEvidence evidence = Assert.Single(reporter.Contexts).Evidence.Workpiece;
        Assert.False(evidence.PlateNo.HasValue);
        Assert.False(evidence.Sequence.HasValue);
        Assert.Equal(680, evidence.Diameter.Value);
        Assert.Equal("ST009", evidence.Source.Value);
        Assert.Equal("ST010", evidence.Target.Value);
        Assert.Equal("ST009", evidence.LastConfirmedLocation.Value);
    }

    [Fact]
    public void Grinding_reports_real_magnet_attempts_inside_the_retry_delegate()
    {
        string grinding = ReadFlowSource("GrindingFlowEngine.cs");

        Assert.Contains("await CraneOpAsync(crane, async c =>", grinding, StringComparison.Ordinal);
        Assert.Matches(
            @"await CraneOpAsync\(crane, async c =>\s*\{\s*operationalTracker\.BeginMagnetOn\(\);\s*try\s*\{\s*await crane\.MagnetOnAsync\(c\);",
            grinding);
        Assert.Matches(
            @"await CraneOpAsync\(crane, async c =>\s*\{\s*operationalTracker\.BeginMagnetOff\(\);\s*try\s*\{\s*await crane\.MagnetOffAsync\(c\);",
            grinding);
    }

    private static OperationalEventContextFactory.OperationalPhysicalEventSite CreateSite() => new(
        Scope: "测试线路",
        Engine: "测试引擎",
        DeviceType: "天车",
        DeviceNo: "99",
        Station: "ST999",
        ActionStage: "测试动作阶段",
        ActionId: "action-physical-test",
        CorrelationKey: "action-physical-test:cycle",
        Workpiece: new WorkpieceCache
        {
            PlateNo = "P-001",
            Sequence = "S-01",
            Diameter = 600,
            Length = 1200
        },
        PlannedSource: "ST001",
        PlannedTarget: "ST002",
        Owner: "测试天车",
        LastConfirmedLocation: "ST001",
        LastSuccessfulCheckpoint: "测试检查点",
        LockName: "测试碰撞区锁");

    private static string ReadFlowSource(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));

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
        public int ReportAttempts { get; private set; }

        public void Report(OperationalEventContext context)
        {
            ReportAttempts++;
            throw new InvalidOperationException("测试Reporter失败");
        }

        public void ObserveTransientFailure(OperationalFailureObservation observation) { }

        public void ObserveRecovery(OperationalRecoveryObservation observation) { }

        public void ObserveStage(OperationalStageObservation observation) { }
    }
}
