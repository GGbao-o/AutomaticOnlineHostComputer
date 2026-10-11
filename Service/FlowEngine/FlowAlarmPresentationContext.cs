using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Service.Alarms;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>仅供弹窗显示的单次动作上下文，不修改动作、缓存、设备或锁。</summary>
internal sealed class FlowAlarmPresentationContext : IDisposable
{
    private static readonly AsyncLocal<FlowAlarmPresentationContext?> Ambient = new();
    private readonly FlowAlarmPresentationContext? _previous;
    private readonly FlowActionContext _action;
    private readonly AlarmWorkpiece? _workpiece;
    private readonly string _scope;
    private readonly int _craneNo;
    private readonly string _operation;
    private readonly object _gate = new();
    private FlowActionSnapshot? _failureAction;
    private DateTime _occurredAtUtc;
    private string _reason = "未知";
    private string _exceptionDetail = "未提供异常对象";
    private string _lastSuccessfulStep = "未知（尚无确认步骤记录）";
    private string _deviceCleanup = "等待原业务收尾，尚未取得清零结果";
    private string _memoryCleanup = "等待原业务收尾";
    private string _cacheCleanup = "未知（尚未取得来源/下游缓存结果）";
    private string _lockCleanup = "等待原业务收尾";
    private bool _finished;
    private DateTime? _cleanupAtUtc;
    private Func<string>? _readLocks;
    private Func<string>? _readCache;
    private Func<string>? _readMemory;
    private string? _phaseSource, _phaseTarget;
    private string _phase = "", _displayStep = "", _failurePhase = "", _failureDisplayStep = "";
    private bool _waitForFrontDispatch, _actionBodyFinished, _frontDispatchFinished;
    private string? _frontDispatchResult;
    private string _actionLockResult = "等待原业务收尾";
    private readonly List<string> _alarmMessages = new();
    private int _disposeStarted;

    private FlowAlarmPresentationContext(FlowActionContext action, WorkpieceCache? wp, string scope, int craneNo, string operation)
    {
        _action = action;
        _scope = scope;
        _craneNo = craneNo;
        _operation = operation;
        _occurredAtUtc = DateTime.UtcNow;
        if (wp.HasValue)
            _workpiece = new AlarmWorkpiece(wp.Value.PlateNo, wp.Value.Sequence, wp.Value.Length, wp.Value.Diameter, wp.Value.BoreType);
        _previous = Ambient.Value;
    }

    public static FlowAlarmPresentationContext? Current => Ambient.Value;
    public string ActionId => _action.OperationId;
    public event Action? Changed;

    // 只在动作退出时读现有软件事实；回调不取得/释放锁、不清理或恢复缓存。
    public void ObserveFinalState(Func<string> readLocks, Func<string>? readCache = null, Func<string>? readMemory = null)
    {
        _readLocks = readLocks;
        _readCache = readCache;
        _readMemory = readMemory;
    }

    internal static string DescribeFrontActionLocks(FlowActionContext action)
    {
        var held = action.Snapshot().HeldLocks.Where(name => name is "ZoneMT" or "ZoneTS").ToArray();
        return held.Length == 0 ? "本动作ZoneMT/ZoneTS账本无持锁记录（未取得或登记已释放；实际释放结果需核对原日志）"
            : $"本动作仍登记持有：{string.Join("、", held)}；请核对原流程释放结果";
    }

    internal static string DescribeLocks(EmergencyActionLease lease)
    {
        var held = new[] { "RearCrane", "TransferRack", "ZoneMT", "ZoneTS", "M817", "M720", "M818", "M821" }.Where(lease.IsHeld).ToArray();
        return held.Length == 0 ? "本动作实际登记的软件锁已释放（不代表现场位置安全）" : $"本动作仍持有：{string.Join("、", held)}";
    }

    internal static string DescribeRackCache(TransferRackWorkpieceLedger ledger, string source, string actionId, WorkpieceCache wp)
    {
        string state = !ledger.TryGet(source, out var current) ? "当前无工件缓存"
            : current.PlateNo == wp.PlateNo && current.Sequence == wp.Sequence ? "当前仍保存本块板身份" : "当前保存其他工件身份";
        if (ledger.IsHeldForManualResolution(source, out var owner, out var reason))
            state += $"；来源已冻结待人工确认（动作={owner}，原因={reason}）";
        else if (ledger.IsReserved(source, out owner))
            state += $"；来源仍被预约（{(owner == actionId ? "本动作" : owner)}）";
        else state += "；当前无来源预约";
        return $"{source}：{state}。自动作废清理未修改中转架账本；已移交下游的缓存不回滚。";
    }

    public static FlowAlarmPresentationContext Begin(FlowActionContext action, WorkpieceCache? wp, string scope, int craneNo, string operation)
    {
        var context = new FlowAlarmPresentationContext(action, wp, scope, craneNo, operation);
        Ambient.Value = context;
        return context;
    }

    public static FlowAlarmPresentationContext BeginFront(FlowActionContext action, WorkpieceCache wp, string scope, int craneNo)
    {
        var context = Begin(action, wp, scope, craneNo, "前天车搬运");
        context._deviceCleanup = "原前天车异常处理未统一清除PLC参数及输出；停止与保护恢复按原流程处理";
        context._cacheCleanup = "本次前天车工件已出队；中转架缓存交接尚未确认，不自动回滚下游缓存";
        context._waitForFrontDispatch = FrontCraneAlarmDispatchScope.Track(context);
        return context;
    }

    // 阶段只覆盖展示快照；不改变FlowActionContext的来源、目标、步骤或工件归属。
    public static void RecordTransferPhase(string source, string target, string phase, string? displayStep = null)
    {
        try
        {
            var context = Current;
            if (context == null) return;
            lock (context._gate)
            {
                context._phaseSource = source;
                context._phaseTarget = target;
                context._phase = phase;
                context._displayStep = displayStep ?? "";
            }
        }
        catch { }
    }

    public static void RecordCacheState(string result)
    {
        try
        {
            var context = Current;
            if (context == null) return;
            lock (context._gate) context._cacheCleanup = result;
            context.NotifyChanged();
        }
        catch { }
    }

    internal void CompleteFrontDispatch(bool released)
    {
        try
        {
            lock (_gate)
            {
                _frontDispatchFinished = true;
                _frontDispatchResult = released ? "前天车调度锁已释放（不代表现场位置安全）"
                    : "前天车调度锁未取得成功释放记录，请核对调度异常";
                _lockCleanup = _actionLockResult + "；" + _frontDispatchResult;
                _finished = _actionBodyFinished;
                _cleanupAtUtc = DateTime.UtcNow;
            }
            NotifyChanged();
        }
        catch { }
    }

    public static void RecordFailure(Exception exception)
    {
        try { Current?.CaptureFailure(exception); } catch { /* 显示失败不得改变异常处理。 */ }
    }

    private void CaptureFailure(Exception exception)
    {
        lock (_gate)
        {
            if (_failureAction != null) return;
            CaptureAction();
            _reason = exception.Message;
            _exceptionDetail = exception.ToString();
            if (exception is CraneMotionTimeoutException timeout && timeout.LastKnownStatus != null)
            {
                var status = timeout.LastKnownStatus;
                _failureAction = _failureAction! with { LastKnownPosition = new(status.XPos, status.YPos, status.ZPos) };
            }
        }
        NotifyChanged();
    }

    // 回调在catch或finally发出；这里仅补齐没有异常对象的告警，不覆盖首次失败快照。
    public void ObserveAlarm(string message)
    {
        try
        {
            lock (_gate)
            {
                if (!_alarmMessages.Contains(message)) _alarmMessages.Add(message);
                if (_failureAction == null)
                {
                    CaptureAction();
                    _reason = message;
                }
            }
            NotifyChanged();
        }
        catch { }
    }

    private void CaptureAction()
    {
        _failureAction = _action.Snapshot();
        if (_phaseSource != null) _failureAction = _failureAction with { Source = _phaseSource };
        if (_phaseTarget != null) _failureAction = _failureAction with { Target = _phaseTarget };
        _failurePhase = _phase;
        _failureDisplayStep = _displayStep;
        _occurredAtUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(_action.LastConfirmedDetail))
            _lastSuccessfulStep = _action.LastConfirmedDetail;
    }

    public static void RecordCleanup(string deviceResult, string memoryResult, string cacheResult)
    {
        try
        {
            var context = Current;
            if (context == null) return;
            lock (context._gate)
            {
                context._deviceCleanup = deviceResult;
                context._memoryCleanup = memoryResult;
                context._cacheCleanup = cacheResult;
                context._cleanupAtUtc = DateTime.UtcNow;
            }
            context.NotifyChanged();
        }
        catch { }
    }

    public SafetyAlarmDetails Snapshot()
    {
        lock (_gate)
            return new(ActionId, _scope, _craneNo, _operation, _occurredAtUtc, _workpiece,
                _failureAction, _reason, _exceptionDetail, _lastSuccessfulStep, _deviceCleanup,
                _memoryCleanup, _cacheCleanup, _lockCleanup, _finished, _cleanupAtUtc,
                string.Join("\n\n", _alarmMessages), _failurePhase, _failureDisplayStep);
    }

    private void NotifyChanged()
    {
        foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { observer(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        try
        {
            string? lockResult = null, cacheResult = null, memoryResult = null;
            if (_failureAction != null)
            {
                try { lockResult = _readLocks?.Invoke(); } catch (Exception ex) { lockResult = $"锁状态读取失败：{ex.Message}"; }
                try { cacheResult = _readCache?.Invoke(); } catch (Exception ex) { cacheResult = $"缓存状态读取失败：{ex.Message}"; }
                try { memoryResult = _readMemory?.Invoke(); } catch (Exception ex) { memoryResult = $"内存状态读取失败：{ex.Message}"; }
            }
            lock (_gate)
            {
                var held = _action.Snapshot().HeldLocks;
                _lockCleanup = lockResult ?? (held.Count == 0 ? "动作账本没有登记持锁，实际锁状态未知" : $"动作账本曾登记：{string.Join("、", held)}；实际释放结果未知");
                _actionLockResult = _lockCleanup;
                if (_waitForFrontDispatch)
                    _lockCleanup += "；" + (_frontDispatchResult ?? "前天车调度锁：等待调度层释放");
                if (cacheResult != null) _cacheCleanup = cacheResult;
                if (memoryResult != null) _memoryCleanup = memoryResult;
                _actionBodyFinished = true;
                _finished = !_waitForFrontDispatch || _frontDispatchFinished;
                _cleanupAtUtc = DateTime.UtcNow;
            }
            NotifyChanged();
        }
        catch { }
        finally { Ambient.Value = _previous; }
    }
}
