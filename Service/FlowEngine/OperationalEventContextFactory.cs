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
        string LastConfirmedLocation);

    public static string NewActionId(string engineCode) =>
        $"{engineCode}-{Guid.NewGuid():N}";

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
        bool x11ConfirmedEmpty,
        string lastSuccessfulCheckpoint,
        string lastConfirmedLocation)
    {
        EvidenceValue<int> x11 = x11ConfirmedEmpty
            ? EvidenceValue<int>.Confirmed(0, "本物理周期取料前已有X11=0有效读取")
            : EvidenceValue<int>.Unknown("本调用点没有可复用的X11前置读数");
        EvidenceValue<bool> valid = x11ConfirmedEmpty
            ? EvidenceValue<bool>.Confirmed(true, "X11读取成功")
            : EvidenceValue<bool>.Unknown("未在此物理周期取得X11证据");
        EvidenceValue<int> attempts = x11ConfirmedEmpty
            ? EvidenceValue<int>.Confirmed(1, "调用点已有一次有效读取")
            : EvidenceValue<int>.Unknown("调用点没有X11尝试次数");

        return new FineTunePhysicalPhase(
            new DeviceCommandEvidence(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, "本次取料充磁尚在微调之后，命令未发送"),
            x11,
            valid,
            attempts,
            EvidenceValue<DateTime>.Unknown("调用点没有保留X11原始读取时间"),
            x11ConfirmedEmpty
                ? EvidenceValue<bool>.Confirmed(false, "X11=0确认天车当前未持件")
                : EvidenceValue<bool>.Unknown("没有X11=1或holdingWorkpiece证据"),
            Checkpoint(lastSuccessfulCheckpoint),
            lastConfirmedLocation);
    }

    public static FineTunePhysicalPhase PlacementBeforeZDown(
        bool magnetOnAcknowledged,
        bool holdingWorkpieceConfirmed,
        string lastSuccessfulCheckpoint,
        string lastConfirmedLocation)
    {
        DeviceCommandEvidence magnet = magnetOnAcknowledged
            ? new DeviceCommandEvidence(DeviceCommandState.Acknowledged, EvidenceAvailability.Confirmed, "本物理周期充磁调用已成功返回")
            : new DeviceCommandEvidence(DeviceCommandState.Unknown, EvidenceAvailability.Unknown, "调用点没有充磁成功返回证据");
        EvidenceValue<bool> holding = holdingWorkpieceConfirmed
            ? EvidenceValue<bool>.Confirmed(true, "调用点holdingWorkpiece或最后有效X11=1")
            : EvidenceValue<bool>.Unknown("充磁返回不能替代X11持件确认");

        return new FineTunePhysicalPhase(
            magnet,
            holdingWorkpieceConfirmed
                ? EvidenceValue<int>.Confirmed(1, "调用点已有最后有效X11=1/holdingWorkpiece确认")
                : EvidenceValue<int>.Unknown("调用点没有保留最后有效X11值"),
            holdingWorkpieceConfirmed
                ? EvidenceValue<bool>.Confirmed(true, "调用点持件证据有效")
                : EvidenceValue<bool>.Unknown("调用点没有X11有效性快照"),
            EvidenceValue<int>.Unknown("调用点没有保留X11尝试次数"),
            EvidenceValue<DateTime>.Unknown("调用点没有保留X11读取时间"),
            holding,
            Checkpoint(lastSuccessfulCheckpoint),
            lastConfirmedLocation);
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
            EvidenceValue<bool>.Unknown("等待Helper用已有Z状态与安全目标判断"),
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
}
