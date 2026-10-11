namespace AutomaticOnlineHostComputer.Service.Alarms;

internal sealed record AlarmWorkpiece(string PlateNo, string Sequence, double Length, double Diameter, int BoreType);

/// <summary>
/// 单次故障的展示快照：FailureAction/Workpiece是失败时证据，Cleanup字段是随后实际收尾结果。
/// ManipulatorNo有值时表示机械手连接，不能按同号天车采样；为空时保持原天车路径。
/// 这个记录只供弹窗读取，不是生产续搬任务，也不通过修改它恢复引擎或清设备。
/// </summary>
internal sealed record SafetyAlarmDetails(
    string ActionId, string Scope, int CraneNo, string Operation, DateTime OccurredAtUtc,
    AlarmWorkpiece? Workpiece, FlowActionSnapshot? FailureAction, string Reason,
    string ExceptionDetail, string LastSuccessfulStep, string DeviceCleanup, string MemoryCleanup,
    string CacheCleanup, string LockCleanup, bool CleanupFinished, DateTime? CleanupAtUtc,
    string AlarmMessages = "", string TransferPhase = "", string FailureDisplayStep = "", int? ManipulatorNo = null);

internal sealed record AlarmInputValue(bool? Value, DateTime? ReadAtUtc, string Error)
{
    public static AlarmInputValue Unknown(string reason) => new(null, null, reason);
}

internal sealed record CraneAlarmPosition(
    int X, int Y, int Z, int AbsX, int AbsY, int AbsZ, short HasRoller,
    short Fault, short ServoAlarm, short PlcServoAlarm, short PlcAlarm, short RunConditionMissing,
    DateTime ReadAtUtc);

internal sealed record CraneAlarmSnapshot(
    DateTime StartedAtUtc, DateTime FinishedAtUtc, CraneAlarmPosition? Position, string PositionError,
    AlarmInputValue X2, AlarmInputValue X6, AlarmInputValue X7, AlarmInputValue X11)
{
    public static CraneAlarmSnapshot Unknown(string reason) => new(
        DateTime.UtcNow, DateTime.UtcNow, null, reason,
        AlarmInputValue.Unknown(reason), AlarmInputValue.Unknown(reason),
        AlarmInputValue.Unknown(reason), AlarmInputValue.Unknown(reason));
}
