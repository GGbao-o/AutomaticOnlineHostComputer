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
    public void ObserveFinalState(Func<string> readLocks, Func<string> readCache)
    {
        _readLocks = readLocks;
        _readCache = readCache;
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
                string.Join("\n\n", _alarmMessages));
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
            string? lockResult = null, cacheResult = null;
            if (_failureAction != null)
            {
                try { lockResult = _readLocks?.Invoke(); } catch (Exception ex) { lockResult = $"锁状态读取失败：{ex.Message}"; }
                try { cacheResult = _readCache?.Invoke(); } catch (Exception ex) { cacheResult = $"缓存状态读取失败：{ex.Message}"; }
            }
            lock (_gate)
            {
                var held = _action.Snapshot().HeldLocks;
                _lockCleanup = lockResult ?? (held.Count == 0 ? "动作账本没有登记持锁，实际锁状态未知" : $"动作账本曾登记：{string.Join("、", held)}；实际释放结果未知");
                if (cacheResult != null) _cacheCleanup = cacheResult;
                _finished = true;
                _cleanupAtUtc = DateTime.UtcNow;
            }
            NotifyChanged();
        }
        catch { }
        finally { Ambient.Value = _previous; }
    }
}
