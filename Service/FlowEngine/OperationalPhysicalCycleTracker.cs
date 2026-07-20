using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 单个真实取放料周期的旁路证据累加器。只接受调用点已经取得的事实，
/// 不读取设备、不返回业务判断结果，且所有更新失败均被隔离。
/// </summary>
internal sealed class OperationalPhysicalCycleTracker
{
    private readonly string _cycleId;
    private int _x11Attempts;
    private int _x11LastValue;
    private bool _hasSuccessfulX11;
    private bool _currentX11Valid;
    private DateTime _lastSuccessfulX11AtUtc;
    private string? _lastSuccessfulCheckpoint;
    private DeviceCommandEvidence _zDown = NotSent("本物理周期尚未开始Z下降调用");
    private bool? _zMayStillBeLow;
    private bool _zKnownSafe;
    private int _safeZTarget;
    private int _safeZTolerance;
    private string _zReason = "本物理周期尚无Z安全检查点";
    private DeviceCommandEvidence _magnetOn = NotSent("本物理周期尚未开始充磁调用");
    private DeviceCommandEvidence _magnetOff = NotSent("本物理周期尚未开始退磁调用");

    public OperationalPhysicalCycleTracker(string cycleId)
    {
        _cycleId = string.IsNullOrWhiteSpace(cycleId) ? "未提供物理周期ID" : cycleId;
    }

    public void BeginX11Attempt() => Try(() =>
    {
        _x11Attempts++;
        _currentX11Valid = false;
    });

    public void CompleteX11(bool value, DateTime readAtUtc) => Try(() =>
    {
        _x11LastValue = value ? 1 : 0;
        _hasSuccessfulX11 = true;
        _currentX11Valid = true;
        _lastSuccessfulX11AtUtc = readAtUtc;
        _lastSuccessfulCheckpoint = $"X11成功读取为{_x11LastValue}（UTC {readAtUtc:O}）";
    });

    public void FailX11() => Try(() => _currentX11Valid = false);

    public void BeginZDown() => Try(() =>
    {
        _zDown = Unknown("Z下降调用已经开始但尚未取得成功返回，命令实际结果未知");
        _zMayStillBeLow = null;
        _zKnownSafe = false;
        _zReason = "Z下降调用已开始，当前高度未由后续安全回升确认";
    });

    public void CompleteZDown() => Try(() =>
    {
        _zDown = Acknowledged("Z下降方法成功返回");
        _zMayStillBeLow = true;
        _zKnownSafe = false;
        _zReason = "Z下降方法已返回，尚未确认回到安全高度";
        _lastSuccessfulCheckpoint = "Z下降方法成功返回";
    });

    public void MarkZUnknown(string reason) => Try(() =>
    {
        _zMayStillBeLow = null;
        _zKnownSafe = false;
        _zReason = NonBlank(reason, "Z当前位置未知，仍需人工确认");
    });

    public void ConfirmSafeZ(int target, int tolerance, string reason) => Try(() =>
    {
        _safeZTarget = target;
        _safeZTolerance = Math.Max(0, tolerance);
        _zKnownSafe = true;
        _zMayStillBeLow = false;
        _zReason = NonBlank(reason, "Z回安全高度方法成功返回");
        _lastSuccessfulCheckpoint = $"Z安全高度已确认：目标={target}，容差={_safeZTolerance}；{_zReason}";
    });

    public void BeginMagnetOn() => Try(() =>
        _magnetOn = Unknown("充磁方法调用已开始；方法可能在模式检查、触发写、反馈轮询或触发复位任一步失败"));

    public void CompleteMagnetOn() => Try(() =>
    {
        _magnetOn = Acknowledged("充磁方法成功返回；该事实不等于X11已确认持件");
        _lastSuccessfulCheckpoint = "充磁方法成功返回（尚不等于X11确认持件）";
    });

    public void FailMagnetOn() => Try(() =>
        _magnetOn = Unknown("充磁方法异常；调用点不能证明命令是否发送或磁铁实际状态"));

    public void BeginMagnetOff() => Try(() =>
        _magnetOff = Unknown("退磁方法调用已开始但尚未取得成功返回，实际退磁结果未知"));

    public void CompleteMagnetOff() => Try(() =>
    {
        _magnetOff = Acknowledged("退磁方法成功返回；该事实与目标位通知分别记录");
        _lastSuccessfulCheckpoint = "退磁方法成功返回（目标通知尚需单独确认）";
    });

    public void FailMagnetOff() => Try(() =>
        _magnetOff = Unknown("退磁方法异常；不能据此断言工件仍吸附或已经释放"));

    public OperationalPhysicalCycleSnapshot Snapshot()
    {
        try
        {
            string noSuccessfulRead = _x11Attempts > 0
                ? "本物理周期X11读取已尝试，但尚无成功返回值"
                : "本物理周期尚未尝试读取X11";
            return new OperationalPhysicalCycleSnapshot(
                _cycleId,
                _zDown,
                _zMayStillBeLow.HasValue
                    ? EvidenceValue<bool>.Confirmed(_zMayStillBeLow.Value, _zReason)
                    : EvidenceValue<bool>.Unknown(_zReason),
                _zKnownSafe
                    ? EvidenceValue<bool>.Confirmed(true, _zReason)
                    : EvidenceValue<bool>.Unknown(_zReason),
                _zKnownSafe
                    ? EvidenceValue<int>.Confirmed(_safeZTarget, _zReason)
                    : EvidenceValue<int>.Unknown(_zReason),
                _zKnownSafe
                    ? EvidenceValue<int>.Confirmed(_safeZTolerance, _zReason)
                    : EvidenceValue<int>.Unknown(_zReason),
                _magnetOn,
                _magnetOff,
                _hasSuccessfulX11
                    ? EvidenceValue<int>.Confirmed(_x11LastValue, "本物理周期最后一次成功X11读取值")
                    : EvidenceValue<int>.Unavailable(noSuccessfulRead),
                _x11Attempts > 0
                    ? EvidenceValue<bool>.Confirmed(_currentX11Valid, _currentX11Valid
                        ? "本次X11读取成功返回"
                        : "本次X11读取失败；最后成功值如有则仅作历史证据")
                    : EvidenceValue<bool>.Unknown(noSuccessfulRead),
                _x11Attempts > 0
                    ? EvidenceValue<int>.Confirmed(_x11Attempts, "本物理周期业务层X11读取尝试次数")
                    : EvidenceValue<int>.Unknown(noSuccessfulRead),
                _hasSuccessfulX11
                    ? EvidenceValue<DateTime>.Confirmed(_lastSuccessfulX11AtUtc, "最后一次成功X11读取时间")
                    : EvidenceValue<DateTime>.Unavailable(noSuccessfulRead),
                string.IsNullOrWhiteSpace(_lastSuccessfulCheckpoint)
                    ? EvidenceValue<string>.Unknown("本物理周期尚无成功设备检查点")
                    : EvidenceValue<string>.Confirmed(_lastSuccessfulCheckpoint, "物理周期跟踪器按调用返回顺序更新"));
        }
        catch
        {
            const string failed = "物理周期监控快照失败";
            return OperationalPhysicalCycleSnapshot.Unavailable(_cycleId, failed);
        }
    }

    private static DeviceCommandEvidence NotSent(string reason) =>
        new(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, reason);

    private static DeviceCommandEvidence Acknowledged(string reason) =>
        new(DeviceCommandState.Acknowledged, EvidenceAvailability.Confirmed, reason);

    private static DeviceCommandEvidence Unknown(string reason) =>
        new(DeviceCommandState.Unknown, EvidenceAvailability.Unknown, reason);

    private static void Try(Action action)
    {
        try { action(); }
        catch { }
    }

    private static string NonBlank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}

internal sealed record OperationalPhysicalCycleSnapshot(
    string CycleId,
    DeviceCommandEvidence ZDownCommand,
    EvidenceValue<bool> ZMayStillBeLow,
    EvidenceValue<bool> ZKnownSafe,
    EvidenceValue<int> SafeZTarget,
    EvidenceValue<int> SafeZTolerance,
    DeviceCommandEvidence MagnetOnCommand,
    DeviceCommandEvidence MagnetOffCommand,
    EvidenceValue<int> X11LastValue,
    EvidenceValue<bool> X11ReadValid,
    EvidenceValue<int> X11Attempts,
    EvidenceValue<DateTime> X11ReadAtUtc,
    EvidenceValue<string> LastSuccessfulCheckpoint)
{
    public static OperationalPhysicalCycleSnapshot Unavailable(string cycleId, string reason)
    {
        var command = new DeviceCommandEvidence(DeviceCommandState.Unavailable, EvidenceAvailability.Unavailable, reason);
        return new(
            cycleId,
            command,
            EvidenceValue<bool>.Unavailable(reason),
            EvidenceValue<bool>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            command,
            command,
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<bool>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<DateTime>.Unavailable(reason),
            EvidenceValue<string>.Unavailable(reason));
    }
}
