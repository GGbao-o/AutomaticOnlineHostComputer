using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 仅把流程调用点已经持有的局部值复制成异常证据；不得在此读取设备、缓存、数据库或文件。
/// </summary>
internal static class OperationalEventContextFactory
{
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

    public static string NewActionId(string engineCode) =>
        $"{engineCode}-{Guid.NewGuid():N}";

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
        string lastConfirmedLocation)
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
            Text(lastConfirmedLocation, "调用点最后确认位置"),
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
