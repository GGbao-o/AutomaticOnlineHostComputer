using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

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

    [Fact]
    public void Move_reporting_does_not_claim_to_know_the_callers_global_pause_state()
    {
        var reporter = new CapturingReporter();
        ProductionFlowEngine engine = CreateUninitializedEngine(reporter);

        InvokePrivate(
            engine,
            "TryReportMoveFinalFailure",
            "天车", 1, "1号天车", "ST001", "测试阶段", "测试详情", "测试结果",
            10, 20, 30, false, false, null, OperationalEventSeverity.Error);

        OperationalEventContext context = Assert.IsType<OperationalEventContext>(reporter.Context);
        Assert.Equal(EvidenceAvailability.Unknown, context.BusinessPaused.Availability);
        Assert.False(context.BusinessPaused.HasValue);
        Assert.Equal(PhysicalConclusionCode.CommandNotSent, context.PhysicalConclusion.Code);
        Assert.Equal(EvidenceAvailability.Confirmed, context.PhysicalConclusion.Availability);

        InvokePrivate(
            engine,
            "TryReportMoveFinalFailure",
            "天车", 1, "1号天车", "ST001", "测试阶段", "测试详情", "测试结果",
            10, 20, 30, true, true, new TimeoutException("测试超时"), OperationalEventSeverity.Error);

        context = Assert.IsType<OperationalEventContext>(reporter.Context);
        Assert.Equal(PhysicalConclusionCode.CommandResultUnknown, context.PhysicalConclusion.Code);
        Assert.Equal(EvidenceAvailability.Unknown, context.PhysicalConclusion.Availability);
    }

    [Fact]
    public void Engine_reporting_marks_empty_identity_and_nonpositive_dimensions_as_unknown()
    {
        var reporter = new CapturingReporter();
        ProductionFlowEngine engine = CreateUninitializedEngine(reporter);
        var workpiece = new WorkpieceContext
        {
            PlateNo = " ",
            Sequence = string.Empty,
            Diameter = 0,
            Length = -1,
            CurrentStage = FlowStage.Loading
        };

        InvokePrivate(
            engine,
            "TryReportEngineFinalFailure",
            workpiece, FlowStage.Loading, OperationalEventSeverity.Error,
            "测试", "测试详情", new InvalidOperationException("测试异常"));

        WorkpieceEvidence evidence = Assert.IsType<OperationalEventContext>(reporter.Context).Evidence.Workpiece;
        AssertUnknown(evidence.PlateNo);
        AssertUnknown(evidence.Sequence);
        AssertUnknown(evidence.Diameter);
        AssertUnknown(evidence.Length);
    }

    [Fact]
    public void Engine_reporting_confirms_only_present_identity_and_positive_dimensions()
    {
        var reporter = new CapturingReporter();
        ProductionFlowEngine engine = CreateUninitializedEngine(reporter);
        var workpiece = new WorkpieceContext
        {
            PlateNo = "PLATE-01",
            Sequence = "SEQ-02",
            Diameter = 123.45,
            Length = 678.9,
            CurrentStage = FlowStage.Boring
        };

        InvokePrivate(
            engine,
            "TryReportEngineFinalFailure",
            workpiece, FlowStage.Boring, OperationalEventSeverity.Error,
            "测试", "测试详情", new InvalidOperationException("测试异常"));

        WorkpieceEvidence evidence = Assert.IsType<OperationalEventContext>(reporter.Context).Evidence.Workpiece;
        AssertConfirmed(evidence.PlateNo, "PLATE-01");
        AssertConfirmed(evidence.Sequence, "SEQ-02");
        AssertConfirmed(evidence.Diameter, 123.45);
        AssertConfirmed(evidence.Length, 678.9);
    }

    [Fact]
    public void Move_catch_stage_markers_precede_existing_device_operations_without_changing_their_order()
    {
        string source = ReadSource("Service", "FlowEngine", "ProductionFlowEngine.cs");
        string crane = ExtractMethod(source, "public async Task<bool> MoveCraneToStationAsync");
        string manipulator = ExtractMethod(source, "public async Task<bool> MoveManipulatorToStationAsync");

        AssertStageBefore(crane, "string monitoringStage = \"获取天车服务\";", "_craneCache.GetOrCreateService(");
        AssertStageBefore(crane, "monitoringStage = \"移动前安全检查\";", ".CheckSafetyAsync(");
        AssertStageBefore(crane, "monitoringStage = \"设置绝对速度\";", "_cfg.GetCraneSpeed(");
        AssertStageBefore(crane, "monitoringStage = \"设置绝对速度\";", ".SetAbsSpeedAsync(");
        AssertStageBefore(crane, "monitoringStage = \"读取移动前坐标\";", "var before = await craneSvc.ReadStatusAsync(");
        AssertStageBefore(crane, "monitoringStage = \"执行绝对移动\";", ".MoveAbsoluteAsync(");
        AssertStageBefore(crane, "monitoringStage = \"读取移动后坐标\";", "var after = await craneSvc.ReadStatusAsync(");
        Assert.Equal(2, Count(crane, "craneName, stationCode, monitoringStage,"));

        AssertStageBefore(manipulator, "string monitoringStage = \"获取机械手服务\";", "_manipulatorCache.GetOrCreateService(");
        AssertStageBefore(manipulator, "monitoringStage = \"移动前安全检查\";", ".CheckSafetyAsync(");
        AssertStageBefore(manipulator, "monitoringStage = \"设置绝对速度\";", "_cfg.GetManipulatorSpeed(");
        AssertStageBefore(manipulator, "monitoringStage = \"设置绝对速度\";", ".SetAbsSpeedAsync(");
        AssertStageBefore(manipulator, "monitoringStage = \"执行绝对移动\";", ".MoveAbsoluteAsync(");
        Assert.Equal(1, Count(manipulator, "info.Name, stationCode, monitoringStage,"));

        Assert.Equal(
            new[] { "GetOrCreateService(", ".CheckSafetyAsync(", ".SetAbsSpeedAsync(", "var before = await craneSvc.ReadStatusAsync(", ".MoveAbsoluteAsync(", "var after = await craneSvc.ReadStatusAsync(" },
            OrderedMarkers(crane,
                "GetOrCreateService(", ".CheckSafetyAsync(", ".SetAbsSpeedAsync(",
                "var before = await craneSvc.ReadStatusAsync(", ".MoveAbsoluteAsync(",
                "var after = await craneSvc.ReadStatusAsync("));
        Assert.Equal(
            new[] { "GetOrCreateService(", ".CheckSafetyAsync(", ".SetAbsSpeedAsync(", ".MoveAbsoluteAsync(" },
            OrderedMarkers(manipulator,
                "GetOrCreateService(", ".CheckSafetyAsync(", ".SetAbsSpeedAsync(", ".MoveAbsoluteAsync("));
    }

    [Fact]
    public void Move_catches_distinguish_failures_before_and_after_the_absolute_move_call_boundary()
    {
        string source = ReadSource("Service", "FlowEngine", "ProductionFlowEngine.cs");
        string crane = ExtractMethod(source, "public async Task<bool> MoveCraneToStationAsync");
        string manipulator = ExtractMethod(source, "public async Task<bool> MoveManipulatorToStationAsync");

        AssertMotionStartEvidence(
            crane,
            "await craneSvc.MoveAbsoluteAsync(",
            expectedCatchReports: 2,
            "_craneCache.GetOrCreateService(",
            ".CheckSafetyAsync(",
            "_cfg.GetCraneSpeed(",
            ".SetAbsSpeedAsync(",
            "var before = await craneSvc.ReadStatusAsync(");
        int craneMove = crane.IndexOf("await craneSvc.MoveAbsoluteAsync(", StringComparison.Ordinal);
        int craneAfter = crane.IndexOf("var after = await craneSvc.ReadStatusAsync(", StringComparison.Ordinal);
        Assert.True(craneMove < craneAfter);

        AssertMotionStartEvidence(
            manipulator,
            "await svc.MoveAbsoluteAsync(",
            expectedCatchReports: 1,
            "_manipulatorCache.GetOrCreateService(",
            ".CheckSafetyAsync(",
            "_cfg.GetManipulatorSpeed(",
            ".SetAbsSpeedAsync(");
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
        Assert.Equal(absoluteMoveFailureCount, Count(method, "stationCode, monitoringStage,"));
    }

    private static void AssertStageBefore(string method, string stageMarker, string operationMarker)
    {
        int stage = method.IndexOf(stageMarker, StringComparison.Ordinal);
        int operation = method.IndexOf(operationMarker, StringComparison.Ordinal);
        Assert.True(stage >= 0, $"未找到监控阶段标记：{stageMarker}");
        Assert.True(operation >= 0, $"未找到设备操作：{operationMarker}");
        Assert.True(stage < operation, $"监控阶段必须在设备操作前更新：{stageMarker}");
    }

    private static void AssertMotionStartEvidence(
        string method,
        string moveCall,
        int expectedCatchReports,
        params string[] operationsBeforeMove)
    {
        const string declaration = "bool monitoringMotionMayHaveStarted = false;";
        const string assignment = "monitoringMotionMayHaveStarted = true;";
        int declarationIndex = method.IndexOf(declaration, StringComparison.Ordinal);
        int assignmentIndex = method.IndexOf(assignment, StringComparison.Ordinal);
        int moveIndex = method.IndexOf(moveCall, StringComparison.Ordinal);

        Assert.True(declarationIndex >= 0);
        Assert.Equal(1, Count(method, declaration));
        Assert.Equal(1, Count(method, assignment));
        Assert.True(declarationIndex < assignmentIndex);
        Assert.True(assignmentIndex < moveIndex);
        Assert.Matches(
            new Regex($@"{Regex.Escape(assignment)}\s*{Regex.Escape(moveCall)}"),
            method);
        Assert.Equal(
            expectedCatchReports,
            Count(method, "motionMayHaveStarted: monitoringMotionMayHaveStarted"));
        Assert.DoesNotMatch(
            new Regex(@"\b(?:if|while|switch)\s*\([^)]*monitoringMotionMayHaveStarted"),
            method);

        foreach (string operation in operationsBeforeMove)
        {
            int operationIndex = method.IndexOf(operation, StringComparison.Ordinal);
            Assert.True(operationIndex >= 0, $"未找到原操作：{operation}");
            Assert.True(operationIndex < assignmentIndex, $"{operation}失败时运动命令证据必须保持false");
        }
    }

    private static string[] OrderedMarkers(string method, params string[] markers) =>
        markers
            .Select(marker => (Marker: marker, Index: method.IndexOf(marker, StringComparison.Ordinal)))
            .OrderBy(item => item.Index)
            .Select(item => item.Marker)
            .ToArray();

    private static ProductionFlowEngine CreateUninitializedEngine(IOperationalEventReporter reporter)
    {
        var engine = (ProductionFlowEngine)RuntimeHelpers.GetUninitializedObject(typeof(ProductionFlowEngine));
        FieldInfo? reporterField = typeof(ProductionFlowEngine).GetField(
            "_exceptionReporter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(reporterField);
        reporterField.SetValue(engine, reporter);
        return engine;
    }

    private static void InvokePrivate(ProductionFlowEngine engine, string methodName, params object?[] arguments)
    {
        MethodInfo? method = typeof(ProductionFlowEngine).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(engine, arguments);
    }

    private static void AssertUnknown<T>(EvidenceValue<T> evidence)
    {
        Assert.Equal(EvidenceAvailability.Unknown, evidence.Availability);
        Assert.False(evidence.HasValue);
    }

    private static void AssertConfirmed<T>(EvidenceValue<T> evidence, T expected)
    {
        Assert.Equal(EvidenceAvailability.Confirmed, evidence.Availability);
        Assert.True(evidence.HasValue);
        Assert.Equal(expected, evidence.Value);
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

    private sealed class CapturingReporter : IOperationalEventReporter
    {
        public OperationalEventContext? Context { get; private set; }

        public void Report(OperationalEventContext context) => Context = context;

        public void ObserveTransientFailure(OperationalFailureObservation observation) { }

        public void ObserveRecovery(OperationalRecoveryObservation observation) { }

        public void ObserveStage(OperationalStageObservation observation) { }
    }
}
