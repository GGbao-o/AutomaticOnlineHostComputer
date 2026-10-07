namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>防止物理命令异常以后，账本、引擎暂停和页面告警又被拆成相互矛盾的分支。</summary>
public sealed class CraneExceptionSafetyContractTests
{
    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    public void Front_crane_sent_command_exception_pauses_before_any_retry_queue(string file)
    {
        string source = Read("Service", "FlowEngine", file);

        int resolver = source.IndexOf("FlowActionExceptionResolver.Resolve(action, ex)", StringComparison.Ordinal);
        int manualPause = source.IndexOf("else if (exceptionConclusion == FlowActionExceptionConclusion.PauseLineManual)", resolver, StringComparison.Ordinal);
        int safetyCallback = source.IndexOf("OnSafetyAlarm?.Invoke", manualPause, StringComparison.Ordinal);
        int retryQueue = source.IndexOf("_craneQueue.Enqueue(wp);", manualPause, StringComparison.Ordinal);

        Assert.True(resolver >= 0, $"{file}必须使用统一异常裁决器");
        Assert.True(manualPause > resolver, $"{file}必须存在命令已发后的人工暂停分支");
        Assert.True(safetyCallback > manualPause && safetyCallback < retryQueue,
            $"{file}物理命令响应未知必须先走主页安全回调，不能直接回队");
        Assert.Contains("唯一允许自动重试的结论", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Rear_unload_unknown_x11_uses_pending_safety_alarm_callback(string file)
    {
        string source = Read("Service", "FlowEngine", file);

        Assert.Contains("FlowActionExceptionResolver.Resolve(action, ex)", source, StringComparison.Ordinal);
        Assert.Contains("物理命令响应未知", source, StringComparison.Ordinal);
        Assert.Contains("物理命令已发送但X11、工件位置或目标交接未确认", source, StringComparison.Ordinal);
        Assert.Contains("OnRearCraneSafetyAlarm?.Invoke(pendingSafetyAlarm)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Crane_writes_are_single_attempt_but_reads_keep_the_reconnect_retry_default()
    {
        string source = Read("Communication", "DeviceServices", "CraneService.cs");

        Assert.Contains("private const int MaxIoAttempts = 3", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxWriteIoAttempts = 1", source, StringComparison.Ordinal);
        Assert.Contains("WriteInt32Async(startAddr, value, token), $\"{label} D{startAddr}~D{startAddr + 1}\", ct, MaxWriteIoAttempts", source, StringComparison.Ordinal);
        Assert.Contains("WriteAsync(address, value, token), $\"{label} D{address}\", ct, MaxWriteIoAttempts", source, StringComparison.Ordinal);
        Assert.Contains("ReadAsync(Addr.D_RequestData, 30, token), \"读取状态 D5000~D5029\", ct)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_refuses_line_resume_until_manual_snapshots_are_settled()
    {
        string source = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");

        Assert.Contains("EnsureNoPendingManualActionsForLine(1)", source, StringComparison.Ordinal);
        Assert.Contains("EnsureNoPendingManualActionsForLine(2)", source, StringComparison.Ordinal);
        Assert.Contains("FlowActionManualRegistry.Snapshot()", source, StringComparison.Ordinal);
        Assert.Contains("item.Disposition != FlowActionDisposition.PauseSourceReserved", source, StringComparison.Ordinal);
        Assert.Contains("禁止启动或恢复", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "1号线后天车上料")]
    [InlineData("Line2RearFlowEngine.cs", "2号线后天车上料")]
    public void Rear_load_manual_recovery_keeps_source_carrier_and_target_settlement_separate(string file, string scope)
    {
        string source = Read("Service", "FlowEngine", file);

        int candidateScan = source.IndexOf("private LoadCandidateScan BuildLoadCandidateScan", StringComparison.Ordinal);
        int dispatch = source.IndexOf("private async Task TryDispatchLoadAsync", StringComparison.Ordinal);
        int targetSettlement = source.IndexOf("public string ResolveManualLoadTargetPending", StringComparison.Ordinal);

        Assert.True(candidateScan >= 0 && dispatch > candidateScan && targetSettlement > 0);
        Assert.Contains("IsHeldForManualResolution(rackCode", source[candidateScan..dispatch], StringComparison.Ordinal);
        Assert.Contains("来源冻结,跳过", source[candidateScan..dispatch], StringComparison.Ordinal);
        int emergencySettlement = source.IndexOf("private static string SettleManualRearLoadForEmergency", StringComparison.Ordinal);
        Assert.True(emergencySettlement > 0, $"{file}斜床应急必须有后天车上料放弃结算器");
        int emergencySettlementEnd = source.IndexOf("    /// <summary>", emergencySettlement, StringComparison.Ordinal);
        Assert.True(emergencySettlementEnd > emergencySettlement, $"{file}后天车上料放弃结算器边界无效");
        string emergencySettlementMethod = source[emergencySettlement..emergencySettlementEnd];
        Assert.Contains("FlowActionDisposition.PauseOnCarrier", emergencySettlementMethod, StringComparison.Ordinal);
        Assert.Contains("FlowActionDisposition.PauseAtTargetPendingHandoff", emergencySettlementMethod, StringComparison.Ordinal);
        Assert.Contains("来源冻结快照必须保留", emergencySettlementMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Disposition == FlowActionDisposition.PauseSourceReserved", emergencySettlementMethod, StringComparison.Ordinal);

        int nextMethod = source.IndexOf("    /// <summary>", targetSettlement + 1, StringComparison.Ordinal);
        string targetMethod = source[targetSettlement..nextMethod];
        Assert.Contains($"action.FlowScope != \"{scope}\"", targetMethod, StringComparison.Ordinal);
        Assert.Contains("action.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff", targetMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveManualReservation", targetMethod, StringComparison.Ordinal);
        Assert.Contains("bed.Wp is not { } wp", targetMethod, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "1号线后天车下料")]
    [InlineData("Line2RearFlowEngine.cs", "2号线后天车下料")]
    public void Rear_unload_on_carrier_emergency_settles_only_the_selected_source_bed(
        string file, string scope)
    {
        string source = Read("Service", "FlowEngine", file);
        int settlement = source.IndexOf("private static string SettleManualRearUnloadOnCarrierForEmergency", StringComparison.Ordinal);
        Assert.True(settlement > 0, $"{file}必须有后天车下料持件应急结算器");
        int settlementEnd = source.IndexOf("    /// <summary>", settlement, StringComparison.Ordinal);
        Assert.True(settlementEnd > settlement, $"{file}后天车下料持件应急结算器边界无效");
        string method = source[settlement..settlementEnd];

        Assert.Contains($"const string flowScope = \"{scope}\"", method, StringComparison.Ordinal);
        Assert.Contains("item.Source == bed.Code", method, StringComparison.Ordinal);
        Assert.Contains("item.Disposition == FlowActionDisposition.PauseOnCarrier", method, StringComparison.Ordinal);
        Assert.DoesNotContain("PauseAtTargetPendingHandoff", method, StringComparison.Ordinal);
        Assert.DoesNotContain("PauseSourceReserved", method, StringComparison.Ordinal);

        int cleared = source.IndexOf("MarkSkewManualCleared(bed);", StringComparison.Ordinal);
        int invoked = source.IndexOf("logs.Add(SettleManualRearUnloadOnCarrierForEmergency(bed));", StringComparison.Ordinal);
        Assert.True(invoked > cleared, $"{file}必须在斜床软件状态清空后再清下料持件快照");
    }

    [Fact]
    public void Rear_load_on_carrier_is_not_routed_to_source_settlement()
    {
        string source = Read("Views", "Home", "Dialogs", "EmergencyCenterDialog.xaml.cs");
        int rearLoad = source.IndexOf("else if (isRearLoad)", StringComparison.Ordinal);
        int nextBranch = source.IndexOf("else if (isManipulator1)", rearLoad, StringComparison.Ordinal);
        string branch = source[rearLoad..nextBranch];

        Assert.Contains("snapshot.Disposition switch", branch, StringComparison.Ordinal);
        Assert.Contains("FlowActionDisposition.PauseSourceReserved", branch, StringComparison.Ordinal);
        Assert.Contains("FlowActionDisposition.PauseOnCarrier", branch, StringComparison.Ordinal);
        Assert.Contains("斜床应急作废", branch, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
