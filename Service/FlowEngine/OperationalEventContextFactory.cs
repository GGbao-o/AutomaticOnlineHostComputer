using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 仅把流程调用点已经持有的局部值复制成异常证据；不得在此读取设备、缓存、数据库或文件。
/// </summary>
internal static class OperationalEventContextFactory
{
    internal sealed record OperationalPhysicalEventSite(
        string Scope,
        string Engine,
        string DeviceType,
        string DeviceNo,
        string Station,
        string ActionStage,
        string ActionId,
        string CorrelationKey,
        WorkpieceCache? Workpiece,
        string PlannedSource,
        string PlannedTarget,
        string Owner,
        string LastConfirmedLocation,
        string LastSuccessfulCheckpoint,
        string LockName = "")
    {
        public bool MonitoringAvailable { get; init; } = true;
    }

    private static readonly OperationalPhysicalEventSite DisabledPhysicalSite = new(
        Scope: "物理异常监控不可用",
        Engine: "物理异常监控不可用",
        DeviceType: "未知",
        DeviceNo: "未知",
        Station: "未知",
        ActionStage: "监控初始化失败",
        ActionId: string.Empty,
        CorrelationKey: string.Empty,
        Workpiece: null,
        PlannedSource: string.Empty,
        PlannedTarget: string.Empty,
        Owner: string.Empty,
        LastConfirmedLocation: string.Empty,
        LastSuccessfulCheckpoint: string.Empty)
    {
        MonitoringAvailable = false
    };

    internal sealed record FineTunePhysicalPhase(
        DeviceCommandEvidence MagnetOnCommand,
        EvidenceValue<int> X11LastValue,
        EvidenceValue<bool> X11ReadValid,
        EvidenceValue<int> X11Attempts,
        EvidenceValue<DateTime> X11ReadAtUtc,
        EvidenceValue<bool> HoldingWorkpiece,
        EvidenceValue<string> LastSuccessfulCheckpoint,
        string LastConfirmedLocation,
        EvidenceValue<bool> ZKnownSafe,
        EvidenceValue<int> SafeZTarget,
        EvidenceValue<int> SafeZTolerance);

    /// <summary>
    /// 后天车上料X11失败在finally完成后的最终旁路快照。
    /// 只描述调用点已经取得的结果，不参与原有收尾判断。
    /// </summary>
    internal sealed record RearLoadX11Finalization(
        RecoveryStepState MagnetOffState,
        string MagnetOffDetail,
        RecoveryStepState SafeZReturnState,
        string SafeZReturnDetail,
        RecoveryStepState RetreatState,
        string RetreatDetail,
        bool ZoneTsHeldBeforeCleanup,
        bool ZoneTsReleasedAfterward,
        bool ZoneMtHeldBeforeCleanup,
        bool ZoneMtReleasedAfterward,
        bool RearCraneHeldBeforeCleanup,
        bool RearCraneReleasedAfterward,
        bool CacheRestored,
        bool RearEnginePaused,
        bool SafetyAlarmCallbackCompleted,
        string ResultSummary);

    internal sealed record FineTunePhysicalSnapshot(
        EvidenceValue<int> X11LastValue,
        EvidenceValue<bool> X11ReadValid,
        EvidenceValue<int> X11Attempts,
        EvidenceValue<DateTime> X11ReadAtUtc,
        EvidenceValue<bool> ZKnownSafe,
        EvidenceValue<int> SafeZTarget,
        EvidenceValue<int> SafeZTolerance);

    /// <summary>只记录流程已经完成的读取/命令结果；所有更新均失败隔离且不参与业务判断。</summary>
    internal sealed class FineTunePhysicalTracker
    {
        private int _x11LastValue;
        private int _x11Attempts;
        private bool _x11Valid;
        private DateTime _x11ReadAtUtc;
        private bool _zKnownSafe;
        private int _safeZTarget;
        private int _safeZTolerance;
        private string _zReason = "调用点尚未提供Z安全检查点";

        public void TryObserveX11(bool value)
        {
            try
            {
                _x11LastValue = value ? 1 : 0;
                _x11Attempts++;
                _x11Valid = true;
                _x11ReadAtUtc = DateTime.UtcNow;
            }
            catch { }
        }

        public void TryConfirmSafeZ(int target, int tolerance, string reason)
        {
            try
            {
                _safeZTarget = target;
                _safeZTolerance = Math.Max(0, tolerance);
                _zKnownSafe = true;
                _zReason = reason;
            }
            catch { }
        }

        public void TryMarkZUnknown(string reason)
        {
            try
            {
                _zKnownSafe = false;
                _zReason = string.IsNullOrWhiteSpace(reason) ? "Z安全状态未知" : reason;
            }
            catch { }
        }

        public FineTunePhysicalSnapshot Snapshot()
        {
            try
            {
                return new FineTunePhysicalSnapshot(
                    _x11Valid
                        ? EvidenceValue<int>.Confirmed(_x11LastValue, "复用本物理周期既有X11读取结果")
                        : EvidenceValue<int>.Unknown("本物理周期尚无有效X11读取"),
                    _x11Valid
                        ? EvidenceValue<bool>.Confirmed(true, "既有X11读取成功返回")
                        : EvidenceValue<bool>.Unknown("本物理周期尚无有效X11读取"),
                    _x11Attempts > 0
                        ? EvidenceValue<int>.Confirmed(_x11Attempts, "本物理周期既有X11读取累计次数")
                        : EvidenceValue<int>.Unknown("本物理周期尚无X11读取尝试"),
                    _x11Valid
                        ? EvidenceValue<DateTime>.Confirmed(_x11ReadAtUtc, "上位机成功接收既有X11读取结果的时间")
                        : EvidenceValue<DateTime>.Unknown("本物理周期没有X11读取时间"),
                    _zKnownSafe
                        ? EvidenceValue<bool>.Confirmed(true, _zReason)
                        : EvidenceValue<bool>.Unknown(_zReason),
                    _zKnownSafe
                        ? EvidenceValue<int>.Confirmed(_safeZTarget, _zReason)
                        : EvidenceValue<int>.Unknown(_zReason),
                    _zKnownSafe
                        ? EvidenceValue<int>.Confirmed(_safeZTolerance, _zReason)
                        : EvidenceValue<int>.Unknown(_zReason));
            }
            catch
            {
                const string failed = "监控跟踪器快照失败";
                return new FineTunePhysicalSnapshot(
                    EvidenceValue<int>.Unknown(failed),
                    EvidenceValue<bool>.Unknown(failed),
                    EvidenceValue<int>.Unknown(failed),
                    EvidenceValue<DateTime>.Unknown(failed),
                    EvidenceValue<bool>.Unknown(failed),
                    EvidenceValue<int>.Unknown(failed),
                    EvidenceValue<int>.Unknown(failed));
            }
        }
    }

    private static long _fallbackActionSequence;

    public static string NewActionId(string engineCode) => TryNewActionId(engineCode);

    public static string TryNewActionId(string engineCode, Func<Guid>? guidFactory = null)
    {
        string prefix = string.IsNullOrWhiteSpace(engineCode) ? "OPERATION" : engineCode;
        try { return $"{prefix}-{(guidFactory ?? Guid.NewGuid)():N}"; }
        catch
        {
            long sequence = Interlocked.Increment(ref _fallbackActionSequence);
            return $"{prefix}-isolated-{Environment.ProcessId}-{sequence}";
        }
    }

    public static OperationalPhysicalCycleTracker? TryCreatePhysicalCycleTracker(string cycleId)
    {
        try { return new OperationalPhysicalCycleTracker(cycleId); }
        catch { return null; }
    }

    public static OperationalPhysicalCycleTracker CreatePhysicalCycleTrackerOrDisabled(
        string cycleId,
        Func<string, OperationalPhysicalCycleTracker>? factory = null)
    {
        try
        {
            Func<string, OperationalPhysicalCycleTracker> create =
                factory ?? (id => new OperationalPhysicalCycleTracker(id));
            return create(cycleId) ?? OperationalPhysicalCycleTracker.Disabled;
        }
        catch { return OperationalPhysicalCycleTracker.Disabled; }
    }

    public static OperationalPhysicalEventSite? TryCreatePhysicalSite(Func<OperationalPhysicalEventSite> factory)
    {
        try { return factory(); }
        catch { return null; }
    }

    public static OperationalPhysicalEventSite CreatePhysicalSiteOrEmpty(Func<OperationalPhysicalEventSite> factory)
    {
        try { return factory() ?? DisabledPhysicalSite; }
        catch { return DisabledPhysicalSite; }
    }

    public static FineTunePhysicalTracker? TryCreatePhysicalTracker()
    {
        try { return new FineTunePhysicalTracker(); }
        catch { return null; }
    }

    public static EvidenceValue<string> ConfirmedLocation(string value, string reason) =>
        string.IsNullOrWhiteSpace(value)
            ? EvidenceValue<string>.Unknown("调用点没有提供位置")
            : EvidenceValue<string>.Confirmed(value, reason);

    public static WorkpieceEvidence Workpiece(
        WorkpieceCache wp,
        EvidenceValue<string> source,
        EvidenceValue<string> target,
        string owner,
        string lastConfirmedLocation) =>
        Workpiece(wp, source, target, owner, Text(lastConfirmedLocation, "调用点最后确认位置"));

    public static WorkpieceEvidence Workpiece(
        WorkpieceCache wp,
        EvidenceValue<string> source,
        EvidenceValue<string> target,
        string owner,
        EvidenceValue<string> lastConfirmedLocation)
    {
        const string local = "流程调用点已有WorkpieceCache局部副本";
        return new WorkpieceEvidence(
            Text(wp.PlateNo, "WorkpieceCache.PlateNo"),
            Text(wp.Sequence, "WorkpieceCache.Sequence"),
            Positive(wp.Diameter, "WorkpieceCache.Diameter"),
            Positive(wp.Length, "WorkpieceCache.Length"),
            source,
            target,
            Text(owner, "动作所有者参数"),
            lastConfirmedLocation,
            local);
    }

    public static FineTunePhysicalPhase PickupBeforeZDown(
        FineTunePhysicalTracker? tracker,
        bool useTrackedX11,
        string lastSuccessfulCheckpoint,
        string lastConfirmedLocation)
    {
        FineTunePhysicalSnapshot snapshot = tracker?.Snapshot() ?? EmptyPhysicalSnapshot("调用点没有物理跟踪器");
        EvidenceValue<int> x11 = useTrackedX11 ? snapshot.X11LastValue : EvidenceValue<int>.Unknown("此取料点没有适用的取料前X11读数");
        EvidenceValue<bool> valid = useTrackedX11 ? snapshot.X11ReadValid : EvidenceValue<bool>.Unknown("此取料点没有适用的取料前X11读数");
        EvidenceValue<int> attempts = useTrackedX11 ? snapshot.X11Attempts : EvidenceValue<int>.Unknown("此取料点没有适用的取料前X11尝试次数");

        return new FineTunePhysicalPhase(
            new DeviceCommandEvidence(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, "本次取料充磁尚在微调之后，命令未发送"),
            x11,
            valid,
            attempts,
            useTrackedX11 ? snapshot.X11ReadAtUtc : EvidenceValue<DateTime>.Unknown("此取料点没有适用的取料前X11读取时间"),
            useTrackedX11 && x11.HasValue && x11.Value == 0
                ? EvidenceValue<bool>.Confirmed(false, "X11=0确认天车当前未持件")
                : EvidenceValue<bool>.Unknown("没有X11=1或holdingWorkpiece证据"),
            Checkpoint(lastSuccessfulCheckpoint),
            lastConfirmedLocation,
            snapshot.ZKnownSafe,
            snapshot.SafeZTarget,
            snapshot.SafeZTolerance);
    }

    public static FineTunePhysicalPhase PlacementBeforeZDown(
        bool magnetOnAcknowledged,
        bool holdingWorkpieceConfirmed,
        FineTunePhysicalTracker? tracker,
        string lastSuccessfulCheckpoint,
        string lastConfirmedLocation)
    {
        FineTunePhysicalSnapshot snapshot = tracker?.Snapshot() ?? EmptyPhysicalSnapshot("调用点没有物理跟踪器");
        DeviceCommandEvidence magnet = magnetOnAcknowledged
            ? new DeviceCommandEvidence(DeviceCommandState.Acknowledged, EvidenceAvailability.Confirmed, "本物理周期充磁调用已成功返回")
            : new DeviceCommandEvidence(DeviceCommandState.Unknown, EvidenceAvailability.Unknown, "调用点没有充磁成功返回证据");
        bool x11ConfirmsHolding = snapshot.X11ReadValid.HasValue && snapshot.X11ReadValid.Value &&
                                  snapshot.X11LastValue.HasValue && snapshot.X11LastValue.Value == 1;
        EvidenceValue<bool> holding = holdingWorkpieceConfirmed || x11ConfirmsHolding
            ? EvidenceValue<bool>.Confirmed(true, "调用点holdingWorkpiece或最后有效X11=1")
            : EvidenceValue<bool>.Unknown("充磁返回不能替代X11持件确认");

        return new FineTunePhysicalPhase(
            magnet,
            snapshot.X11LastValue,
            snapshot.X11ReadValid,
            snapshot.X11Attempts,
            snapshot.X11ReadAtUtc,
            holding,
            Checkpoint(lastSuccessfulCheckpoint),
            lastConfirmedLocation,
            snapshot.ZKnownSafe,
            snapshot.SafeZTarget,
            snapshot.SafeZTolerance);
    }

    public static OperationalEventContext FineTuneFailure(
        string scope,
        string engine,
        string deviceNo,
        string station,
        string actionStage,
        WorkpieceCache workpiece,
        EvidenceValue<string> source,
        EvidenceValue<string> target,
        string owner,
        EvidenceValue<int> targetZ,
        FineTunePhysicalPhase physicalPhase)
    {
        const string pending = "微调失败时本次Z下降尚未发送";
        OperationalEvidence unavailable = OperationalEvidence.Unavailable("微调调用点没有该组业务证据");
        var motion = new MotionAndMagnetEvidence(
            new DeviceCommandEvidence(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, pending),
            physicalPhase.ZKnownSafe.HasValue && physicalPhase.ZKnownSafe.Value
                ? EvidenceValue<bool>.Confirmed(false, $"{physicalPhase.SafeZTarget.Value}±{physicalPhase.SafeZTolerance.Value}安全检查点已成功返回")
                : EvidenceValue<bool>.Unknown(physicalPhase.ZKnownSafe.Reason),
            physicalPhase.MagnetOnCommand,
            new DeviceCommandEvidence(DeviceCommandState.NotApplicable, EvidenceAvailability.NotApplicable, "微调阶段没有执行退磁"),
            physicalPhase.X11LastValue,
            physicalPhase.X11ReadValid,
            physicalPhase.X11Attempts,
            physicalPhase.X11ReadAtUtc);
        var business = new BusinessStateEvidence(
            EvidenceAvailability.Inferred,
            "仅复制微调调用点已有物理检查点",
            physicalPhase.LastSuccessfulCheckpoint,
            EvidenceValue<string>.Unknown("调用点没有状态机修改前值"),
            EvidenceValue<string>.NotApplicable("微调监控不修改状态机"),
            EvidenceValue<string>.Unknown("调用点没有缓存修改前值"),
            EvidenceValue<string>.NotApplicable("微调监控不修改缓存"),
            EvidenceValue<string>.Confirmed(owner, "动作所有者参数"),
            EvidenceValue<string>.NotApplicable("微调监控不修改工件所有权"),
            physicalPhase.HoldingWorkpiece,
            EvidenceValue<bool>.Confirmed(false, "尚未执行本次Z下降和放料"),
            EvidenceValue<bool>.NotApplicable("微调阶段没有缓存通知"),
            new[]
            {
                new PhysicalCommitmentEvidence("Z下降命令", EvidenceValue<bool>.Confirmed(false, pending), pending),
                new PhysicalCommitmentEvidence("当前持件", physicalPhase.HoldingWorkpiece, physicalPhase.HoldingWorkpiece.Reason)
            });

        return new OperationalEventContext
        {
            EventCode = "CRANE_XY_FINE_TUNE_FAILED",
            Severity = OperationalEventSeverity.Error,
            Category = OperationalEventCategory.FineTune,
            Scope = scope,
            Engine = engine,
            DeviceType = "天车",
            DeviceNo = deviceNo,
            Station = station,
            ActionStage = actionStage,
            Title = $"{station}下降前XY绝对编码器微调失败",
            Source = nameof(XAbsFineTuneHelper),
            IndependentAction = true,
            DetailMessage = actionStage,
            PhysicalConclusion = new PhysicalConclusionEvidence(
                PhysicalConclusionCode.CommandNotSent,
                EvidenceAvailability.Confirmed,
                "微调未通过，本次Z下降命令未发送",
                pending),
            BusinessPaused = EvidenceValue<bool>.Unknown("微调助手不读取或修改引擎暂停状态；原异常继续传播"),
            Evidence = unavailable with
            {
                Workpiece = Workpiece(workpiece, source, target, owner, physicalPhase.LastConfirmedLocation),
                Position = PositionEvidence.Unavailable("Helper尚未取得位置快照") with { TargetZ = targetZ },
                MotionAndMagnet = motion,
                BusinessState = business
            }
        };
    }

    /// <summary>
    /// 物理异常的唯一失败隔离边界。字段复制、目录解析和Report全部在最外层try/catch中；
    /// 该方法无返回值，失败时不影响原业务异常、catch/finally或设备命令。
    /// </summary>
    public static void TryReportPhysical(
        IOperationalEventReporter reporter,
        string eventCode,
        OperationalPhysicalEventSite? site,
        OperationalPhysicalCycleTracker? tracker,
        Exception? exception,
        string detail,
        bool usePlannedWorkpieceEvidence,
        bool? holdingWorkpiece = null,
        bool? placed = null,
        bool? cacheNotified = null,
        bool? downstreamNotified = null,
        bool? handoffClosed = null,
        FlowActionSnapshot? actionSnapshot = null)
    {
        try
        {
            if (site is null || !site.MonitoringAvailable)
                return;
            OperationalEventDefinition definition = OperationalEventCatalog.Default.GetRequired(eventCode);
            OperationalPhysicalCycleSnapshot snapshot = tracker?.Snapshot() ??
                OperationalPhysicalCycleSnapshot.Unavailable(site.CorrelationKey, "调用点没有物理周期跟踪器");
            bool magnetOffResultUnknown = snapshot.MagnetOffCommand.State == DeviceCommandState.Unknown;
            EvidenceValue<bool> holding = magnetOffResultUnknown || snapshot.HoldingWorkpiece.HasValue
                ? snapshot.HoldingWorkpiece
                : OptionalBool(
                    holdingWorkpiece,
                    "调用点已有holdingWorkpiece/X11=1证据",
                    "调用点没有当前持件结论");
            EvidenceValue<bool> placedEvidence = magnetOffResultUnknown || snapshot.Placed.HasValue
                ? snapshot.Placed
                : OptionalBool(
                    placed,
                    "调用点已有放料承诺点",
                    "调用点没有放料承诺点结果");
            EvidenceValue<string> lastLocation = magnetOffResultUnknown || snapshot.LastConfirmedLocation.HasValue
                ? snapshot.LastConfirmedLocation
                : Text(site.LastConfirmedLocation, "调用点最后确认位置");
            WorkpieceEvidence workpiece = usePlannedWorkpieceEvidence && site.Workpiece.HasValue
                ? Workpiece(
                    site.Workpiece.Value,
                    ConfirmedLocation(site.PlannedSource, "物理动作计划来源"),
                    ConfirmedLocation(site.PlannedTarget, "物理动作计划目标"),
                    site.Owner,
                    lastLocation)
                : UnknownWorkpiece("异常涉及磁铁上既有或未知工件，不能冒充本次计划工件");
            EvidenceValue<bool> cacheEvidence = MonitorStepState(
                snapshot.MonitorSteps, OperationalMonitorStepKind.CacheNotification,
                cacheNotified, "缓存通知");
            EvidenceValue<bool> downstreamEvidence = MonitorStepState(
                snapshot.MonitorSteps, OperationalMonitorStepKind.DownstreamNotification,
                downstreamNotified, "PLC/CNC通知");
            EvidenceValue<bool> closedEvidence = PhysicalHandoffState(snapshot.MonitorSteps, handoffClosed);
            var commitments = new List<PhysicalCommitmentEvidence>
            {
                new PhysicalCommitmentEvidence("充磁命令调用", CommandReturned(snapshot.MagnetOnCommand), snapshot.MagnetOnCommand.Reason),
                new PhysicalCommitmentEvidence("X11确认持件", holding, holding.Reason),
                new PhysicalCommitmentEvidence("退磁命令返回", CommandReturned(snapshot.MagnetOffCommand), snapshot.MagnetOffCommand.Reason),
                new PhysicalCommitmentEvidence("推定已放料", placedEvidence, placedEvidence.Reason),
                new PhysicalCommitmentEvidence("缓存通知", cacheEvidence, cacheEvidence.Reason),
                new PhysicalCommitmentEvidence("PLC/CNC通知", downstreamEvidence, downstreamEvidence.Reason),
                new PhysicalCommitmentEvidence("完整物理交接闭环", closedEvidence, closedEvidence.Reason)
            };
            commitments.AddRange(snapshot.MonitorSteps.Select(step => new PhysicalCommitmentEvidence(
                $"{step.Kind}:{step.Name}",
                StepState(step),
                step.Detail)));
            OperationalEvidence unavailable = OperationalEvidence.Unavailable("物理异常调用点没有该组证据");
            var motion = new MotionAndMagnetEvidence(
                snapshot.ZDownCommand,
                snapshot.ZMayStillBeLow,
                snapshot.MagnetOnCommand,
                snapshot.MagnetOffCommand,
                snapshot.X11LastValue,
                snapshot.X11ReadValid,
                snapshot.X11Attempts,
                snapshot.X11ReadAtUtc);
            var business = new BusinessStateEvidence(
                EvidenceAvailability.Inferred,
                "仅复制异常调用点已经掌握的物理承诺点",
                snapshot.LastSuccessfulCheckpoint.HasValue
                    ? snapshot.LastSuccessfulCheckpoint
                    : Checkpoint(site.LastSuccessfulCheckpoint),
                EvidenceValue<string>.Unknown("调用点没有状态机修改前值"),
                EvidenceValue<string>.NotApplicable("监控不修改状态机"),
                snapshot.CacheBefore,
                snapshot.CacheAfter,
                Text(site.Owner, "物理动作所有者参数"),
                EvidenceValue<string>.NotApplicable("监控不修改工件所有权"),
                holding,
                placedEvidence,
                cacheEvidence,
                commitments);
            if (actionSnapshot != null)
            {
                FlowActionSnapshot action = actionSnapshot;
                workpiece = workpiece with
                {
                    Source = Text(action.Source, "动作账本来源"),
                    Target = Text(action.Target, "动作账本目标"),
                    SoftwareOwner = Text($"{action.Ownership}；步骤={action.Step}；命令={action.CommandState}", "动作账本"),
                    LastConfirmedLocation = Text(ActionLocation(action.LastKnownPosition), "动作账本最后坐标"),
                    EvidenceSource = "FlowActionContext动作账本"
                };
                motion = motion with
                {
                    X11LastValue = action.X11.HasValue
                        ? EvidenceValue<int>.Confirmed(action.X11.Value ? 1 : 0, "动作账本最后X11")
                        : EvidenceValue<int>.Unknown("动作账本尚未读取X11"),
                    X11ReadValid = OptionalBool(action.X11.HasValue, "动作账本X11读取有效性", "动作账本尚未读取X11")
                };
                business = new BusinessStateEvidence(
                    business.Availability,
                    business.Reason,
                    Text($"{action.Step}/{action.CommandState}: {action.Detail}", "动作账本当前阶段"),
                    business.StateBefore,
                    Text($"{action.Disposition}; 人工确认={action.ManualConfirmationRequired}; 原因={action.PauseReason}", "动作账本异常结论"),
                    business.CacheBefore,
                    business.CacheAfter,
                    business.OwnerBefore,
                    Text(action.Ownership.ToString(), "动作账本工件归属"),
                    business.HoldingWorkpiece,
                    business.Placed,
                    business.CacheNotified,
                    business.PhysicalCommitments.Concat(new[]
                    {
                        new PhysicalCommitmentEvidence(
                            "动作账本步骤/命令",
                            EvidenceValue<bool>.Confirmed(action.CommandState == FlowCommandState.Confirmed,
                                $"{action.Step} / {action.CommandState}：{action.Detail}"),
                            "确认表示本步骤收到设备反馈；未确认或响应未知时不得自动重试"),
                        new PhysicalCommitmentEvidence(
                            "动作账本工件归属",
                            EvidenceValue<bool>.Confirmed(action.Ownership == FlowWorkpieceOwnership.OnCarrier,
                                $"{action.Ownership}；异常结论={action.Disposition}"),
                            "用于人工确认工件在来源、载具或目标待交接的位置"),
                        new PhysicalCommitmentEvidence(
                            "人工确认是否必需",
                            EvidenceValue<bool>.Confirmed(action.ManualConfirmationRequired,
                                action.ManualConfirmationRequired ? action.PauseReason : "当前快照未进入人工确认暂停"),
                            "需要人工确认时不能通过清空PLC状态直接恢复自动派发")
                    }));
            }
            LockEvidence locks = string.IsNullOrWhiteSpace(site.LockName)
                ? new LockEvidence(EvidenceAvailability.Unknown, "调用点没有锁证据", Array.Empty<LockItemEvidence>())
                : new LockEvidence(
                    EvidenceAvailability.Unknown,
                    "异常发生在业务try/catch内，finally尚未完成，不能提前断言锁已释放",
                    new[]
                    {
                        new LockItemEvidence(
                            site.LockName,
                            EvidenceValue<bool>.Unknown("catch时可能仍持有或正在等待finally"),
                            EvidenceValue<bool>.Unknown("finally尚未执行完成，释放结果未知"),
                            "确认相关设备和工件已离开碰撞区域后，再核对锁最终状态")
                    });
            if (actionSnapshot != null)
            {
                locks = new LockEvidence(
                    EvidenceAvailability.Confirmed,
                    "动作账本在异常点登记的实际软件锁；释放结果需以finally后的最终事件为准",
                    actionSnapshot.HeldLocks.Select(lockName => new LockItemEvidence(
                        lockName,
                        EvidenceValue<bool>.Confirmed(true, "异常点动作账本仍登记持有"),
                        EvidenceValue<bool>.Unknown("当前事件在finally前生成，释放结果尚未知"),
                        actionSnapshot.ManualConfirmationRequired
                            ? "暂停后请人工确认现场，再执行对应应急处置"
                            : "按正常动作收尾")));
            }
            OperatorGuidance guidance = new(
                EvidenceAvailability.Confirmed,
                "来自稳定事件目录",
                definition.RequiredActions,
                definition.ForbiddenActions,
                definition.ContinueCondition);
            PhysicalConclusionEvidence conclusion = Conclusion(eventCode, detail);

            reporter.Report(new OperationalEventContext
            {
                EventCode = eventCode,
                Severity = definition.DefaultSeverity,
                Category = definition.DefaultCategory,
                Scope = site.Scope,
                Engine = site.Engine,
                DeviceType = site.DeviceType,
                DeviceNo = site.DeviceNo,
                Station = site.Station,
                ActionStage = site.ActionStage,
                Title = definition.Title,
                Source = nameof(OperationalEventContextFactory),
                ActionId = site.ActionId,
                CorrelationKey = site.CorrelationKey,
                IndependentAction = true,
                DetailMessage = detail,
                Result = exception is null ? "原业务路径继续执行既有分支" : "原异常路径保持不变",
                CapturedException = exception,
                PhysicalConclusion = conclusion,
                BusinessPaused = EvidenceValue<bool>.Unknown("调用点不为监控额外读取或修改暂停状态"),
                Evidence = unavailable with
                {
                    Workpiece = workpiece,
                    Position = unavailable.Position with { TargetZ = snapshot.TargetZ },
                    MotionAndMagnet = motion,
                    BusinessState = business,
                    Locks = locks,
                    Guidance = guidance
                }
            });
        }
        catch
        {
            // 证据构造、目录和Reporter任何失败都不得影响生产流程。
        }
    }

    /// <summary>
    /// 在后天车上料X11三次失败的原finally完成后记录唯一最终事件。
    /// 该方法只复制快照，不读取设备、不改变状态，也不抛异常回业务流程。
    /// </summary>
    public static void TryReportRearLoadX11FinalFailure(
        IOperationalEventReporter reporter,
        OperationalPhysicalEventSite site,
        OperationalPhysicalCycleTracker tracker,
        RearLoadX11Finalization finalization,
        Exception? exception,
        FlowActionSnapshot? actionSnapshot = null)
    {
        try
        {
            if (!site.MonitoringAvailable) return;

            OperationalEventDefinition definition = OperationalEventCatalog.Default.GetRequired("CRANE_X11_NOT_CONFIRMED");
            OperationalPhysicalCycleSnapshot snapshot = tracker.Snapshot();
            WorkpieceEvidence workpiece = site.Workpiece.HasValue
                ? Workpiece(
                    site.Workpiece.Value,
                    ConfirmedLocation(site.PlannedSource, "物理动作计划来源"),
                    ConfirmedLocation(site.PlannedTarget, "物理动作计划目标"),
                    site.Owner,
                    snapshot.LastConfirmedLocation)
                : UnknownWorkpiece("后天车上料调用点没有计划工件");
            if (actionSnapshot != null)
            {
                workpiece = workpiece with
                {
                    Source = Text(actionSnapshot.Source, "动作账本来源"),
                    Target = Text(actionSnapshot.Target, "动作账本目标"),
                    SoftwareOwner = Text($"{actionSnapshot.Ownership}；步骤={actionSnapshot.Step}；命令={actionSnapshot.CommandState}", "动作账本"),
                    LastConfirmedLocation = Text(ActionLocation(actionSnapshot.LastKnownPosition), "动作账本最后坐标"),
                    EvidenceSource = "FlowActionContext动作账本"
                };
            }

            var recoverySteps = new[]
            {
                new RecoveryStepEvidence("异常收尾退磁", finalization.MagnetOffState, finalization.MagnetOffDetail),
                new RecoveryStepEvidence("异常收尾Z回安全", finalization.SafeZReturnState, finalization.SafeZReturnDetail),
                new RecoveryStepEvidence("安全Z后X退避", finalization.RetreatState, finalization.RetreatDetail)
            };
            bool recoveryComplete = recoverySteps.All(step =>
                step.State is RecoveryStepState.Succeeded or RecoveryStepState.Skipped);
            EvidenceValue<bool> cacheRestored = finalization.CacheRestored
                ? EvidenceValue<bool>.Confirmed(true, "原业务finally已按到达顺序恢复中转架缓存")
                : EvidenceValue<bool>.Unknown("最终事件调用点没有缓存恢复成功证据");
            EvidenceValue<string> cacheAfter = finalization.CacheRestored
                ? EvidenceValue<string>.Confirmed("中转架缓存已按原到达顺序恢复", "原业务catch完成缓存恢复")
                : EvidenceValue<string>.Unknown("最终事件调用点没有缓存恢复成功证据");
            var commitments = new[]
            {
                new PhysicalCommitmentEvidence("X11确认持件", EvidenceValue<bool>.Confirmed(false, "三次有效X11读取均返回0"), "只能确认未确认持件，不能推断工件仍在来源位"),
                new PhysicalCommitmentEvidence("退磁命令返回", CommandReturned(snapshot.MagnetOffCommand), snapshot.MagnetOffCommand.Reason),
                new PhysicalCommitmentEvidence("Z回安全", snapshot.ZKnownSafe, snapshot.ZKnownSafe.Reason),
                new PhysicalCommitmentEvidence("ZoneTS释放", LockReleased(finalization.ZoneTsHeldBeforeCleanup, finalization.ZoneTsReleasedAfterward, "ZoneTS"), "最终动作租约状态"),
                new PhysicalCommitmentEvidence("ZoneMT释放", LockReleased(finalization.ZoneMtHeldBeforeCleanup, finalization.ZoneMtReleasedAfterward, "ZoneMT"), "最终动作租约状态"),
                new PhysicalCommitmentEvidence("后天车锁释放", LockReleased(finalization.RearCraneHeldBeforeCleanup, finalization.RearCraneReleasedAfterward, "RearCrane"), "最终动作租约状态"),
                new PhysicalCommitmentEvidence("整线暂停告警", finalization.SafetyAlarmCallbackCompleted
                    ? EvidenceValue<bool>.Confirmed(true, "后天车安全告警回调已完成")
                    : EvidenceValue<bool>.Unknown("安全告警回调没有完成证据"), "回调完成后由主页面同步暂停对应整线")
            };
            var business = new BusinessStateEvidence(
                EvidenceAvailability.Confirmed,
                "最终事件只复制原finally已经完成的业务收尾事实",
                snapshot.LastSuccessfulCheckpoint,
                EvidenceValue<string>.Unknown("调用点没有保存状态机修改前值"),
                EvidenceValue<string>.Confirmed("Idle/人工确认暂停", "X11失败原业务分支已恢复缓存并设置暂停"),
                snapshot.CacheBefore,
                cacheAfter,
                Text(site.Owner, "物理动作所有者参数"),
                EvidenceValue<string>.NotApplicable("X11=0未确认工件已离开中转架"),
                EvidenceValue<bool>.Confirmed(false, "三次有效X11读取均为0"),
                EvidenceValue<bool>.Confirmed(false, "X11未确认持件，未执行放料"),
                cacheRestored,
                commitments);
            if (actionSnapshot != null)
            {
                business = new BusinessStateEvidence(
                    business.Availability,
                    business.Reason,
                    Text($"{actionSnapshot.Step}/{actionSnapshot.CommandState}: {actionSnapshot.Detail}", "动作账本当前阶段"),
                    business.StateBefore,
                    Text($"{actionSnapshot.Disposition}; 人工确认={actionSnapshot.ManualConfirmationRequired}; 原因={actionSnapshot.PauseReason}", "动作账本异常结论"),
                    business.CacheBefore,
                    business.CacheAfter,
                    business.OwnerBefore,
                    Text(actionSnapshot.Ownership.ToString(), "动作账本工件归属"),
                    business.HoldingWorkpiece,
                    business.Placed,
                    business.CacheNotified,
                    business.PhysicalCommitments);
            }
            var locks = new LockEvidence(
                EvidenceAvailability.Confirmed,
                "最终事件在原finally锁释放调用完成后生成",
                new[]
                {
                    LockItem("ZoneTS", finalization.ZoneTsHeldBeforeCleanup, finalization.ZoneTsReleasedAfterward),
                    LockItem("ZoneMT", finalization.ZoneMtHeldBeforeCleanup, finalization.ZoneMtReleasedAfterward),
                    LockItem("RearCrane", finalization.RearCraneHeldBeforeCleanup, finalization.RearCraneReleasedAfterward)
                });
            var recovery = new RecoveryEvidence(
                EvidenceAvailability.Confirmed,
                "最终事件在原finally恢复和锁释放完成后生成",
                EvidenceValue<bool>.Confirmed(true, "X11三次失败后的异常收尾已进入finally"),
                recoveryComplete
                    ? EvidenceValue<bool>.Confirmed(true, "所有适用收尾步骤成功或按安全策略跳过")
                    : EvidenceValue<bool>.Confirmed(false, "至少一个收尾步骤失败或结果未知"),
                recoverySteps,
                new[]
                {
                    finalization.ResultSummary,
                    finalization.RearEnginePaused
                        ? "后端引擎已设置暂停"
                        : "后端引擎暂停状态未取得确认"
                },
                "异常收尾结果只说明软件已执行的步骤，不等于现场物理状态已安全确认");
            OperatorGuidance guidance = new(
                new[]
                {
                    "人工确认中转架、磁铁、Z轴、后天车和工件实际位置。",
                    "确认锁释放和整线暂停状态后，再按现场应急流程处理。"
                },
                new[]
                {
                    "禁止把X11=0解释为工件仍在中转架。",
                    "禁止在物理位置未确认时直接重新启动或重放取料命令。"
                },
                "人工确认物理状态和业务条件后，才允许按既有应急流程继续。");

            OperationalEvidence unavailable = OperationalEvidence.Unavailable("后天车X11最终事件只复制本动作快照");
            reporter.Report(new OperationalEventContext
            {
                EventCode = "CRANE_X11_NOT_CONFIRMED",
                Severity = definition.DefaultSeverity,
                Category = definition.DefaultCategory,
                Scope = site.Scope,
                Engine = site.Engine,
                DeviceType = site.DeviceType,
                DeviceNo = site.DeviceNo,
                Station = site.Station,
                ActionStage = $"{site.Station}三次X11均未确认持件（最终收尾）",
                Title = definition.Title,
                Source = nameof(OperationalEventContextFactory),
                ActionId = site.ActionId,
                CorrelationKey = site.CorrelationKey,
                IndependentAction = true,
                DetailMessage = $"三次有效X11读取均返回0；最终收尾：{finalization.ResultSummary}",
                Result = "原业务已完成异常收尾并保持整线人工暂停",
                CapturedException = exception,
                PhysicalConclusion = new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.ManualConfirmationRequired,
                    EvidenceAvailability.Confirmed,
                    "X11未确认持件，最终收尾结果已记录但现场仍需人工确认",
                    finalization.ResultSummary),
                BusinessPaused = finalization.RearEnginePaused
                    ? EvidenceValue<bool>.Confirmed(true, "后端引擎已设置暂停")
                    : EvidenceValue<bool>.Unknown("后端引擎暂停状态未取得确认"),
                Evidence = unavailable with
                {
                    Workpiece = workpiece,
                    Position = unavailable.Position with
                    {
                        TargetZ = snapshot.TargetZ,
                        FailureStage = EvidenceValue<string>.Confirmed("X11三次有效返回0后最终收尾", "最终事件调用点")
                    },
                    MotionAndMagnet = new MotionAndMagnetEvidence(
                        snapshot.ZDownCommand,
                        snapshot.ZMayStillBeLow,
                        snapshot.MagnetOnCommand,
                        snapshot.MagnetOffCommand,
                        actionSnapshot?.X11 is bool x11
                            ? EvidenceValue<int>.Confirmed(x11 ? 1 : 0, "动作账本最后X11")
                            : snapshot.X11LastValue,
                        actionSnapshot?.X11.HasValue == true
                            ? EvidenceValue<bool>.Confirmed(true, "动作账本X11读取有效性")
                            : snapshot.X11ReadValid,
                        snapshot.X11Attempts,
                        snapshot.X11ReadAtUtc),
                    BusinessState = business,
                    Locks = locks,
                    Recovery = recovery,
                    Guidance = guidance
                }
            });
        }
        catch
        {
            // 监控快照失败不得影响原finally、锁释放、暂停或弹窗。
        }
    }

    private static LockItemEvidence LockItem(string name, bool heldBeforeCleanup, bool releasedAfterward) =>
        new(
            name,
            heldBeforeCleanup
                ? EvidenceValue<bool>.Confirmed(true, "最终收尾开始时动作租约持有该锁")
                : EvidenceValue<bool>.NotApplicable("最终收尾开始时动作租约未持有该锁"),
            heldBeforeCleanup
                ? EvidenceValue<bool>.Confirmed(releasedAfterward, releasedAfterward ? "最终动作租约已不再持有该锁" : "最终收尾后仍持有该锁")
                : EvidenceValue<bool>.NotApplicable("最终收尾开始时动作租约未持有该锁"),
            releasedAfterward ? "仍需按现场流程确认锁和设备位置" : "禁止继续派发动作");

    private static EvidenceValue<bool> LockReleased(bool heldBeforeCleanup, bool releasedAfterward, string name) =>
        heldBeforeCleanup
            ? EvidenceValue<bool>.Confirmed(releasedAfterward, $"{name}最终动作租约状态")
            : EvidenceValue<bool>.NotApplicable($"最终收尾开始时未持有{name}");

    private static PhysicalConclusionEvidence Conclusion(string eventCode, string detail) => eventCode switch
    {
        "CRANE_X11_UNEXPECTED_WORKPIECE" => new(
            PhysicalConclusionCode.WorkpieceConfirmedHeld,
            EvidenceAvailability.Confirmed,
            "X11=1确认磁铁上存在工件，但身份未知",
            "取料前既有X11读取成功返回1；不得绑定本次计划工件"),
        "CRANE_X11_NOT_CONFIRMED" => new(
            PhysicalConclusionCode.ManualConfirmationRequired,
            EvidenceAvailability.Confirmed,
            "X11连续有效读取仍未确认持件",
            "只能确认X11未确认持件，不能推断工件仍在来源位"),
        "PHYSICAL_HANDOFF_NOT_CLOSED" => new(
            PhysicalConclusionCode.ManualConfirmationRequired,
            EvidenceAvailability.Unknown,
            "退磁或放料后的通知闭环不完整",
            detail),
        _ => new(
            PhysicalConclusionCode.CommandResultUnknown,
            EvidenceAvailability.Unknown,
            "设备命令或传感器结果未知",
            detail)
    };

    private static WorkpieceEvidence UnknownWorkpiece(string reason) => new(
        EvidenceValue<string>.Unknown(reason),
        EvidenceValue<string>.Unknown(reason),
        EvidenceValue<double>.Unknown(reason),
        EvidenceValue<double>.Unknown(reason),
        EvidenceValue<string>.Unknown(reason),
        EvidenceValue<string>.Unknown(reason),
        EvidenceValue<string>.Unknown(reason),
        EvidenceValue<string>.Unknown(reason),
        reason);

    private static string ActionLocation(FlowActionPosition position) =>
        $"X={position.X?.ToString() ?? "未知"} Y={position.Y?.ToString() ?? "未知"} Z={position.Z?.ToString() ?? "未知"}";

    private static EvidenceValue<bool> OptionalBool(bool? value, string confirmedReason, string unknownReason) =>
        value.HasValue
            ? EvidenceValue<bool>.Confirmed(value.Value, confirmedReason)
            : EvidenceValue<bool>.Unknown(unknownReason);

    private static EvidenceValue<bool> CommandReturned(DeviceCommandEvidence command) =>
        command.State == DeviceCommandState.Acknowledged
            ? EvidenceValue<bool>.Confirmed(true, command.Reason)
            : command.State == DeviceCommandState.NotSent
                ? EvidenceValue<bool>.Confirmed(false, command.Reason)
                : EvidenceValue<bool>.Unknown(command.Reason);

    private static EvidenceValue<bool> MonitorStepState(
        IReadOnlyList<OperationalMonitorStepEvidence> steps,
        OperationalMonitorStepKind kind,
        bool? legacyValue,
        string label)
    {
        OperationalMonitorStepEvidence[] matching = steps.Where(step => step.Kind == kind).ToArray();
        if (matching.Length == 0)
            return OptionalBool(legacyValue, $"调用点已有{label}结果", $"调用点没有{label}结果");
        if (matching.Any(step => step.State == OperationalMonitorStepState.StartedResultUnknown))
            return EvidenceValue<bool>.Unknown($"{label}调用已开始但未取得成功返回：{string.Join("；", matching.Select(step => $"{step.Name}={step.Detail}"))}");
        if (matching.All(step => step.State == OperationalMonitorStepState.Succeeded))
            return EvidenceValue<bool>.Confirmed(true, $"{label}全部成功返回：{string.Join("；", matching.Select(step => step.Name))}");
        return EvidenceValue<bool>.Confirmed(false, $"{label}尚未开始：{string.Join("；", matching.Where(step => step.State == OperationalMonitorStepState.NotStarted).Select(step => step.Name))}");
    }

    private static EvidenceValue<bool> PhysicalHandoffState(
        IReadOnlyList<OperationalMonitorStepEvidence> steps,
        bool? legacyValue)
    {
        OperationalMonitorStepEvidence[] prerequisites = steps
            .Where(step => step.Kind != OperationalMonitorStepKind.PhysicalHandoff)
            .ToArray();
        OperationalMonitorStepEvidence[] notStarted = prerequisites
            .Where(step => step.State == OperationalMonitorStepState.NotStarted)
            .ToArray();
        if (notStarted.Length > 0)
        {
            return EvidenceValue<bool>.Confirmed(false,
                $"完整物理交接尚未开始全部预期步骤：{string.Join("；", notStarted.Select(step => step.Name))}");
        }

        OperationalMonitorStepEvidence[] startedUnknown = prerequisites
            .Where(step => step.State == OperationalMonitorStepState.StartedResultUnknown)
            .ToArray();
        if (startedUnknown.Length > 0)
        {
            return EvidenceValue<bool>.Unknown(
                $"完整物理交接存在已开始但结果未知的步骤：{string.Join("；", startedUnknown.Select(step => step.Name))}");
        }

        return MonitorStepState(
            steps,
            OperationalMonitorStepKind.PhysicalHandoff,
            legacyValue,
            "完整物理交接闭环");
    }

    private static EvidenceValue<bool> StepState(OperationalMonitorStepEvidence step) => step.State switch
    {
        OperationalMonitorStepState.Succeeded => EvidenceValue<bool>.Confirmed(true, step.Detail),
        OperationalMonitorStepState.StartedResultUnknown => EvidenceValue<bool>.Unknown(step.Detail),
        _ => EvidenceValue<bool>.Confirmed(false, step.Detail)
    };

    private static EvidenceValue<string> Text(string? value, string source) =>
        string.IsNullOrWhiteSpace(value)
            ? EvidenceValue<string>.Unknown($"{source}为空，调用点未提供")
            : EvidenceValue<string>.Confirmed(value, source);

    private static EvidenceValue<double> Positive(double value, string source) =>
        value > 0
            ? EvidenceValue<double>.Confirmed(value, source)
            : EvidenceValue<double>.Unknown($"{source}<=0，调用点未提供有效值");

    private static EvidenceValue<string> Checkpoint(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? EvidenceValue<string>.Unknown("调用点没有最后成功检查点")
            : EvidenceValue<string>.Confirmed(value, "调用点已有成功返回");

    private static FineTunePhysicalSnapshot EmptyPhysicalSnapshot(string reason) => new(
        EvidenceValue<int>.Unknown(reason),
        EvidenceValue<bool>.Unknown(reason),
        EvidenceValue<int>.Unknown(reason),
        EvidenceValue<DateTime>.Unknown(reason),
        EvidenceValue<bool>.Unknown(reason),
        EvidenceValue<int>.Unknown(reason),
        EvidenceValue<int>.Unknown(reason));
}
