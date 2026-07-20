using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class OperationalEventFormatterTests
{
    [Fact]
    public void Catalog_has_complete_stable_definitions_and_safe_unknown_fallback()
    {
        OperationalEventCatalog catalog = OperationalEventCatalog.Default;
        string[] requiredCodes =
        [
            "LEGACY_SAFETY_ALARM", "LEGACY_WARNING", "LEGACY_EMERGENCY",
            "ENGINE_FINAL_FAILURE", "CRANE_XY_FINE_TUNE_FAILED",
            "CRANE_MAGNET_ON_RESPONSE_UNKNOWN", "CRANE_MAGNET_OFF_FAILED",
            "CRANE_X11_READ_FAILED", "CRANE_X11_NOT_CONFIRMED",
            "PRESSURE_STOP_DETECTED", "PRESSURE_STOP_RECOVERED",
            "PHYSICAL_HANDOFF_NOT_CLOSED", "DEVICE_TRANSIENT_FAILURE",
            "DEVICE_CONNECTION_RECOVERED", "STAGE_STALLED", "STAGE_RECOVERED"
        ];

        Assert.Equal(catalog.Definitions.Count, catalog.Definitions.Select(item => item.EventCode).Distinct(StringComparer.Ordinal).Count());
        foreach (string code in requiredCodes)
        {
            OperationalEventDefinition definition = catalog.GetRequired(code);
            Assert.False(string.IsNullOrWhiteSpace(definition.Title));
            Assert.NotEmpty(definition.RequiredActions);
            Assert.NotEmpty(definition.ForbiddenActions);
            Assert.False(string.IsNullOrWhiteSpace(definition.ContinueCondition));
            string allText = string.Join("|", definition.RequiredActions.Concat(definition.ForbiddenActions).Append(definition.ContinueCondition));
            Assert.DoesNotContain("自行处理", allText, StringComparison.Ordinal);
            Assert.DoesNotContain("酌情处理", allText, StringComparison.Ordinal);
        }

        OperationalEvent unknown = OperationalEventFormattingTestFactory.CreateCompleteEvent() with
        {
            EventCode = "SITE_SPECIFIC_FAILURE",
            Title = "现场专用异常",
            Severity = OperationalEventSeverity.Critical,
            Category = OperationalEventCategory.Handshake
        };
        OperationalEventDefinition fallback = catalog.Resolve(unknown);

        Assert.Equal("现场专用异常", fallback.Title);
        Assert.Equal(OperationalEventSeverity.Critical, fallback.DefaultSeverity);
        Assert.Equal(OperationalEventCategory.Handshake, fallback.DefaultCategory);
        Assert.Contains(fallback.ForbiddenActions, action => action.Contains("禁止", StringComparison.Ordinal));
        Assert.Contains(
            catalog.GetRequired("CRANE_MAGNET_ON_RESPONSE_UNKNOWN").ForbiddenActions,
            action => action.Contains("禁止仅凭软件false变量重复取料或清工件身份", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DeviceCommandState.NotSent, "未发送")]
    [InlineData(DeviceCommandState.SentUnconfirmed, "可能已发送但未取得确认")]
    [InlineData(DeviceCommandState.Acknowledged, "命令成功返回")]
    public void Command_wording_does_not_overstate_physical_result(DeviceCommandState state, string expected)
    {
        string text = OperationalEventFormatter.FormatCommand(new DeviceCommandEvidence(
            state,
            EvidenceAvailability.Confirmed,
            "调用点原始依据"));

        Assert.Contains(expected, text, StringComparison.Ordinal);
        if (state == DeviceCommandState.Acknowledged)
        {
            Assert.DoesNotContain("X11已确认", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Full_detail_contains_overview_eight_groups_first_latest_and_every_evidence_field()
    {
        var formatter = new OperationalEventFormatter();
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();

        string text = formatter.FormatFullDetail(item);

        string[] sections =
        [
            "事件总览", "线路设备与阶段", "工件与流向", "坐标与微调", "Z磁铁与X11",
            "状态缓存与承诺点", "暂停锁与恢复", "必须和禁止操作", "完整异常链"
        ];
        foreach (string section in sections)
        {
            Assert.Contains(section, text, StringComparison.Ordinal);
        }

        string[] fields =
        [
            "EventId", "EventCode", "First ActionId", "Latest ActionId", "首次物理结论", "最近物理结论",
            "首次DetailMessage", "最近DetailMessage", "首次BusinessPaused", "最近BusinessPaused",
            "DisplayX", "DisplayY", "DisplayZ", "AbsX", "AbsY", "TargetX", "TargetY", "TargetZ", "TargetAbsX", "TargetAbsY",
            "LastSentDisplayTargetX", "LastSentDisplayTargetY", "XFineTuneCommand", "YFineTuneCommand", "FailureStage",
            "DeltaX", "DeltaY", "ToleranceX", "ToleranceY", "MaximumCorrectionX", "MaximumCorrectionY",
            "StableSampleCount", "StageReadCount", "TotalReadCount", "FineTuneAttemptCount", "FeedbackRereadCount", "CapturedAtUtc",
            "ZDownCommand", "ZMayStillBeLow", "MagnetOnCommand", "MagnetOffCommand", "X11LastValue", "X11ReadValid", "X11Attempts", "X11ReadAtUtc",
            "LastSuccessfulCheckpoint", "StateBefore", "StateAfter", "CacheBefore", "CacheAfter", "OwnerBefore", "OwnerAfter",
            "HoldingWorkpiece", "Placed", "CacheNotified", "PhysicalCommitments", "Locks", "RecoverySteps", "PostRecoveryVerification",
            "RequiredActions", "ForbiddenActions", "ContinueCondition", "Exception.Type", "Exception.Message", "Exception.InnerExceptionChain",
            "Exception.BusinessContext", "Exception.StackTrace"
        ];
        foreach (string field in fields)
        {
            Assert.Contains(field, text, StringComparison.Ordinal);
        }

        Assert.Contains("[首次证据]", text, StringComparison.Ordinal);
        Assert.Contains("[最近证据]", text, StringComparison.Ordinal);
        Assert.Contains("DisplayX: Confirmed/HasValue=True/Value=0", text, StringComparison.Ordinal);
        Assert.Contains("TargetZ: Confirmed/HasValue=True/Value=0/Reason=Z动作目标已明确为零", text, StringComparison.Ordinal);
        Assert.Contains("TargetZ: Unavailable/HasValue=False/Value=不可用：最近动作没有Z目标/Reason=最近动作没有Z目标", text, StringComparison.Ordinal);
        Assert.Contains("可能已发送但未取得确认", text, StringComparison.Ordinal);
        Assert.Contains("首次异常消息", text, StringComparison.Ordinal);
        Assert.Contains("最近异常消息", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Position_detail_keeps_first_and_latest_target_z_evidence_separate()
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();

        string section = new OperationalEventFormatter().FormatSections(item).PositionAndFineTune;

        int firstMarker = section.IndexOf("[首次证据]", StringComparison.Ordinal);
        int confirmedZero = section.IndexOf(
            "TargetZ: Confirmed/HasValue=True/Value=0/Reason=Z动作目标已明确为零",
            StringComparison.Ordinal);
        int latestMarker = section.IndexOf("[最近证据]", StringComparison.Ordinal);
        int unavailable = section.IndexOf(
            "TargetZ: Unavailable/HasValue=False/Value=不可用：最近动作没有Z目标/Reason=最近动作没有Z目标",
            StringComparison.Ordinal);

        Assert.True(firstMarker >= 0);
        Assert.True(confirmedZero > firstMarker);
        Assert.True(latestMarker > confirmedZero);
        Assert.True(unavailable > latestMarker);
    }

    [Fact]
    public void Full_detail_explains_every_unavailable_value_and_status_observation_exception()
    {
        OperationalEvidence unavailable = OperationalEvidence.Unavailable("调用点未取得该证据");
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent() with
        {
            OccurrenceCount = 1,
            FirstActionId = string.Empty,
            LatestActionId = string.Empty,
            FirstEvidence = unavailable,
            LatestEvidence = unavailable,
            FirstBusinessPaused = EvidenceValue<bool>.Unknown("未取得暂停状态"),
            LatestBusinessPaused = EvidenceValue<bool>.Unknown("未取得暂停状态")
        };

        string text = new OperationalEventFormatter().FormatFullDetail(item);

        Assert.Contains("无动作上下文", text, StringComparison.Ordinal);
        Assert.Contains("不可用", text, StringComparison.Ordinal);
        Assert.Contains("调用点未取得该证据", text, StringComparison.Ordinal);
        Assert.Contains("不适用：该事件来自状态观察/既有文本", text, StringComparison.Ordinal);
        Assert.Contains("仅在页面列出的未知项已人工确认", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_merges_call_site_guidance_before_catalog_fallback_and_keeps_continue_condition_separate()
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();
        string text = new OperationalEventFormatter().FormatFullDetail(item);

        int siteAction = text.IndexOf("核对现场工件", StringComparison.Ordinal);
        int catalogAction = text.IndexOf("记录事件编号", StringComparison.Ordinal);
        int continueCondition = text.LastIndexOf("ContinueCondition", StringComparison.Ordinal);

        Assert.True(siteAction >= 0);
        Assert.True(catalogAction > siteAction);
        Assert.True(continueCondition > catalogAction);
    }

    [Fact]
    public void Diagnostics_show_both_tracker_eviction_counters()
    {
        string text = OperationalEventFormatter.FormatDiagnostics(new OperationalEventDiagnostics(
            10, 2, 1, 3, DateTime.UnixEpoch, "测试失败", 4, 5));

        Assert.Contains("TransientStateEvictedCount=4", text, StringComparison.Ordinal);
        Assert.Contains("StageStateEvictedCount=5", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EvidenceAvailability.Confirmed, "已确认无承诺点")]
    [InlineData(EvidenceAvailability.NotApplicable, "不适用：当前阶段没有物理承诺点")]
    [InlineData(EvidenceAvailability.Unavailable, "不可用：调用点没有承诺点快照")]
    public void Empty_physical_commitments_preserve_group_availability(
        EvidenceAvailability availability,
        string expected)
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();
        string reason = availability switch
        {
            EvidenceAvailability.NotApplicable => "当前阶段没有物理承诺点",
            EvidenceAvailability.Unavailable => "调用点没有承诺点快照",
            _ => "已取得完整业务快照"
        };
        BusinessStateEvidence businessState = EmptyBusinessState(
            item.FirstEvidence.BusinessState,
            availability,
            reason);
        OperationalEvidence evidence = item.FirstEvidence with { BusinessState = businessState };
        item = item with { FirstEvidence = evidence, LatestEvidence = evidence };

        string section = new OperationalEventFormatter().FormatSections(item).StateCacheAndCommitments;

        Assert.Contains($"PhysicalCommitments: {expected}", section, StringComparison.Ordinal);
    }

    private static BusinessStateEvidence EmptyBusinessState(
        BusinessStateEvidence source,
        EvidenceAvailability availability,
        string reason) =>
        new(
            availability,
            reason,
            source.LastSuccessfulCheckpoint,
            source.StateBefore,
            source.StateAfter,
            source.CacheBefore,
            source.CacheAfter,
            source.OwnerBefore,
            source.OwnerAfter,
            source.HoldingWorkpiece,
            source.Placed,
            source.CacheNotified,
            []);
}

internal static class OperationalEventFormattingTestFactory
{
    public static OperationalEvent CreateCompleteEvent()
    {
        OperationalEvidence unavailable = OperationalEvidence.Unavailable("调用点未取得该证据");
        PositionEvidence firstPosition = unavailable.Position with
        {
            DisplayX = EvidenceValue<int>.Confirmed(0, "显示坐标快照"),
            DisplayY = EvidenceValue<int>.Confirmed(20, "显示坐标快照"),
            DisplayZ = EvidenceValue<int>.Unknown("Z读取超时"),
            AbsX = EvidenceValue<int>.Confirmed(1000, "绝对编码器"),
            AbsY = EvidenceValue<int>.Confirmed(2000, "绝对编码器"),
            TargetX = EvidenceValue<int>.Confirmed(5, "计算目标"),
            TargetY = EvidenceValue<int>.Confirmed(25, "计算目标"),
            TargetZ = EvidenceValue<int>.Confirmed(0, "Z动作目标已明确为零"),
            TargetAbsX = EvidenceValue<int>.Confirmed(1005, "配置目标"),
            TargetAbsY = EvidenceValue<int>.Confirmed(2005, "配置目标"),
            LastSentDisplayTargetX = EvidenceValue<int>.Confirmed(5, "最后X命令参数"),
            LastSentDisplayTargetY = EvidenceValue<int>.Confirmed(25, "最后Y命令参数"),
            XFineTuneCommand = new(DeviceCommandState.Acknowledged, EvidenceAvailability.Confirmed, "X命令返回"),
            YFineTuneCommand = new(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, "Y无需修正"),
            FailureStage = EvidenceValue<string>.Confirmed("stable-sample", "异常阶段"),
            DeltaX = EvidenceValue<int>.Confirmed(-5, "已计算"),
            DeltaY = EvidenceValue<int>.Confirmed(0, "已计算"),
            ToleranceX = EvidenceValue<int>.Confirmed(2, "配置"),
            ToleranceY = EvidenceValue<int>.Confirmed(3, "配置"),
            MaximumCorrectionX = EvidenceValue<int>.Confirmed(30, "配置"),
            MaximumCorrectionY = EvidenceValue<int>.Confirmed(40, "配置"),
            StableSampleCount = EvidenceValue<int>.Confirmed(2, "累计"),
            StageReadCount = EvidenceValue<int>.Confirmed(3, "累计"),
            TotalReadCount = EvidenceValue<int>.Confirmed(6, "累计"),
            FineTuneAttemptCount = EvidenceValue<int>.Confirmed(1, "累计"),
            FeedbackRereadCount = EvidenceValue<int>.Confirmed(0, "累计"),
            CapturedAtUtc = EvidenceValue<DateTime>.Confirmed(DateTime.UnixEpoch, "快照时间")
        };
        PositionEvidence latestPosition = firstPosition with
        {
            DisplayX = EvidenceValue<int>.Confirmed(4, "最近显示坐标"),
            TargetZ = EvidenceValue<int>.Unavailable("最近动作没有Z目标"),
            YFineTuneCommand = new(DeviceCommandState.SentUnconfirmed, EvidenceAvailability.Unknown, "Y命令等待响应超时"),
            FailureStage = EvidenceValue<string>.Confirmed("feedback-reread", "最近异常阶段"),
            TotalReadCount = EvidenceValue<int>.Confirmed(9, "累计")
        };

        WorkpieceEvidence workpiece = unavailable.Workpiece with
        {
            PlateNo = EvidenceValue<string>.Confirmed("=P-001", "任务对象"),
            Sequence = EvidenceValue<string>.Confirmed("001", "任务对象"),
            Diameter = EvidenceValue<double>.Confirmed(120.5, "任务对象"),
            Length = EvidenceValue<double>.Confirmed(500, "任务对象"),
            Source = EvidenceValue<string>.Confirmed("ST713,入口", "动作参数"),
            Target = EvidenceValue<string>.Confirmed("双头\"镗\"\r\nA", "动作参数"),
            SoftwareOwner = EvidenceValue<string>.Confirmed("前天车", "缓存快照"),
            LastConfirmedLocation = EvidenceValue<string>.Unknown("未取得物理位置确认")
        };
        MotionAndMagnetEvidence firstMotion = unavailable.MotionAndMagnet with
        {
            ZDownCommand = new(DeviceCommandState.Acknowledged, EvidenceAvailability.Confirmed, "Z下降命令返回"),
            ZMayStillBeLow = EvidenceValue<bool>.Inferred(true, "尚未取得Z抬升确认"),
            MagnetOnCommand = new(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, "异常发生在充磁前"),
            MagnetOffCommand = new(DeviceCommandState.NotApplicable, EvidenceAvailability.NotApplicable, "尚未进入退磁阶段"),
            X11LastValue = EvidenceValue<int>.Confirmed(0, "最后有效读取"),
            X11ReadValid = EvidenceValue<bool>.Confirmed(true, "读取调用返回"),
            X11Attempts = EvidenceValue<int>.Confirmed(3, "累计"),
            X11ReadAtUtc = EvidenceValue<DateTime>.Confirmed(DateTime.UnixEpoch, "读取时间")
        };
        MotionAndMagnetEvidence latestMotion = firstMotion with
        {
            MagnetOnCommand = new(DeviceCommandState.SentUnconfirmed, EvidenceAvailability.Unknown, "充磁调用响应超时")
        };
        BusinessStateEvidence state = new(
            EvidenceAvailability.Confirmed,
            "调用点已有业务快照",
            EvidenceValue<string>.Confirmed("Z下降完成", "检查点"),
            EvidenceValue<string>.Confirmed("取料中", "异常前"),
            EvidenceValue<string>.Confirmed("回源中", "catch后"),
            EvidenceValue<string>.Confirmed("队列A", "异常前"),
            EvidenceValue<string>.Confirmed("队列A", "未修改"),
            EvidenceValue<string>.Confirmed("前天车", "异常前"),
            EvidenceValue<string>.Confirmed("前天车", "未修改"),
            EvidenceValue<bool>.Unknown("X11最终读取失败"),
            EvidenceValue<bool>.Confirmed(false, "未进入放料"),
            EvidenceValue<bool>.Confirmed(false, "未通知缓存"),
            [new("当前持件", EvidenceValue<bool>.Unknown("X11无有效结果"), "必须现场确认")]);
        LockEvidence locks = new(
            EvidenceAvailability.Confirmed,
            "已取得锁快照",
            [new("ZONE_MT", EvidenceValue<bool>.Confirmed(true, "异常时持有"), EvidenceValue<bool>.Confirmed(true, "finally已释放"), "确认设备已离开冲突区")]);
        RecoveryEvidence recovery = new(
            EvidenceAvailability.Confirmed,
            "已取得恢复结果",
            EvidenceValue<bool>.Confirmed(true, "原流程已调用恢复"),
            EvidenceValue<bool>.Confirmed(false, "第二步失败"),
            [new("抬升Z", RecoveryStepState.Succeeded, "命令返回"), new("回安全位", RecoveryStepState.Failed, "位置未确认")],
            ["复核位置锁已释放", "未复核物理工件位置"],
            "原业务保持暂停");
        OperatorGuidance guidance = new(
            EvidenceAvailability.Confirmed,
            "调用点具体指引",
            ["核对现场工件", "核对现场工件"],
            ["禁止重复取料"],
            "仅在工件、Z高度和冲突区均确认后继续");
        ExceptionEvidence firstException = new("System.TimeoutException", "首次异常消息", "Inner: 首次内部异常", "第一次充磁", "first stack");
        ExceptionEvidence latestException = new("System.InvalidOperationException", "最近异常消息", "Inner: 最近内部异常", "反馈重读", "latest stack");
        OperationalEvidence first = new(workpiece, firstPosition, firstMotion, state, locks, recovery, guidance, firstException);
        OperationalEvidence latest = new(workpiece with { Sequence = EvidenceValue<string>.Confirmed("002", "最近任务对象") }, latestPosition, latestMotion, state, locks, recovery, guidance, latestException);

        return new OperationalEvent(
            "EVT-20260720-00001", string.Empty, "ACT-002", "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
            OperationalEventSeverity.Error, OperationalEventCategory.PhysicalUnknown,
            DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc),
            DateTime.SpecifyKind(DateTime.UnixEpoch.AddMinutes(1), DateTimeKind.Utc),
            2,
            "1号线", "前端引擎", "XAbsFineTuneHelper", "天车", "1", "ST713", "Pos3取料", "现场充磁异常",
            new(PhysicalConclusionCode.CommandNotSent, EvidenceAvailability.Confirmed, "首次未发送充磁命令", "异常发生在调用前"),
            new(PhysicalConclusionCode.CommandResultUnknown, EvidenceAvailability.Unknown, "充磁可能已执行", "命令响应超时"),
            EvidenceValue<bool>.Confirmed(false, "首次业务未暂停"),
            EvidenceValue<bool>.Confirmed(true, "最近业务已暂停"),
            "首次详情,含逗号\r单独CR", "最近详情\"含引号\"\n单独LF", "首次结果\r\n跨行", "最近结果", first, latest);
    }
}
