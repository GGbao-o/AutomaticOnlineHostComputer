using System;
using System.Collections.Generic;
using System.Linq;

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
    FineTuneTarget,
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
    PauseSourceReserved,
    PauseOnCarrier,
    PauseAtTargetPendingHandoff,
    Completed
}

/// <summary>
/// 人工核实现场后，对“来源缓存仍被动作占用”的唯一结案结论。
/// 不允许用“清空缓存”替代人工结论，否则下一轮自动任务会丢失工件去向。
/// </summary>
public enum FlowActionManualResolution
{
    StillAtSource,
    OnCarrier,
    AtTargetPendingHandoff,
    RemovedManually
}

/// <summary>用于异常页面和日志的最后已知坐标，不以缺失坐标伪造零坐标。</summary>
internal readonly record struct FlowActionPosition(int? X, int? Y, int? Z)
{
    public static FlowActionPosition Unknown => new(null, null, null);
}

/// <summary>单次动作在异常页面、任务牌和人工处置中使用的不可变事实快照。</summary>
internal sealed record FlowActionSnapshot(
    string OperationId,
    string FlowScope,
    string DeviceName,
    string WorkpieceIdentity,
    string Source,
    string Target,
    string SourceCacheKey,
    FlowActionStep Step,
    FlowCommandState CommandState,
    FlowWorkpieceOwnership Ownership,
    FlowActionDisposition Disposition,
    FlowActionPosition TargetPosition,
    FlowActionPosition LastKnownPosition,
    bool? X11,
    bool? MagnetOn,
    IReadOnlyList<string> HeldLocks,
    bool ManualConfirmationRequired,
    string Detail,
    string PauseReason);

/// <summary>
/// 单次搬运动作的事实账本。
/// 阶段和命令状态只能从未知推进为更完整的事实；响应未知不能被后续普通 catch 降级为“未发送”。
/// </summary>
internal sealed class FlowActionContext
{
    private readonly HashSet<string> _heldLocks = new(StringComparer.Ordinal);

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
    public string Target { get; private set; }
    public string SourceCacheKey { get; }
    public FlowActionStep Step { get; private set; } = FlowActionStep.PreCheck;
    public FlowCommandState CommandState { get; private set; } = FlowCommandState.NotSent;
    public FlowWorkpieceOwnership Ownership { get; private set; } = FlowWorkpieceOwnership.ReservedAtSource;
    public FlowActionDisposition Disposition { get; private set; } = FlowActionDisposition.None;
    public FlowActionPosition TargetPosition { get; private set; } = FlowActionPosition.Unknown;
    public FlowActionPosition LastKnownPosition { get; private set; } = FlowActionPosition.Unknown;
    public bool? X11 { get; private set; }
    public bool? MagnetOn { get; private set; }
    public bool ManualConfirmationRequired { get; private set; }
    /// <summary>动作已经正常闭环或已转入人工处置，catch/finally 不能再改写其结论。</summary>
    public bool IsFinalized => Disposition is FlowActionDisposition.PauseSourceReserved
        or FlowActionDisposition.PauseOnCarrier
        or FlowActionDisposition.PauseAtTargetPendingHandoff
        or FlowActionDisposition.Completed;
    public string PauseReason { get; private set; } = string.Empty;
    public string Detail { get; private set; } = string.Empty;

    /// <summary>只登记当前动作实际已经取得的锁；释放时同步删掉，快照不会伪称仍持有。</summary>
    public void RegisterLock(string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        _heldLocks.Add(lockName);
    }

    public bool TryReleaseLock(string lockName) => _heldLocks.Remove(lockName);

    /// <summary>写入已从设备读取/已确认的事实；null 表示本次没有该类反馈而非 false。</summary>
    public void RecordFeedback(FlowActionPosition lastKnownPosition, bool? x11, bool? magnetOn, string detail)
    {
        if (lastKnownPosition != FlowActionPosition.Unknown)
            LastKnownPosition = lastKnownPosition;
        if (x11.HasValue) X11 = x11;
        if (magnetOn.HasValue) MagnetOn = magnetOn;
        Detail = detail;
    }

    public FlowActionSnapshot Snapshot() => new(
        OperationId, FlowScope, DeviceName, WorkpieceIdentity, Source, Target, SourceCacheKey,
        Step, CommandState, Ownership, Disposition, TargetPosition, LastKnownPosition,
        X11, MagnetOn, _heldLocks.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        ManualConfirmationRequired, Detail, PauseReason);

    public void BeginStep(FlowActionStep step, FlowActionPosition targetPosition, string detail)
    {
        ThrowIfFinalized();
        Step = step;
        TargetPosition = targetPosition;
        CommandState = FlowCommandState.NotSent;
        Detail = detail;
    }

    /// <summary>尚未取得可用坐标时的阶段标记；不会伪造零坐标。</summary>
    public void BeginStep(FlowActionStep step) => BeginStep(step, FlowActionPosition.Unknown, Detail);

    public void MarkCommandSent() => CommandState = FlowCommandState.SentAwaitingEvidence;

    public void Confirm(FlowActionPosition lastKnownPosition, string detail)
    {
        LastKnownPosition = lastKnownPosition;
        CommandState = FlowCommandState.Confirmed;
        Detail = detail;
    }

    /// <summary>调用方没有新位置快照时，只确认命令已正常返回。</summary>
    public void Confirm() => Confirm(LastKnownPosition, Detail);

    public void MarkCommandResponseUnknown(FlowActionPosition lastKnownPosition, string detail)
    {
        LastKnownPosition = lastKnownPosition;
        CommandState = FlowCommandState.ResponseUnknown;
        Detail = detail;
    }

    public void MarkCommandResponseUnknown(string detail) => MarkCommandResponseUnknown(LastKnownPosition, detail);

    public void SetOwnership(FlowWorkpieceOwnership ownership, string detail)
    {
        Ownership = ownership;
        Detail = detail;
    }

    public void SetOwnership(FlowWorkpieceOwnership ownership) => SetOwnership(ownership, Detail);

    /// <summary>仅用于“多个物理空位中运行时选择实际目标”的动作；选择完成后快照必须展示真实站号。</summary>
    public void SetTarget(string target, string detail)
    {
        ThrowIfFinalized();
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Target = target;
        Detail = detail;
    }

    public void WaitRetry(string detail)
    {
        if (CommandState != FlowCommandState.NotSent)
            throw new InvalidOperationException("已发送物理命令的动作不能降级为自动等待重试。");
        Disposition = FlowActionDisposition.WaitRetry;
        Detail = detail;
    }

    /// <summary>
    /// 已发送物理命令后发生超时、下压或响应未知时的统一结论。
    /// 结论由已经确认的工件归属决定，不能把“天车持件”伪造回来源位。
    /// </summary>
    public void PauseForManualResolution(string detail)
    {
        if (Disposition == FlowActionDisposition.Completed)
            throw new InvalidOperationException("已完成动作不能重新标记为人工暂停。");
        Disposition = Ownership switch
        {
            FlowWorkpieceOwnership.ReservedAtSource or FlowWorkpieceOwnership.ManualConfirmation
                => FlowActionDisposition.PauseSourceReserved,
            FlowWorkpieceOwnership.OnCarrier
                => FlowActionDisposition.PauseOnCarrier,
            FlowWorkpieceOwnership.AtTargetPendingHandoff
                => FlowActionDisposition.PauseAtTargetPendingHandoff,
            _ => throw new InvalidOperationException("已完成工件不能重新标记为人工暂停。")
        };
        ManualConfirmationRequired = true;
        PauseReason = detail;
        Detail = detail;
        // catch 返回后上下文会离开调用栈。保存不可变快照供异常监控与应急中心读取，
        // 但不在这里做任何设备、缓存或锁的“猜测性恢复”。
        FlowActionManualRegistry.Record(Snapshot());
    }

    [Obsolete("请使用 PauseForManualResolution，使异常结论与工件归属一致。")]
    public void PauseManual(string detail) => PauseForManualResolution(detail);

    public void Complete(string detail)
    {
        Disposition = FlowActionDisposition.Completed;
        Ownership = FlowWorkpieceOwnership.Completed;
        Step = FlowActionStep.Completed;
        Detail = detail;
        FlowActionManualRegistry.MarkCompleted(OperationId);
    }

    private void ThrowIfFinalized()
    {
        if (IsFinalized)
            throw new InvalidOperationException("已结束的动作不能再进入新的物理步骤。");
    }
}
