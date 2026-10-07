using System;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 页面内存快照的新鲜度规则。该规则只决定页面是否把旧值标为“当前证据”，
/// 不得用于派发、运动、握手、缓存或任何设备控制判断。
/// </summary>
public static class DisplaySnapshotFreshness
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    public static bool IsFresh(DateTime capturedAtUtc, DateTime nowUtc)
    {
        if (capturedAtUtc == default) return false;
        TimeSpan age = nowUtc - capturedAtUtc;
        return age >= TimeSpan.Zero && age <= MaxAge;
    }
}

/// <summary>
/// 全流程状态页使用的只读工件信息副本。该类型不持有设备连接、锁或业务回调。
/// </summary>
public sealed record WorkpieceDisplaySnapshot(
    string PlateNo,
    string Sequence,
    double Diameter,
    double Length,
    string Process)
{
    public string IdentityText => string.IsNullOrWhiteSpace(PlateNo)
        ? "版号=未知"
        : $"版号={PlateNo} 序号={Sequence}";

    public static WorkpieceDisplaySnapshot? From(WorkpieceCache? workpiece)
    {
        if (!workpiece.HasValue) return null;
        var value = workpiece.Value;
        return new WorkpieceDisplaySnapshot(
            value.PlateNo,
            value.Sequence,
            value.Diameter,
            value.Length,
            value.SkewBedProcess);
    }

    public static WorkpieceDisplaySnapshot From(WorkpieceCache workpiece)
        => From((WorkpieceCache?)workpiece)!;
}

/// <summary>五台天车在当前流程中的只读任务快照。</summary>
public sealed record CraneTaskSnapshot(
    bool IsActive,
    int CraneNo,
    string CraneName,
    WorkpieceDisplaySnapshot? Workpiece,
    string Stage,
    string SourceStation,
    string TargetStation,
    bool NeedsManualConfirmation,
    string ManualConfirmationText,
    DateTime UpdatedAtUtc)
{
    public static CraneTaskSnapshot Idle(int craneNo, string craneName)
        => new(false, craneNo, craneName, null, "空闲", string.Empty, string.Empty,
            false, string.Empty, DateTime.UtcNow);
}

/// <summary>斜床或研磨机的只读状态快照。</summary>
public sealed record ProcessStationSnapshot(
    string StationCode,
    string State,
    WorkpieceDisplaySnapshot? Workpiece,
    DateTime StateChangedAtUtc,
    bool NeedsManualWorkpiece);

/// <summary>中转架物理有板信号与软件身份缓存的组合快照。</summary>
public sealed record TransferRackSnapshot(
    string StationCode,
    bool PhysicalSignalAvailable,
    bool PhysicalHasPlate,
    WorkpieceDisplaySnapshot? Workpiece,
    DateTime PhysicalSignalAtUtc,
    bool PhysicalSignalSourcePaused,
    bool IsReserved = false,
    string ReservationOperationId = "",
    bool ManualResolutionRequired = false,
    string ManualResolutionReason = "")
{
    public bool HasIdentityMismatch => PhysicalSignalAvailable && PhysicalHasPlate != (Workpiece != null);

    public string PhysicalSignalUnavailableText => PhysicalSignalSourcePaused
        ? "物理信号来源已暂停，当前仅显示最后已知软件身份"
        : PhysicalSignalAtUtc == default
            ? "尚未形成物理信号快照"
            : $"物理信号快照已过期（最近 {PhysicalSignalAtUtc.ToLocalTime():HH:mm:ss}）";

    public string ReservationText => ManualResolutionRequired
        ? $"来源缓存已冻结等待人工结案（{ReservationOperationId}）：{ManualResolutionReason}"
        : IsReserved
        ? $"上料动作预约中（{ReservationOperationId}）；X11确认前缓存仍属于中转架"
        : string.Empty;
}
