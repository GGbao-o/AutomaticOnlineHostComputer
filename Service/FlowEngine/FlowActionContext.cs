using System;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>自动搬运动作中已经发生的物理/软件事实；不持有设备连接，也不发送命令。</summary>
internal enum FlowActionStep
{
    PreCheck,
    SourceReserved,
    MoveXYToSource,
    FineTuneSource,
    MoveZDownToPick,
    MagnetOnSent,
    ConfirmPickup,
    HoldingWorkpiece,
    MoveZSafeWithWorkpiece,
    MoveXYToTarget,
    MoveZDownToPlace,
    MagnetOffSent,
    ConfirmPlaced,
    TargetPendingHandoff,
    NotifyDownstream,
    ReturnSafe,
    Completed
}

/// <summary>当前步骤最后一次会改变现场的命令的确定性。</summary>
internal enum FlowCommandState
{
    NotSent,
    SentAwaitingEvidence,
    Confirmed,
    ResponseUnknown
}

/// <summary>工件身份在本动作账本中的唯一归属。</summary>
internal enum FlowWorkpieceOwnership
{
    ReservedAtSource,
    OnCarrier,
    AtTargetPendingHandoff,
    Completed,
    ManualConfirmation
}

/// <summary>动作的最终业务结论。WaitRetry 仅允许尚未发送物理命令的前置等待使用。</summary>
internal enum FlowActionDisposition
{
    None,
    WaitRetry,
    PauseManual,
    Completed
}

/// <summary>用于异常页面和日志的最后已知坐标，不以缺失坐标伪造零坐标。</summary>
internal readonly record struct FlowActionPosition(int? X, int? Y, int? Z)
{
    public static FlowActionPosition Unknown => new(null, null, null);
}

/// <summary>
/// 单次搬运动作的事实账本。
/// 阶段和命令状态只能从未知推进为更完整的事实；响应未知不能被后续普通 catch 降级为“未发送”。
/// </summary>
internal sealed class FlowActionContext
{
    public FlowActionContext(string operationId, string flowScope, string deviceName,
        string workpieceIdentity, string source, string target, string sourceCacheKey)
    {
        OperationId = operationId;
        FlowScope = flowScope;
        DeviceName = deviceName;
        WorkpieceIdentity = workpieceIdentity;
        Source = source;
        Target = target;
        SourceCacheKey = sourceCacheKey;
    }

    public string OperationId { get; }
    public string FlowScope { get; }
    public string DeviceName { get; }
    public string WorkpieceIdentity { get; }
    public string Source { get; }
    public string Target { get; }
    public string SourceCacheKey { get; }
    public FlowActionStep Step { get; private set; } = FlowActionStep.PreCheck;
    public FlowCommandState CommandState { get; private set; } = FlowCommandState.NotSent;
    public FlowWorkpieceOwnership Ownership { get; private set; } = FlowWorkpieceOwnership.ReservedAtSource;
    public FlowActionDisposition Disposition { get; private set; } = FlowActionDisposition.None;
    public FlowActionPosition TargetPosition { get; private set; } = FlowActionPosition.Unknown;
    public FlowActionPosition LastKnownPosition { get; private set; } = FlowActionPosition.Unknown;
    public string Detail { get; private set; } = string.Empty;

    public void BeginStep(FlowActionStep step, FlowActionPosition targetPosition, string detail)
    {
        ThrowIfFinalized();
        Step = step;
        TargetPosition = targetPosition;
        CommandState = FlowCommandState.NotSent;
        Detail = detail;
    }

    public void MarkCommandSent() => CommandState = FlowCommandState.SentAwaitingEvidence;

    public void Confirm(FlowActionPosition lastKnownPosition, string detail)
    {
        LastKnownPosition = lastKnownPosition;
        CommandState = FlowCommandState.Confirmed;
        Detail = detail;
    }

    public void MarkCommandResponseUnknown(FlowActionPosition lastKnownPosition, string detail)
    {
        LastKnownPosition = lastKnownPosition;
        CommandState = FlowCommandState.ResponseUnknown;
        Detail = detail;
    }

    public void SetOwnership(FlowWorkpieceOwnership ownership, string detail)
    {
        Ownership = ownership;
        Detail = detail;
    }

    public void WaitRetry(string detail)
    {
        if (CommandState != FlowCommandState.NotSent)
            throw new InvalidOperationException("已发送物理命令的动作不能降级为自动等待重试。");
        Disposition = FlowActionDisposition.WaitRetry;
        Detail = detail;
    }

    public void PauseManual(string detail)
    {
        if (Disposition == FlowActionDisposition.Completed)
            throw new InvalidOperationException("已完成动作不能重新标记为人工暂停。");
        Disposition = FlowActionDisposition.PauseManual;
        Ownership = FlowWorkpieceOwnership.ManualConfirmation;
        Detail = detail;
    }

    public void Complete(string detail)
    {
        Disposition = FlowActionDisposition.Completed;
        Ownership = FlowWorkpieceOwnership.Completed;
        Step = FlowActionStep.Completed;
        Detail = detail;
    }

    private void ThrowIfFinalized()
    {
        if (Disposition is FlowActionDisposition.PauseManual or FlowActionDisposition.Completed)
            throw new InvalidOperationException("已结束的动作不能再进入新的物理步骤。");
    }
}
