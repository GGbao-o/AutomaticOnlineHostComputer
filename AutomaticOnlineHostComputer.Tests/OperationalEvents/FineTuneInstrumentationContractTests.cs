using System.Text.RegularExpressions;
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
        Assert.Equal(8, Regex.Matches(allCalls, @"PickupBeforeZDown\(true,").Count);
        Assert.Equal(2, Regex.Matches(allCalls, @"PickupBeforeZDown\(false,").Count);
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
        Assert.Contains("evidence.CommandPrepared(targetX, targetY, movingAxes);", helper, StringComparison.Ordinal);
        Assert.Contains("await crane.MoveAbsoluteAsync(targetX, targetY, -1", helper, StringComparison.Ordinal);
        Assert.Contains("evidence.CommandAcknowledged(movingAxes);", helper, StringComparison.Ordinal);
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
