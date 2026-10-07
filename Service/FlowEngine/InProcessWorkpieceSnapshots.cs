using System;
using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 在制工件记录来自哪一种内存位置。
/// 仅用于页面分组和统计，不参与任务所有权、派发或异常恢复判断。
/// </summary>
public enum InProcessWorkpieceKind
{
    GlobalQueue,
    FrontCache,
    FrontCurrent,
    FrontCraneQueue,
    CraneTask,
    TransferRack,
    ProcessStation,
    BalancingCache,
    BalancingInTransit,
    GrindingCache,
    SourceFailure
}

/// <summary>
/// 在制工件页面的展示状态。
/// 多处记录只表示需要结合交接阶段核对，不能据此自动清理任何缓存。
/// </summary>
public enum InProcessWorkpieceStatus
{
    Normal,
    Handoff,
    SignalMismatch,
    EvidenceUnavailable,
    ManualConfirmation
}

/// <summary>
/// 一条工件位置证据的不可变副本。
/// 此类型不持有引擎、设备连接、锁、Task、回调或控制命令。
/// </summary>
public sealed record InProcessWorkpieceSnapshot(
    InProcessWorkpieceKind Kind,
    string FilterScope,
    string FlowScope,
    string Location,
    string Stage,
    WorkpieceDisplaySnapshot? Workpiece,
    string Evidence,
    InProcessWorkpieceStatus Status,
    string Detail,
    DateTime CapturedAtUtc)
{
    // 只有“版号+序号”都存在时才允许跨位置归并；任一缺失都按未知身份逐条展示。
    public bool HasKnownIdentity => Workpiece != null
        && !string.IsNullOrWhiteSpace(Workpiece.PlateNo)
        && !string.IsNullOrWhiteSpace(Workpiece.Sequence);

    /// <summary>仅供页面去重/多处记录提示；未知版号永远返回空，不会被错误合并。</summary>
    public string IdentityKey => !HasKnownIdentity
        ? string.Empty
        : $"{Workpiece!.PlateNo.Trim().ToUpperInvariant()}\u001f{Workpiece.Sequence.Trim().ToUpperInvariant()}";

    public string IdentityText => Workpiece?.IdentityText ?? "未知工件";
    public string DiameterText => Workpiece == null || Workpiece.Diameter <= 0 ? "未记录" : $"{Workpiece.Diameter:0.###}";
    public string LengthText => Workpiece == null || Workpiece.Length <= 0 ? "未记录" : $"{Workpiece.Length:0.###}";

    public string StatusText => Status switch
    {
        InProcessWorkpieceStatus.Normal => "正常",
        InProcessWorkpieceStatus.Handoff => "交接中",
        InProcessWorkpieceStatus.SignalMismatch => "信号不一致",
        InProcessWorkpieceStatus.EvidenceUnavailable => "证据不足",
        InProcessWorkpieceStatus.ManualConfirmation => "需人工确认",
        _ => "未知"
    };
}

/// <summary>
/// 在制工件页面一次刷新使用的完整内存快照。
/// 返回后只由UI读取，页面异常不得反向影响生产流程。
/// </summary>
public sealed record InProcessWorkpieceOverviewSnapshot(
    IReadOnlyList<InProcessWorkpieceSnapshot> Items,
    DateTime CapturedAtUtc);
