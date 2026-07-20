using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class OperationalEventModelTests
{
    [Fact]
    public void Unavailable_position_does_not_use_zero_as_evidence()
    {
        PositionEvidence evidence = PositionEvidence.Unavailable("调用点没有成功坐标快照");

        Assert.Equal(EvidenceAvailability.Unavailable, evidence.DisplayX.Availability);
        Assert.False(evidence.DisplayX.HasValue);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.DisplayY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.DisplayZ.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.AbsX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.AbsY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.TargetX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.TargetY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.TargetAbsX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.TargetAbsY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.DeltaX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.DeltaY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.LastSentDisplayTargetX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.LastSentDisplayTargetY.Availability);
        Assert.Equal(DeviceCommandState.Unavailable, evidence.XFineTuneCommand.State);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.XFineTuneCommand.Availability);
        Assert.Equal("调用点没有成功坐标快照", evidence.XFineTuneCommand.Reason);
        Assert.Equal(DeviceCommandState.Unavailable, evidence.YFineTuneCommand.State);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.YFineTuneCommand.Availability);
        Assert.Equal("调用点没有成功坐标快照", evidence.YFineTuneCommand.Reason);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.FailureStage.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.ToleranceX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.ToleranceY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.MaximumCorrectionX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.MaximumCorrectionY.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.StableSampleCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.StageReadCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.TotalReadCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.FineTuneAttemptCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.FeedbackRereadCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.CapturedAtUtc.Availability);
        Assert.All(
            new[]
            {
                evidence.DisplayX.Reason, evidence.DisplayY.Reason, evidence.DisplayZ.Reason,
                evidence.AbsX.Reason, evidence.AbsY.Reason, evidence.TargetX.Reason,
                evidence.TargetY.Reason, evidence.TargetAbsX.Reason, evidence.TargetAbsY.Reason,
                evidence.LastSentDisplayTargetX.Reason, evidence.LastSentDisplayTargetY.Reason,
                evidence.FailureStage.Reason, evidence.DeltaX.Reason, evidence.DeltaY.Reason,
                evidence.ToleranceX.Reason, evidence.ToleranceY.Reason,
                evidence.MaximumCorrectionX.Reason, evidence.MaximumCorrectionY.Reason,
                evidence.StableSampleCount.Reason, evidence.StageReadCount.Reason,
                evidence.TotalReadCount.Reason, evidence.FineTuneAttemptCount.Reason,
                evidence.FeedbackRereadCount.Reason, evidence.CapturedAtUtc.Reason
            },
            reason => Assert.Equal("调用点没有成功坐标快照", reason));
    }

    [Fact]
    public void Confirmed_false_is_distinct_from_unknown()
    {
        EvidenceValue<bool> confirmedFalse = EvidenceValue<bool>.Confirmed(false, "已读取并确认未持件");
        EvidenceValue<bool> unknown = EvidenceValue<bool>.Unknown("调用点没有持件证据");

        Assert.Equal(EvidenceAvailability.Confirmed, confirmedFalse.Availability);
        Assert.True(confirmedFalse.HasValue);
        Assert.False(confirmedFalse.Value);
        Assert.Equal(EvidenceAvailability.Unknown, unknown.Availability);
        Assert.False(unknown.HasValue);
    }

    [Fact]
    public void Business_pause_state_is_evidence_not_an_inferred_boolean()
    {
        OperationalEventContext context = new()
        {
            EventCode = "LEGACY",
            Severity = OperationalEventSeverity.Warning,
            Category = OperationalEventCategory.Safety,
            Scope = "line-1",
            Engine = "legacy-adapter",
            DeviceType = "unknown",
            DeviceNo = "",
            Station = "",
            ActionStage = "callback",
            Title = "legacy event",
            Evidence = OperationalEvidence.Unavailable("旧入口没有提供证据")
        };

        Assert.Equal(EvidenceAvailability.Unknown, context.BusinessPaused.Availability);
        Assert.False(context.BusinessPaused.HasValue);
        Assert.Equal(string.Empty, context.ActionId);
        Assert.Equal(string.Empty, context.CorrelationKey);
    }

    [Fact]
    public void Confirmed_zero_coordinate_is_distinct_from_unknown_coordinate()
    {
        EvidenceValue<int> confirmedZero = EvidenceValue<int>.Confirmed(0, "显示坐标已读取为零");
        EvidenceValue<int> unknown = EvidenceValue<int>.Unknown("坐标读取没有成功");

        Assert.True(confirmedZero.HasValue);
        Assert.Equal(0, confirmedZero.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, confirmedZero.Availability);
        Assert.False(unknown.HasValue);
        Assert.Equal(EvidenceAvailability.Unknown, unknown.Availability);
    }

    [Fact]
    public void Position_snapshot_can_mix_confirmed_and_unavailable_fields()
    {
        PositionEvidence position = PositionEvidence.Unavailable("其余坐标未采集") with
        {
            DisplayX = EvidenceValue<int>.Confirmed(120, "界面坐标快照"),
            AbsX = EvidenceValue<int>.Confirmed(1000, "绝对坐标快照"),
            TargetX = EvidenceValue<int>.Confirmed(125, "动作目标"),
            DeltaX = EvidenceValue<int>.Confirmed(-5, "调用点已计算"),
            LastSentDisplayTargetX = EvidenceValue<int>.Confirmed(125, "最后一次X移动命令参数"),
            XFineTuneCommand = new DeviceCommandEvidence(
                DeviceCommandState.Acknowledged,
                EvidenceAvailability.Confirmed,
                "X移动命令成功返回"),
            FailureStage = EvidenceValue<string>.Confirmed("feedback-reread", "异常捕获时阶段"),
            ToleranceX = EvidenceValue<int>.Confirmed(2, "X微调参数"),
            MaximumCorrectionX = EvidenceValue<int>.Confirmed(30, "X微调参数"),
            StageReadCount = EvidenceValue<int>.Confirmed(3, "当前阶段计数"),
            TotalReadCount = EvidenceValue<int>.Confirmed(7, "动作累计计数"),
            FeedbackRereadCount = EvidenceValue<int>.Confirmed(1, "反馈重读计数")
        };

        Assert.Equal(120, position.DisplayX.Value);
        Assert.Equal(EvidenceAvailability.Confirmed, position.DisplayX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, position.DisplayY.Availability);
        Assert.Equal(EvidenceAvailability.Confirmed, position.AbsX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, position.AbsY.Availability);
        Assert.Equal(-5, position.DeltaX.Value);
        Assert.Equal(EvidenceAvailability.Unavailable, position.ToleranceY.Availability);
        Assert.Equal(1, position.FeedbackRereadCount.Value);
        Assert.True(position.FeedbackRereadCount.HasValue);
    }

    [Fact]
    public void Confirmed_lock_not_held_is_distinct_from_unknown_lock_evidence()
    {
        var notHeld = new LockItemEvidence(
            "ZONE_MT",
            EvidenceValue<bool>.Confirmed(false, "异常发生时未持有"),
            EvidenceValue<bool>.NotApplicable("无需释放"),
            "无需确认");
        var unknown = new LockItemEvidence(
            "ZONE_MT",
            EvidenceValue<bool>.Unknown("调用点没有锁快照"),
            EvidenceValue<bool>.Unknown("释放结果未知"),
            "人工核对锁状态");

        Assert.True(notHeld.HeldAtFailure.HasValue);
        Assert.False(notHeld.HeldAtFailure.Value);
        Assert.False(unknown.HeldAtFailure.HasValue);
        Assert.Equal(EvidenceAvailability.Unknown, unknown.HeldAtFailure.Availability);
    }

    [Fact]
    public void Evidence_collection_constructors_make_defensive_copies()
    {
        var lockItems = new List<LockItemEvidence>
        {
            new(
                "POSITION_LOCK",
                EvidenceValue<bool>.Confirmed(true, "已持有"),
                EvidenceValue<bool>.Confirmed(true, "finally已释放"),
                "确认设备位置")
        };
        var recoverySteps = new List<RecoveryStepEvidence>
        {
            new("释放软件锁", RecoveryStepState.Succeeded, "释放调用已返回")
        };
        var verification = new List<string> { "确认位置锁已释放" };
        var requiredActions = new List<string> { "核对工件位置" };
        var forbiddenActions = new List<string> { "禁止重复取料" };

        var locks = new LockEvidence(lockItems);
        var recovery = new RecoveryEvidence(
            EvidenceValue<bool>.Confirmed(true, "已尝试"),
            EvidenceValue<bool>.Confirmed(true, "已完成"),
            recoverySteps,
            verification,
            "原业务继续运行");
        var guidance = new OperatorGuidance(requiredActions, forbiddenActions, "人工确认后按原流程处理");

        lockItems.Clear();
        recoverySteps.Clear();
        verification.Clear();
        requiredActions.Clear();
        forbiddenActions.Clear();

        Assert.Single(locks.Items);
        Assert.Single(recovery.Steps);
        Assert.Single(recovery.PostRecoveryVerification);
        Assert.Single(guidance.RequiredActions);
        Assert.Single(guidance.ForbiddenActions);
        Assert.Equal(EvidenceAvailability.Confirmed, locks.Availability);
        Assert.Equal(EvidenceAvailability.Confirmed, recovery.Availability);
        Assert.Equal(EvidenceAvailability.Confirmed, guidance.Availability);
        Assert.Throws<NotSupportedException>(() => ((IList<LockItemEvidence>)locks.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RecoveryStepEvidence>)recovery.Steps).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)guidance.RequiredActions).Clear());
    }

    [Fact]
    public void Mutating_array_input_does_not_change_frozen_evidence_collection()
    {
        var original = new LockItemEvidence(
            "ZONE_MT",
            EvidenceValue<bool>.Confirmed(true, "异常时持有"),
            EvidenceValue<bool>.Confirmed(true, "finally已释放"),
            "确认天车已离开冲突区");
        var replacement = original with { Name = "ZONE_TS" };
        LockItemEvidence[] input = [original];

        var locks = new LockEvidence(input);
        input[0] = replacement;

        Assert.Equal("ZONE_MT", locks.Items[0].Name);
        Assert.NotSame(input, locks.Items);
    }

    [Fact]
    public void Workpiece_fields_and_physical_commitments_have_independent_evidence()
    {
        var commitments = new List<PhysicalCommitmentEvidence>
        {
            new("当前持件", EvidenceValue<bool>.Confirmed(false, "X11已确认无工件"), "读取有效")
        };
        var state = new BusinessStateEvidence(
            EvidenceAvailability.Confirmed,
            "已取得业务快照",
            EvidenceValue<string>.Confirmed("Z下降完成", "调用已返回"),
            EvidenceValue<string>.Confirmed("取料中", "状态机快照"),
            EvidenceValue<string>.Confirmed("回源中", "catch赋值后"),
            EvidenceValue<string>.Unknown("未取得缓存前值"),
            EvidenceValue<string>.Confirmed("已重新入队", "缓存调用已返回"),
            EvidenceValue<string>.Confirmed("前天车", "动作所有权"),
            EvidenceValue<string>.Confirmed("前天车", "所有权未变"),
            EvidenceValue<bool>.Confirmed(false, "X11有效"),
            EvidenceValue<bool>.Unknown("放料结果没有传入"),
            EvidenceValue<bool>.Confirmed(true, "缓存通知已返回"),
            commitments);
        var workpiece = new WorkpieceEvidence(
            EvidenceValue<string>.Confirmed("P-100", "任务对象"),
            EvidenceValue<string>.Unknown("没有序号"),
            EvidenceValue<double>.Confirmed(120D, "任务对象"),
            EvidenceValue<double>.Unavailable("长度未传入"),
            EvidenceValue<string>.Confirmed("ST713", "动作参数"),
            EvidenceValue<string>.Confirmed("双头镗", "动作参数"),
            EvidenceValue<string>.Confirmed("前天车", "所有权快照"),
            EvidenceValue<string>.Unknown("最后位置未确认"),
            "调用点局部变量");

        commitments.Clear();

        Assert.Equal(EvidenceAvailability.Confirmed, workpiece.PlateNo.Availability);
        Assert.Equal(EvidenceAvailability.Unknown, workpiece.Sequence.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, workpiece.Length.Availability);
        Assert.Single(state.PhysicalCommitments);
        Assert.False(state.PhysicalCommitments[0].State.Value);
    }

    [Fact]
    public void Unavailable_operational_evidence_has_all_eight_groups_and_fresh_collections()
    {
        OperationalEvidence first = OperationalEvidence.Unavailable("调用点没有证据快照");
        OperationalEvidence second = OperationalEvidence.Unavailable("另一个调用点没有证据快照");

        Assert.NotNull(first.Workpiece);
        Assert.Equal(EvidenceAvailability.Unavailable, first.Workpiece.PlateNo.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.Position.DisplayX.Availability);
        Assert.Equal(DeviceCommandState.Unavailable, first.MotionAndMagnet.ZDownCommand.State);
        Assert.Equal(EvidenceAvailability.Unavailable, first.MotionAndMagnet.X11Attempts.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.MotionAndMagnet.X11ReadAtUtc.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.BusinessState.StateBefore.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.BusinessState.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.Locks.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.Recovery.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, first.Guidance.Availability);
        Assert.Empty(first.Locks.Items);
        Assert.Empty(first.Recovery.Steps);
        Assert.Empty(first.Guidance.RequiredActions);
        Assert.Equal(EvidenceAvailability.Unavailable, first.Exception.Availability);
        Assert.NotSame(first.Locks.Items, second.Locks.Items);
        Assert.NotSame(first.Recovery.Steps, second.Recovery.Steps);
        Assert.NotSame(first.Guidance.RequiredActions, second.Guidance.RequiredActions);
    }

    [Fact]
    public void Operational_event_retains_first_and_latest_legacy_details()
    {
        OperationalEvidence evidence = OperationalEvidence.Unavailable("测试");
        var physical = new PhysicalConclusionEvidence(
            PhysicalConclusionCode.Unknown,
            EvidenceAvailability.Unknown,
            "物理结论未知",
            "没有确认依据");

        var item = new OperationalEvent(
            "EVT-1", "ACT-1", "ACT-2", "TEST_FAILURE",
            OperationalEventSeverity.Error, OperationalEventCategory.FinalFailure,
            DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(5), 2L,
            "1号线", "前端引擎", "原安全回调", "天车", "1", "ST713",
            "取料", "取料失败", physical,
            physical with { Code = PhysicalConclusionCode.ManualConfirmationRequired },
            EvidenceValue<bool>.Confirmed(false, "业务未暂停"),
            EvidenceValue<bool>.Confirmed(true, "业务已暂停"),
            "首次详情", "最近详情",
            "首次结果", "最近结果",
            evidence, evidence);

        Assert.Equal("原安全回调", item.Source);
        Assert.Equal("ACT-1", item.FirstActionId);
        Assert.Equal("ACT-2", item.LatestActionId);
        Assert.Equal(PhysicalConclusionCode.Unknown, item.FirstPhysicalConclusion.Code);
        Assert.Equal(PhysicalConclusionCode.ManualConfirmationRequired, item.LatestPhysicalConclusion.Code);
        Assert.Equal("首次详情", item.FirstDetailMessage);
        Assert.Equal("最近详情", item.LatestDetailMessage);
        Assert.Equal("首次结果", item.FirstResult);
        Assert.Equal("最近结果", item.LatestResult);
        Assert.True(item.FirstBusinessPaused.HasValue);
        Assert.False(item.FirstBusinessPaused.Value);
        Assert.True(item.LatestBusinessPaused.HasValue);
        Assert.True(item.LatestBusinessPaused.Value);
    }

    [Fact]
    public void Store_snapshot_defensively_copies_and_freezes_event_list()
    {
        OperationalEvidence evidence = OperationalEvidence.Unavailable("测试");
        var physical = new PhysicalConclusionEvidence(
            PhysicalConclusionCode.Unknown,
            EvidenceAvailability.Unknown,
            "未知",
            "无证据");
        var events = new List<OperationalEvent>
        {
            new(
                "EVT-1", "ACT-1", "ACT-1", "TEST",
                OperationalEventSeverity.Warning, OperationalEventCategory.StageStall,
                DateTime.UnixEpoch, DateTime.UnixEpoch, 1L,
                "全局", "测试", "测试", "PLC", "1", "R6101",
                "等待", "等待滞留", physical, physical,
                EvidenceValue<bool>.Unknown("未取得暂停状态"),
                EvidenceValue<bool>.Unknown("未取得暂停状态"),
                "首次", "最近", "", "", evidence, evidence)
        };
        var diagnostics = new OperationalEventDiagnostics(1, 0, 0, 0, null, string.Empty);

        var snapshot = new OperationalEventStoreSnapshot(1, events, diagnostics);
        events.Clear();

        Assert.Single(snapshot.Events);
        Assert.Throws<NotSupportedException>(() => ((IList<OperationalEvent>)snapshot.Events).Clear());
    }
}
