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
    public void Every_physical_device_call_has_a_balanced_enclosing_report_catch_with_correct_cancellation_semantics()
    {
        foreach (string file in Baseline.Keys)
        {
            string source = ReadFlowSource(file);
            AssertEveryCallHasReportingCatch(source, file, "MagnetOnAsync(", "CRANE_MAGNET_ON_RESPONSE_UNKNOWN", mustExcludeCancellation: false);
            AssertEveryCallHasReportingCatch(source, file, "MagnetOffAsync(", "CRANE_MAGNET_OFF_FAILED", mustExcludeCancellation: false);
            AssertEveryCallHasReportingCatch(source, file, "ReadXBitAsync(63497,", "CRANE_X11_READ_FAILED", mustExcludeCancellation: true);
        }
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
    public void Instrumentation_contract_mentions_all_required_event_codes_and_fourteen_safe_cycles()
    {
        string all = string.Join(Environment.NewLine, Baseline.Keys.Select(ReadFlowSource));

        Assert.Equal(14, Regex.Matches(all, @"OperationalEventContextFactory\.CreatePhysicalCycleTrackerOrDisabled\(").Count);
        Assert.Equal(14, Regex.Matches(all, @"OperationalEventContextFactory\.CreatePhysicalSiteOrEmpty\(").Count);
        Assert.DoesNotContain("new OperationalPhysicalCycleTracker(", all, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"=\s*new OperationalEventContextFactory\.OperationalPhysicalEventSite\(", all);
        Assert.DoesNotMatch(@"\.BeginZDown\(\s*\)", all);
        Assert.Equal(26, Regex.Matches(all, @"CRANE_MAGNET_ON_RESPONSE_UNKNOWN").Count);
        Assert.Equal(26, Regex.Matches(all, @"CRANE_X11_READ_FAILED").Count);
        Assert.Equal(36, Regex.Matches(all, @"CRANE_MAGNET_OFF_FAILED").Count);
        // 两条后天车上料出口延后到finally最终化助手；其余12个出口仍直接上报。
        Assert.Equal(12, Regex.Matches(all, @"CRANE_X11_NOT_CONFIRMED").Count);
        Assert.Equal(2, Regex.Matches(all, @"TryReportRearLoadX11FinalFailure").Count);
        Assert.Equal(12, Regex.Matches(all, @"CRANE_X11_UNEXPECTED_WORKPIECE").Count);
        Assert.Equal(16, Regex.Matches(all, @"PHYSICAL_HANDOFF_NOT_CLOSED").Count);
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

    [Fact]
    public void X11_attempts_are_segmented_without_losing_cycle_total()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-x11-stages");
        tracker.BeginX11Stage("取料前残留检查");
        tracker.BeginX11Attempt();
        tracker.CompleteX11(false, DateTime.UnixEpoch);
        tracker.BeginX11Stage("充磁后持件确认");
        for (int i = 0; i < 3; i++)
        {
            tracker.BeginX11Attempt();
            tracker.CompleteX11(false, DateTime.UnixEpoch.AddSeconds(i + 1));
        }

        OperationalPhysicalCycleSnapshot snapshot = tracker.Snapshot();
        Assert.Equal("充磁后持件确认", snapshot.X11StageName.Value);
        Assert.Equal(3, snapshot.X11Attempts.Value);
        Assert.Equal(4, snapshot.X11CycleAttempts.Value);
        Assert.Contains("物理周期累计4次", snapshot.X11Attempts.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Placement_magnet_off_started_is_unknown_and_success_immediately_infers_target_location()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-placement-off");
        tracker.ConfirmHolding("X11=1");
        tracker.BeginPlacementMagnetOff("ST010");

        OperationalPhysicalCycleSnapshot started = tracker.Snapshot();
        Assert.Equal(EvidenceAvailability.Unknown, started.HoldingWorkpiece.Availability);
        Assert.Equal(EvidenceAvailability.Unknown, started.Placed.Availability);
        Assert.Equal(EvidenceAvailability.Unknown, started.LastConfirmedLocation.Availability);

        tracker.CompletePlacementMagnetOff("ST010");
        OperationalPhysicalCycleSnapshot succeeded = tracker.Snapshot();
        Assert.Equal(EvidenceAvailability.Inferred, succeeded.HoldingWorkpiece.Availability);
        Assert.False(succeeded.HoldingWorkpiece.Value);
        Assert.Equal(EvidenceAvailability.Inferred, succeeded.Placed.Availability);
        Assert.True(succeeded.Placed.Value);
        Assert.Equal("ST010", succeeded.LastConfirmedLocation.Value);
        Assert.Equal(EvidenceAvailability.Inferred, succeeded.LastConfirmedLocation.Availability);
    }

    [Fact]
    public void New_z_descent_invalidates_old_safe_target_and_does_not_leak_it_into_the_next_stage()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-z-reset");
        tracker.ConfirmSafeZ(500, 5, "上一阶段已回安全高度");
        Assert.Equal(500, tracker.Snapshot().SafeZTarget.Value);

        tracker.BeginZDown(260);
        OperationalPhysicalCycleSnapshot started = tracker.Snapshot();
        Assert.Equal(EvidenceAvailability.Unknown, started.ZKnownSafe.Availability);
        Assert.Equal(EvidenceAvailability.Unknown, started.ZMayStillBeLow.Availability);
        Assert.False(started.SafeZTarget.HasValue);
        Assert.False(started.SafeZTolerance.HasValue);
        Assert.Equal(260, started.TargetZ.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, started.TargetZ.Availability);

        tracker.CompleteZDown();
        OperationalPhysicalCycleSnapshot lowered = tracker.Snapshot();
        Assert.True(lowered.ZMayStillBeLow.Value);
        Assert.Equal(260, lowered.TargetZ.Value);
        Assert.False(lowered.SafeZTarget.HasValue);
        Assert.False(lowered.SafeZTolerance.HasValue);
    }

    [Fact]
    public void Physical_report_exposes_actual_z_target_without_reusing_safe_z_target()
    {
        var reporter = new CapturingReporter();
        var tracker = new OperationalPhysicalCycleTracker("cycle-z-report");
        tracker.ConfirmSafeZ(500, 5, "上一阶段安全Z");
        tracker.BeginZDown(275);
        tracker.MarkZUnknown("Z运动异常，实际位置未知");

        OperationalEventContextFactory.TryReportPhysical(
            reporter,
            "CRANE_MAGNET_OFF_FAILED",
            CreateSite(),
            tracker,
            new InvalidOperationException("Z运动后退磁异常"),
            "验证实际Z目标证据",
            usePlannedWorkpieceEvidence: true);

        OperationalEventContext context = Assert.Single(reporter.Contexts);
        Assert.Equal(275, context.Evidence.Position.TargetZ.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, context.Evidence.Position.TargetZ.Availability);
        Assert.Equal(EvidenceAvailability.Unknown, tracker.Snapshot().SafeZTarget.Availability);
    }

    [Fact]
    public void Placement_success_remains_visible_when_z_is_low_or_later_becomes_unknown()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-placement-low-z");
        tracker.BeginZDown();
        tracker.CompleteZDown();
        tracker.BeginPlacementMagnetOff("ST710");
        tracker.CompletePlacementMagnetOff("ST710");

        OperationalPhysicalCycleSnapshot low = tracker.Snapshot();
        Assert.True(low.Placed.Value);
        Assert.Equal(EvidenceAvailability.Inferred, low.Placed.Availability);
        Assert.True(low.ZMayStillBeLow.Value);
        Assert.Equal("ST710", low.LastConfirmedLocation.Value);

        tracker.MarkZUnknown("退磁后Z回升异常，当前位置未知");
        OperationalPhysicalCycleSnapshot unknown = tracker.Snapshot();
        Assert.True(unknown.Placed.Value);
        Assert.Equal("ST710", unknown.LastConfirmedLocation.Value);
        Assert.Equal(EvidenceAvailability.Unknown, unknown.ZMayStillBeLow.Availability);
        Assert.Equal(EvidenceAvailability.Unknown, unknown.ZKnownSafe.Availability);
    }

    [Fact]
    public void Monitor_step_distinguishes_not_started_started_unknown_and_succeeded()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-monitor-step");
        tracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801");
        Assert.Equal(
            OperationalMonitorStepState.NotStarted,
            Assert.Single(tracker.Snapshot().MonitorSteps).State);

        tracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801", "SetPickupDoneAsync调用已开始");
        Assert.Equal(
            OperationalMonitorStepState.StartedResultUnknown,
            Assert.Single(tracker.Snapshot().MonitorSteps).State);

        tracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801", "SetPickupDoneAsync成功返回");
        Assert.Equal(
            OperationalMonitorStepState.Succeeded,
            Assert.Single(tracker.Snapshot().MonitorSteps).State);
    }

    [Fact]
    public void Cache_mutation_preserves_exact_before_after_facts_and_unknown_started_result()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-cache-mutation");
        tracker.BeginCacheMutation(
            "M2:pickReg=710",
            EvidenceValue<string>.Confirmed("存在工件P-001", "锁内读取已确认"),
            "Remove调用已开始");

        OperationalPhysicalCycleSnapshot started = tracker.Snapshot();
        Assert.Equal("M2:pickReg=710", started.CacheKey.Value);
        Assert.Equal("存在工件P-001", started.CacheBefore.Value);
        Assert.Equal(EvidenceAvailability.Unknown, started.CacheAfter.Availability);
        Assert.Equal(OperationalMonitorStepState.StartedResultUnknown, Assert.Single(started.MonitorSteps).State);

        tracker.CompleteCacheMutation(
            "M2:pickReg=710",
            EvidenceValue<string>.Confirmed("存在工件P-001", "锁内读取已确认"),
            EvidenceValue<string>.Confirmed("键已移除", "Remove成功返回"),
            "Remove成功返回");
        OperationalPhysicalCycleSnapshot succeeded = tracker.Snapshot();
        Assert.Equal("存在工件P-001", succeeded.CacheBefore.Value);
        Assert.Equal("键已移除", succeeded.CacheAfter.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, succeeded.CacheAfter.Availability);
        Assert.Equal(OperationalMonitorStepState.Succeeded, Assert.Single(succeeded.MonitorSteps).State);
    }

    [Fact]
    public void Cache_callback_before_fact_stays_unknown_instead_of_becoming_confirmed_unknown_text()
    {
        var tracker = new OperationalPhysicalCycleTracker("cycle-cache-callback");
        tracker.BeginCacheMutation(
            "M817",
            EvidenceValue<string>.Unknown("回调前未确认M817缓存已写入本周期工件"),
            "缓存回调调用已开始");

        OperationalPhysicalCycleSnapshot started = tracker.Snapshot();
        Assert.Equal(EvidenceAvailability.Unknown, started.CacheBefore.Availability);
        Assert.False(started.CacheBefore.HasValue);
        Assert.Contains("未确认", started.CacheBefore.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("已确认", started.CacheBefore.Reason, StringComparison.Ordinal);

        tracker.CompleteCacheMutation(
            "M817",
            EvidenceValue<string>.Unknown("回调前未确认M817缓存已写入本周期工件"),
            EvidenceValue<string>.Confirmed("回调成功后M817缓存已写入本周期工件", "缓存回调成功返回"),
            "缓存回调成功返回");

        OperationalPhysicalCycleSnapshot completed = tracker.Snapshot();
        Assert.Equal(EvidenceAvailability.Unknown, completed.CacheBefore.Availability);
        Assert.Equal(EvidenceAvailability.Confirmed, completed.CacheAfter.Availability);
    }

    [Fact]
    public void Monitoring_initialization_factory_swallows_constructor_failure()
    {
        OperationalPhysicalCycleTracker tracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(
            "cycle-init-failure",
            _ => throw new InvalidOperationException("测试监控tracker构造失败"));
        OperationalEventContextFactory.OperationalPhysicalEventSite site =
            OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(
                () => throw new InvalidOperationException("测试监控site构造失败"));

        Assert.False(tracker.MonitoringAvailable);
        Assert.False(site.MonitoringAvailable);
        Assert.Equal(EvidenceAvailability.Unavailable, tracker.Snapshot().ZKnownSafe.Availability);
        var reporter = new ThrowingReporter();
        Assert.Null(Record.Exception(() =>
            OperationalEventContextFactory.TryReportPhysical(
                reporter,
                "CRANE_X11_READ_FAILED",
                site,
                tracker,
                exception: new InvalidOperationException("原业务异常"),
                detail: "监控初始化失败仍不得影响业务",
                usePlannedWorkpieceEvidence: true)));
        Assert.Equal(0, reporter.ReportAttempts);
    }

    [Fact]
    public void Monitor_step_tri_state_is_preserved_in_report_context()
    {
        var reporter = new CapturingReporter();
        var tracker = new OperationalPhysicalCycleTracker("cycle-monitor-context");
        tracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801");

        OperationalEventContext notStarted = ReportAndTake(reporter, tracker);
        EvidenceValue<bool> notStartedState = Commitment(notStarted, "PLC/CNC通知");
        Assert.False(notStartedState.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, notStartedState.Availability);

        tracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801", "PLC写调用已开始");
        OperationalEventContext started = ReportAndTake(reporter, tracker);
        Assert.Equal(EvidenceAvailability.Unknown, Commitment(started, "PLC/CNC通知").Availability);

        tracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801", "PLC写成功返回");
        OperationalEventContext succeeded = ReportAndTake(reporter, tracker);
        EvidenceValue<bool> succeededState = Commitment(succeeded, "PLC/CNC通知");
        Assert.True(succeededState.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, succeededState.Availability);
    }

    private static EvidenceValue<bool> Commitment(OperationalEventContext context, string name) =>
        Assert.Single(context.Evidence.BusinessState.PhysicalCommitments, item => item.Name == name).State;

    private static OperationalEventContext ReportAndTake(CapturingReporter reporter, OperationalPhysicalCycleTracker tracker)
    {
        reporter.Contexts.Clear();
        OperationalEventContextFactory.TryReportPhysical(
            reporter,
            "PHYSICAL_HANDOFF_NOT_CLOSED",
            CreateSite(),
            tracker,
            new InvalidOperationException("测试交接异常"),
            "测试监控步骤三态",
            usePlannedWorkpieceEvidence: true);
        return Assert.Single(reporter.Contexts);
    }

    private static void AssertEveryCallHasReportingCatch(
        string source,
        string file,
        string callToken,
        string eventCode,
        bool mustExcludeCancellation)
    {
        foreach (Match call in Regex.Matches(source, Regex.Escape(callToken)))
        {
            IReadOnlyList<(string Header, string Body)> catches =
                FindEnclosingCatches(source, call.Index, file, callToken);
            if (mustExcludeCancellation)
            {
                (string Header, string Body) reporting = Assert.Single(catches);
                AssertCatchReports(reporting, file, callToken, call.Index, eventCode);
                if (!ExcludesCancellation(reporting.Header))
                    throw new Xunit.Sdk.XunitException($"{file} 中 {callToken}（索引{call.Index}）的X11 catch没有排除OperationCanceledException：{reporting.Header.Trim()}");
            }
            else
            {
                foreach ((string Header, string Body) item in catches)
                    AssertCatchReports(item, file, callToken, call.Index, eventCode);
                if (!catches.Any(item => HandlesCancellation(item.Header)))
                    throw new Xunit.Sdk.XunitException($"{file} 中 {callToken}（索引{call.Index}）没有覆盖并上报OperationCanceledException路径");
            }
        }

        Assert.True(Regex.Matches(source, Regex.Escape(callToken)).Count > 0);
    }

    private static void AssertCatchReports(
        (string Header, string Body) item,
        string file,
        string callToken,
        int callIndex,
        string eventCode)
    {
        if (!item.Body.Contains("TryReportPhysical", StringComparison.Ordinal) ||
            !item.Body.Contains(eventCode, StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException(
                $"{file} 中 {callToken}（索引{callIndex}）的直接catch未上报{eventCode}。catch头={item.Header.Trim()}；catch体={item.Body.Trim()}");
        }
    }

    private static bool ExcludesCancellation(string header) =>
        header.Contains("OperationCanceledException", StringComparison.Ordinal) &&
        header.Contains("is not", StringComparison.Ordinal);

    private static bool HandlesCancellation(string header) =>
        !ExcludesCancellation(header) &&
        (header.Contains("OperationCanceledException", StringComparison.Ordinal) ||
         header.Contains("Exception", StringComparison.Ordinal) ||
         Regex.IsMatch(header, @"^catch\s*$"));

    private static IReadOnlyList<(string Header, string Body)> FindEnclosingCatches(
        string source,
        int callIndex,
        string file,
        string callToken)
    {
        MatchCollection candidates = Regex.Matches(source[..callIndex], @"\btry\b");
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            int tryOpen = source.IndexOf('{', candidates[i].Index + candidates[i].Length);
            if (tryOpen < 0 || tryOpen > callIndex)
                continue;
            int tryClose = FindMatchingBrace(source, tryOpen);
            if (tryClose < callIndex)
                continue;

            int cursor = SkipWhitespace(source, tryClose + 1);
            if (!source.AsSpan(cursor).StartsWith("catch", StringComparison.Ordinal))
                continue;
            var catches = new List<(string Header, string Body)>();
            while (cursor < source.Length && source.AsSpan(cursor).StartsWith("catch", StringComparison.Ordinal))
            {
                int catchOpen = source.IndexOf('{', cursor + "catch".Length);
                if (catchOpen < 0)
                    break;
                int catchClose = FindMatchingBrace(source, catchOpen);
                catches.Add((source[cursor..catchOpen], source[(catchOpen + 1)..catchClose]));
                cursor = SkipWhitespace(source, catchClose + 1);
            }
            return catches;
        }

        throw new Xunit.Sdk.XunitException($"{file} 中 {callToken}（索引{callIndex}）找不到平衡的直接try/catch块");
    }

    private static int FindMatchingBrace(string source, int openIndex)
    {
        int depth = 0;
        bool inString = false, inVerbatimString = false, inChar = false, inLineComment = false, inBlockComment = false;
        for (int i = openIndex; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }
            if (inString)
            {
                if (inVerbatimString && c == '"' && next == '"') { i++; continue; }
                if (!inVerbatimString && c == '\\') { i++; continue; }
                if (c == '"') { inString = false; inVerbatimString = false; }
                continue;
            }
            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c == '"') { inString = true; inVerbatimString = i > 0 && source[i - 1] == '@'; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        throw new Xunit.Sdk.XunitException($"索引{openIndex}处的大括号未闭合");
    }

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index])) index++;
        return index;
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
