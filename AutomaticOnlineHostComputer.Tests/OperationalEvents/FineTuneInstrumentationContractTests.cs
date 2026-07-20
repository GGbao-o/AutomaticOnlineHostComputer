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

        Assert.Single(Regex.Matches(helper, @"\bReadStatusAsync\(").Cast<Match>());
        Assert.Single(Regex.Matches(helper, @"\bMoveAbsoluteAsync\(").Cast<Match>());
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
        Assert.Contains("await crane.MoveAbsoluteAsync(targetX, targetY, -1", helper, StringComparison.Ordinal);
        Assert.Contains("evidence?.CommandAcknowledged(movingAxes);", helper, StringComparison.Ordinal);
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

        ArgumentOutOfRangeException original = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                null!, new MotionConfig(), 99, "TEST", "测试负Y偏移",
                reporter, context, "ACT-NEGATIVE", null, CancellationToken.None,
                yCenterToPickOffsetMm: -1));

        Assert.Single(reporter.Contexts);
        Assert.Same(original, reporter.Contexts[0].CapturedException);
        Assert.Equal("ACT-NEGATIVE", reporter.Contexts[0].ActionId);
    }

    [Fact]
    public async Task Pre_cancelled_active_axis_propagates_cancellation_without_reporting()
    {
        var reporter = new CapturingReporter();
        MotionConfig config = ActiveConfig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                new CraneService("fine-tune-cancel-test", "127.0.0.1", 1),
                config, 99, "TEST", "测试预取消",
                reporter, CreateFailureContext(), "ACT-CANCEL", null, cts.Token));

        Assert.Empty(reporter.Contexts);
    }

    [Fact]
    public async Task First_status_read_failure_preserves_configured_targets_and_attempt_counts()
    {
        var reporter = new CapturingReporter();
        MotionConfig config = ActiveConfig();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                new CraneService("fine-tune-read-test", "127.0.0.1", 1),
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

    private static MotionConfig ActiveConfig()
    {
        var config = new MotionConfig();
        config.XAbsFineTune.Enabled = true;
        config.XAbsFineTune.StationTargets = new Dictionary<int, Dictionary<string, int>>
        {
            [99] = new(StringComparer.OrdinalIgnoreCase) { ["TEST"] = 1000 }
        };
        config.YAbsFineTune.Enabled = false;
        return config;
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
