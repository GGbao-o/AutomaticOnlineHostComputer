using System.Text.RegularExpressions;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class FineTuneInstrumentationContractTests
{
    private static readonly IReadOnlyDictionary<string, int> ExpectedCallCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Line1FrontFlowEngine.cs"] = 4,
            ["Line2FrontFlowEngine.cs"] = 4,
            ["Line1RearFlowEngine.cs"] = 5,
            ["Line2RearFlowEngine.cs"] = 5,
            ["GrindingFlowEngine.cs"] = 4
        };

    [Fact]
    public void All_22_real_call_sites_supply_structured_failure_evidence()
    {
        int total = 0;
        foreach ((string fileName, int expectedCount) in ExpectedCallCounts)
        {
            string source = ReadFlowSource(fileName);
            IReadOnlyList<string> invocations = ExtractInvocations(
                source,
                "XAbsFineTuneHelper.VerifyAndFineTuneAsync(");

            Assert.Equal(expectedCount, invocations.Count);
            total += invocations.Count;

            foreach (string invocation in invocations)
            {
                Assert.Contains("reporter: _exceptionReporter", invocation, StringComparison.Ordinal);
                Assert.Contains("actionId: actionId", invocation, StringComparison.Ordinal);
                Assert.Contains("workpiece:", invocation, StringComparison.Ordinal);
                Assert.Contains("source:", invocation, StringComparison.Ordinal);
                Assert.Contains("target:", invocation, StringComparison.Ordinal);
                Assert.Contains("targetZ:", invocation, StringComparison.Ordinal);
                Assert.Contains("physicalPhase:", invocation, StringComparison.Ordinal);
                Assert.Contains("failureContext:", invocation, StringComparison.Ordinal);
                Assert.Contains("physicalTracker:", invocation, StringComparison.Ordinal);
            }
        }

        Assert.Equal(22, total);
    }

    [Fact]
    public void Eight_physical_cycle_entries_create_fresh_action_ids_and_helper_never_creates_one()
    {
        IReadOnlyDictionary<string, int> expectedCreations = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Line1FrontFlowEngine.cs"] = 1,
            ["Line2FrontFlowEngine.cs"] = 1,
            ["Line1RearFlowEngine.cs"] = 2,
            ["Line2RearFlowEngine.cs"] = 2,
            ["GrindingFlowEngine.cs"] = 2
        };

        int total = 0;
        foreach ((string fileName, int expected) in expectedCreations)
        {
            string source = ReadFlowSource(fileName);
            int actual = Regex.Matches(source, @"\bstring\s+actionId\s*=\s*OperationalEventContextFactory\.NewActionId\(").Count;
            Assert.Equal(expected, actual);
            total += actual;
        }

        Assert.Equal(8, total);
        Assert.DoesNotContain("NewActionId", ReadFlowSource("XAbsFineTuneHelper.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Physical_phase_distribution_is_10_pickups_and_12_placements_without_fabricating_marker_x11()
    {
        string allCalls = string.Join(
            Environment.NewLine,
            ExpectedCallCounts.Keys.Select(ReadFlowSource));

        Assert.Equal(10, Regex.Matches(allCalls, @"physicalPhase:\s*OperationalEventContextFactory\.PickupBeforeZDown\(").Count);
        Assert.Equal(12, Regex.Matches(allCalls, @"physicalPhase:\s*OperationalEventContextFactory\.PlacementBeforeZDown\(").Count);
        Assert.Equal(8, Regex.Matches(allCalls, @"PickupBeforeZDown\(physicalTracker,\s*true,").Count);
        Assert.Equal(2, Regex.Matches(allCalls, @"PickupBeforeZDown\(physicalTracker,\s*false,").Count);
        Assert.Contains("ST019/ST010分流尚未确定", allCalls, StringComparison.Ordinal);
        Assert.Contains("ST020/ST021分流尚未确定", allCalls, StringComparison.Ordinal);
        Assert.Contains("中转架位置尚未选择", allCalls, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_marks_missing_workpiece_values_unknown_instead_of_confirming_empty_or_zero()
    {
        WorkpieceEvidence evidence = OperationalEventContextFactory.Workpiece(
            default,
            EvidenceValue<string>.Confirmed("ST713", "动作来源"),
            EvidenceValue<string>.Confirmed("ST107", "动作目标"),
            owner: "1号线前天车",
            lastConfirmedLocation: "ST713");

        Assert.Equal(EvidenceAvailability.Unknown, evidence.PlateNo.Availability);
        Assert.False(evidence.PlateNo.HasValue);
        Assert.Equal(EvidenceAvailability.Unknown, evidence.Sequence.Availability);
        Assert.False(evidence.Sequence.HasValue);
        Assert.Equal(EvidenceAvailability.Unknown, evidence.Diameter.Availability);
        Assert.False(evidence.Diameter.HasValue);
        Assert.Equal(EvidenceAvailability.Unknown, evidence.Length.Availability);
        Assert.False(evidence.Length.HasValue);
    }

    [Fact]
    public void Helper_isolates_reporter_failure_and_rethrows_original_non_cancellation_exception()
    {
        string helper = ReadFlowSource("XAbsFineTuneHelper.cs");

        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", helper, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"catch \(Exception ex\) when \(ex is not OperationCanceledException\)[\s\S]*?try[\s\S]*?reporter\.Report\([\s\S]*?catch\s*\{\s*\}[\s\S]*?throw;", RegexOptions.CultureInvariant),
            helper);
        Assert.DoesNotContain("catch (OperationCanceledException", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("throw ex;", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_keeps_original_device_io_and_retry_limits()
    {
        string helper = ReadFlowSource("XAbsFineTuneHelper.cs");

        Assert.Single(Regex.Matches(helper, @"\b_crane\.ReadStatusAsync\(").Cast<Match>());
        Assert.Single(Regex.Matches(helper, @"\b_crane\.MoveAbsoluteAsync\(").Cast<Match>());
        Assert.Equal(3, Regex.Matches(helper, @"\bTask\.Delay\(").Count);
        Assert.Contains("private const int StableReadMaxAttempts = 20;", helper, StringComparison.Ordinal);
        Assert.Contains("private const int StableReadRequiredCount = 5;", helper, StringComparison.Ordinal);
        Assert.Contains("private const int FineTuneMaxAttempts = 2;", helper, StringComparison.Ordinal);
        Assert.Contains("private const int AbsFeedbackRefreshMaxRetries = 3;", helper, StringComparison.Ordinal);
        Assert.Contains("for (int fineTuneAttempt = 1; fineTuneAttempt <= FineTuneMaxAttempts; fineTuneAttempt++)", helper, StringComparison.Ordinal);
        Assert.Contains("for (int attempt = 1; attempt <= StableReadMaxAttempts; attempt++)", helper, StringComparison.Ordinal);
        Assert.Contains("for (int retry = 0; retry <= AbsFeedbackRefreshMaxRetries; retry++)", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Original_failure_modes_and_messages_remain_observable()
    {
        string helper = ReadFlowSource("XAbsFineTuneHelper.cs");

        string[] failureMarkers =
        {
            "读取状态失败, 禁止Z下降",
            "连续读取不稳定",
            "绝对编码器偏差过大",
            "两次微调后仍超差",
            "微调后反馈未跟随显示坐标"
        };

        Assert.All(failureMarkers, marker => Assert.Contains(marker, helper, StringComparison.Ordinal));
        Assert.Contains("evidence?.CommandPrepared(targetX, targetY, movingAxes);", helper, StringComparison.Ordinal);
        Assert.Contains("await execution.MoveAbsoluteAsync(targetX, targetY, -1", helper, StringComparison.Ordinal);
        Assert.Contains("evidence?.CommandAcknowledged(movingAxes);", helper, StringComparison.Ordinal);
        Assert.Contains("_crane.ReadStatusAsync(ct)", helper, StringComparison.Ordinal);
        Assert.Contains("_crane.MoveAbsoluteAsync(x, y, z, tolerance: tolerance, timeoutMs: timeoutMs, ct: ct)", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_accumulates_all_required_position_evidence_without_device_reads_for_reporting()
    {
        string helper = ReadFlowSource("XAbsFineTuneHelper.cs");

        string[] requiredEvidence =
        {
            "DisplayX", "DisplayY", "DisplayZ", "AbsX", "AbsY",
            "TargetX", "TargetY", "TargetZ", "TargetAbsX", "TargetAbsY",
            "LastSentDisplayTargetX", "LastSentDisplayTargetY",
            "XFineTuneCommand", "YFineTuneCommand", "FailureStage",
            "DeltaX", "DeltaY", "ToleranceX", "ToleranceY",
            "MaximumCorrectionX", "MaximumCorrectionY", "StableSampleCount",
            "StageReadCount", "TotalReadCount", "FineTuneAttemptCount",
            "FeedbackRereadCount", "CapturedAtUtc"
        };

        Assert.All(requiredEvidence, name => Assert.Contains(name, helper, StringComparison.Ordinal));
        Assert.Contains("LastSuccessfulCheckpoint", helper, StringComparison.Ordinal);
        Assert.Contains("ZMayStillBeLow", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_factory_is_a_pure_copy_boundary()
    {
        string factory = ReadFlowSource("OperationalEventContextFactory.cs");

        Assert.DoesNotContain("ReadStatusAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadXBitAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveAbsoluteAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOnAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOffAsync", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrCreate", factory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_reporter_does_not_replace_original_argument_exception()
    {
        var reporter = new CapturingReporter(throwOnReport: true);
        OperationalEventContext context = CreateFailureContext();
        var execution = new FakeFineTuneExecution();

        ArgumentOutOfRangeException original = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                execution, new MotionConfig(), 99, "TEST", "测试负Y偏移",
                reporter, context, "ACT-NEGATIVE", null, CancellationToken.None,
                yCenterToPickOffsetMm: -1));

        Assert.Single(reporter.Contexts);
        Assert.Same(original, reporter.Contexts[0].CapturedException);
        Assert.Equal("ACT-NEGATIVE", reporter.Contexts[0].ActionId);
        Assert.Empty(execution.Calls);
    }

    [Fact]
    public async Task Pre_cancelled_active_axis_propagates_cancellation_without_reporting()
    {
        var reporter = new CapturingReporter();
        MotionConfig config = ActiveConfig();
        var execution = new FakeFineTuneExecution();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                execution,
                config, 99, "TEST", "测试预取消",
                reporter, CreateFailureContext(), "ACT-CANCEL", null, cts.Token));

        Assert.Empty(reporter.Contexts);
        Assert.Equal(new[] { "delay:Phase:500" }, execution.Calls);
    }

    [Fact]
    public async Task First_status_read_failure_preserves_configured_targets_and_attempt_counts()
    {
        var reporter = new CapturingReporter();
        MotionConfig config = ActiveConfig();
        var execution = new FakeFineTuneExecution((CraneStatus?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                execution,
                config, 99, "TEST", "测试首次读取失败",
                reporter, CreateFailureContext(), "ACT-READ", null, CancellationToken.None));

        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        PositionEvidence position = reported.Evidence.Position;
        Assert.Equal(EvidenceAvailability.Unavailable, position.DisplayX.Availability);
        Assert.Equal(1000, position.TargetAbsX.Value);
        Assert.Equal(5, position.ToleranceX.Value);
        Assert.Equal(50, position.MaximumCorrectionX.Value);
        Assert.Equal(1, position.StageReadCount.Value);
        Assert.Equal(1, position.TotalReadCount.Value);
        Assert.Equal("微调前", position.FailureStage.Value);
        Assert.Equal(new[] { "delay:Phase:500", "read" }, execution.Calls);
    }

    [Fact]
    public void Fine_tune_actions_with_different_action_ids_do_not_aggregate_but_same_action_does()
    {
        OperationalEventMonitorOptions options = OperationalEventMonitorOptions.Create(
            capacity: 5000,
            aggregationWindow: TimeSpan.FromMinutes(10),
            transientFailureCountThreshold: 3,
            transientFailureDuration: TimeSpan.FromSeconds(30),
            stageThresholds: null);
        var clock = new MutableClock();
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, clock);
        OperationalEventContext context = CreateFactoryFailureContext();

        Assert.True(context.IndependentAction);
        reporter.Report(context with { ActionId = "ACT-A", CorrelationKey = "stable-fine-tune-key" });
        clock.Advance(TimeSpan.FromMinutes(1));
        reporter.Report(context with { ActionId = "ACT-B", CorrelationKey = "stable-fine-tune-key" });
        clock.Advance(TimeSpan.FromMinutes(1));
        reporter.Report(context with { ActionId = "ACT-A", CorrelationKey = "stable-fine-tune-key" });

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Contains(snapshot.Events, item => item.LatestActionId == "ACT-A" && item.OccurrenceCount == 2);
        Assert.Contains(snapshot.Events, item => item.LatestActionId == "ACT-B" && item.OccurrenceCount == 1);
    }

    [Fact]
    public async Task Twenty_unstable_reads_report_exact_counts_without_move()
    {
        CraneStatus?[] statuses = Enumerable.Range(0, 20)
            .Select(index => (CraneStatus?)Status(index % 2 == 0 ? 100 : 110, index % 2 == 0 ? 1000 : 1010, z: 17))
            .ToArray();
        var execution = new FakeFineTuneExecution(statuses);
        var reporter = new CapturingReporter();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            execution, ActiveConfig(), reporter, "ACT-UNSTABLE"));

        Assert.Contains("连续读取不稳定", error.Message, StringComparison.Ordinal);
        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        Assert.Equal(20, reported.Evidence.Position.StageReadCount.Value);
        Assert.Equal(20, reported.Evidence.Position.TotalReadCount.Value);
        Assert.Equal(5, reported.Evidence.Position.StableSampleCount.Value);
        Assert.Equal("微调前", reported.Evidence.Position.FailureStage.Value);
        Assert.Empty(execution.Moves);
        Assert.Equal(20, execution.Calls.Count(call => call == "read"));
        Assert.Equal(21, execution.Calls.Count(call => call.StartsWith("delay:", StringComparison.Ordinal)));
        var expected = new List<string> { "delay:Phase:500" };
        for (int index = 0; index < 20; index++)
        {
            expected.Add("read");
            expected.Add("delay:StableRead:200");
        }
        Assert.Equal(expected, execution.Calls);
    }

    [Fact]
    public async Task Excessive_correction_reports_without_sending_move()
    {
        var execution = new FakeFineTuneExecution(Repeat(Status(100, 900, z: 23), 5));
        var reporter = new CapturingReporter();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            execution, ActiveConfig(), reporter, "ACT-MAX"));

        Assert.Contains("绝对编码器偏差过大", error.Message, StringComparison.Ordinal);
        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        Assert.Equal("第1次X偏差超过最大允许修正", reported.Evidence.Position.FailureStage.Value);
        Assert.Equal(DeviceCommandState.NotSent, reported.Evidence.Position.XFineTuneCommand.State);
        Assert.Equal(PhysicalConclusionCode.CommandNotSent, reported.PhysicalConclusion.Code);
        Assert.Empty(execution.Moves);
        Assert.Equal(5, execution.Calls.Count(call => call == "read"));
        var expected = new List<string> { "delay:Phase:500" };
        AddStableReadCalls(expected);
        Assert.Equal(expected, execution.Calls);
    }

    [Fact]
    public async Task Move_exception_keeps_original_error_marks_result_unknown_and_invalidates_only_xy()
    {
        var execution = new FakeFineTuneExecution(Repeat(Status(100, 990, z: 321), 5));
        var reporter = new CapturingReporter();
        var original = new InvalidOperationException("fake move failed");
        execution.MoveBehavior = _ => Task.FromException(original);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            execution, ActiveConfig(), reporter, "ACT-MOVE"));

        Assert.Same(original, thrown);
        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        PositionEvidence position = reported.Evidence.Position;
        Assert.Equal(PhysicalConclusionCode.CommandResultUnknown, reported.PhysicalConclusion.Code);
        Assert.Equal(DeviceCommandState.SentUnconfirmed, position.XFineTuneCommand.State);
        Assert.Equal(EvidenceAvailability.Unavailable, position.DisplayX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, position.AbsX.Availability);
        Assert.Equal(EvidenceAvailability.Confirmed, position.DisplayZ.Availability);
        Assert.Equal(321, position.DisplayZ.Value);
        Assert.Equal(110, position.LastSentDisplayTargetX.Value);
        Assert.Single(execution.Moves);
        Assert.Equal(new MoveInvocation(1, 110, -1, -1, 5, 30_000), execution.Moves[0]);
        var expected = new List<string> { "delay:Phase:500" };
        AddStableReadCalls(expected);
        expected.Add("move:1:110:-1:-1:5:30000");
        Assert.Equal(expected, execution.Calls);
    }

    [Fact]
    public async Task Second_y_only_move_failure_preserves_first_x_acknowledgement_and_target()
    {
        CraneStatus initial = Status(100, 990, 200, 2000, 40);
        CraneStatus afterX = Status(110, 1000, 200, 1990, 40);
        var execution = new FakeFineTuneExecution(Repeat(initial, 5).Concat(Repeat(afterX, 5)).ToArray());
        var reporter = new CapturingReporter();
        var original = new InvalidOperationException("second Y move failed");
        execution.MoveBehavior = move => move.Index == 2 ? Task.FromException(original) : Task.CompletedTask;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            execution, ActiveConfig(includeY: true), reporter, "ACT-Y2"));

        Assert.Same(original, thrown);
        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        PositionEvidence position = reported.Evidence.Position;
        Assert.Equal(DeviceCommandState.Acknowledged, position.XFineTuneCommand.State);
        Assert.Equal(DeviceCommandState.SentUnconfirmed, position.YFineTuneCommand.State);
        Assert.Equal(110, position.LastSentDisplayTargetX.Value);
        Assert.Equal(210, position.LastSentDisplayTargetY.Value);
        Assert.Equal(PhysicalConclusionCode.CommandResultUnknown, reported.PhysicalConclusion.Code);
        Assert.Equal(new MoveInvocation(1, 110, -1, -1, 5, 30_000), execution.Moves[0]);
        Assert.Equal(new MoveInvocation(2, -1, 210, -1, 5, 30_000), execution.Moves[1]);
        var expected = new List<string> { "delay:Phase:500" };
        AddStableReadCalls(expected);
        expected.Add("move:1:110:-1:-1:5:30000");
        expected.Add("delay:Phase:600");
        AddStableReadCalls(expected);
        expected.Add("move:2:-1:210:-1:5:30000");
        Assert.Equal(expected, execution.Calls);
    }

    [Fact]
    public async Task Three_feedback_rereads_then_failure_report_exact_stage_and_call_sequence()
    {
        CraneStatus before = Status(100, 990, z: 50);
        CraneStatus staleFeedback = Status(110, 990, z: 50);
        CraneStatus?[] statuses = Repeat(before, 5)
            .Concat(Repeat(staleFeedback, 20))
            .Cast<CraneStatus?>()
            .ToArray();
        var execution = new FakeFineTuneExecution(statuses);
        var reporter = new CapturingReporter();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            execution, ActiveConfig(), reporter, "ACT-FEEDBACK"));

        Assert.Contains("微调后反馈未跟随显示坐标", error.Message, StringComparison.Ordinal);
        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        Assert.Equal("第1次X绝对编码器反馈最终未跟随", reported.Evidence.Position.FailureStage.Value);
        Assert.Equal(3, reported.Evidence.Position.FeedbackRereadCount.Value);
        Assert.Equal(25, reported.Evidence.Position.TotalReadCount.Value);
        Assert.Equal(25, execution.Calls.Count(call => call == "read"));
        Assert.Single(execution.Moves);
        Assert.Equal(25, execution.Calls.Count(call => call.StartsWith("delay:", StringComparison.Ordinal)));

        var expected = new List<string> { "delay:Phase:500" };
        AddStableReadCalls(expected);
        expected.Add("move:1:110:-1:-1:5:30000");
        expected.Add("delay:Phase:600");
        AddStableReadCalls(expected);
        for (int retry = 0; retry < 3; retry++)
        {
            expected.Add("delay:FeedbackRefresh:1000");
            AddStableReadCalls(expected);
        }
        Assert.Equal(expected, execution.Calls);
    }

    [Fact]
    public async Task Dual_axis_drift_reaches_final_two_attempt_failure_without_algorithm_changes()
    {
        CraneStatus initial = Status(100, 990, 200, 2000, 60);
        CraneStatus afterXWithYDrift = Status(110, 1000, 200, 1990, 60);
        CraneStatus afterYWithXDrift = Status(110, 990, 210, 2000, 60);
        CraneStatus?[] statuses = Repeat(initial, 5)
            .Concat(Repeat(afterXWithYDrift, 5))
            .Concat(Repeat(afterYWithXDrift, 5))
            .Cast<CraneStatus?>()
            .ToArray();
        var execution = new FakeFineTuneExecution(statuses);
        var reporter = new CapturingReporter();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            execution, ActiveConfig(includeY: true), reporter, "ACT-FINAL"));

        Assert.Contains("两次微调后仍超差", error.Message, StringComparison.Ordinal);
        OperationalEventContext reported = Assert.Single(reporter.Contexts);
        PositionEvidence position = reported.Evidence.Position;
        Assert.Equal("第二次微调后最终复核仍超差", position.FailureStage.Value);
        Assert.Equal(2, position.FineTuneAttemptCount.Value);
        Assert.Equal(15, position.TotalReadCount.Value);
        Assert.Equal(DeviceCommandState.Acknowledged, position.XFineTuneCommand.State);
        Assert.Equal(DeviceCommandState.Acknowledged, position.YFineTuneCommand.State);
        Assert.Equal(PhysicalConclusionCode.ManualConfirmationRequired, reported.PhysicalConclusion.Code);
        Assert.Equal(2, execution.Moves.Count);

        var expected = new List<string> { "delay:Phase:500" };
        AddStableReadCalls(expected);
        expected.Add("move:1:110:-1:-1:5:30000");
        expected.Add("delay:Phase:600");
        AddStableReadCalls(expected);
        expected.Add("move:2:-1:210:-1:5:30000");
        expected.Add("delay:Phase:600");
        AddStableReadCalls(expected);
        Assert.Equal(expected, execution.Calls);
    }

    [Fact]
    public void Monitoring_does_not_add_or_advance_business_z_formula_evaluation()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> expected =
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
            {
                ["Line1FrontFlowEngine.cs"] = FormulaCounts(7, 0, 0, 0),
                ["Line2FrontFlowEngine.cs"] = FormulaCounts(7, 0, 0, 0),
                ["Line1RearFlowEngine.cs"] = FormulaCounts(0, 4, 0, 0),
                ["Line2RearFlowEngine.cs"] = FormulaCounts(0, 4, 0, 0),
                ["GrindingFlowEngine.cs"] = FormulaCounts(2, 0, 7, 2)
            };

        foreach ((string fileName, IReadOnlyDictionary<string, int> counts) in expected)
        {
            string source = ReadFlowSource(fileName);
            foreach ((string marker, int count) in counts)
                Assert.Equal(count, Regex.Matches(source, $@"\b{marker}\(").Count);
        }
    }

    [Fact]
    public void Front_placement_contexts_reuse_confirmed_x11_instead_of_false_placeholder()
    {
        string fronts = ReadFlowSource("Line1FrontFlowEngine.cs") + ReadFlowSource("Line2FrontFlowEngine.cs");

        Assert.DoesNotContain("PlacementBeforeZDown(true, false", fronts, StringComparison.Ordinal);
        Assert.Contains("TryObserveX11", fronts, StringComparison.Ordinal);
    }

    [Fact]
    public void Physical_tracker_preserves_last_confirmed_x11_value_attempt_count_and_read_time()
    {
        DateTime before = DateTime.UtcNow;
        OperationalEventContextFactory.FineTunePhysicalTracker tracker =
            OperationalEventContextFactory.TryCreatePhysicalTracker()!;

        tracker.TryObserveX11(false);
        tracker.TryObserveX11(true);
        OperationalEventContextFactory.FineTunePhysicalSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(2, snapshot.X11Attempts.Value);
        Assert.True(snapshot.X11ReadValid.Value);
        Assert.Equal(1, snapshot.X11LastValue.Value);
        Assert.InRange(snapshot.X11ReadAtUtc.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void Placement_phase_reuses_tracker_x11_and_safe_z_checkpoint()
    {
        OperationalEventContextFactory.FineTunePhysicalTracker tracker =
            OperationalEventContextFactory.TryCreatePhysicalTracker()!;
        tracker.TryObserveX11(true);
        tracker.TryConfirmSafeZ(0, 5, "测试Z安全位");

        OperationalEventContextFactory.FineTunePhysicalPhase phase =
            OperationalEventContextFactory.PlacementBeforeZDown(
                magnetOnAcknowledged: true,
                holdingWorkpieceConfirmed: true,
                tracker,
                lastSuccessfulCheckpoint: "测试检查点",
                lastConfirmedLocation: "测试工位");

        Assert.Equal(1, phase.X11LastValue.Value);
        Assert.Equal(1, phase.X11Attempts.Value);
        Assert.True(phase.ZKnownSafe.Value);
        Assert.Equal(0, phase.SafeZTarget.Value);
        Assert.Equal(5, phase.SafeZTolerance.Value);
    }

    [Fact]
    public void Report_builds_one_position_snapshot_and_derives_physical_conclusion_from_command_state()
    {
        string helper = ReadFlowSource("XAbsFineTuneHelper.cs");
        string catchBlock = helper[helper.IndexOf("catch (Exception ex) when", StringComparison.Ordinal)..];

        Assert.Single(Regex.Matches(catchBlock, @"ToPositionEvidence\(").Cast<Match>());
        Assert.Contains("PhysicalConclusion =", catchBlock, StringComparison.Ordinal);
        Assert.Contains("CommandResultUnknown", helper, StringComparison.Ordinal);
        Assert.Contains("ManualConfirmationRequired", helper, StringComparison.Ordinal);
    }

    private static Task ExecuteAsync(
        FakeFineTuneExecution execution,
        MotionConfig config,
        IOperationalEventReporter reporter,
        string actionId) => XAbsFineTuneHelper.VerifyAndFineTuneAsync(
            execution,
            config,
            craneNo: 99,
            stationCode: "TEST",
            context: "确定性微调测试",
            reporter,
            CreateFailureContext(),
            actionId,
            physicalTracker: null,
            CancellationToken.None);

    private static CraneStatus Status(int x, int absX, int y = 0, int absY = 0, int z = 0) => new()
    {
        XPos = x,
        XEncoderAbs = absX,
        YPos = y,
        YEncoderAbs = absY,
        ZPos = z
    };

    private static IEnumerable<CraneStatus> Repeat(CraneStatus status, int count) =>
        Enumerable.Repeat(status, count);

    private static void AddStableReadCalls(ICollection<string> calls)
    {
        for (int index = 0; index < 5; index++)
        {
            calls.Add("read");
            if (index < 4)
                calls.Add("delay:StableRead:200");
        }
    }

    private static MotionConfig ActiveConfig(bool includeY = false)
    {
        var config = new MotionConfig();
        config.AbsMove.TimeoutMs = 30_000;
        config.XAbsFineTune.Enabled = true;
        config.XAbsFineTune.ToleranceMm = 5;
        config.XAbsFineTune.MaxAdjustMm = 50;
        config.XAbsFineTune.StationTargets = new Dictionary<int, Dictionary<string, int>>
        {
            [99] = new(StringComparer.OrdinalIgnoreCase) { ["TEST"] = 1000 }
        };
        config.YAbsFineTune.Enabled = includeY;
        config.YAbsFineTune.ToleranceMm = 5;
        config.YAbsFineTune.MaxAdjustMm = 50;
        config.YAbsFineTune.StationTargets = new Dictionary<int, Dictionary<string, int>>
        {
            [99] = new(StringComparer.OrdinalIgnoreCase) { ["TEST"] = 2000 }
        };
        config.YAbsFineTune.AbsolutePerDisplayDirections = new Dictionary<int, int> { [99] = +1 };
        return config;
    }

    private static OperationalEventContext CreateFactoryFailureContext()
    {
        var workpiece = new WorkpieceCache
        {
            PlateNo = "PLATE-001",
            Sequence = "01",
            Diameter = 100,
            Length = 1000
        };
        return OperationalEventContextFactory.FineTuneFailure(
            scope: "测试线路",
            engine: "测试引擎",
            deviceNo: "99",
            station: "TEST",
            actionStage: "测试微调",
            workpiece,
            source: OperationalEventContextFactory.ConfirmedLocation("SOURCE", "测试来源"),
            target: OperationalEventContextFactory.ConfirmedLocation("TEST", "测试目标"),
            owner: "测试天车",
            targetZ: EvidenceValue<int>.Confirmed(123, "测试Z目标"),
            physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(
                tracker: null,
                useTrackedX11: false,
                lastSuccessfulCheckpoint: "测试检查点",
                lastConfirmedLocation: "SOURCE"));
    }

    private static OperationalEventContext CreateFailureContext() => new()
    {
        EventCode = "CRANE_XY_FINE_TUNE_FAILED",
        Severity = OperationalEventSeverity.Error,
        Category = OperationalEventCategory.FineTune,
        Scope = "测试",
        Engine = "测试引擎",
        DeviceType = "天车",
        DeviceNo = "99",
        Station = "TEST",
        ActionStage = "测试微调",
        Title = "测试微调失败",
        Evidence = OperationalEvidence.Unavailable("测试没有业务证据") with
        {
            Position = PositionEvidence.Unavailable("读取尚未开始") with
            {
                TargetZ = EvidenceValue<int>.Confirmed(123, "测试已有Z目标")
            }
        }
    };

    private static IReadOnlyDictionary<string, int> FormulaCounts(
        int computePickupZ,
        int pz,
        int applyOffsetZ,
        int computeUnloadZ) => new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ComputePickupZ"] = computePickupZ,
        ["Pz"] = pz,
        ["ApplyOffsetZ"] = applyOffsetZ,
        ["ComputeUnloadZ"] = computeUnloadZ
    };

    private sealed class CapturingReporter : IOperationalEventReporter
    {
        private readonly bool _throwOnReport;

        public CapturingReporter(bool throwOnReport = false) => _throwOnReport = throwOnReport;

        public List<OperationalEventContext> Contexts { get; } = new();

        public void Report(OperationalEventContext context)
        {
            Contexts.Add(context);
            if (_throwOnReport)
                throw new InvalidOperationException("测试Reporter失败");
        }

        public void ObserveTransientFailure(OperationalFailureObservation observation) { }

        public void ObserveRecovery(OperationalRecoveryObservation observation) { }

        public void ObserveStage(OperationalStageObservation observation) { }
    }

    private sealed class MutableClock : IOperationalEventClock
    {
        public DateTime UtcNow { get; private set; } = DateTime.UnixEpoch;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed record MoveInvocation(
        int Index,
        int X,
        int Y,
        int Z,
        int Tolerance,
        int TimeoutMs);

    private sealed class FakeFineTuneExecution : XAbsFineTuneHelper.IFineTuneExecution
    {
        private readonly Queue<CraneStatus?> _statuses;

        public FakeFineTuneExecution(params CraneStatus?[] statuses) =>
            _statuses = new Queue<CraneStatus?>(statuses);

        public FakeFineTuneExecution(IEnumerable<CraneStatus> statuses) =>
            _statuses = new Queue<CraneStatus?>(statuses.Cast<CraneStatus?>());

        public List<string> Calls { get; } = new();

        public List<MoveInvocation> Moves { get; } = new();

        public Func<MoveInvocation, Task>? MoveBehavior { get; set; }

        public Task<CraneStatus?> ReadStatusAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("read");
            if (_statuses.Count == 0)
                throw new InvalidOperationException("Fake没有剩余CraneStatus");
            return Task.FromResult(_statuses.Dequeue());
        }

        public Task MoveAbsoluteAsync(
            int x,
            int y,
            int z,
            int tolerance,
            int timeoutMs,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var invocation = new MoveInvocation(Moves.Count + 1, x, y, z, tolerance, timeoutMs);
            Moves.Add(invocation);
            Calls.Add($"move:{invocation.Index}:{x}:{y}:{z}:{tolerance}:{timeoutMs}");
            return MoveBehavior?.Invoke(invocation) ?? Task.CompletedTask;
        }

        public Task DelayAsync(
            XAbsFineTuneHelper.FineTuneDelayKind kind,
            int delayMs,
            CancellationToken ct)
        {
            Calls.Add($"delay:{kind}:{delayMs}");
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static string ReadFlowSource(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));

    private static IReadOnlyList<string> ExtractInvocations(string source, string marker)
    {
        var results = new List<string>();
        int searchFrom = 0;
        while (true)
        {
            int markerIndex = source.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (markerIndex < 0)
                return results;

            int openParenthesis = markerIndex + marker.Length - 1;
            int closeParenthesis = FindMatchingParenthesis(source, openParenthesis);
            results.Add(source[(openParenthesis + 1)..closeParenthesis]);
            searchFrom = closeParenthesis + 1;
        }
    }

    private static int FindMatchingParenthesis(string source, int openParenthesis)
    {
        int depth = 0;
        bool inString = false;
        bool inCharacter = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int index = openParenthesis; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (!inString && !inCharacter && current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }

            if (!inString && !inCharacter && current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (!inCharacter && current == '"' && !IsEscaped(source, index))
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == '\'' && !IsEscaped(source, index))
            {
                inCharacter = !inCharacter;
                continue;
            }

            if (inString || inCharacter)
                continue;

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        throw new InvalidDataException($"未找到位置 {openParenthesis} 对应的右括号。");
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int current = index - 1; current >= 0 && source[current] == '\\'; current--)
            slashCount++;
        return slashCount % 2 != 0;
    }
}
