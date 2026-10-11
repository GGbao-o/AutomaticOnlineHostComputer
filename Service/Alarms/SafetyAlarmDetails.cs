namespace AutomaticOnlineHostComputer.Service.Alarms;

internal sealed record AlarmWorkpiece(string PlateNo, string Sequence, double Length, double Diameter, int BoreType);

internal sealed record SafetyAlarmDetails(
    string ActionId, string Scope, int CraneNo, string Operation, DateTime OccurredAtUtc,
    AlarmWorkpiece? Workpiece, FlowActionSnapshot? FailureAction, string Reason,
    string ExceptionDetail, string LastSuccessfulStep, string DeviceCleanup, string MemoryCleanup,
    string CacheCleanup, string LockCleanup, bool CleanupFinished, DateTime? CleanupAtUtc,
    string AlarmMessages = "", string TransferPhase = "", string FailureDisplayStep = "");

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
