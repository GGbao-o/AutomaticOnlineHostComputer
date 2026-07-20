using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

internal enum OperationalMonitorStepKind
{
    CacheMutation,
    CacheNotification,
    DownstreamNotification,
    FileHandshake,
    PhysicalHandoff
}

internal enum OperationalMonitorStepState
{
    NotStarted,
    StartedResultUnknown,
    Succeeded
}

internal sealed record OperationalMonitorStepEvidence(
    OperationalMonitorStepKind Kind,
    string Name,
    OperationalMonitorStepState State,
    string Detail);

/// <summary>
/// 单个真实取放料周期的旁路证据累加器。只接受调用点已经取得的事实，
/// 不读取设备、不返回业务判断结果，且所有更新失败均被隔离。
/// </summary>
internal sealed class OperationalPhysicalCycleTracker
{
    private static readonly OperationalPhysicalCycleTracker DisabledInstance = new(disabled: true);
    private readonly bool _disabled;
    private readonly string _cycleId;
    private int _x11CycleAttempts;
    private int _x11StageAttempts;
    private string _x11StageName = "未分段X11检查";
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
    private EvidenceValue<int> _targetZ = EvidenceValue<int>.Unknown("本物理周期尚未记录Z运动目标");
    private string _zReason = "本物理周期尚无Z安全检查点";
    private DeviceCommandEvidence _magnetOn = NotSent("本物理周期尚未开始充磁调用");
    private DeviceCommandEvidence _magnetOff = NotSent("本物理周期尚未开始退磁调用");
    private EvidenceValue<bool> _holdingWorkpiece = EvidenceValue<bool>.Unknown("本物理周期尚无持件证据");
    private EvidenceValue<bool> _placed = EvidenceValue<bool>.Unknown("本物理周期尚无放料证据");
    private EvidenceValue<string> _lastConfirmedLocation = EvidenceValue<string>.Unknown("本物理周期尚无位置承诺点");
    private readonly Dictionary<string, OperationalMonitorStepEvidence> _monitorSteps = new(StringComparer.Ordinal);
    private EvidenceValue<string> _cacheKey = EvidenceValue<string>.Unknown("调用点尚未提供缓存键");
    private EvidenceValue<string> _cacheBefore = EvidenceValue<string>.Unknown("调用点尚未提供缓存修改前事实");
    private EvidenceValue<string> _cacheAfter = EvidenceValue<string>.Unknown("调用点尚未提供缓存修改后事实");

    public OperationalPhysicalCycleTracker(string cycleId)
    {
        _cycleId = string.IsNullOrWhiteSpace(cycleId) ? "未提供物理周期ID" : cycleId;
    }

    private OperationalPhysicalCycleTracker(bool disabled)
    {
        _disabled = disabled;
        _cycleId = "物理周期监控初始化失败";
    }

    internal static OperationalPhysicalCycleTracker Disabled => DisabledInstance;

    internal bool MonitoringAvailable => !_disabled;

    public void BeginX11Stage(string stageName) => Try(() =>
    {
        _x11StageName = NonBlank(stageName, "未命名X11检查阶段");
        _x11StageAttempts = 0;
        _currentX11Valid = false;
    });

    public void BeginX11Attempt() => Try(() =>
    {
        _x11CycleAttempts++;
        _x11StageAttempts++;
        _currentX11Valid = false;
    });

    public void CompleteX11(bool value, DateTime readAtUtc) => Try(() =>
    {
        _x11LastValue = value ? 1 : 0;
        _hasSuccessfulX11 = true;
        _currentX11Valid = true;
        _lastSuccessfulX11AtUtc = readAtUtc;
        _lastSuccessfulCheckpoint = $"X11成功读取为{_x11LastValue}（UTC {readAtUtc:O}）";
        if (value)
            _holdingWorkpiece = EvidenceValue<bool>.Confirmed(true, $"{_x11StageName}成功读取X11=1");
    });

    public void FailX11() => Try(() => _currentX11Valid = false);

    public void BeginZDown() => BeginZDownCore(null);

    public void BeginZDown(int targetZ) => BeginZDownCore(targetZ);

    private void BeginZDownCore(int? targetZ) => Try(() =>
    {
        _targetZ = targetZ.HasValue
            ? EvidenceValue<int>.Confirmed(targetZ.Value, "调用点在发送Z运动命令前提供的实际目标值")
            : EvidenceValue<int>.Unknown("调用点未提供本次Z运动目标，不继承上一阶段目标");
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
    {
        _magnetOff = Unknown("退磁方法调用已开始但尚未取得成功返回，实际退磁结果未知");
        _holdingWorkpiece = EvidenceValue<bool>.Unknown("退磁调用已开始，命令可能已执行；调用前持件值只保留为历史检查点");
        _placed = EvidenceValue<bool>.Unknown("退磁调用已开始，不能断言已放料或未放料");
        _lastConfirmedLocation = EvidenceValue<string>.Unknown("退磁调用结果未知，工件当前位置需人工确认");
    });

    public void CompleteMagnetOff() => Try(() =>
    {
        _magnetOff = Acknowledged("退磁方法成功返回；该事实与目标位通知分别记录");
        _lastSuccessfulCheckpoint = "退磁方法成功返回（目标通知尚需单独确认）";
    });

    public void FailMagnetOff() => Try(() =>
        _magnetOff = Unknown("退磁方法异常；不能据此断言工件仍吸附或已经释放"));

    public void ConfirmHolding(string reason) => Try(() =>
        _holdingWorkpiece = EvidenceValue<bool>.Confirmed(true, NonBlank(reason, "调用点确认当前持件")));

    public void BeginPlacementMagnetOff(string target) => Try(() =>
    {
        BeginMagnetOff();
        string normalizedTarget = NonBlank(target, "未知目标位");
        _holdingWorkpiece = EvidenceValue<bool>.Unknown($"面向{normalizedTarget}的退磁调用已开始，释放结果未知；此前X11=1仅为历史检查点");
        _placed = EvidenceValue<bool>.Unknown($"面向{normalizedTarget}的退磁调用已开始，不能断言已放料或未放料");
        _lastConfirmedLocation = EvidenceValue<string>.Unknown($"退磁调用已开始，工件可能仍在磁铁或已到{normalizedTarget}");
    });

    public void CompletePlacementMagnetOff(string target) => Try(() =>
    {
        CompleteMagnetOff();
        string normalizedTarget = NonBlank(target, "未知目标位");
        _holdingWorkpiece = EvidenceValue<bool>.Inferred(false, "退磁方法成功返回；这是调用返回推定，不替代现场传感器确认");
        _placed = EvidenceValue<bool>.Inferred(true, $"退磁方法成功返回，推定工件已释放到{normalizedTarget}");
        _lastConfirmedLocation = EvidenceValue<string>.Inferred(normalizedTarget, "退磁方法成功返回后的物理位置推定，仍需结合现场确认");
        _lastSuccessfulCheckpoint = $"目标位{normalizedTarget}退磁方法成功返回，放料推定成立";
    });

    public void RegisterMonitorStep(OperationalMonitorStepKind kind, string name) => Try(() =>
    {
        string normalized = NonBlank(name, kind.ToString());
        string key = StepKey(kind, normalized);
        if (!_monitorSteps.ContainsKey(key))
            _monitorSteps[key] = new(kind, normalized, OperationalMonitorStepState.NotStarted, "业务调用尚未开始");
    });

    public void BeginMonitorStep(OperationalMonitorStepKind kind, string name, string detail) => Try(() =>
    {
        string normalized = NonBlank(name, kind.ToString());
        _monitorSteps[StepKey(kind, normalized)] = new(
            kind,
            normalized,
            OperationalMonitorStepState.StartedResultUnknown,
            NonBlank(detail, "业务调用已开始但尚未取得成功返回；副作用结果未知"));
    });

    public void CompleteMonitorStep(OperationalMonitorStepKind kind, string name, string detail) => Try(() =>
    {
        string normalized = NonBlank(name, kind.ToString());
        string checkpoint = NonBlank(detail, $"{normalized}成功返回");
        _monitorSteps[StepKey(kind, normalized)] = new(
            kind,
            normalized,
            OperationalMonitorStepState.Succeeded,
            checkpoint);
        _lastSuccessfulCheckpoint = checkpoint;
    });

    public void BeginCacheMutation(string key, string before, string detail) => Try(() =>
    {
        string normalizedKey = NonBlank(key, "未知缓存键");
        _cacheKey = EvidenceValue<string>.Confirmed(normalizedKey, "调用点提供的缓存键");
        _cacheBefore = EvidenceValue<string>.Confirmed(NonBlank(before, "修改前值未知"), "调用点在缓存修改前已掌握");
        _cacheAfter = EvidenceValue<string>.Unknown("缓存修改调用已开始，修改后值未知");
        BeginMonitorStep(OperationalMonitorStepKind.CacheMutation, normalizedKey, detail);
    });

    public void CompleteCacheMutation(string key, string before, string after, string detail) => Try(() =>
    {
        string normalizedKey = NonBlank(key, "未知缓存键");
        _cacheKey = EvidenceValue<string>.Confirmed(normalizedKey, "调用点提供的缓存键");
        _cacheBefore = EvidenceValue<string>.Confirmed(NonBlank(before, "修改前值未知"), "调用点在缓存修改前已掌握");
        _cacheAfter = EvidenceValue<string>.Confirmed(NonBlank(after, "修改后值未知"), "原业务缓存修改成功完成后的事实");
        CompleteMonitorStep(OperationalMonitorStepKind.CacheMutation, normalizedKey, detail);
    });

    public OperationalPhysicalCycleSnapshot Snapshot()
    {
        if (_disabled)
            return OperationalPhysicalCycleSnapshot.Unavailable(_cycleId, "物理周期监控初始化失败；本次业务不受影响，监控证据不可用");

        try
        {
            string noSuccessfulRead = _x11CycleAttempts > 0
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
                _targetZ,
                _magnetOn,
                _magnetOff,
                _hasSuccessfulX11
                    ? EvidenceValue<int>.Confirmed(_x11LastValue, "本物理周期最后一次成功X11读取值")
                    : EvidenceValue<int>.Unavailable(noSuccessfulRead),
                _x11CycleAttempts > 0
                    ? EvidenceValue<bool>.Confirmed(_currentX11Valid, _currentX11Valid
                        ? "本次X11读取成功返回"
                        : "本次X11读取失败；最后成功值如有则仅作历史证据")
                    : EvidenceValue<bool>.Unknown(noSuccessfulRead),
                _x11StageAttempts > 0
                    ? EvidenceValue<int>.Confirmed(_x11StageAttempts, $"当前阶段“{_x11StageName}”尝试{_x11StageAttempts}次；物理周期累计{_x11CycleAttempts}次")
                    : EvidenceValue<int>.Unknown(noSuccessfulRead),
                _hasSuccessfulX11
                    ? EvidenceValue<DateTime>.Confirmed(_lastSuccessfulX11AtUtc, "最后一次成功X11读取时间")
                    : EvidenceValue<DateTime>.Unavailable(noSuccessfulRead),
                string.IsNullOrWhiteSpace(_lastSuccessfulCheckpoint)
                    ? EvidenceValue<string>.Unknown("本物理周期尚无成功设备检查点")
                    : EvidenceValue<string>.Confirmed(_lastSuccessfulCheckpoint, "物理周期跟踪器按调用返回顺序更新"),
                EvidenceValue<string>.Confirmed(_x11StageName, "当前X11检查阶段"),
                _x11CycleAttempts > 0
                    ? EvidenceValue<int>.Confirmed(_x11CycleAttempts, "本物理周期X11读取累计尝试次数")
                    : EvidenceValue<int>.Unknown(noSuccessfulRead),
                _holdingWorkpiece,
                _placed,
                _lastConfirmedLocation,
                _monitorSteps.Values.ToArray(),
                _cacheKey,
                _cacheBefore,
                _cacheAfter);
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

    private void Try(Action action)
    {
        if (_disabled)
            return;

        try { action(); }
        catch { }
    }

    private static string NonBlank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string StepKey(OperationalMonitorStepKind kind, string name) => $"{kind}:{name}";
}

internal sealed record OperationalPhysicalCycleSnapshot(
    string CycleId,
    DeviceCommandEvidence ZDownCommand,
    EvidenceValue<bool> ZMayStillBeLow,
    EvidenceValue<bool> ZKnownSafe,
    EvidenceValue<int> SafeZTarget,
    EvidenceValue<int> SafeZTolerance,
    EvidenceValue<int> TargetZ,
    DeviceCommandEvidence MagnetOnCommand,
    DeviceCommandEvidence MagnetOffCommand,
    EvidenceValue<int> X11LastValue,
    EvidenceValue<bool> X11ReadValid,
    EvidenceValue<int> X11Attempts,
    EvidenceValue<DateTime> X11ReadAtUtc,
    EvidenceValue<string> LastSuccessfulCheckpoint,
    EvidenceValue<string> X11StageName,
    EvidenceValue<int> X11CycleAttempts,
    EvidenceValue<bool> HoldingWorkpiece,
    EvidenceValue<bool> Placed,
    EvidenceValue<string> LastConfirmedLocation,
    IReadOnlyList<OperationalMonitorStepEvidence> MonitorSteps,
    EvidenceValue<string> CacheKey,
    EvidenceValue<string> CacheBefore,
    EvidenceValue<string> CacheAfter)
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
            EvidenceValue<int>.Unavailable(reason),
            command,
            command,
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<bool>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<DateTime>.Unavailable(reason),
            EvidenceValue<string>.Unavailable(reason),
            EvidenceValue<string>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<bool>.Unavailable(reason),
            EvidenceValue<bool>.Unavailable(reason),
            EvidenceValue<string>.Unavailable(reason),
            Array.Empty<OperationalMonitorStepEvidence>(),
            EvidenceValue<string>.Unavailable(reason),
            EvidenceValue<string>.Unavailable(reason),
            EvidenceValue<string>.Unavailable(reason));
    }
}
