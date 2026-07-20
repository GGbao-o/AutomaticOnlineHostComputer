using System.Text.RegularExpressions;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class LegacyAlarmOrderingContractTests
{
    [Fact]
    public void Safety_alarm_handlers_record_every_alarm_but_dedupe_only_the_popup()
    {
        string source = ReadSource("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        string lineHandler = ExtractMethod(source, "private void HandleLineSafetyAlarm");
        string standaloneHandler = ExtractMethod(source, "private void HandleStandaloneSafetyAlarm");

        AssertPopupDedupeOrdering(
            lineHandler,
            expectedPauseCalls: 4,
            expectedPauseForCraneZeroCalls: 0,
            "_line1SafetyPopupShown",
            "_line2SafetyPopupShown");
        AssertPopupDedupeOrdering(
            standaloneHandler,
            expectedPauseCalls: 1,
            expectedPauseForCraneZeroCalls: 1,
            "_balancingSafetyPopupShown",
            "_grindingSafetyPopupShown");

        Assert.Equal(1, Count(source, "_line1SafetyPopupShown = true;"));
        Assert.Equal(1, Count(source, "_line2SafetyPopupShown = true;"));
        Assert.Equal(1, Count(source, "_balancingSafetyPopupShown = true;"));
        Assert.Equal(1, Count(source, "_grindingSafetyPopupShown = true;"));
        Assert.Equal(1, Count(source, "_line1SafetyPopupShown = false;"));
        Assert.Equal(1, Count(source, "_line2SafetyPopupShown = false;"));
        Assert.Equal(1, Count(source, "_balancingSafetyPopupShown = false;"));
        Assert.Equal(1, Count(source, "_grindingSafetyPopupShown = false;"));
    }

    [Theory]
    [InlineData("private void HandleCraneZeroPositionAlarm")]
    [InlineData("private void HandleSharedManipulatorSafetyAlarm")]
    [InlineData("private void HandleRearCraneWarning")]
    public void Unrelated_alarm_handlers_keep_their_single_record_and_popup(string signature)
    {
        string source = ReadSource("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        string method = ExtractMethod(source, signature);

        Assert.Equal(1, Count(method, "_attentionEvents.Record("));
        Assert.Equal(1, Count(method, "MessageBox.Show("));
        Assert.Equal(1, Count(method, ".BeginInvoke("));
    }

    [Fact]
    public void Crane_move_reports_each_of_its_six_final_false_exits_without_changing_control_flow()
    {
        string source = ReadSource("Service", "FlowEngine", "ProductionFlowEngine.cs");
        string method = ExtractMethod(source, "public async Task<bool> MoveCraneToStationAsync");

        Assert.Equal(6, Count(method, "return false;"));
        Assert.Equal(6, Count(method, "TryReportMoveFinalFailure("));
        Assert.Equal(6, Regex.Matches(method, @"TryReportMoveFinalFailure\([\s\S]*?\);\s*return false;").Count);
        Assert.Equal(2, Regex.Matches(method, @"\bcatch\s*\(").Count);
        Assert.Single(Regex.Matches(method, @"\bfinally\b").Cast<Match>());
        Assert.Equal(1, Count(method, "ReleaseStation(stationCode, craneName);"));
        Assert.Equal(6, Regex.Matches(method, @"\bawait\b").Count);
        Assert.Equal(1, Count(method, "_craneCache.GetCraneInfo("));
        Assert.Equal(1, Count(method, "_stationCoords.TryGetValue("));
        Assert.Equal(2, Count(method, "ReserveStation("));
        Assert.Equal(1, Count(method, "WaitForStationAsync("));
        Assert.Equal(1, Count(method, "_craneCache.GetOrCreateService("));
        Assert.Equal(1, Count(method, ".CheckSafetyAsync("));
        Assert.Equal(1, Count(method, ".SetAbsSpeedAsync("));
        Assert.Equal(2, Count(method, ".ReadStatusAsync("));
        Assert.Equal(1, Count(method, ".MoveAbsoluteAsync("));
        AssertFailureStages(method, absoluteMoveFailureCount: 2);
        AssertNoBusinessSideEffects(method);
    }

    [Fact]
    public void Manipulator_move_reports_each_of_its_five_final_false_exits_without_adding_a_catch()
    {
        string source = ReadSource("Service", "FlowEngine", "ProductionFlowEngine.cs");
        string method = ExtractMethod(source, "public async Task<bool> MoveManipulatorToStationAsync");

        Assert.Equal(5, Count(method, "return false;"));
        Assert.Equal(5, Count(method, "TryReportMoveFinalFailure("));
        Assert.Equal(5, Regex.Matches(method, @"TryReportMoveFinalFailure\([\s\S]*?\);\s*return false;").Count);
        Assert.Single(Regex.Matches(method, @"\bcatch\s*\(").Cast<Match>());
        Assert.Contains("catch (TimeoutException ex)", method, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(method, @"\bfinally\b").Cast<Match>());
        Assert.Equal(1, Count(method, "ReleaseStation(stationCode, info.Name);"));
        Assert.Equal(4, Regex.Matches(method, @"\bawait\b").Count);
        Assert.Equal(1, Count(method, "_manipulatorCache.GetManipulatorInfo("));
        Assert.Equal(1, Count(method, "_stationCoords.TryGetValue("));
        Assert.Equal(2, Count(method, "ReserveStation("));
        Assert.Equal(1, Count(method, "WaitForStationAsync("));
        Assert.Equal(1, Count(method, "_manipulatorCache.GetOrCreateService("));
        Assert.Equal(1, Count(method, ".CheckSafetyAsync("));
        Assert.Equal(1, Count(method, ".SetAbsSpeedAsync("));
        Assert.Equal(1, Count(method, ".MoveAbsoluteAsync("));
        AssertFailureStages(method, absoluteMoveFailureCount: 1);
        AssertNoBusinessSideEffects(method);
    }

    [Fact]
    public void Engine_loop_reports_cancellation_and_failure_before_the_original_idle_reset()
    {
        string source = ReadSource("Service", "FlowEngine", "ProductionFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task EngineLoopAsync");

        Assert.Equal(2, Regex.Matches(method, @"\bcatch\s*\(").Count);
        Assert.Equal(2, Count(method, "FlowStage failedStage = ctx.CurrentStage;"));
        Assert.Equal(2, Count(method, "TryReportEngineFinalFailure("));
        Assert.Equal(2, Regex.Matches(
            method,
            @"FlowStage failedStage = ctx\.CurrentStage;\s*TryReportEngineFinalFailure\([\s\S]*?\);\s*ctx\.CurrentStage = FlowStage\.Idle;").Count);
        Assert.Equal(2, Count(method, "ctx.CurrentStage = FlowStage.Idle;"));
        Assert.Equal(2, Regex.Matches(method, @"\bawait\b").Count);
        Assert.Equal(1, Count(method, "ProcessWorkpieceAsync(ctx, ct)"));
        Assert.Equal(1, Count(method, "Task.Delay(1000, ct)"));
        Assert.Contains("OperationalEventSeverity.Information", method, StringComparison.Ordinal);
        Assert.Contains("OperationalEventSeverity.Error", method, StringComparison.Ordinal);
        Assert.DoesNotContain("return;", method, StringComparison.Ordinal);
        AssertNoBusinessSideEffects(method);
    }

    [Fact]
    public void Reporting_helpers_are_void_isolated_and_do_not_read_devices_or_change_business_state()
    {
        string source = ReadSource("Service", "FlowEngine", "ProductionFlowEngine.cs");
        string moveHelper = ExtractMethod(source, "private void TryReportMoveFinalFailure");
        string engineHelper = ExtractMethod(source, "private void TryReportEngineFinalFailure");

        AssertIsolatedReporterHelper(moveHelper);
        AssertIsolatedReporterHelper(engineHelper);
        Assert.Contains("公共移动入口未接收工件上下文", moveHelper, StringComparison.Ordinal);
        Assert.Contains("原finally将尝试释放；结果尚未确认", moveHelper, StringComparison.Ordinal);
        Assert.Contains("CapturedException = exception", moveHelper, StringComparison.Ordinal);
        Assert.Contains("CapturedException = exception", engineHelper, StringComparison.Ordinal);
    }

    private static void AssertPopupDedupeOrdering(
        string method,
        int expectedPauseCalls,
        int expectedPauseForCraneZeroCalls,
        params string[] popupFlags)
    {
        Assert.Equal(1, Count(method, "_attentionEvents.Record("));
        Assert.Equal(1, Count(method, "MessageBox.Show("));
        Assert.Equal(1, Count(method, ".BeginInvoke("));
        Assert.Equal(expectedPauseCalls, Regex.Matches(method, @"\.Pause\(\);").Count);
        Assert.Equal(expectedPauseForCraneZeroCalls, Count(method, ".PauseForCraneZeroPosition();"));
        Assert.Equal(1, Count(method, "bool popupAlreadyShown;"));
        Assert.Equal(1, Count(method, "if (popupAlreadyShown) return;"));

        int lockStart = method.IndexOf("lock (_lineSafetyPopupLock)", StringComparison.Ordinal);
        Assert.True(lockStart >= 0);
        int lockBodyStart = method.IndexOf('{', lockStart);
        int lockBodyEnd = FindMatchingBrace(method, lockBodyStart);
        string lockBody = method[lockBodyStart..(lockBodyEnd + 1)];

        Assert.DoesNotContain("_attentionEvents.Record(", lockBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_exceptionReporter", lockBody, StringComparison.Ordinal);
        Assert.DoesNotContain(".Pause(", lockBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PauseForCraneZeroPosition", lockBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher", lockBody, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", lockBody, StringComparison.Ordinal);
        Assert.DoesNotContain("return;", lockBody, StringComparison.Ordinal);

        int record = method.IndexOf("_attentionEvents.Record(", StringComparison.Ordinal);
        int dedupeReturn = method.IndexOf("if (popupAlreadyShown) return;", StringComparison.Ordinal);
        int messageBox = method.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        Assert.True(lockBodyEnd < record);
        Assert.True(record < dedupeReturn);
        Assert.True(dedupeReturn < messageBox);

        foreach (Match pause in Regex.Matches(method, @"\.(?:Pause|PauseForCraneZeroPosition)\(\);"))
            Assert.True(pause.Index < lockStart);
        foreach (string flag in popupFlags)
        {
            Assert.Equal(1, Count(lockBody, $"popupAlreadyShown = {flag};"));
            Assert.Equal(1, Count(lockBody, $"if (!popupAlreadyShown) {flag} = true;"));
        }
    }

    private static void AssertFailureStages(string method, int absoluteMoveFailureCount)
    {
        Assert.Equal(1, Count(method, "\"坐标解析\","));
        Assert.Equal(1, Count(method, "\"等待工位预约\","));
        Assert.Equal(1, Count(method, "\"重新预约工位\","));
        Assert.Equal(1, Count(method, "\"移动前安全检查\","));
        Assert.Equal(absoluteMoveFailureCount, Count(method, "\"绝对移动执行\","));
    }

    private static void AssertIsolatedReporterHelper(string method)
    {
        Assert.Equal(1, Count(method, "_exceptionReporter.Report("));
        Assert.Single(Regex.Matches(method, @"\btry\b").Cast<Match>());
        Assert.Single(Regex.Matches(method, @"\bcatch\s*\{").Cast<Match>());
        Assert.DoesNotContain("await", method, StringComparison.Ordinal);
        Assert.DoesNotContain("return", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Read", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrCreate", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReserveStation", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseStation", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentStage =", method, StringComparison.Ordinal);
        AssertNoBusinessSideEffects(method);
    }

    private static void AssertNoBusinessSideEffects(string method)
    {
        Assert.DoesNotContain(".Pause(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher", method, StringComparison.Ordinal);
    }

    private static int Count(string source, string value) =>
        Regex.Matches(source, Regex.Escape(value)).Count;

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(segments).ToArray()));

    private static string ExtractMethod(string source, string signature)
    {
        int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureStart >= 0, $"未找到方法：{signature}");
        int bodyStart = source.IndexOf('{', signatureStart);
        Assert.True(bodyStart >= 0, $"方法缺少左大括号：{signature}");
        int bodyEnd = FindMatchingBrace(source, bodyStart);
        return source[signatureStart..(bodyEnd + 1)];
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        int depth = 0;
        bool inString = false;
        bool inCharacter = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int index = openBrace; index < source.Length; index++)
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
            if (inString || inCharacter) continue;

            if (current == '{') depth++;
            else if (current == '}' && --depth == 0) return index;
        }

        throw new InvalidDataException($"未找到位置 {openBrace} 对应的右大括号。");
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int current = index - 1; current >= 0 && source[current] == '\\'; current--)
            slashCount++;
        return slashCount % 2 != 0;
    }
}
