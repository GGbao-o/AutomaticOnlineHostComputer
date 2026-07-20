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
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.DeltaX.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.Tolerance.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.StableSampleCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.FineTuneAttemptCount.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, evidence.CapturedAtUtc.Availability);
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
            false, true,
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
        Assert.False(item.FirstBusinessPaused);
        Assert.True(item.LatestBusinessPaused);
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
                false, false, "首次", "最近", "", "", evidence, evidence)
        };
        var diagnostics = new OperationalEventDiagnostics(1, 0, 0, 0, null, string.Empty);

        var snapshot = new OperationalEventStoreSnapshot(1, events, diagnostics);
        events.Clear();

        Assert.Single(snapshot.Events);
        Assert.Throws<NotSupportedException>(() => ((IList<OperationalEvent>)snapshot.Events).Clear());
    }
}
