using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Logging;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service.OperationalEvents;
using RackAddr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.CenteringRackAddress;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 2号线后端流程引擎 — 后天车(Crane#4) + 5台斜床(ST606~ST610, 全FANUC FOCAS)
///
/// 【主循环 500ms/轮 — 5步骤】
///   步骤① 轮询斜床FANUC信号(请求数据/上料/夹紧/下料/张开) → 同步引擎状态
///   步骤② 斜床断线重连(每20轮, 仅斜床)
///   步骤③ 状态汇总(纯日志)
///   步骤④/⑤ 动态调度 — 先生成上/下料候选，再按中转架压力、下料压力、长度匹配决定本轮动作
///       ≥800mm或任务勾选动平衡 → ST020/M818(2号线动平衡下料架1) → OnBalancingRackPlaced("M818")
///       &lt;800mm且未勾选动平衡 → ST021/M821(2号线不需动平衡位置)
///
/// 【与1号线差异】
///   - 5台斜床全FANUC FOCAS, 无Modbus/锦州
///   - 后天车: ST904/CraneNo=4
///   - 中转架: ST016/M814, ST017/M815, ST018/M816
///   - 级联清零: SafeSetMacro(与1号线FANUC相同)
/// </summary>
public sealed class Line2RearFlowEngine : IDisposable
{
    private readonly CraneConnectionCache _craneCache;
    private readonly ManipulatorConnectionCache _manipulatorCache;
    private readonly MotionConfig _cfg;
    private readonly SemaphoreSlim _transferRackLock;
    private readonly SafetyFlags _safety;
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords;
    private readonly IOperationalEventReporter _exceptionReporter;
    private readonly Line2FrontFlowEngine.Line2DeviceStatus? _frontDs;
    private readonly CancellationTokenSource _engineCts = new();
    private Task? _engineTask;
    private bool _disposed;
    private volatile bool _paused;
    private volatile bool _fastNextCycle; // DoLoad/DoUnload完成后设true, 主循环缩短延迟
    private int _cycleCount;
    private readonly RearSkewDispatchLogGate _dispatchLogGate = new();

    // ── 后天车 ──
    private readonly SemaphoreSlim _craneRearLock = new(1, 1); // 串行化后天车操作
    private const int CraneRearNo = 4;                           // 后天车编号(对应192.168.2.82)
    private int _ox, _oy, _oz;                                   // 后天车数据库偏移量(mm)
    private readonly object _rearCraneTaskLock = new();
    private CraneTaskSnapshot _rearCraneTask = CraneTaskSnapshot.Idle(CraneRearNo, "2号线后天车");

    // ── 5台斜床上下文 ──
    private readonly SkewCtx[] _beds = new SkewCtx[5];

    // ── MC连接缓存(共享, 避免多引擎重复连同一PLC) ──
    private readonly McConnectionCache _mcCache;
    private MitsubishiMcClient? GetMc65() => _mcCache.TryGet("192.168.2.65", 9000);
    private MitsubishiMcClient? GetMc63() => _mcCache.TryGet("192.168.2.63", 9000);
    /// <summary>按需连接MC65(调用方需自行加超时CancellationToken)</summary>
    private async Task<MitsubishiMcClient> EnsureMc65Async(CancellationToken ct)
        => await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, ct);
    /// <summary>按需连接MC63(调用方需自行加超时CancellationToken)</summary>
    private async Task<MitsubishiMcClient> EnsureMc63Async(CancellationToken ct)
    {
        var c = GetMc63();
        return c ?? await _mcCache.GetOrCreateAsync("192.168.2.63", 9000, ct);
    }

    // ── 工件缓存账本：预约不代表已取走；仅X11确认后才从来源位提交移除。 ──
    private readonly TransferRackWorkpieceLedger _rackLedger = new();
    private readonly object _wpLock = new();
    public int CachedCount => _rackLedger.Count;

    /// <summary>下料放至动平衡架时回调, 通知平衡引擎取料</summary>
    public Action<string, WorkpieceCache>? OnBalancingRackPlaced;
    /// <summary>后天车异常恢复失败、现场位置不再可确认时通知主页面暂停整条2号线并弹窗。</summary>
    public Action<string>? OnRearCraneSafetyAlarm;
    /// <summary>X11三次失败已有最终结构化事件；此回调只复用整线暂停和弹窗，避免重复Legacy事件。</summary>
    public Action<string>? OnRearCraneX11FinalSafetyAlarm;
    /// <summary>X11连续无板但已安全退磁回升时通知主页面一次，不暂停引擎。</summary>
    public Action<string>? OnRearCraneWarning;

    /// <summary>单台斜床运行时上下文</summary>
    private sealed class SkewCtx
    {
        public string Code = "";                         // 站号 ST606~ST610 (2号线全FANUC斜床)
        private SkewState _st;                             // 当前引擎状态
        public SkewState St
        {
            get => _st;
            set { if (_st != value) { _st = value; StateChangedAtUtc = DateTime.UtcNow; } }
        }
        public DateTime StateChangedAtUtc { get; private set; } = DateTime.UtcNow;
        // 2号线全FANUC, 无Modbus — 仅保留FANUC服务
        public FanucSkewBedService? F;                    // FANUC服务 (5台全FANUC)
        public WorkpieceCache? Wp;                        // 正在加工的工件数据(上料时从_wps取出绑定)
        public bool Ok => F?.IsConnected == true;
        public bool Idle => St == SkewState.Idle && Ok;
        /// <summary>最新轮询的CNC信号快照</summary>
        public bool RqData, RqLoad, RqUnload, Clamped;
        /// <summary>本线五台均为FANUC，#1006原始值必须==1；与#1005尾座张开独立。</summary>
        public bool DoorOpen;
        /// <summary>本轮CNC信号是否新鲜。轮询失败时禁止用旧RqData/RqUnload派发动作。</summary>
        public bool SignalFresh;
        /// <summary>状态轮询版本。超时/新一轮启动会递增, 防止旧轮询晚返回覆盖新状态。</summary>
        public long PollVersion;
        /// <summary>是否已有一个状态读取在飞。FANUC同步SDK异常阻塞时, 禁止每轮继续堆积新读取。</summary>
        public int PollInFlight;
        /// <summary>当前工件斜床完工记录是否已写入job2。只防重复导出, 不参与运动流程。</summary>
        public bool CompletionExported;
        /// <summary>后台重连连续失败次数(成功时清零, UI可查看)</summary>
        public int ConsecutiveReconnectFails;
        /// <summary>引擎重启后检测到斜床上有工件但Wp=null → 需人工确认</summary>
        public bool WpRecoveryNeeded;
        /// <summary>同一故障周期只弹一次X11连续无板警告，X11成功或应急清除后复位。</summary>
        public bool X11UnloadMissWarningShown;
        /// <summary>当前上/下料动作令牌。人工应急作废时取消, 防止旧动作继续写状态。</summary>
        public EmergencyActionLease? ActiveOperation;
        public long NextOperationVersion;
    }

    private sealed class LoadHandshakeState
    {
        public bool PlacedOnBed;      // 已退磁放到斜床; 后续异常不能再按“工件仍在中转架”自动重试
        public bool LoadDoneNotified; // 已通知CNC上料完成/启动加工; 异常时斜床应保持Machining语义
        // 仅用于异常收尾判断，不改变正常上料步骤。
        // 在发送Z下降命令以前置true：即使运动调用报错，也可能是PLC已收到命令而响应丢失，不能再横移X。
        public bool ZMayBeDown;
        // 退磁后Z成功回到安全高度才置true；供异常日志说明现场已经完成到哪个物理阶段。
        public bool ZSafeAfterPlace;
        // 中转架取料时三次有效X11读取均为0；异常收尾完成后必须暂停对应整线。
        public bool X11PickupFailed;
        public string X11RecoverySummary = "未执行";
        // 仅供最终结构化事件复制已发生的收尾结果，不参与任何业务分支。
        public RecoveryStepState X11MagnetOffState = RecoveryStepState.NotAttempted;
        public string X11MagnetOffDetail = "尚未进入X11失败退磁收尾";
        public RecoveryStepState X11SafeZState = RecoveryStepState.NotAttempted;
        public string X11SafeZDetail = "尚未进入X11失败Z回安全收尾";
        public bool X11CacheRestored;
        public Exception? X11FailureException;
    }

    /// <summary>斜床引擎状态</summary>
    private enum SkewState { Idle, Loading, Machining, WaitingUnload, Unloading }

    public bool IsRunning => _engineTask != null && !_engineTask.IsCompleted;
    public bool IsPaused => _paused;
    public Line2RearDeviceStatus DeviceStatus => DS;
    public Line2RearDeviceStatus DS { get; } = new();

    public CraneTaskSnapshot GetCraneTaskSnapshot()
    {
        lock (_rearCraneTaskLock) return _rearCraneTask;
    }

    public ProcessStationSnapshot[] GetSkewStationSnapshots()
    {
        var result = new List<ProcessStationSnapshot>(_beds.Length);
        foreach (var bed in _beds)
        {
            if (bed == null) continue;
            result.Add(new ProcessStationSnapshot(bed.Code, StateText(bed.St),
                WorkpieceDisplaySnapshot.From(bed.Wp), bed.StateChangedAtUtc, bed.WpRecoveryNeeded));
        }
        return result.ToArray();
    }

    public TransferRackSnapshot[] GetTransferRackSnapshots()
    {
        Dictionary<string, WorkpieceCache> cached = _rackLedger.SnapshotWorkpieces();
        var front = _frontDs;
        bool sourcePaused = front?.DisplaySnapshotPaused == true;
        bool signalAvailable = front?.RackConnected == true
                               && !sourcePaused
                               && DisplaySnapshotFreshness.IsFresh(front.SnapshotAtUtc, DateTime.UtcNow);
        return new[]
        {
            CreateRackSnapshot("ST016"), CreateRackSnapshot("ST017"), CreateRackSnapshot("ST018")
        };

        TransferRackSnapshot CreateRackSnapshot(string code) => new(code, signalAvailable,
            signalAvailable && RackHasPhysicalPlateSnapshot(code),
            cached.TryGetValue(code, out var wp) ? WorkpieceDisplaySnapshot.From(wp) : null,
            front?.SnapshotAtUtc ?? default, sourcePaused,
            _rackLedger.IsReserved(code, out string reservationOperationId), reservationOperationId,
            _rackLedger.IsHeldForManualResolution(code, out _, out string manualReason), manualReason);
    }

    private void SetRearCraneTask(WorkpieceCache wp, string stage, string source, string target, bool manual = false, string manualText = "")
    {
        lock (_rearCraneTaskLock)
            _rearCraneTask = new CraneTaskSnapshot(true, CraneRearNo, "2号线后天车", WorkpieceDisplaySnapshot.From(wp),
                stage, source, target, manual, manualText, DateTime.UtcNow);
    }

    private void ClearRearCraneTask()
    {
        lock (_rearCraneTaskLock) _rearCraneTask = CraneTaskSnapshot.Idle(CraneRearNo, "2号线后天车");
    }

    public sealed class Line2RearDeviceStatus
    {
        /// <summary>最近一次完成整轮后端页面状态维护的时间；仅供UI判断数据是否过期。</summary>
        public DateTime SnapshotAtUtc { get; set; }
        public bool CraneRearConnected { get; set; }
        public int CycleCount { get; set; }
        public bool Skew1Connected, Skew2Connected, Skew3Connected, Skew4Connected, Skew5Connected;
        public string Skew1State { get; set; } = "--";
        public string Skew1Signals { get; set; } = "--";
        public string Skew2State { get; set; } = "--";
        public string Skew2Signals { get; set; } = "--";
        public string Skew3State { get; set; } = "--";
        public string Skew3Signals { get; set; } = "--";
        public string Skew4State { get; set; } = "--";
        public string Skew4Signals { get; set; } = "--";
        public string Skew5State { get; set; } = "--";
        public string Skew5Signals { get; set; } = "--";
        /// <summary>后台重连连续失败次数(成功自动清零)</summary>
        public int Skew1ReconnectFails, Skew2ReconnectFails, Skew3ReconnectFails, Skew4ReconnectFails, Skew5ReconnectFails;
        /// <summary>引擎重启后CNC显示有工件但Wp=null → 需操作工手动确认工件参数</summary>
        public bool Skew1NeedsManualWp, Skew2NeedsManualWp, Skew3NeedsManualWp, Skew4NeedsManualWp, Skew5NeedsManualWp;
    }

    /// <summary>
    /// 构造器 — 保存依赖引用, 连接在 Start()→Loop()→Init 中建立。
    /// </summary>
    /// <param name="fd">2号线前端引擎 DeviceStatus, 用于读取中转架状态和M818</param>
    /// <param name="lockM818">M818位置锁: Line2后天车放料 ↔ M2Flow取料 互斥</param>
    /// <param name="lockM821">M821位置锁: Line2后天车放料 ↔ M3Flow取料 互斥</param>
    public Line2RearFlowEngine(CraneConnectionCache cc, ManipulatorConnectionCache mc,
        McConnectionCache mcc, MotionConfig cfg, Dictionary<string, MachineManagementRowVm> sc,
        SemaphoreSlim trl, SafetyFlags sf, IOperationalEventReporter exceptionReporter,
        Line2FrontFlowEngine.Line2DeviceStatus? fd = null,
        SemaphoreSlim? lockM818 = null, SemaphoreSlim? lockM821 = null)
    {
        _craneCache = cc; _manipulatorCache = mc; _mcCache = mcc; _cfg = cfg; _stationCoords = sc;
        _transferRackLock = trl; _safety = sf; _exceptionReporter = exceptionReporter; _frontDs = fd;
        _lockM818 = lockM818; _lockM821 = lockM821;
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  [后引擎2] 2号线后端流程引擎 已创建");
        Console.WriteLine("  天车编号=4号(ST904)  斜床=ST606~ST610(全FANUC)");
        Console.WriteLine($"  平衡锁: M818={(_lockM818 != null ? "✓" : "✗(无锁!)")} M821={(_lockM821 != null ? "✓" : "✗(无锁!)")}");
        Console.WriteLine("══════════════════════════════════════════");
    }

    // ── 平衡料架位置锁(防止天车和机械手同时操作同位置) ──
    private readonly SemaphoreSlim? _lockM818; // M818位置: 后天车(放) vs M2Flow(取)
    private readonly SemaphoreSlim? _lockM821; // M821位置: 后天车(放) vs M3Flow(取)

    public void Start()
    {
        if (IsRunning) return;
        using var logScope = EngineLogRouter.BeginScope(EngineLogRouter.Line2);
        _paused = false;
        Console.WriteLine("[后引擎2] ▶ 启动");
        _engineTask = Task.Run(() => Loop(_engineCts.Token));
    }
    public void Stop() { Console.WriteLine("[后引擎2] ■ 停止"); _engineCts.Cancel(); }
    public void Pause() { _paused = true; Console.WriteLine("[后引擎2] ⏸ 暂停"); }
    public void Resume() { _paused = false; Console.WriteLine("[后引擎2] ▶ 继续"); }

    public void EnqueueWp(string rs, WorkpieceCache wp) { _rackLedger.Enqueue(rs, wp, out var seq); Console.WriteLine($"[后引擎2] 工件缓存写入: 中转架={rs} 顺序={seq} {wp.IdentityText} 直径={wp.Diameter}mm 版长={wp.Length}mm 缓存总数={CachedCount}"); }
    public void EnqueueWorkpiece(string rs, WorkpieceCache wp) => EnqueueWp(rs, wp);
    public void SetRackWorkpiece(string code, WorkpieceCache wp)
    {
        _rackLedger.Enqueue(code, wp, out var seq);
        Console.WriteLine($"[后引擎2] 工件缓存: 中转架={code} 顺序={seq} {wp.IdentityText} 直径={wp.Diameter}mm 版长={wp.Length}mm 堵孔={wp.BoreType}mm 缓存总数={CachedCount}");
    }

    public bool TrySetManualRackWorkpiece(string code, WorkpieceCache wp, out string message)
    {
        // 人工入口也必须走同一账本，不能覆盖一个正在被后天车预约的来源位。
        if (!_rackLedger.TryEnqueueIfEmpty(code, wp, out var seq))
        {
            message = $"2号线{code}已有工件缓存或正在取料预约中, 拒绝覆盖";
            return false;
        }
        message = string.Empty;
        Console.WriteLine($"[后引擎2] 人工中转架缓存写入: 中转架={code} 顺序={seq} {wp.IdentityText} 直径={wp.Diameter}mm 版长={wp.Length}mm 缓存总数={CachedCount}");
        return true;
    }

    /// <summary>只按操作员确认的结论转换冻结来源账本；不发送设备命令，也不自动恢复引擎。</summary>
    public string ResolveManualSourceReservation(string rackCode, FlowActionManualResolution resolution)
    {
        if (resolution is not FlowActionManualResolution.StillAtSource and not FlowActionManualResolution.RemovedManually)
            return "2号线来源账本结案只接受“仍在来源位”或“已人工移走”；已确认天车持件/目标待交接请使用斜床专用应急，未修改任何状态。";
        if (!_rackLedger.IsHeldForManualResolution(rackCode, out string operationId, out string reason))
            return $"2号线{rackCode}没有待人工结案的来源缓存";
        if (!_rackLedger.TryResolveManualReservation(rackCode, operationId, resolution, out WorkpieceCache wp, out _))
            return $"2号线{rackCode}来源缓存结案失败；请刷新后重试";

        string result = resolution switch
        {
            FlowActionManualResolution.StillAtSource => "确认工件仍在来源位：保留缓存，解除冻结，允许后续重新派发",
            FlowActionManualResolution.RemovedManually => "确认工件已人工移走：已从来源账本移除",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null)
        };
        if (GetCraneTaskSnapshot().SourceStation == rackCode)
            ClearRearCraneTask();

        // 先完成来源缓存和任务牌的真实结案，再移除仅供诊断的暂停快照。
        FlowActionManualRegistry.Remove(operationId);

        string message = $"[Line2Rear] {rackCode} 动作={operationId} 原因={reason}；{result}。后端保持暂停，需人工复核后再恢复。";
        Console.WriteLine(message);
        return message;
    }

    /// <summary>后天车上料已在斜床待交接时，仅转换软件账本；不补发CNC握手或恢复引擎。</summary>
    public string ResolveManualLoadTargetPending(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "2号线后天车上料" || action.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff)
            return "2号线后天车上料目标结案拒绝：动作不是斜床目标待交接。";
        SkewCtx? bed = _beds.FirstOrDefault(x => x?.Code == action.Target);
        // X11=1 后来源预约已被 CommitPickup 消费；目标结案只能核对斜床已有的工件身份，
        // 不能再把已经离开来源位的工件伪造回来源账本。
        if (bed == null || bed.Wp is not { } wp || wp.IdentityText != action.WorkpieceIdentity)
            return "2号线后天车上料目标结案失败：斜床当前工件与动作账本不一致。";
        bed.St = SkewState.Loading;
        bed.CompletionExported = false;
        _paused = true;
        SetRearCraneTask(wp, "人工确认斜床待交接", action.Source, bed.Code, true, "不自动补发CNC握手");
        FlowActionManualRegistry.Remove(operationId);
        return $"2号线后天车已确认工件在{bed.Code}待交接：斜床置Loading并保留人工任务牌；后端保持暂停。";
    }

    /// <summary>2号线后天车斜床下料的人工结案；不凭人工文字伪造 M818/M821 缓存或 PLC 放料通知。</summary>
    public string ResolveManualUnloadAction(string operationId, FlowActionManualResolution resolution)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot) ||
            snapshot.FlowScope != "2号线后天车下料")
            return $"2号线后天车下料动作账本不存在: {operationId}";
        SkewCtx? bed = _beds.FirstOrDefault(x => x != null && x.Code == snapshot.Source);
        if (bed?.Wp is not { } wp || wp.IdentityText != snapshot.WorkpieceIdentity)
            return "后天车下料结案拒绝：斜床当前工件与动作账本不一致，未改动任何状态。";

        _paused = true;
        switch (resolution)
        {
            case FlowActionManualResolution.StillAtSource:
                bed.St = SkewState.WaitingUnload;
                ClearRearCraneTask();
                FlowActionManualRegistry.Remove(operationId);
                return $"2号线后天车确认{bed.Code}工件仍在斜床：保留工件账本并回WaitingUnload；引擎保持暂停，恢复后会重新下料派发。";
            case FlowActionManualResolution.OnCarrier:
                bed.St = SkewState.Unloading;
                SetRearCraneTask(wp, "人工确认天车持件", bed.Code, snapshot.Target, true,
                    "工件仍由后天车持有；禁止自动重试，等待后续专用应急处理。");
                FlowActionManualRegistry.Remove(operationId);
                return $"2号线后天车确认工件在天车上：斜床账本和人工任务牌保留，后端保持暂停。";
            case FlowActionManualResolution.RemovedManually:
                bed.St = SkewState.Idle;
                bed.Wp = null;
                ClearRearCraneTask();
                FlowActionManualRegistry.Remove(operationId);
                return $"2号线后天车确认{bed.Code}工件已人工移走：斜床工件账本已清，后端保持暂停。";
            case FlowActionManualResolution.AtTargetPendingHandoff:
                return "后天车目标待交接不能直接结案：请先按实际目标补齐/核对M818、M821及PLC放料通知；动作账本、斜床账本和暂停状态均已保留。";
            default:
                return $"不支持的人工结论: {resolution}";
        }
    }

    /// <summary>人工确认已在ST020/ST021后，补目标软件缓存和对应MC63放料通知；失败绝不清斜床账本。</summary>
    public async Task<string> ResolveManualUnloadTargetPendingAsync(string operationId, CancellationToken ct = default)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot) ||
            snapshot.FlowScope != "2号线后天车下料" ||
            snapshot.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff)
            return $"2号线后天车目标待交接动作账本不存在: {operationId}";
        SkewCtx? bed = _beds.FirstOrDefault(x => x != null && x.Code == snapshot.Source);
        if (bed?.Wp is not { } wp || wp.IdentityText != snapshot.WorkpieceIdentity)
            return "目标待交接结案拒绝：斜床当前工件与动作账本不一致，未改动任何状态。";
        _paused = true;
        try
        {
            int bit;
            string cache;
            switch (snapshot.Target)
            {
                case "ST020": cache = "M818"; bit = RackAddr.Bit_ST020_PlaceDone; break;
                case "ST021": cache = "M821"; bit = RackAddr.Bit_ST021_PlaceDone; break;
                default: return $"2号线目标待交接站号无效: {snapshot.Target}；未改动账本。";
            }
            if (OnBalancingRackPlaced == null) return $"{cache}动平衡缓存回调未绑定；账本和斜床状态保留。";
            OnBalancingRackPlaced(cache, wp);
            await WriteMc63HandshakeBitAsync(bit, snapshot.Target == "ST020" ? "M819(ST020放料完成)" : "M821(ST021放料完成)", ct);
            bed.St = SkewState.Idle;
            bed.Wp = null;
            ClearRearCraneTask();
            FlowActionManualRegistry.Remove(operationId);
            return $"2号线后天车确认工件已在{snapshot.Target}：{cache}缓存和PLC放料通知补齐成功，斜床账本已结案；后端保持暂停。";
        }
        catch (Exception ex)
        {
            return $"2号线{snapshot.Target}目标待交接补齐失败: {ex.Message}；动作账本、斜床工件和人工任务牌均保留，后端保持暂停。";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  主循环
    // ═══════════════════════════════════════════════════════════════════
    private async Task Loop(CancellationToken ct)
    {
        // 不得因暂停后重新启动而清空来源账本；应急结案必须由操作员明确工件真实去向。
        Console.WriteLine($"[后引擎2] 保留来源工件账本，当前缓存={CachedCount}");
        Console.WriteLine("[后引擎2] ═══ 开始初始化设备连接 ═══");
        await Init(ct);
        Console.WriteLine("[后引擎2] ══════ 主循环启动 ══════");

        while (!ct.IsCancellationRequested)
        {
            if (_paused) { await Task.Delay(500, ct); continue; }
            try
            {
                _cycleCount++; 
                var ds = DS;
                ds.CycleCount = _cycleCount;
                if (_cycleCount % 50 == 1) Console.WriteLine($"\n┌────── [后引擎2] 第 {_cycleCount} 轮开始 ──────");

                // ═══════════════════════════════════════════════════════════
                //  步骤① 轮询斜床PLC信号
                //    读每台已连接斜床的5个信号: 请求数据/请求上料/夹紧完成/请求下料/尾座张开
                //    同步引擎状态(Idle→Machining→WaitingUnload)
                // ═══════════════════════════════════════════════════════════
                if (_cycleCount % 10 == 1) Console.WriteLine("│ [步骤1] 轮询斜床PMC信号...");
                // 5台斜床并行轮询。2号线全FANUC, 这里只读Worker快照;
                // 不用Task.Run占线程池, 避免离线FANUC影响FlowStatus页面刷新。
                var pollTasks = new Task[5];
                var pollCts = new CancellationTokenSource?[5];
                var pollBeds = new SkewCtx?[5];
                for (int i = 0; i < 5; i++)
                {
                    var b = _beds[i];
                    if (b == null || !b.Ok)
                    {
                        // 离线床不能保留上轮门开true；重连本身不能代替新的门开状态读取。
                        if (b != null) { b.SignalFresh = false; b.DoorOpen = false; }
                        pollTasks[i] = Task.CompletedTask;
                        continue;
                    }
                    b.SignalFresh = false; // 每轮先标无效, PollBed成功后再置true, 防止沿用上一轮请求信号。
                    b.DoorOpen = false;
                    if (Interlocked.CompareExchange(ref b.PollInFlight, 1, 0) != 0)
                    {
                        Interlocked.Increment(ref b.PollVersion);
                        MarkSkewPollFailed(ds, i, b.WpRecoveryNeeded, "上轮信号读取未返回");
                        pollTasks[i] = Task.CompletedTask;
                        continue;
                    }
                    // 单台兜底略长于整体兜底, 真正刷新上限由整体2s控制。
                    var bedCts = new CancellationTokenSource(3_000);
                    pollCts[i] = bedCts;
                    pollBeds[i] = b;
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bedCts.Token);
                    long pollVersion = Interlocked.Increment(ref b.PollVersion);
                    pollTasks[i] = PollBedWithCleanupAsync(b, bedCts, linked, ds, i, pollVersion);
                }
                // 整体2s超时: 单台斜床断线不拖死主循环和FlowStatus刷新。
                var overallTimeout = Task.Delay(2000, ct);
                var allPolls = Task.WhenAll(pollTasks);
                if (await Task.WhenAny(allPolls, overallTimeout) != allPolls)
                {
                    for (int i = 0; i < pollCts.Length; i++)
                    {
                        try { pollCts[i]?.Cancel(); } catch { }
                        if (pollTasks[i]?.IsCompleted == false && pollBeds[i] != null)
                        {
                            Interlocked.Increment(ref pollBeds[i]!.PollVersion);
                            MarkSkewPollFailed(ds, i, pollBeds[i]!.WpRecoveryNeeded, "斜床状态轮询超过2s");
                        }
                    }
                    if (_cycleCount % 10 == 1) Console.WriteLine("│ [步骤1] 斜床状态轮询超过2s, 已取消未完成读, 本轮禁止使用旧信号");
                }

                // ═══════════════════════════════════════════════════════════
                //  步骤② 斜床断线重连 (每20轮一次, fire-and-forget不阻塞)
                //    不连MC65 — 不做动平衡的短工件下料时步骤④按需连
                // ═══════════════════════════════════════════════════════════
                if (_cycleCount % 20 == 1)
                {
                    Console.WriteLine("│ [步骤2] 定期重连检查(每20轮)...");
                    foreach (var b in _beds)
                    {
                        if (b == null || b.Ok) continue;
                        if (b.F is { IsWorkerAlive: true })
                        {
                            // FANUC由FanucSkewBedService独立Worker线程自行退避重连。
                            // 这里不能每10秒重建Worker, 否则会造成旧线程残留和UI状态抖动。
                            if (_cycleCount % 100 == 1)
                                Console.WriteLine($"│   {b.Code} FANUC Worker重连中: {b.F.LatestSnapshot.Error ?? "未连接"}");
                            continue;
                        }
                        if (b.F != null)
                            Console.WriteLine($"│   {b.Code} FANUC Worker已退出, 准备重建...");
                        if (TryIp(b.Code, out var rip, out var rp) && !string.IsNullOrWhiteSpace(rip))
                        {
                            Console.WriteLine($"│   后台重连 {b.Code} {rip}:{rp}...");
                            _ = TryConnectBedBg(b, rip, rp);
                        }
                    }
                    // 同步重连失败计数到 DeviceStatus(合并到重连检查块内)
                    for (int i = 0; i < 5; i++)
                    {
                        var b = _beds[i]; if (b == null) continue;
                        var cnt = b.ConsecutiveReconnectFails;
                        switch (i) { case 0: ds.Skew1ReconnectFails = cnt; break; case 1: ds.Skew2ReconnectFails = cnt; break; case 2: ds.Skew3ReconnectFails = cnt; break; case 3: ds.Skew4ReconnectFails = cnt; break; case 4: ds.Skew5ReconnectFails = cnt; break; }
                    }
                }

                // ═══════════════════════════════════════════════════════════
                //  步骤③ 状态汇总 (每10轮输出一次, 减少日志量)
                // ═══════════════════════════════════════════════════════════
                var fds = _frontDs; // 步骤③和④都用, 声明在if外面
                if (_cycleCount % 10 == 1)
                {
                    Console.WriteLine("│ [步骤3] 状态汇总 ──────────────────────");
                    Console.WriteLine($"│   斜床: {codes[0]}={StCn(0)} {codes[1]}={StCn(1)} {codes[2]}={StCn(2)} {codes[3]}={StCn(3)} {codes[4]}={StCn(4)}");
                    Console.WriteLine($"│   中转架: T1(016)={(fds != null && !fds.TransferRack4Free ? "有版" : "空")} T2(017)={(fds != null && !fds.TransferRack5Free ? "有版" : "空")} T3(018)={(fds != null && !fds.TransferRack6Free ? "有版" : "空")}");
                    Console.WriteLine($"│   ST020放料允许M818={(fds != null && fds.M818CanPlace ? "1" : "0")}  ST021放料允许M820={(fds != null && fds.M820CanPlace ? "1" : "0")}");
                    Console.WriteLine($"│   锁: 后天车锁={_craneRearLock.CurrentCount} ZoneMT={_safety.MarkerTransferCollisionLock.CurrentCount} ZoneTS={_safety.TransferSkew1CollisionLock.CurrentCount}");
                    Console.WriteLine($"│   工件缓存: {CachedCount}个");
                }
                
                // ═══════════════════════════════════════════════════════════
                //  步骤④/⑤ 动态调度：先收集候选，再决定上料还是下料
                //    - 不改DoLoad/DoUnload正常业务步骤
                //    - 中转架放板顺序仍按_wpArrivalSeqs
                //    - 当前可用斜床加工不了的中转架板，在候选期允许跳过，看后面板是否可加工
                // ═══════════════════════════════════════════════════════════
                bool logDetails = _cycleCount % 10 == 1;
                if (logDetails) Console.WriteLine("│ [步骤4/5] 后天车动态调度候选扫描...");
                var unloadScan = await BuildUnloadCandidateScanAsync(fds, logDetails, ct);
                var loadScan = BuildLoadCandidateScan(logDetails);
                //输入 上料候选数 / 下料候选数 / 中转架物理板数 / 可匹配板数 / 有效斜床数  按 6条规则得出 baselineDecision（Load/Unload/None）
                var baselineDecision = RearSkewDispatchPlanner.Decide(
                    loadScan.Candidates.Count,
                    unloadScan.Candidates.Count,
                    loadScan.PhysicalRackPlateCount,
                    loadScan.MatchableRackPlateCount,
                    unloadScan.UsableBedCount);
                var immediatelyDispatchableUnloads = GetImmediatelyDispatchableUnloadCandidates(unloadScan);
                bool loadPathImmediatelyAvailable = IsLoadTransferPathImmediatelyAvailable();
                //让位覆盖（ApplyLoadPathAvailability 
                /*
前提：baseline = Load（第一层说要上料）
检查：上料通道（ZoneMT 和 ZoneTS）是否都空闲？
  ├─ 都空闲 → 保持 Load（正常上料）
  └─ 任一被占（比如前天车打号持 ZoneMT）
      ├─ 有可立即下料候选（普通床，或 ST108/ST606 且 ZoneTS 空闲）
      │    → 覆盖为 Unload（改派下料，不白占后天车锁）
      └─ 没有可立即下料 → 保持 Load（去 DoLoad 等锁）
      */
                var decision = RearSkewDispatchPlanner.ApplyLoadPathAvailability(
                    baselineDecision,
                    loadPathImmediatelyAvailable,
                    immediatelyDispatchableUnloads.Count,
                    DescribeLoadPathAvailability());
                
                string? preferredUnloadBedCode =
                    baselineDecision.Action == RearSkewDispatchAction.Load &&
                    decision.Action == RearSkewDispatchAction.Unload
                        ? immediatelyDispatchableUnloads[0].Bed.Code
                        : null;

                if (preferredUnloadBedCode != null)
                {
                    Console.WriteLine($"│ [后调度] 上料通道暂不可进入，改派立即下料={preferredUnloadBedCode}；未创建上料任务、未抢后天车锁、未预约中转架来源");
                }

                if (_dispatchLogGate.ShouldLog(decision,
                    loadScan.Candidates.Count, unloadScan.Candidates.Count,
                    loadScan.PhysicalRackPlateCount, loadScan.MatchableRackPlateCount, unloadScan.UsableBedCount))
                {
                    Console.WriteLine($"│ [后调度] 决策={decision.Action} 原因={decision.Reason} " +
                                      $"上料候选={loadScan.Candidates.Count} 下料候选={unloadScan.Candidates.Count} " +
                                      $"中转架物理板={loadScan.PhysicalRackPlateCount} 可匹配板={loadScan.MatchableRackPlateCount} " +
                                      $"有效斜床={unloadScan.UsableBedCount} 下料高压阈值={decision.HighUnloadThreshold}");
                }

                if (decision.Action == RearSkewDispatchAction.Load)
                    //派发上料  这里面有doload方法   决策层确实把"前天车占锁（通道被占）考虑进去了
                    await TryDispatchLoadAsync(ct);
                else if (decision.Action == RearSkewDispatchAction.Unload)
                    //下料
                    await TryDispatchUnloadAsync(fds, ct, preferredUnloadBedCode);

                // ── 刷新后天车连接状态到UI ──
                try { ds.CraneRearConnected = _craneCache.GetOrCreateService(CraneRearNo).IsConnected; } catch { }
                // 展示时间戳不参与调度、锁、握手或设备动作。
                ds.SnapshotAtUtc = DateTime.UtcNow;
                if (_cycleCount % 50 == 1) Console.WriteLine($"└────── [后引擎2] 第 {_cycleCount} 轮结束 ──────");
                int delay = _fastNextCycle ? 50 : _cfg.Grinding.PollIntervalMs;
                _fastNextCycle = false;
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[后引擎2] 主循环异常: {ex.Message}\n{ex.StackTrace}"); await Task.Delay(2000, ct); }
        }
        Console.WriteLine("[后引擎2] 主循环退出");
    }

    private string StCn(int i) { var b = _beds[i]; return b == null ? "?" : $"{b.St}({(b.Ok ? "连" : "断")})"; }
    private static readonly string[] codes = { "ST606", "ST607", "ST608", "ST609", "ST610" }; // 2号线全FANUC

    private sealed class LoadPair
    {
        public SkewCtx Bed = null!;
        public string RackCode = string.Empty;
        public WorkpieceCache Wp;
    }

    private sealed class UnloadCandidate
    {
        public SkewCtx Bed = null!;
        public WorkpieceCache Wp;
    }

    private sealed class LoadCandidateScan
    {
        public List<LoadPair> Candidates { get; } = new();
        public int PhysicalRackPlateCount { get; set; }
        public int MatchableRackPlateCount { get; set; }
    }

    private sealed class UnloadCandidateScan
    {
        public List<UnloadCandidate> Candidates { get; } = new();
        public int UsableBedCount { get; set; }
        public bool HasActiveUnloading { get; set; }
    }

    private List<string> GetTransferRackCodesWithPlateSnapshot()
    {
        var racks = new List<string>(3);
        var fd = _frontDs;
        if (fd == null || !fd.RackConnected) return racks;

        if (!fd.TransferRack4Free) racks.Add("ST016");
        if (!fd.TransferRack5Free) racks.Add("ST017");
        if (!fd.TransferRack6Free) racks.Add("ST018");
        Dictionary<string, long> seqSnapshot = _rackLedger.SnapshotArrivalSequences();
        racks.Sort((a, b) =>
        {
            var sa = seqSnapshot.TryGetValue(a, out var seqA) ? seqA : long.MaxValue;
            var sb = seqSnapshot.TryGetValue(b, out var seqB) ? seqB : long.MaxValue;
            var cmp = sa.CompareTo(sb);
            return cmp != 0 ? cmp : TransferRackPhysicalPriority(a).CompareTo(TransferRackPhysicalPriority(b));
        });
        return racks;
    }

    private bool TryGetRackWorkpieceCache(string rackCode, out WorkpieceCache wp)
    {
        return _rackLedger.TryGet(rackCode, out wp);
    }

    private bool RackHasPhysicalPlateSnapshot(string rackCode)
    {
        var fd = _frontDs;
        if (fd == null || !fd.RackConnected) return false;
        return rackCode switch
        {
            "ST016" => !fd.TransferRack4Free,
            "ST017" => !fd.TransferRack5Free,
            "ST018" => !fd.TransferRack6Free,
            _ => false
        };
    }

    private static int TransferRackPhysicalPriority(string rackCode) => rackCode switch
    {
        "ST016" => 0,
        "ST017" => 1,
        "ST018" => 2,
        _ => 99
    };

    // ═════════判断斜床可以上料 空闲+已连接+请求数据══════════════════════════════════════════════════════════
    private List<SkewCtx> GetReadyBedsByPriority()
    {
        var result = new List<SkewCtx>(5);
        // ST606靠近中转架-斜床1碰撞区。ZoneTS忙时临时优先其它斜床,
        // 减少后天车拿着后天车锁等待ZoneTS; 无其它选择时仍允许ST606兜底等待。
        if (_safety.TransferSkew1CollisionLock.CurrentCount == 0)
        {
            for (int i = 1; i < _beds.Length; i++)
                if (IsReadyForLoad(_beds[i]))
                    result.Add(_beds[i]);
            if (IsReadyForLoad(_beds[0]))
            {
                if (result.Count == 0)
                    Console.WriteLine("│   ZoneTS忙且无其它斜床可用, 仍允许ST606等待ZoneTS");
                result.Add(_beds[0]);
            }
            return result;
        }

        foreach (var b in _beds)
            if (IsReadyForLoad(b))
                result.Add(b);
        return result;
    }

    private string BuildReadyBedCapacityText(List<SkewCtx> beds)
    {
        if (beds.Count == 0) return "无";
        var parts = new List<string>(beds.Count);
        foreach (var b in beds)
            parts.Add($"{b.Code}:max={_cfg.SkewBed.GetMaxWorkpieceLengthMm(b.Code)}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// 生成上料候选。
    /// 只做候选扫描，不抢后天车锁、不移除缓存、不启动DoLoad。
    /// 中转架按前天车放板到达顺序扫描；如果某块板当前没有任何可用斜床能加工，则记录并跳过，
    /// 继续看后面的板，避免一块超长/不匹配板把整条后端堵死。
    /// </summary>
    private LoadCandidateScan BuildLoadCandidateScan(bool logDetails)
    {
        var scan = new LoadCandidateScan();
        var beds = GetReadyBedsByPriority();
        var racks = GetTransferRackCodesWithPlateSnapshot();
        scan.PhysicalRackPlateCount = racks.Count;

        if (beds.Count == 0)
        {
            if (logDetails)
            {
                Console.WriteLine("│   [上料候选] 无可用斜床(需同时满足:空闲+已连接+有效快照+CNC请求数据+门开确认)");
                for (int i = 0; i < 5; i++)
                {
                    var b = _beds[i];
                    if (b == null) continue;
                    Console.WriteLine($"│     {b.Code}: St={b.St} Ok={b.Ok} Fresh={b.SignalFresh} RqData={b.RqData} RqLoad={b.RqLoad} 门开确认={(b.SignalFresh ? YN(b.DoorOpen) : "未知")} → {(IsReadyForLoad(b) ? "可用" : "不可用")}");
                }
            }
            return scan;
        }

        if (racks.Count == 0)
        {
            if (logDetails) Console.WriteLine("│   [上料候选] 中转架无物理板");
            return scan;
        }

        foreach (var rackCode in racks)
        {
            if (_rackLedger.IsHeldForManualResolution(rackCode, out _, out string manualReason))
            {
                if (logDetails) Console.WriteLine($"│   [上料候选] {rackCode}来源冻结,跳过该位，不影响其它中转架上料：{manualReason}");
                continue;
            }
            if (!TryGetRackWorkpieceCache(rackCode, out var wp))
            {
                if (logDetails) Console.WriteLine($"│   [上料候选] {rackCode}有物理板但无工件缓存,等待缓存写入/人工确认");
                continue;
            }

            SkewCtx? matchedBed = null;
            foreach (var bed in beds)
            {
                if (_cfg.SkewBed.CanProcessLength(bed.Code, wp.Length))
                {
                    matchedBed = bed;
                    break;
                }
            }

            if (matchedBed == null)
            {
                if (logDetails)
                    Console.WriteLine($"│   [上料候选] 跳过{rackCode}: {wp.IdentityText} L={wp.Length}mm, 当前可用斜床无长度匹配({BuildReadyBedCapacityText(beds)})");
                continue;
            }

            scan.MatchableRackPlateCount++;
            scan.Candidates.Add(new LoadPair { Bed = matchedBed, RackCode = rackCode, Wp = wp });
            if (logDetails)
                Console.WriteLine($"│   [上料候选] ✓ {rackCode} {wp.IdentityText} L={wp.Length} → {matchedBed.Code}(max={_cfg.SkewBed.GetMaxWorkpieceLengthMm(matchedBed.Code)})");
        }

        if (logDetails)
            Console.WriteLine($"│   [上料候选] 物理板={scan.PhysicalRackPlateCount} 可匹配板={scan.MatchableRackPlateCount} 候选={scan.Candidates.Count}");
        return scan;
    }

    /// <summary>
    /// 生成下料候选。
    /// 会保留原来的必要状态同步和完工记录写入，但不会抢后天车锁、不会启动DoUnload。
    /// </summary>
    private async Task<UnloadCandidateScan> BuildUnloadCandidateScanAsync(Line2FrontFlowEngine.Line2DeviceStatus? fds, bool logDetails, CancellationToken ct)
    {
        var scan = new UnloadCandidateScan();
        bool? manipulatorsAlarmFree = null; // 每轮候选扫描只读一次；后天车锁内重扫时自动再次复核。
        foreach (var b in _beds)
        {
            if (b == null || !b.Ok) continue;
            if (!b.SignalFresh)
            {
                Console.WriteLine($"│   {b.Code} 本轮信号未刷新成功, 禁止使用旧信号派发上/下料");
                continue;
            }

            scan.UsableBedCount++;

            if (b.St == SkewState.Machining)
            {
                if (b.RqUnload)
                {
                    b.St = SkewState.WaitingUnload;
                    Console.WriteLine($"│   ✓ {b.Code} 加工完成→请求下料");
                }
                else if (logDetails)
                {
                    Console.WriteLine($"│   {b.Code} 加工中...继续等待");
                }
            }

            if (b.St == SkewState.Unloading)
            {
                if (logDetails) Console.WriteLine($"│   {b.Code} 正在下料中...");
                scan.HasActiveUnloading = true;
                continue;
            }

            if (b.St != SkewState.WaitingUnload) continue;

            if (b.Wp == null)
            {
                Console.WriteLine($"│   {b.Code} 等待下料但缺工件数据(b.Wp=null),跳过(需人工确认)");
                continue;
            }

            var wp = b.Wp.Value;
            if (!b.CompletionExported)
            {
                b.CompletionExported = await SkewCompletionExportHelper.TryAppendAsync(_cfg, b.Code, wp, ct);
                if (!b.CompletionExported)
                {
                    Console.WriteLine($"│   {b.Code} {wp.IdentityText} 完工记录未写入job2, 本轮暂不下料, 下轮重试");
                    continue;
                }
            }

            // 原WaitingUnload同步和job2导出不受门状态影响；门未开仅使本床本轮不入候选。
            // 五台FANUC都使用Worker快照，候选阶段不直接读FOCAS，也不持锁等待开门。
            if (!b.DoorOpen)
            {
                if (logDetails) Console.WriteLine($"│   [下料候选] {b.Code}门开未确认(要求原#1006=1)，跳过本轮");
                continue;
            }

            if (!await CanUnloadDestinationAcceptAsync(fds, wp, ct))
                continue;

            // 机械手2/3任一报警或读不到状态时，只禁止新下料候选，不改变斜床的WaitingUnload状态。
            manipulatorsAlarmFree ??= await ManipulatorAlarmDispatchGate.CanDispatchRearSkewUnloadAsync(
                _manipulatorCache, "2号线", ct);
            if (!manipulatorsAlarmFree.Value)
                continue;

            scan.Candidates.Add(new UnloadCandidate { Bed = b, Wp = wp });
            if (logDetails) Console.WriteLine($"│   [下料候选] ✓ {b.Code} {wp.IdentityText} L={wp.Length}mm");
        }

        if (logDetails)
            Console.WriteLine($"│   [下料候选] 有效斜床={scan.UsableBedCount} 候选={scan.Candidates.Count} 正在下料={scan.HasActiveUnloading}");
        return scan;
    }

    /// <summary>
    /// 后天车上料必须先进入中转架，因此只有 ZoneMT、ZoneTS 同时空闲才视为可立即执行。
    /// 此检查只决定本轮是否让位给下料，实际 DoLoad 仍会重新获取两把锁。
    /// </summary>
    private bool IsLoadTransferPathImmediatelyAvailable()
        => _safety.MarkerTransferCollisionLock.CurrentCount > 0 &&
           _safety.TransferSkew1CollisionLock.CurrentCount > 0;

    private string DescribeLoadPathAvailability()
        => $"ZoneMT={(_safety.MarkerTransferCollisionLock.CurrentCount > 0 ? "空闲" : "占用")}, " +
           $"ZoneTS={(_safety.TransferSkew1CollisionLock.CurrentCount > 0 ? "空闲" : "占用")}";

    /// <summary>
    /// 普通斜床下料不进入共享中转架区；ST606 下料需要 ZoneTS，故 ZoneTS 被占时不算立即可执行。
    /// </summary>
    private List<UnloadCandidate> GetImmediatelyDispatchableUnloadCandidates(UnloadCandidateScan scan)
    {
        bool zoneTsAvailable = _safety.TransferSkew1CollisionLock.CurrentCount > 0;
        var result = new List<UnloadCandidate>();
        foreach (var candidate in scan.Candidates)
        {
            if (candidate.Bed.Code != "ST606" || zoneTsAvailable)
                result.Add(candidate);
        }

        return result;
    }

    private async Task<bool> CanUnloadDestinationAcceptAsync(Line2FrontFlowEngine.Line2DeviceStatus? fds, WorkpieceCache wp, CancellationToken ct)
    {
        bool needsBalancing = wp.Length >= 800 || wp.ForceBalancing;
        if (needsBalancing)
        {
            if (fds == null)
            {
                Console.WriteLine("│   目的地不可用: frontDs=null");
                return false;
            }
            if (!fds.DropRackSnapshotValid)
            {
                Console.WriteLine("│   目的地不可用: ST020/ST021握手快照无效(禁止按旧状态放料)");
                return false;
            }
            if (!fds.M818CanPlace)
            {
                Console.WriteLine("│   目的地不可用: M818=0(ST020未允许天车放料)");
                return false;
            }
            return true;
        }

        try
        {
            using var mcts = new CancellationTokenSource(3000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, mcts.Token);
            var mc63 = await EnsureMc63Async(linked.Token);
            var r = await mc63.ReadMAlignedWordAsync(800, 2, linked.Token);
            var m816 = r.IntValues[1]; // 第2字=M816~M831, M820=bit4
            bool m820CanPlace = (m816 & (1 << RackAddr.Bit_ST021_CanPlace)) != 0;
            Console.WriteLine($"│   M820(ST021允许天车放料)={(m820CanPlace ? "1" : "0")} rawM816=0x{m816:X4}");
            if (!m820CanPlace)
            {
                Console.WriteLine("│   目的地不可用: M820=0(ST021未允许天车放料)");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│   M820按需读MC63失败: {ex.Message},下轮重试");
            return false;
        }
    }

    private async Task TryDispatchLoadAsync(CancellationToken ct)
    {
        Console.WriteLine($"│ [后调度] 尝试派发上料, 抢后天车锁(当前={_craneRearLock.CurrentCount})...");
        //抢锁
        if (!await _craneRearLock.WaitAsync(0, ct))
        {
            Console.WriteLine("│ [后调度] 后天车被占用, 本轮不改派下料");
            return;
        }

        bool triggered = false;
        try
        {
            var scan = BuildLoadCandidateScan(logDetails: true);
            if (scan.Candidates.Count == 0)
            {
                Console.WriteLine("│ [后调度] 上料复核无候选, 释放后天车锁");
                return;
            }
            
            var pair = scan.Candidates[0];
            Console.WriteLine($"│   ▶ 触发上料! 源={pair.RackCode} {pair.Wp.IdentityText} L={pair.Wp.Length} → 目标={pair.Bed.Code}(max={_cfg.SkewBed.GetMaxWorkpieceLengthMm(pair.Bed.Code)})");
            pair.Bed.St = SkewState.Loading;
            SetRearCraneTask(pair.Wp, "准备从中转架取料", pair.RackCode, pair.Bed.Code);
            triggered = true;
            //上料方法
            _ = DoLoad(pair.Bed, pair.RackCode, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│ [后调度] 上料派发复核异常: {ex.Message}");
        }
        finally
        {
            if (!triggered)
            {
                _craneRearLock.Release();
                Console.WriteLine("│ [后调度] 未触发上料, 后天车锁已释放");
            }
        }
    }

    private async Task TryDispatchUnloadAsync(
        Line2FrontFlowEngine.Line2DeviceStatus? fds,
        CancellationToken ct,
        string? preferredBedCode = null)
    {
        Console.WriteLine($"│ [后调度] 尝试派发下料, 抢后天车锁(当前={_craneRearLock.CurrentCount})...");
        if (!await _craneRearLock.WaitAsync(0, ct))
        {
            Console.WriteLine("│ [后调度] 后天车被占用, 本轮不改派上料");
            return;
        }

        bool triggered = false;
        try
        {
            var scan = await BuildUnloadCandidateScanAsync(fds, logDetails: true, ct);
            if (scan.Candidates.Count == 0)
            {
                Console.WriteLine("│ [后调度] 下料复核无候选, 释放后天车锁");
                return;
            }

            UnloadCandidate? candidate = null;
            foreach (var item in scan.Candidates)
            {
                if (string.IsNullOrWhiteSpace(preferredBedCode) || item.Bed.Code == preferredBedCode)
                {
                    candidate = item;
                    break;
                }
            }

            if (candidate == null)
            {
                Console.WriteLine($"│ [后调度] 下料复核时立即下料候选{preferredBedCode}已失效，释放后天车锁等待下轮");
                return;
            }

            candidate.Bed.St = SkewState.Unloading;
            triggered = true;
            Console.WriteLine($"│   ▶ {candidate.Bed.Code} 开始下料! {candidate.Wp.IdentityText} 工件={candidate.Wp.Diameter}mm L={candidate.Wp.Length}mm");
            SetRearCraneTask(candidate.Wp, "准备从斜床下料", candidate.Bed.Code, "分流目的地待确认");
            _ = DoUnload(candidate.Bed, ct);
        }
        finally
        {
            if (!triggered)
            {
                _craneRearLock.Release();
                Console.WriteLine("│ [后调度] 未触发下料, 后天车锁已释放");
            }
        }
    }

    // 初扫和后天车锁后重扫共用门开条件；未满足时只跳过本床，不暂停或清理任务。
    private static bool IsReadyForLoad(SkewCtx? b) => b != null && b.Idle && b.SignalFresh && b.RqData && b.DoorOpen;

    // ═══════════════════════════════════════════════════════════════════
    private async Task PollBedWithCleanupAsync(SkewCtx b, CancellationTokenSource bedCts,
        CancellationTokenSource linkedCts, Line2RearDeviceStatus ds, int idx, long pollVersion)
    {
        try { await PollBed(b, linkedCts.Token, ds, idx, pollVersion); }
        finally
        {
            Interlocked.Exchange(ref b.PollInFlight, 0);
            bedCts.Dispose();
            linkedCts.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    /// <summary>读取单台FANUC斜床六信号Worker快照(含门开)，同步引擎状态。</summary>
    private Task PollBed(SkewCtx b, CancellationToken ct, Line2RearDeviceStatus ds, int idx, long pollVersion)
    {
        string sg = "--", st = "?", fail = "本轮信号未刷新"; bool ok = false;
        bool rqData = false, rqLoad = false, rqUnload = false, clamped = false, doorOpen = false;
        b.SignalFresh = false;
        b.DoorOpen = false;
        try
        {
            // 2号线全FANUC — 从Worker快照读取，不直接调FOCAS
            {
                var snap = b.F!.LatestSnapshot;
                if (snap.Connected)
                {
                    rqData = snap.RqData; rqLoad = snap.RqLoad; clamped = snap.Clamped; rqUnload = snap.RqUnload; doorOpen = snap.DoorOpen;
                    sg = $"请求数据={YN(snap.RqData)} 请求上料={YN(snap.RqLoad)} 夹紧={YN(snap.Clamped)} 请求下料={YN(snap.RqUnload)} 张开={YN(snap.Opened)} 门开确认={YN(snap.DoorOpen)}";
                    if (snap.RqUnload) st = "请求下料"; else if (snap.RqLoad) st = "请求上料"; else if (snap.Clamped) st = "尾座顶紧"; else if (snap.RqData) st = "请求数据"; else st = "空闲";
                    ok = true;
                }
                else
                {
                    fail = snap.Error ?? "FANUC Worker未就绪";
                }
            }
        }
        catch (Exception ex) { fail = ex.Message; Console.WriteLine($"│   {b.Code} 轮询异常: {ex.Message}"); }
        if (Interlocked.Read(ref b.PollVersion) != pollVersion)
        {
            Console.WriteLine($"│   {b.Code} 丢弃过期斜床轮询结果 poll={pollVersion}, current={Interlocked.Read(ref b.PollVersion)}");
            return Task.CompletedTask;
        }

        if (ok)
        {
            b.RqData = rqData; b.RqLoad = rqLoad; b.Clamped = clamped; b.RqUnload = rqUnload;
            b.DoorOpen = doorOpen; // 通过原PollVersion校验才提交，门状态与请求信号属于同一份快照。
            if (clamped && b.St == SkewState.Idle) { b.St = SkewState.Machining; Console.WriteLine($"│   ⚡ {b.Code} 同步: CNC已夹紧 → Machining"); }
            else if (clamped && rqUnload && b.St == SkewState.Machining) { b.St = SkewState.WaitingUnload; Console.WriteLine($"│   ⚡ {b.Code} 同步: 请求下料 → WaitingUnload"); }
            else if (!clamped && rqUnload && b.St == SkewState.Idle) { b.St = SkewState.WaitingUnload; Console.WriteLine($"│   ⚡ {b.Code} 同步: 空闲+请求下料 → WaitingUnload"); }
            else if (!clamped && !rqLoad && !rqData && b.St == SkewState.Machining) { b.St = SkewState.Idle; Console.WriteLine($"│   ⚡ {b.Code} 同步: CNC已释放 → Idle"); }
        }
        else
        {
            b.RqData = b.RqLoad = b.RqUnload = b.Clamped = false;
            b.DoorOpen = false;
        }
        // ── Wp 恢复标记: 引擎重启后CNC显示正在加工/等待下料但本地Wp=null → 需人工确认
        if ((b.St == SkewState.Machining || b.St == SkewState.WaitingUnload) && b.Wp == null)
        {
            if (!b.WpRecoveryNeeded)
            {
                b.WpRecoveryNeeded = true;
                Console.WriteLine($"│   ⚠ {b.Code} CNC状态={b.St} 但Wp=null(引擎重启丢失工件数据) → 需人工确认工件参数后手动处理");
            }
        }
        else if (b.St == SkewState.Idle && b.WpRecoveryNeeded)
        {
            b.WpRecoveryNeeded = false; // 工件已取走或状态恢复, 清除标记
        }
        if (ok)
        {
            b.SignalFresh = true;
            switch (idx) { case 0: ds.Skew1Connected = true; ds.Skew1State = st; ds.Skew1Signals = sg; ds.Skew1NeedsManualWp = b.WpRecoveryNeeded; break; case 1: ds.Skew2Connected = true; ds.Skew2State = st; ds.Skew2Signals = sg; ds.Skew2NeedsManualWp = b.WpRecoveryNeeded; break; case 2: ds.Skew3Connected = true; ds.Skew3State = st; ds.Skew3Signals = sg; ds.Skew3NeedsManualWp = b.WpRecoveryNeeded; break; case 3: ds.Skew4Connected = true; ds.Skew4State = st; ds.Skew4Signals = sg; ds.Skew4NeedsManualWp = b.WpRecoveryNeeded; break; case 4: ds.Skew5Connected = true; ds.Skew5State = st; ds.Skew5Signals = sg; ds.Skew5NeedsManualWp = b.WpRecoveryNeeded; break; }
            if (_cycleCount % 10 == 1) Console.WriteLine($"│   {b.Code} 轮询OK → 状态=[{st}] 信号=[{sg}]");
        }
        else
        {
            MarkSkewPollFailed(ds, idx, b.WpRecoveryNeeded, fail);
        }
        return Task.CompletedTask;
    }

    private static void MarkSkewPollFailed(Line2RearDeviceStatus ds, int idx, bool needsManualWp, string reason)
    {
        string sg = ShortStatus(reason);
        switch (idx)
        {
            case 0: ds.Skew1Connected = false; ds.Skew1State = "读失败"; ds.Skew1Signals = sg; ds.Skew1NeedsManualWp = needsManualWp; break;
            case 1: ds.Skew2Connected = false; ds.Skew2State = "读失败"; ds.Skew2Signals = sg; ds.Skew2NeedsManualWp = needsManualWp; break;
            case 2: ds.Skew3Connected = false; ds.Skew3State = "读失败"; ds.Skew3Signals = sg; ds.Skew3NeedsManualWp = needsManualWp; break;
            case 3: ds.Skew4Connected = false; ds.Skew4State = "读失败"; ds.Skew4Signals = sg; ds.Skew4NeedsManualWp = needsManualWp; break;
            case 4: ds.Skew5Connected = false; ds.Skew5State = "读失败"; ds.Skew5Signals = sg; ds.Skew5NeedsManualWp = needsManualWp; break;
        }
    }

    private static string ShortStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "本轮信号未刷新";
        return text.Length <= 80 ? text : text[..80];
    }

    /// <summary>读取斜床应急处理前的关键软件状态和CNC信号。</summary>
    public string GetSkewEmergencyInfo(string bedCode)
    {
        var bed = FindBed(bedCode);
        if (bed == null) return $"2号线未找到斜床 {bedCode}";

        return string.Join(Environment.NewLine,
            $"线体: 2号线",
            $"斜床: {bed.Code}",
            $"当前斜床状态: {StateText(bed.St)}",
            $"当前工件: {FormatWorkpiece(bed.Wp)}",
            $"当前动作: {ActiveOperationText(bed)}",
            $"后天车锁: {LockText(_craneRearLock)}",
            $"ZoneMT(打号机-中转架): {LockText(_safety.MarkerTransferCollisionLock)}",
            $"ZoneTS(中转架-ST606): {LockText(_safety.TransferSkew1CollisionLock)}",
            $"下料分流锁: M818={YN(bed.ActiveOperation?.IsHeld("M818") == true)} M821={YN(bed.ActiveOperation?.IsHeld("M821") == true)}",
            $"CNC信号: 请求数据={YN(bed.RqData)} 请求上料={YN(bed.RqLoad)} 夹紧={YN(bed.Clamped)} 请求下料={YN(bed.RqUnload)}",
            $"信号新鲜: {(bed.SignalFresh ? "是" : "否")}");
    }

    /// <summary>
    /// 仅复位目标斜床阻塞新上料候选的内存字段。不会写PLC/CNC，也不会清信号、恢复标记、动作账本或锁。
    /// 仅适用于PLC已正常请求数据且后天车尚未派发的人工恢复场景。
    /// </summary>
    public string ResetSkewMemoryState(string bedCode)
    {
        var bed = FindBed(bedCode);
        if (bed == null) return $"2号线未找到斜床 {bedCode}";

        string oldState = StateText(bed.St);
        string oldWorkpiece = bed.Wp?.IdentityText ?? "无";
        bed.St = SkewState.Idle;
        bed.Wp = null;

        string result = $"{bed.Code} 内存状态已复位：状态 {oldState}→空闲，工件 {oldWorkpiece}→无。未写PLC/CNC，未清信号、快照、账本或锁。";
        Console.WriteLine($"[Line2Rear] {result}");
        return result;
    }

    private static string SettleManualRearLoadForEmergency(SkewCtx bed)
    {
        // 斜床应急的前提是操作员已人工处理工件、磁铁和天车安全位置。
        // 已人工放弃本斜床工件时，结清“本线后天车上料 → 本斜床”的持件和目标待交接快照。
        // 来源冻结快照必须保留：X11未确认时工件仍可能在中转架。
        const string flowScope = "2号线后天车上料";
        FlowActionSnapshot[] snapshots = FlowActionManualRegistry.Snapshot()
            .Where(item => item.FlowScope == flowScope &&
                           item.Target == bed.Code &&
                           (item.Disposition == FlowActionDisposition.PauseOnCarrier ||
                            item.Disposition == FlowActionDisposition.PauseAtTargetPendingHandoff))
            .ToArray();
        int onCarrierCount = snapshots.Count(item => item.Disposition == FlowActionDisposition.PauseOnCarrier);
        int targetPendingCount = snapshots.Count(item => item.Disposition == FlowActionDisposition.PauseAtTargetPendingHandoff);
        foreach (FlowActionSnapshot snapshot in snapshots)
            FlowActionManualRegistry.Remove(snapshot.OperationId);
        return snapshots.Length == 0
            ? "动作账本=无后天车持件或目标待交接快照"
            : $"动作账本=已结清持件{onCarrierCount}条、目标待交接{targetPendingCount}条后天车上料快照(工件已人工移走/放弃)";
    }

    private static string SettleManualRearUnloadOnCarrierForEmergency(SkewCtx bed)
    {
        // 下料目标待交接表示工件可能已在 M818/M821，必须走目标位专用结案器。
        const string flowScope = "2号线后天车下料";
        FlowActionSnapshot[] snapshots = FlowActionManualRegistry.Snapshot()
            .Where(item => item.FlowScope == flowScope &&
                           item.Source == bed.Code &&
                           item.Disposition == FlowActionDisposition.PauseOnCarrier)
            .ToArray();
        foreach (FlowActionSnapshot snapshot in snapshots)
            FlowActionManualRegistry.Remove(snapshot.OperationId);
        return snapshots.Length == 0
            ? "动作账本=无后天车下料持件快照"
            : $"动作账本=已结清{snapshots.Length}条后天车下料持件快照(工件已人工移走/放弃)";
    }

    /// <summary>
    /// 人工处理完成后的上位机应急清空。
    /// 只清本地斜床状态/缓存和释放软件锁, 不给天车或CNC写动作指令。
    /// </summary>
    public string EmergencyClearSkewBed(string bedCode)
        => EmergencyClearSkewBedAsync(bedCode, skipDeviceClear: true, resumeAfterClear: false).GetAwaiter().GetResult();

    public async Task<string> EmergencyClearSkewBedAsync(string bedCode, bool skipDeviceClear,
        bool resumeAfterClear, CancellationToken ct = default)
    {
        var bed = FindBed(bedCode);
        if (bed == null) return $"2号线未找到斜床 {bedCode}";

        _paused = true;
        var operation = bed.ActiveOperation;
        string? wpText;
        SkewState oldState;
        lock (_wpLock)
        {
            wpText = bed.Wp?.IdentityText;
            oldState = bed.St;
        }

        var logs = new List<string>();
        if (operation != null)
        {
            operation.CancelAndInvalidate();
            logs.Add($"当前动作={operation.Name} v{operation.Version} 已取消并失效");

            if (!await WaitForOperationExitAsync(operation, ct))
            {
                string timeoutMessage = $"[Line2Rear] [斜床应急作废] {bed.Code} 旧动作6秒内未退出; " +
                    $"{string.Join("; ", logs)}; 后端保持暂停, 未释放软件锁, 未清软件缓存/设备寄存器, 请稍后重试";
                Console.WriteLine(timeoutMessage);
                return timeoutMessage;
            }
            logs.Add("旧动作=已确认退出");

            // 必须先确认旧动作退出，再释放finally仍未能释放的残留锁。
            // 先释放会让其它引擎在旧SDK调用尚未返回时进入同一物理区域。
            logs.Add(ReleaseLeaseLock(operation, "RearCrane", _craneRearLock, "后天车锁"));
            logs.Add(ReleaseLeaseLock(operation, "ZoneMT", _safety.MarkerTransferCollisionLock, "ZoneMT(打号机-中转架)"));
            logs.Add(ReleaseLeaseLock(operation, "ZoneTS", _safety.TransferSkew1CollisionLock, "ZoneTS(中转架-ST606)"));
            logs.Add(ReleaseLeaseLock(operation, "M821", _lockM821, "M821分流锁"));
            logs.Add(ReleaseLeaseLock(operation, "M818", _lockM818, "M818分流锁"));
            if (ReferenceEquals(bed.ActiveOperation, operation)) bed.ActiveOperation = null;
        }
        else
        {
            logs.Add("当前动作=无");
        }

        string deviceClearLog;
        if (skipDeviceClear)
        {
            deviceClearLog = "设备寄存器=用户确认跳过清零, 仅清上位机软件状态";
        }
        else
        {
            try
            {
                deviceClearLog = await ClearSkewDeviceEmergencyRegistersAsync(bed, ct);
            }
            catch (Exception ex)
            {
                string failure = $"设备侧清零失败: {ex.Message}{Environment.NewLine}" +
                                 "旧动作已退出且软件锁已释放, 但未清上位机缓存。请确认是否仅清上位机软件状态。";
                Console.WriteLine($"[Line2Rear] [斜床应急作废] {bed.Code} {failure.Replace(Environment.NewLine, " ")}");
                return failure;
            }
        }

        lock (_wpLock)
        {
            bed.Wp = null;
            bed.St = SkewState.Idle;
            bed.RqData = bed.RqLoad = bed.RqUnload = bed.Clamped = false;
            bed.SignalFresh = false;
            bed.DoorOpen = false;
            bed.CompletionExported = false;
            bed.WpRecoveryNeeded = false;
            bed.X11UnloadMissWarningShown = false;
            Interlocked.Increment(ref bed.PollVersion);
        }

        // 仅应急软件状态已成功清空后才清展示任务；超时/失败路径会在此前return，保留旧身份供人工确认。
        ClearRearCraneTask();
        MarkSkewManualCleared(bed);
        logs.Add(SettleManualRearLoadForEmergency(bed));
        logs.Add(SettleManualRearUnloadOnCarrierForEmergency(bed));
        logs.Add(deviceClearLog);
        if (resumeAfterClear && IsRunning) _paused = false;
        _fastNextCycle = true;

        string message = $"[Line2Rear] [斜床应急作废] {DateTime.Now:yyyy-MM-dd HH:mm:ss} 操作员=本机 斜床={bed.Code} 原状态={StateText(oldState)} 工件={wpText ?? "无"}; {string.Join("; ", logs)}; 后端={(IsPaused ? "保持暂停" : "已恢复")}";
        Console.WriteLine(message);
        return message + Environment.NewLine + GetSkewEmergencyInfo(bed.Code);
    }

    private static async Task<string> ClearSkewDeviceEmergencyRegistersAsync(SkewCtx bed, CancellationToken ct)
    {
        if (bed.F != null)
        {
            await bed.F.ClearEmergencyRegistersAsync(ct);
            return "设备寄存器=FANUC #800/#801/#802/#909/#1101~#1105 已清零";
        }
        throw new InvalidOperationException($"{bed.Code}斜床通信服务未注入或未连接, 设备寄存器未清零");
    }

    /// <summary>自动上料前只清目标斜床的命令输出，不清尺寸/堵孔参数、FANUC #909或机床反馈。</summary>
    private static async Task ClearSkewLoadCommandOutputsAsync(SkewCtx bed, CancellationToken ct)
    {
        if (bed.F != null)
        {
            await bed.F.ClearUpperComputerCommandOutputsAsync(ct);
            return;
        }
        throw new InvalidOperationException($"{bed.Code}斜床通信服务未注入或未连接，无法清理上料前命令输出");
    }

    private SkewCtx? FindBed(string bedCode)
    {
        foreach (var bed in _beds)
            if (bed != null && string.Equals(bed.Code, bedCode, StringComparison.OrdinalIgnoreCase))
                return bed;
        return null;
    }

    private static string StateText(SkewState st) => st switch
    {
        SkewState.Idle => "空闲",
        SkewState.Loading => "上料中",
        SkewState.Machining => "加工中",
        SkewState.WaitingUnload => "等待下料",
        SkewState.Unloading => "下料中",
        _ => st.ToString()
    };

    private static string LockText(SemaphoreSlim sem) => sem.CurrentCount > 0 ? "空闲" : "占用";

    private static string ActiveOperationText(SkewCtx bed)
        => bed.ActiveOperation == null
            ? "无"
            : $"{bed.ActiveOperation.Name} v{bed.ActiveOperation.Version}(后天车锁={YN(bed.ActiveOperation.IsHeld("RearCrane"))} ZoneMT={YN(bed.ActiveOperation.IsHeld("ZoneMT"))} ZoneTS={YN(bed.ActiveOperation.IsHeld("ZoneTS"))} 分流M818={YN(bed.ActiveOperation.IsHeld("M818"))} 分流M821={YN(bed.ActiveOperation.IsHeld("M821"))} 超时待人工确认={YN(bed.ActiveOperation.RequiresManualSafetyRecovery)})";

    private static EmergencyActionLease BeginSkewOperation(SkewCtx bed, string name, CancellationToken parent, bool holdsTransferRack)
    {
        // 每轮新建有效租约并递增床的动作版本；上轮异常暂停不会使新租约失效。
        // IsValid只由应急取消使其失效，_paused是另一层派发门，不能混为同一个判断。
        var operation = new EmergencyActionLease(Interlocked.Increment(ref bed.NextOperationVersion), name, parent);
        operation.MarkHeld("RearCrane");
        if (holdsTransferRack) operation.MarkHeld("TransferRack");
        bed.ActiveOperation = operation;
        return operation;
    }

    private static void EndSkewOperation(SkewCtx bed, EmergencyActionLease operation)
    {
        // 动作已经退出时，不再让已释放的软件锁以“活动动作”形式残留。
        // 位置未知/待人工确认状态由任务牌、斜床工件和异常监控保存。
        if (ReferenceEquals(bed.ActiveOperation, operation)) bed.ActiveOperation = null;
        operation.Complete();
    }

    private static async Task<bool> WaitForOperationExitAsync(EmergencyActionLease operation, CancellationToken ct)
    {
        var timeout = Task.Delay(6000, ct);
        return await Task.WhenAny(operation.Completion, timeout) == operation.Completion;
    }

    private static string ReleaseLeaseLock(EmergencyActionLease operation, string key,
        SemaphoreSlim? gate, string name)
        => operation.TryRelease(key, gate) ? $"{name}=已释放" : $"{name}=当前动作未持有, 未释放";
    private static string FormatWorkpiece(WorkpieceCache? wp)
    {
        if (!wp.HasValue) return "无";
        var value = wp.Value;
        return $"{value.IdentityText} 直径={value.Diameter} 长度={value.Length}";
    }

    private static string ReleaseIfBusy(SemaphoreSlim sem, string name)
    {
        if (sem.CurrentCount > 0) return $"{name}=原本空闲";
        try
        {
            sem.Release();
            return $"{name}=已应急释放";
        }
        catch (SemaphoreFullException)
        {
            return $"{name}=已满, 无需释放";
        }
        catch (ObjectDisposedException)
        {
            return $"{name}=已释放/已销毁";
        }
    }

    private static string ReleaseIfHeld(bool held, SemaphoreSlim sem, string name, Action clearHeld)
    {
        if (!held) return $"{name}=当前斜床未持有, 未释放";
        string result = ReleaseIfBusy(sem, name);
        clearHeld();
        return result;
    }

    private static string ReleaseNullableIfHeld(bool held, SemaphoreSlim? sem, string name, Action clearHeld)
    {
        if (!held) return $"{name}=当前斜床未持有, 未释放";
        if (sem == null)
        {
            clearHeld();
            return $"{name}=未配置锁对象, 已清持有标记";
        }
        string result = ReleaseIfBusy(sem, name);
        clearHeld();
        return result;
    }

    private static void SafeRelease(SemaphoreSlim sem, string name)
    {
        try { sem.Release(); }
        catch (SemaphoreFullException) { Console.WriteLine($"│ [应急] {name} 已被释放, 跳过重复Release"); }
        catch (ObjectDisposedException) { Console.WriteLine($"│ [应急] {name} 已销毁, 跳过Release"); }
    }

    private void MarkSkewManualCleared(SkewCtx bed)
    {
        int idx = Array.IndexOf(_beds, bed);
        string sg = "人工清空, 等待下轮刷新";
        switch (idx)
        {
            case 0: DS.Skew1Connected = bed.Ok; DS.Skew1State = "人工清空"; DS.Skew1Signals = sg; DS.Skew1NeedsManualWp = false; break;
            case 1: DS.Skew2Connected = bed.Ok; DS.Skew2State = "人工清空"; DS.Skew2Signals = sg; DS.Skew2NeedsManualWp = false; break;
            case 2: DS.Skew3Connected = bed.Ok; DS.Skew3State = "人工清空"; DS.Skew3Signals = sg; DS.Skew3NeedsManualWp = false; break;
            case 3: DS.Skew4Connected = bed.Ok; DS.Skew4State = "人工清空"; DS.Skew4Signals = sg; DS.Skew4NeedsManualWp = false; break;
            case 4: DS.Skew5Connected = bed.Ok; DS.Skew5State = "人工清空"; DS.Skew5Signals = sg; DS.Skew5NeedsManualWp = false; break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DoLoad  取料放斜床
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>上料流程: 中转架取料→X11检测→送斜床→握手→标记加工中</summary>
    private async Task DoLoad(SkewCtx bed, string rs, CancellationToken ct)
    {
        int pickupStableDelayMs = _cfg.Grinding.X11StableDelayMs;
        //生成动作id 其实是为了方便查看日志
        string actionId = OperationalEventContextFactory.NewActionId("L2-REAR-LOAD");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        bool transferLocked = false; // 新Zone模型下不再使用_transferRackLock作为中转架业务互斥
        WorkpieceCache wp;
        long rackArrivalSeq;
        // 预约来源位不是取走：在X11确认前，缓存仍属于中转架，异常不会丢失工件身份。预约设计
        if (!_rackLedger.TryReserve(rs, actionId, out wp, out rackArrivalSeq))
        {
            _craneRearLock.Release();
            bed.St = SkewState.Idle;
            Console.WriteLine($"[上料] ❌ 失败: 中转架{rs}没有可用工件缓存（为空或已被其它动作预约）");
            return;
        }
        // 预约成功后才拥有真实工件身份；后续异常一律以这个动作账本的事实为准。
        var action = new FlowActionContext(actionId, "2号线后天车上料", "4号天车", wp.IdentityText, rs, bed.Code, rs);
        //using var 的意思是：当前作用域结束时自动调用对象的 Dispose() 适用于文件 数据库连接 锁 事务 设备操作上下文 日志 Scope
        using var logAction = OperationalLog.BeginAction("2号线后天车", "4号天车", actionId, wp.IdentityText, rs, bed.Code);
        using var alarmPresentation = FlowAlarmPresentationContext.Begin(action, wp, "2号线", 4, "斜床上料");
        OperationalLog.Info("搬运动作开始", "已创建后天车上料任务", ("板号与序号", wp.IdentityText));
        action.BeginStep(FlowActionStep.SourceReserved);
        action.SetOwnership(FlowWorkpieceOwnership.ReservedAtSource);
        if (!_cfg.SkewBed.CanProcessLength(bed.Code, wp.Length))
        {
            _rackLedger.ReleaseReservation(rs, actionId);
            _craneRearLock.Release();
            bed.St = SkewState.Idle;
            Console.WriteLine($"[上料] ❌ 失败: {wp.IdentityText} L={wp.Length}mm 超出 {bed.Code} 最大加工长度 {_cfg.SkewBed.GetMaxWorkpieceLengthMm(bed.Code)}mm, 已退回中转架缓存");
            return;
        }
        wp.ReportStage($"2号线 后端上斜床 {bed.Code}");
        bool mag = false;
        bool discardOnFailure = false;
        bool holdingWorkpiece = false; // 只有X11确认后才算工件已经离开中转架并由后天车持有
        // 只要物理写命令已开始调用，通信异常就不能再推定“命令没有发出”。
        bool physicalCommandStarted = false;
        bool requiresManualConfirmation = false;
        bool sharedLocked = false; // ST606上料取完中转架后继续持有ZoneTS, 直到斜床1交互后退避释放
        var loadState = new LoadHandshakeState();
        string? pendingSafetyAlarm = null; // 异常正文先保留，等finally完成退避和锁释放后只弹一次完整信息。
        CraneService? cr = null;
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(actionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "2号线", "后端引擎", "后天车", CraneRearNo.ToString(), rs,
            $"{rs}取料并放入{bed.Code}", actionId, $"{actionId}:rear-load", wp,
            rs, bed.Code, "2号线后天车", rs, "中转架工件缓存已取出",
            "ZoneMT/ZoneTS/后天车锁"));
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, $"{bed.Code}CNC上料完成");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{bed.Code}上料完整交接");
        using var operation = BeginSkewOperation(bed, "上料", ct, transferLocked);
        alarmPresentation.ObserveFinalState(() => FlowAlarmPresentationContext.DescribeLocks(operation),
            () => FlowAlarmPresentationContext.DescribeRackCache(_rackLedger, rs, actionId, wp));
        ct = operation.Token;
        Console.WriteLine($"\n┌── [上料] 开始 ──────────────────────────");
        Console.WriteLine($"│ 源={rs} → 目标={bed.Code}  {wp.IdentityText}  直径={wp.Diameter}mm  版长={wp.Length}mm  堵孔={wp.BoreType}mm");
        try
        {
            await ClearSkewLoadCommandOutputsAsync(bed, ct);
            // 中转架位于打号机和斜床1中间，进入中转架取料必须同时持有ZoneMT+ZoneTS。
            // 取料完成且Z轴升到安全高度后：
            //   - 普通斜床：释放ZoneMT+ZoneTS；
            //   - ST606：释放ZoneMT，继续持有ZoneTS到斜床1交互和X+1000退避结束。
            Console.WriteLine("│ [上料] 获取ZoneMT(打号机↔中转架)...");
            //获取锁
            await operation.AcquireAsync("ZoneMT", _safety.MarkerTransferCollisionLock, ct);
            action.RegisterLock("ZoneMT");
            Console.WriteLine("│ ZoneMT已获取 ✓");
            Console.WriteLine("│ [上料] 获取ZoneTS(中转架↔ST606)...");
            //获取锁
            await operation.AcquireAsync("ZoneTS", _safety.TransferSkew1CollisionLock, ct);
            action.RegisterLock("ZoneTS");
            sharedLocked = bed.Code == "ST606";
            Console.WriteLine("│ ZoneTS已获取 ✓，当前可进入中转架取料");

            // 4号后天车上料已持有ZoneMT+ZoneTS，实时读3号实际X<=本线独立阈值。
            // 不满足时持锁每500ms无限等，不移动、不清工件；取消/通信异常沿用原动作收尾。
            // 等待中继续校验动作租约，满足后仍执行原来源身份和目标机请求数据复核。
            await CranePeerXGate.WaitAsync(
                _craneCache.GetOrCreateService(3), 4, 3,
                () => _cfg.CranePeerXAdmission.Crane4MaxCrane3X, requireAtLeast: false, ct: ct,
                validateAction: operation.ThrowIfInvalid);

            if (!RackHasPhysicalPlateSnapshot(rs))
                throw new InvalidOperationException($"中转架{rs}物理信号无板, 禁止后天车取料; 软件缓存将恢复等待人工/下轮确认");
            // 锁后复核（获取ZoneMT/ZoneTS可能持续较久）：
            //   ① 来源预约必须仍属于本动作、且中转架缓存身份未变（防止等锁期间工人取走+前台补入新板）；
            //   ② 目标斜床必须仍处于“请求数据”状态。
            // 任一不满足：本趟尚未取料，解除预约、目标床回Idle，释放锁等待下一轮。
            bool sourceUnchanged =
                _rackLedger.IsReserved(rs, out string reservedOp) &&
                StringComparer.Ordinal.Equals(reservedOp, actionId) &&
                _rackLedger.TryGet(rs, out WorkpieceCache currentWp) &&
                StringComparer.Ordinal.Equals(currentWp.IdentityText, wp.IdentityText);
            bool targetRequestData = false;
            try
            {
                if (bed.F != null) targetRequestData = await bed.F.IsRequestDataAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"│ [上料] {bed.Code} 锁后复核请求数据读取失败: {ex.Message}");
            }
            if (!sourceUnchanged || !targetRequestData)
            {
                _rackLedger.ReleaseReservation(rs, actionId);
                bed.St = SkewState.Idle;
                Console.WriteLine($"[上料] ⏳ 锁后复核未通过(来源未变={sourceUnchanged}, {bed.Code}请求数据={targetRequestData}), 未取中转架{rs}工件, 释放预约等待下一轮");
                return;
            }
            cr = _craneCache.GetOrCreateService(CraneRearNo);
            if (!cr.IsConnected)
            {
                Console.WriteLine("│ 后天车未连接,连接中...");
                await cr.ConnectAsync(ct);
            }

            int sz = _cfg.Grinding.SafeZHeight;
            if (!TryCoords(rs, out int rx, out int ry, out int rz)) throw new Exception($"缺少中转架{rs}坐标");
            int pz = Pz(rz, wp.Diameter);
            Console.WriteLine($"│ [取料] 中转架{rs} {wp.IdentityText} 坐标=({rx},{ry},{rz}) 取料Z={pz + _oz} 安全高度={sz}");
            //设置速度 (2号线后天车=4号)
            var crSpd = _cfg.GetCraneSpeed(CraneRearNo);
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置后天车绝对速度");
            physicalCommandStarted = true;
            action.MarkCommandSent();
            await cr.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel,
                crSpd.Y.Speed, crSpd.Y.Accel, crSpd.Y.Decel,
                crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);
            action.Confirm();
            // (Zone锁已在try前获取, 此处不需重复检查)

            // 安全: 取料前检查天车磁铁是否已有工件(断电/急停重启后X11不受PLC内存影响)
            operationalTracker.BeginX11Stage("取料前残留检查");
            operationalTracker.BeginX11Attempt();
            bool hasRoller;
            try
            {
                hasRoller = await cr.ReadXBitAsync(63497, ct);
                operationalTracker.CompleteX11(hasRoller, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    operationalSite with { ActionStage = $"{rs}取料前X11残留检查" }, operationalTracker, ex,
                    "取料前X11读取失败，当前值无效；保留最后成功值（如有）", true);
                throw;
            }
            physicalTracker?.TryObserveX11(hasRoller);
            if (hasRoller)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    operationalSite with { ActionStage = $"{rs}取料前发现磁铁已有工件" }, operationalTracker, null,
                    "取料前X11成功读取为1，确认磁铁上存在身份未知的残留工件", false, holdingWorkpiece: true);
                throw new InvalidOperationException("后天车X11=1(磁铁已有工件), 拒绝取料防止碰撞,需人工确认");
            }
            Console.WriteLine("│ [取料] step1: Z回原点");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "取料前Z回原点");
            try
            {
                action.MarkCommandSent();
                await cr.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
                action.Confirm(new FlowActionPosition(null, null, 0), "取料前Z已回原点");
                physicalTracker?.TryConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "中转架取料前Z回原点命令成功返回");
            }
            catch (PressureStopException)
            {
                await cr.RecoverFromPressureStopAsync(ct);
                // 仅完成报警复位，不代表Z已到位；必须交给外层暂停分支，禁止继续取料。
                throw;
            }

            Console.WriteLine($"│ [取料] step2: XY到中转架({rx + _ox},{ry + _oy})");
            action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(rx + _ox, ry + _oy, null), "XY到中转架来源位");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(rx + _ox, ry + _oy, -1, ct: ct);
            action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, 0), "来源位XY命令已到位");
            action.BeginStep(FlowActionStep.FineTuneSource, new FlowActionPosition(rx + _ox, ry + _oy, 0), "来源位XY绝对编码器微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                cr, _cfg, CraneRearNo, rs, $"2号线后天车-{rs}取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "2号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: rs,
                    actionStage: $"{rs}中转架取料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation(rs, "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "已选斜床目标"),
                    owner: "2号线后天车", targetZ: EvidenceValue<int>.Confirmed(pz + _oz, "已有中转架取料Z公式与偏移"),
                    physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, true, "Z回原点命令已返回", rs)),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, 0), "来源位XY微调完成");
            Console.WriteLine($"│ [取料] step3: Z下降到{pz + _oz}");
            loadState.ZMayBeDown = true;
            // 来源中转架此时只是预约，尚未消费。共用组件先读放磁反馈再下降，
            // 按本次配置探测（默认实际Z0/+5/+10），充磁后X11=1才算持件；原Z偏移只用于首次目标。
            // 每次取板IO前复核本动作租约，应急失效后不得继续运动或充磁。
            var pickup = await PickupProbeHelper.PickAsync(cr, pz + _oz, pickupStableDelayMs,
                action, operationalTracker, ct, validateAction: operation.ThrowIfInvalid,
                settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm));
            int currentZ = pickup.ActualZ;
            mag = true;
            holdingWorkpiece = true;
            physicalTracker?.TryObserveX11(true);
            // 只有充磁成功且充磁后X11=1才提交中转架取走。
            if (!_rackLedger.CommitPickup(rs, actionId, out _, out _))
                throw new InvalidOperationException($"中转架{rs}工件预约提交失败");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece);
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
            action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, sz), "取料后Z已升安全高度");
            physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "中转架取料后Z升安全命令成功返回");
            Console.WriteLine("│ [上料] 工件已取走且Z已安全, 按现场策略释放中转架侧Zone锁");
            if (!TryCoords(bed.Code, out int bx, out int by, out int bz)) throw new Exception($"缺少斜床{bed.Code}坐标");
            Console.WriteLine($"│ [移动] 天车到斜床{bed.Code} XY=({bx + _ox},{by + _oy}) Z保持{sz}");
            if (bed.Code == "ST606")
            {
                operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                Console.WriteLine("│ [上料] ST606目标: Z已安全, 释放ZoneMT, 继续持有ZoneTS到斜床1退避完成");
            }
            else
            {
                operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                Console.WriteLine("│ [上料] 普通斜床目标: Z已安全, ZoneTS+ZoneMT已释放");
            }
            //后天车拿着料移动到斜床
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(bx + _ox, by + _oy, sz), "持件XY到斜床目标上方");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(bx + _ox, by + _oy, -1, ct: ct);
            var posAfter = await cr.ReadStatusAsync(ct);
            if (posAfter == null) throw new InvalidOperationException("后天车状态读取为空, 无法确认XY到位");
            Console.WriteLine($"│ [移动] XY到位确认: X={posAfter.XPos}(目标{bx + _ox}) Y={posAfter.YPos}(目标{by + _oy})");
            if (Math.Abs(posAfter.XPos - (bx + _ox)) > 50 || Math.Abs(posAfter.YPos - (by + _oy)) > 50)
            {
                Console.WriteLine("│ [移动] ⚠ XY未到位! 重试...");
                await cr.MoveAbsoluteAsync(bx + _ox, by + _oy, -1, ct: ct);
            }
            action.Confirm(new FlowActionPosition(posAfter.XPos, posAfter.YPos, posAfter.ZPos), "持件XY到目标上方已确认");
            action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床目标XY绝对编码器微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                cr, _cfg, CraneRearNo, bed.Code, $"2号线后天车-{bed.Code}上料放斜床前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "2号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: bed.Code,
                    actionStage: $"{bed.Code}上料放斜床前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation(rs, "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "已选斜床目标"),
                    owner: "2号线后天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后的DoHandshake中计算bz-Round(d/2)+_oz"),
                    physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(true, holdingWorkpiece, physicalTracker, "中转架取料X11=1且Z已升安全", "天车/斜床上方")),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床目标XY微调完成");

            // 到这里才绑定斜床工件: X11已确认吸住、Z已升安全、后天车已到目标斜床上方。
            // 在此之前异常按“工件仍在中转架/后天车”处理, 避免Idle斜床残留旧Wp。
            bed.Wp = wp;
            SetRearCraneTask(wp, "斜床上料握手中", rs, bed.Code);
            bed.CompletionExported = false; // 新工件开始加工前清除job2导出标志, 完工时只追加一次。
            Console.WriteLine("│ [握手] 天车到位,开始与CNC通讯");
            //开始握手流程
            await DoHandshake(cr, bed, wp, sz, bx, by, bz, loadState, action, operationalTracker, operationalSite, ct);
            Console.WriteLine("│ [上料] Z回原点");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "上料完成后Z回原点");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
            action.Confirm(new FlowActionPosition(null, null, 0), "上料完成后Z已回原点");
            bed.St = SkewState.Machining;
            wp.ReportStage($"2号线 斜床加工中 {bed.Code}");
            ClearRearCraneTask();
            action.Complete("斜床上料、CNC通知和天车回位均已完成");
            Console.WriteLine($"│ [上料] ✓ 完成 {wp.IdentityText} {bed.Code}→加工中");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine($"│ [上料] ⚠ {bed.Code} 当前动作已被应急取消, 不再继续写CNC/天车动作");
        }
        // 应急可能先取消，SDK稍后仍返回运动超时而非取消异常；必须先拒绝失效旧动作。
        // 普通故障只置_paused，不使operation失效，所以有效主动作仍正常进入作废收尾。
        catch (CraneMotionTimeoutException ex)
        {
            if (!operation.IsValid) return;
            _paused = true;
            discardOnFailure = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnRearCraneSafetyAlarm?.Invoke($"2号线后天车{bed.Code}异常：{ex.Message}。线路已暂停，正在按本次动作作废收尾；请勿恢复或删除下一板。");
            // 运动超时意味着位置未知：暂停且禁止自动恢复动作；软件锁在finally释放，
            // 冻结本板及现场证据，finally再按本动作归属作废；不保留旧任务自动重试。
            requiresManualConfirmation = true;
            action.PauseForManualResolution(ex.Message);
            if (action.Ownership == FlowWorkpieceOwnership.ReservedAtSource)
                _rackLedger.HoldForManualResolution(rs, actionId, ex.Message);
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = $"{action.Step}：天车运动超时" }, operationalTracker, ex,
                "后天车运动超时，动作账本已暂停并保留来源预约，等待人工确认。", true,
                holdingWorkpiece: holdingWorkpiece, actionSnapshot: action.Snapshot());
            bed.St = SkewState.Loading;
            bed.Wp = wp;
            SetRearCraneTask(wp, "运动超时，位置未知", rs, bed.Code, true,
                $"{ex.Stage}超时；未到位轴={string.Join("/", ex.UnreachedAxes)}；等待人工确认");
            pendingSafetyAlarm = $"2号线后天车给{bed.Code}上料时{ex.Stage}运动超时。" +
                $"目标=({ex.XTarget},{ex.YTarget},{ex.ZTarget})，最后坐标={ex.LastKnownStatus?.XPos}/{ex.LastKnownStatus?.YPos}/{ex.LastKnownStatus?.ZPos}，" +
                $"未到位轴={string.Join("/", ex.UnreachedAxes)}，D4518停止={(ex.EmergencyStopCommandSucceeded ? "已发送" : "发送失败")}。" +
                "引擎已暂停；本动作软件锁将在收尾后释放，本板软件任务将在收尾作废，现场位置须人工确认，禁止自动重试。";
            Console.WriteLine($"│ [上料] ⚠ {pendingSafetyAlarm}");
        }
        //处理异常
        catch (Exception ex)
        {
            if (!operation.IsValid) return;
            _paused = true;
            discardOnFailure = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnRearCraneSafetyAlarm?.Invoke($"2号线后天车{bed.Code}异常：{ex.Message}。线路已暂停，正在按本次动作作废收尾；请勿恢复或删除下一板。");
            // 停止后禁止finally追加横移、下探或退磁；保留实际现场供人工处理。
            operation.HoldForManualSafetyRecovery(ex.Message);
            requiresManualConfirmation = true;
            if (!action.IsFinalized) action.PauseForManualResolution(ex.Message);
            pendingSafetyAlarm = $"2号线后天车{bed.Code}上料严重异常：{ex.Message}。本次板任务作废，来源仅按预约和到达代次清理，设备内存将回Idle；请人工移走本板，确认天车安全及实际放磁后恢复。";
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, ex is PickupFailedException pickupFailure ? pickupFailure.EventCode : "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "严重异常作废" }, operationalTracker, ex,
                pendingSafetyAlarm, true, actionSnapshot: action.Snapshot());
        }
        finally
        {
            // 仅严重失败且仍归本动作时作废本床；来源按预约、代次和完整身份清理。
            // 普通取消、失效动作和已完成任务不套用这次作废。
            if (discardOnFailure && operation.IsValid && ReferenceEquals(bed.ActiveOperation, operation))
            {
                // 同时校验本架位预约动作、到达代次和完整身份，只删本次未消费的来源。
                // 已取走或架位已有新板则不删；仅当前ActiveOperation有权清本床PLC/内存。
                bool sourceDiscarded = _rackLedger.DiscardReservedWorkpiece(rs, actionId, rackArrivalSeq, wp);
                await ClearSkewOnAbortAsync(bed, isLoad: true);
                FlowAlarmPresentationContext.RecordCacheState(sourceDiscarded
                    ? $"{rs}本次来源身份及预约已作废（到达代次{rackArrivalSeq}）"
                    : $"{rs}本次来源已消费/不存在或归属已变化，未删除新到达工件");
                action.Discard("异常本板已作废，设备内存Idle，旧人工续搬入口删除");
            }

            if (operation.RequiresManualSafetyRecovery)
            {
                // 超时后引擎已暂停，不应把软件锁当作长期安全设施；锁只能保护正在执行的
                // 动作。动作退出时释放，现场位置与工件身份通过任务牌/账本等待人工确认。
                bool rearReleased = operation.TryRelease("RearCrane", _craneRearLock);
                bool mtReleased = operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                bool tsReleased = operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                bool m818Released = operation.TryRelease("M818", _lockM818);
                bool m821Released = operation.TryRelease("M821", _lockM821);
                string alarm = (pendingSafetyAlarm ?? operation.ManualSafetyHoldReason ?? "后天车运动超时") +
                    $" 本动作软件锁已释放(后天车={YN(rearReleased)} ZoneMT={YN(mtReleased)} ZoneTS={YN(tsReleased)} M818={YN(m818Released)} M821={YN(m821Released)})。" +
                    AbortClearInstruction;
                Console.WriteLine($"│ [上料] ⚠ {alarm}");
                OnRearCraneSafetyAlarm?.Invoke(alarm);
                _fastNextCycle = true;
            }
            else
            {
            // 以下布尔值只观察动作租约在原finally前后的状态，不参与锁释放判断。
            bool zoneTsHeldBeforeCleanup = operation.IsHeld("ZoneTS");
            bool zoneMtHeldBeforeCleanup = operation.IsHeld("ZoneMT");
            bool rearCraneHeldBeforeCleanup = operation.IsHeld("RearCrane");
            RecoveryStepState x11RetreatState = RecoveryStepState.Skipped;
            string x11RetreatDetail = bed.Code == "ST606"
                ? "ZoneTS退避尚未执行"
                : $"目标{bed.Code}不需要ZoneTS侧X退避";
            bool releasedSharedInFinally = false;
            string sharedCleanupText = string.Empty;
            // ST606持有ZoneTS时，异常收尾需要按斜床1侧退避策略处理。
            if (sharedLocked)
            {
                if (!operation.IsHeld("ZoneTS"))
                {
                    sharedLocked = false;
                    sharedCleanupText = "ZoneTS已由斜床应急提前释放";
                    Console.WriteLine("│ [上料] ZoneTS已由应急释放, 跳过finally退避释放");
                    if (loadState.X11PickupFailed) x11RetreatDetail = "ZoneTS已由应急提前释放，未执行自动X退避";
                }
                else
                {
                    bool retreatSucceeded = false;
                    if (requiresManualConfirmation || loadState.ZMayBeDown)
                    {
                        // 情景：物理命令响应未知，或已发送Z下降命令/正在等待尾座夹紧/退磁/Z回升。
                        // 此时Z、Y和尾座结果可能未知，禁止finally自动执行X+1000，避免低位横移。
                        sharedCleanupText = requiresManualConfirmation
                            ? "物理命令响应未知，finally未发送自动退避命令"
                            : "Z轴已经或可能已经下降，程序未执行X轴+1000毫米退避";
                        if (loadState.X11PickupFailed) x11RetreatDetail = "Z轴可能仍在低位，按安全策略跳过X退避";
                        Console.WriteLine("│ [上料] ⚠ Z轴已经或可能已经下降, 禁止X+1000低位横移");
                    }
                    else
                    {
                        // 情景：Z尚未下降，或异常恢复已确认Z安全；按现场策略尝试X+1000。
                        retreatSucceeded = await TryRetreatSharedAreaBeforeReleaseAsync(cr, "ST606", "上料", ct);
                        if (loadState.X11PickupFailed)
                        {
                            x11RetreatState = retreatSucceeded ? RecoveryStepState.Succeeded : RecoveryStepState.Failed;
                            x11RetreatDetail = retreatSucceeded
                                ? "Z轴已确认安全，ZoneTS侧X退避成功"
                                : "Z轴已确认安全，但ZoneTS侧X退避失败或位置无法确认";
                        }
                        sharedCleanupText = retreatSucceeded
                            ? "Z轴已确认安全，X轴+1000毫米退避成功"
                            : "Z轴已确认安全，但X轴+1000毫米退避失败或位置无法确认";
                    }

                    // 现场已确认的异常策略：无论退避成功、失败或因Z下降而跳过，
                    // 都释放本次上料动作持有的ZoneTS；红色弹窗会明确提示锁已释放并要求立即人工处理。
                    releasedSharedInFinally = operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                    sharedLocked = false;
                    sharedCleanupText += "；ZoneTS已释放";

                    // 此分支位于主要作废检查之后：若主动作已Completed而空载退出失败，
                    // 这里只暂停及报警，不能认为它重新清了Machining/Wp。
                    // 下方现有统一提示仍可能声称已作废，现场须以实际收尾字段和机床工件为准。
                    if (!retreatSucceeded && pendingSafetyAlarm == null)
                    {
                        _paused = true;
                        pendingSafetyAlarm = $"2号线后天车在{bed.Code}上料异常收尾时未完成ZoneTS侧X退避。引擎已暂停，请立即人工确认天车位置";
                    }
                }
            }

            if (operation.IsHeld("ZoneTS"))
            {
                operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                sharedCleanupText = string.IsNullOrWhiteSpace(sharedCleanupText)
                    ? "ZoneTS已释放"
                    : $"{sharedCleanupText}；ZoneTS兜底释放";
            }
            if (operation.IsHeld("ZoneMT"))
            {
                operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                sharedCleanupText = string.IsNullOrWhiteSpace(sharedCleanupText)
                    ? "ZoneMT已释放"
                    : $"{sharedCleanupText}；ZoneMT已释放";
            }

            bool releasedRearInFinally = operation.TryRelease("RearCrane", _craneRearLock);
            string rearCleanupText = releasedRearInFinally ? "后天车锁已释放" : "后天车锁已由斜床应急提前释放";

            // X11连续三次为0属于需要人工确认的取料失败。物理恢复、退避和锁释放全部收尾后，
            // 再通过既有安全告警回调暂停对应线前后端，禁止主循环自动进入下一次取料。
            if (loadState.X11PickupFailed)
            {
                _paused = true;
                string x11Alarm = $"2号线后天车从中转架{rs}取料时，X11连续三次为0。" +
                                  $"异常恢复：{loadState.X11RecoverySummary}。引擎已暂停，" +
                                  "请人工确认中转架、磁铁、Z轴和天车位置，禁止自动重试";
                pendingSafetyAlarm = pendingSafetyAlarm == null
                    ? x11Alarm
                    : $"{x11Alarm}；{pendingSafetyAlarm}";
            }

            string lockAndRetreat = string.IsNullOrWhiteSpace(sharedCleanupText)
                ? rearCleanupText
                : $"{sharedCleanupText}；{rearCleanupText}";
            bool safetyAlarmCallbackCompleted = false;
            if (pendingSafetyAlarm != null)
            {
                pendingSafetyAlarm += $"。异常收尾：{lockAndRetreat}。" + AbortClearInstruction;
                Console.WriteLine($"│ [上料] ⚠ {pendingSafetyAlarm}");
                Action<string>? safetyAlarmCallback = loadState.X11PickupFailed
                    ? OnRearCraneX11FinalSafetyAlarm
                    : OnRearCraneSafetyAlarm;
                bool callbackWasSubscribed = safetyAlarmCallback != null;
                safetyAlarmCallback?.Invoke(pendingSafetyAlarm);
                safetyAlarmCallbackCompleted = callbackWasSubscribed;
            }

            if (loadState.X11PickupFailed)
            {
                // 告警回调返回后才记录，确保事件展示的是本次finally的最终结果而不是中间态。
                var finalization = new OperationalEventContextFactory.RearLoadX11Finalization(
                    loadState.X11MagnetOffState,
                    loadState.X11MagnetOffDetail,
                    loadState.X11SafeZState,
                    loadState.X11SafeZDetail,
                    x11RetreatState,
                    x11RetreatDetail,
                    zoneTsHeldBeforeCleanup,
                    !operation.IsHeld("ZoneTS"),
                    zoneMtHeldBeforeCleanup,
                    !operation.IsHeld("ZoneMT"),
                    rearCraneHeldBeforeCleanup,
                    !operation.IsHeld("RearCrane"),
                    loadState.X11CacheRestored,
                    _paused,
                    safetyAlarmCallbackCompleted,
                    $"{loadState.X11RecoverySummary}；{lockAndRetreat}");
                OperationalEventContextFactory.TryReportRearLoadX11FinalFailure(
                    _exceptionReporter,
                    operationalSite with { ActionStage = $"{rs}三次X11均未确认持件（最终收尾）" },
                    operationalTracker,
                    finalization,
                    loadState.X11FailureException,
                    action.Snapshot());
            }
            
            _fastNextCycle = true;
            Console.WriteLine("│ [上料] 释放后天车锁" + (!string.IsNullOrWhiteSpace(sharedCleanupText) ? " + Zone锁收尾" : ""));
            Console.WriteLine("└── [上料] 结束 ──────────────────────────");
            }
            EndSkewOperation(bed, operation);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DoHandshake   上料握手流程
    // ═══════════════════════════════════════════════════════════════════
    private async Task DoHandshake(CraneService cr, SkewCtx bed, WorkpieceCache wp, int sz,
        int bx, int by, int bz, LoadHandshakeState loadState,
        FlowActionContext action,
        OperationalPhysicalCycleTracker operationalTracker,
        OperationalEventContextFactory.OperationalPhysicalEventSite operationalSite,
        CancellationToken ct)
    {
        bool isF = true; // 2号线5台全FANUC
        int lz = bz - (int)Math.Round(wp.Diameter / 2.0);
        //公式计算yt  天车下降到斜床中心位置 需要再向左走一段距离
        int yT = _cfg.SkewBed.ComputeYOffset(bed.Code, wp.Length, wp.BoreType);
        Console.WriteLine($"│ [握手] {wp.IdentityText} 斜床Z={bz} 装料Z={lz}(Z-半径) Y目标={yT}");
        Console.WriteLine("│ [握手] ① 等待CNC请求数据(F1=1)...");
        action.BeginStep(FlowActionStep.NotifyDownstream, FlowActionPosition.Unknown, "等待CNC请求数据");
        await W(async () => await bed.F!.IsRequestDataAsync(ct), "请求数据", ct);
        double skewD = wp.Diameter + _cfg.GetDiameterOffset(bed.Code);
        if (skewD != wp.Diameter)
            Console.WriteLine($"│ [握手] ② 写入加工参数 {wp.IdentityText} 直径={skewD}(原{wp.Diameter} + 偏移{_cfg.GetDiameterOffset(bed.Code)}) 版长={wp.Length} 堵孔={wp.BoreType}");
        else
            Console.WriteLine($"│ [握手] ② 写入加工参数 {wp.IdentityText} 直径={wp.Diameter} 版长={wp.Length} 堵孔={wp.BoreType}");
        // 2号线斜床正常任务工艺统一映射为 1/2/3。
        // 设备级对刀模式只按当前斜床站号覆盖, 不修改任务里的原始工艺, 便于追溯。
        int skewMode = ResolveFanucSkewMode(wp.SkewBedProcess);
        if (_cfg.SkewBed.IsToolSettingEnabled(bed.Code))
        {
            skewMode = 6;
            Console.WriteLine($"│ [握手] ⚠ {bed.Code}已启用对刀模式, 覆盖任务工艺[{wp.SkewBedProcess}] → 加工模式=6");
        }
        // 2号线全FANUC
        action.MarkCommandSent();
        await bed.F!.SendMachiningParamsAsync(wp.Length, skewD, wp.BoreType, skewMode, ct);
        action.Confirm(FlowActionPosition.Unknown, "CNC加工参数写入成功返回");
        
        Console.WriteLine("│ [握手] ③ 等待CNC请求上料(F2=1)...");
        await W(async () => await bed.F!.IsRequestLoadAsync(ct), "请求上料", ct);
        //下降到装料位置
        Console.WriteLine($"│ [握手] ④ Z下降到装料位置{lz + _oz}");
        // 从发送Z下降命令这一刻起，异常收尾不能再假设Z仍在安全位。
        // 即使MoveAbsoluteAsync抛出通信异常，PLC也可能已经收到下降命令，只是响应没有返回。
        action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(bx + _ox, by + _oy, lz + _oz), "斜床上料Z下降");
        action.MarkCommandSent();
        loadState.ZMayBeDown = true;
        operationalTracker.BeginZDown(lz + _oz);
        try
        {
            //z移动   下降到装料位置   加上了数据库的偏移
            await cr.MoveAbsoluteAsync(-1, -1, lz + _oz, ct: ct);
            operationalTracker.CompleteZDown();
            action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, lz + _oz), "斜床上料Z下降到位");
        }
        catch (PressureStopException)
        {
            operationalTracker.MarkZUnknown("斜床上料Z下降触发下压保护，恢复后位置等待后续安全高度确认");
            await cr.RecoverFromPressureStopAsync(ct);
            throw;
        }
        catch (Exception)
        {
            operationalTracker.MarkZUnknown("斜床上料Z下降方法异常，命令实际结果和当前位置未知");
            throw;
        }
        int yAbs = by - yT+ _oy; //  数据库Y - 偏移 = 顶尖对中绝对位置   再加上天车偏移量
        Console.WriteLine($"│ [握手] ⑤ Y移动到顶尖对中位置=数据库Y({by})-偏移({yT})={yAbs} 这个是加过天车偏移的z");
        try
        {
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(bx + _ox, yAbs, lz + _oz), "斜床顶尖对中Y移动");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(-1, yAbs, -1, ct: ct);
            action.Confirm(new FlowActionPosition(bx + _ox, yAbs, lz + _oz), "斜床顶尖对中Y到位");
        }
        catch (PressureStopException)
        {
            await cr.RecoverFromPressureStopAsync(ct);
            throw;
        }
        Console.WriteLine("│ [握手] ⑥ 清上一步信号 → 写天车上料到位 → 等CNC夹紧(F3=1)...");
        bed.F!.SafeSetMacro(1101, 0); // 清#1101
        await bed.F!.SetCraneLoadInPlaceAsync(true, ct); // 2号线全FANUC
        await W(async () => await bed.F!.IsTailstockClampedAsync(ct), "夹紧完成", ct);
        Console.WriteLine($"│ [握手] ⑦ 退磁 + Z升到安全高度{sz}");
        operationalTracker.BeginPlacementMagnetOff(bed.Code);
        try
        {
            action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(bx + _ox, yAbs, lz + _oz), "斜床夹紧后退磁放料");
            action.MarkCommandSent();
            await cr.MagnetOffAsync(ct);
            operationalTracker.CompletePlacementMagnetOff(bed.Code);
            action.Confirm(new FlowActionPosition(bx + _ox, yAbs, lz + _oz), "斜床退磁放料成功返回");
            action.RecordFeedback(FlowActionPosition.Unknown, null, false, "斜床退磁放料成功返回");
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{bed.Code}上料完整交接",
                $"{bed.Code}目标位退磁成功返回，放料推定成立；等待Z安全回升和CNC上料完成通知");
        }
        catch (Exception ex)
        {
            operationalTracker.FailMagnetOff();
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                operationalSite with { Station = bed.Code, ActionStage = $"{bed.Code}夹紧后退磁放入斜床" }, operationalTracker, ex,
                "斜床夹紧后退磁方法异常；不能断言工件仍吸附或已经释放", true,
                holdingWorkpiece: true, placed: null, cacheNotified: null,
                downstreamNotified: null, handoffClosed: false);
            throw;
        }
        loadState.PlacedOnBed = true; // 退磁成功后工件已经离开后天车, 后续异常必须人工确认斜床状态
        action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "工件已退磁放入斜床，等待CNC上料完成通知");
        action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(bx + _ox, yAbs, sz), "放料后Z升安全高度");
        action.MarkCommandSent();
        await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
        action.Confirm(new FlowActionPosition(bx + _ox, yAbs, sz), "放料后Z已升安全高度");
        operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "斜床上料退磁后Z升安全命令成功返回");
        // 只有Z回安全运动成功返回，异常finally才允许ST606执行X+1000退避。
        loadState.ZMayBeDown = false;
        loadState.ZSafeAfterPlace = true;
        Console.WriteLine("│ [握手] ⑧ 清上一步信号 → 写天车上料完成,CNC启动加工");
        if (isF) bed.F!.SafeSetMacro(1102, 0); // 清#1102
        // 2号线全FANUC
        operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, $"{bed.Code}CNC上料完成", "CNC上料完成通知调用已开始，结果未知");
        action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(bx + _ox, yAbs, sz), "CNC上料完成通知");
        action.MarkCommandSent();
        await bed.F!.SetCraneLoadDoneAsync(ct); // 2号线全FANUC
        action.Confirm(new FlowActionPosition(bx + _ox, yAbs, sz), "CNC上料完成通知成功返回");
        operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, $"{bed.Code}CNC上料完成", "CNC上料完成通知成功返回");
        operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{bed.Code}上料完整交接",
            $"{bed.Code}退磁、Z安全回升及CNC上料完成通知均成功返回");
        loadState.LoadDoneNotified = true;
        Console.WriteLine($"│ [握手] ✓ 全部完成 {wp.IdentityText}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DoUnload  斜床加工完 下料取料流程 送给动平衡下料架和研磨机上料架1号位
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>下料流程: 取料→X11检测→尾座张开→分流到动平衡/不需动平衡架</summary>
    private async Task DoUnload(SkewCtx bed, CancellationToken ct)
    {
        int pickupStableDelayMs = _cfg.Grinding.X11StableDelayMs;
        string actionId = OperationalEventContextFactory.NewActionId("L2-REAR-UNLOAD");
        WorkpieceCache initialWorkpiece = bed.Wp ?? throw new InvalidOperationException($"{bed.Code}下料缺少工件账本");
        var action = new FlowActionContext(actionId, "2号线后天车下料", "4号天车",
            initialWorkpiece.IdentityText, bed.Code, "ST020/ST021分流目标", bed.Code);
        using var logAction = OperationalLog.BeginAction("2号线后天车", "4号天车", actionId,
            initialWorkpiece.IdentityText, bed.Code, "ST020/ST021分流目标");
        using var alarmPresentation = FlowAlarmPresentationContext.Begin(action, initialWorkpiece, "2号线", 4, "斜床下料");
        OperationalLog.Info("搬运动作开始", "已创建后天车下料任务", ("板号与序号", initialWorkpiece.IdentityText));
        action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "斜床下料前检查");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        bool magnetOn = false;
        bool discardOnFailure = false;
        bool holdingWorkpiece = false;       // X11确认有版后才算工件已经在后天车上, 不能只看是否发过充磁命令
        bool placedToDestination = false;    // 退磁放到目标位 + Z升安全 + 必要通知完成后, 才允许斜床Idle并清Wp
        bool operationalPlaced = false;
        bool operationalCacheNotified = false;
        bool operationalDownstreamNotified = false;
        bool sharedLocked = false;
        bool sharedWasAcquired = false;       // 用于后续分流异常弹窗说明：ZoneTS可能已在正常提前退避后释放
        bool zMayBeDown = false;              // 只用于异常收尾：Z下降命令发出后，到确认Z回安全高度前均为true
        bool sharedRetreatAttempted = false;  // 防止正常提前退避失败后，finally再次发送同一X运动
        bool sharedRetreatSucceeded = false;
        bool requiresManualConfirmation = false;
        string? pendingSafetyAlarm = null;    // 等finally完成退避判断和两把锁释放后，再统一弹一次完整告警
        CraneService? cr = null;
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(actionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "2号线", "后端引擎", "后天车", CraneRearNo.ToString(), bed.Code,
            $"{bed.Code}下料并送分流目标", actionId, $"{actionId}:rear-unload", bed.Wp,
            bed.Code, "ST020/ST021分流目标", "2号线后天车", bed.Code,
            "斜床加工完成等待后天车下料", "ZoneTS/后天车锁/M818/M821"));
        using var operation = BeginSkewOperation(bed, "下料", ct, holdsTransferRack: false);
        alarmPresentation.ObserveFinalState(() => FlowAlarmPresentationContext.DescribeLocks(operation),
            () => operationalCacheNotified ? "原流程已确认下游缓存通知；已移交缓存不回滚。斜床内存清理见上方结果。" : "原流程未确认下游缓存通知完成，接收端状态需核对；斜床清理不会回滚已移交的缓存。");
        ct = operation.Token;
        action.RegisterLock("RearCrane");
        try
        {
            Console.WriteLine($"\n┌── [下料] 开始 {bed.Wp?.IdentityText ?? "版号=未知"} {bed.Code} ──────────────────");
            cr = _craneCache.GetOrCreateService(CraneRearNo);
            if (!cr.IsConnected)
            {
                Console.WriteLine("│ 后天车未连接,连接中...");
                await cr.ConnectAsync(ct);
            }

            // 安全: 取料前检查天车磁铁是否已有工件
            operationalTracker.BeginX11Stage("取料前残留检查");
            operationalTracker.BeginX11Attempt();
            bool hasExistingWorkpiece;
            try
            {
                hasExistingWorkpiece = await cr.ReadXBitAsync(63497, ct);
                operationalTracker.CompleteX11(hasExistingWorkpiece, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    operationalSite with { ActionStage = $"{bed.Code}下料取料前X11残留检查" }, operationalTracker, ex,
                    "取料前X11读取失败，当前值无效；保留最后成功值（如有）", true);
                throw;
            }
            physicalTracker?.TryObserveX11(hasExistingWorkpiece);
            if (hasExistingWorkpiece)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    operationalSite with { ActionStage = $"{bed.Code}取料前发现磁铁已有工件" }, operationalTracker, null,
                    "取料前X11成功读取为1，确认磁铁上存在身份未知的残留工件", false,
                    holdingWorkpiece: true);
                throw new InvalidOperationException("后天车X11=1(磁铁已有工件), 拒绝下料取料防止碰撞");
            }

            // 仅ST606紧邻中转架-斜床1碰撞区, 下料取料时只需持有ZoneTS。
            if (bed.Code == "ST606")
            {
                Console.WriteLine("│ [下料] 获取ZoneTS(ST606↔中转架)...");
                await operation.AcquireAsync("ZoneTS", _safety.TransferSkew1CollisionLock, ct);
                action.RegisterLock("ZoneTS");
                sharedLocked = true;
                sharedWasAcquired = true;
                Console.WriteLine("│ [下料] ZoneTS已获取(ST606) ✓");
            }

            // 设置天车速度(与DoLoad一致, 防止PLC残留速度导致异常)
            var crSpd = _cfg.GetCraneSpeed(CraneRearNo);
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置后天车绝对速度");
            action.MarkCommandSent();
            await cr.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel,
                crSpd.Y.Speed, crSpd.Y.Accel, crSpd.Y.Decel,
                crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);
            action.Confirm();

            int sz = _cfg.Grinding.SafeZHeight;
            var wp = initialWorkpiece;
            if (!TryCoords(bed.Code, out int bx, out int by, out int bz)) throw new Exception($"缺少斜床{bed.Code}坐标");
            bool isF = true; // 2号线5台全FANUC
            int lz = bz - (int)Math.Round(wp.Diameter / 2.0);
            //公式y偏移
            int yOff = _cfg.SkewBed.ComputeYOffset(bed.Code, wp.Length, wp.BoreType);
            //y-y偏移   加上天车自己数据库的偏移
            int yPick = by - yOff + _oy;
            Console.WriteLine($"│ {wp.IdentityText} 斜床Z={bz} 取料Z={lz}(Z-半径) 直径={wp.Diameter}mm 版长={wp.Length}mm");
            Console.WriteLine($"│ Y偏移={yOff} 数据库Y={by}+天车={_oy}={by + _oy} → 取料Y={yPick}");

            // ① Z→0 → XY到数据库位置 → Y偏移到顶尖对中 → Z↓充磁取料
            Console.WriteLine($"│ [下料] ① Z→0(避免碰撞)");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "斜床取料前Z回原点");
            try
            {
                action.MarkCommandSent();
                await cr.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
                action.Confirm(new FlowActionPosition(null, null, 0), "斜床取料前Z已回原点");
                physicalTracker?.TryConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "斜床取料前Z回原点命令成功返回");
            }
            catch (PressureStopException)
            {
                await cr.RecoverFromPressureStopAsync(ct);
            }

            Console.WriteLine($"│ [下料] ① XY→数据库位置({bx + _ox},{by + _oy})");
            // //第一次移动
            // await cr.MoveAbsoluteAsync(bx + _ox, by + _oy, -1, ct: ct);
            // Console.WriteLine($"│ [下料] ① Y→顶尖对中位置{yPick}");
            //先移动xy   移动到xy指定位置
            action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(bx + _ox, yPick, 0), "XY到斜床取料位");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(bx + _ox, yPick, -1, ct: ct);
            action.Confirm(new FlowActionPosition(bx + _ox, yPick, 0), "斜床取料位XY已到位");
            action.BeginStep(FlowActionStep.FineTuneSource, new FlowActionPosition(bx + _ox, yPick, 0), "斜床取料位XY微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                cr, _cfg, CraneRearNo, bed.Code, $"2号线后天车-{bed.Code}下料取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "2号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: bed.Code,
                    actionStage: $"{bed.Code}下料取料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "本物理周期来源"),
                    target: EvidenceValue<string>.Unknown("ST020/ST021分流尚未确定"),
                    owner: "2号线后天车", targetZ: EvidenceValue<int>.Confirmed(lz + _oz, "已有斜床取料Z公式与偏移"),
                    physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, true, "Z回原点命令已返回", bed.Code)),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct,
                yCenterToPickOffsetMm: yOff);
            //下降取料    加上数据库的偏移值   lz是计算公式算的
            action.Confirm(new FlowActionPosition(bx + _ox, yPick, 0), "斜床取料位XY微调完成");
            int zDown = lz + _oz;
            Console.WriteLine($"│ [下料] ① Z下降到{lz}(Z-半径)+天车偏移({_oz})={zDown} 充磁取料");
            // 从发送Z下降命令起，异常finally不能再假定Z位于安全高度。
            // 即使运动调用报错，也可能是PLC已执行下降而上位机没有收到响应。
            zMayBeDown = true;
            // 斜床下料同样先确认已放磁，按原zDown下降并探测，再充磁后独立确认持件。
            // 未确认持件不开始原下料交接；严重失败由本动作catch作废本床，已移交下游的数据不回滚。
            var pickup = await PickupProbeHelper.PickAsync(cr, zDown, pickupStableDelayMs,
                action, operationalTracker, ct, validateAction: operation.ThrowIfInvalid,
                settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm));
            int unlz = pickup.ActualZ;
            magnetOn = true;
            holdingWorkpiece = true;
            bed.X11UnloadMissWarningShown = false;
            physicalTracker?.TryObserveX11(true);

            Console.WriteLine("│ [下料] ② 清上一步信号 → 写天车下料到位 → 等CNC张开尾座(F5=1)...");
            bed.F!.SafeSetMacro(1103, 0); // 清#1103
            await bed.F!.SetCraneUnloadInPlaceAsync(true, ct); // 2号线全FANUC
            await W(async () => await bed.F!.IsTailstockOpenedAsync(ct), "尾座张开", ct);
            Console.WriteLine("│ [下料] 尾座已张开");

            Console.WriteLine($"│ [下料] ③ Y回数据库坐标({by + _oy}) → Z升到安全高度{sz}");
            //y回数据库坐标   z升安全高度
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(bx + _ox, by + _oy, unlz), "斜床取料后Y回数据库安全位置");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(-1, by + _oy, -1, ct: ct);
            action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, unlz), "斜床取料后Y安全位置到位");
            //z升安全高度
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床取料后Z升安全高度");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
            action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床取料后Z安全到位");
            physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "斜床取料后Z升安全命令成功返回");
            operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "斜床取料后Z升安全命令成功返回");
            zMayBeDown = false; // 正常下料已确认Z安全，保留原提前X退避流程
            Console.WriteLine("│ [下料] 清上一步信号 → 写天车下料完成");
            if (isF) bed.F!.SafeSetMacro(1104, 0); // 清#1104
            // 2号线全FANUC  今洲斜床
            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(bx + _ox, by + _oy, sz), "写斜床天车下料完成通知");
            action.MarkCommandSent();
            await bed.F!.SetCraneUnloadDoneAsync(ct); // 2号线全FANUC
            action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床天车下料完成通知成功返回");

            if (sharedLocked)
            {
                // ST606下料已完成: 尾座张开、Y回数据库坐标、Z已升安全、CNC已收到下料完成。
                // 下料仍按原策略先执行X+1000/配置退避，再释放ZoneTS。
                sharedRetreatAttempted = true;
                sharedRetreatSucceeded = await TryRetreatSharedAreaBeforeReleaseAsync(cr, bed.Code, "下料", ct);
                if (sharedRetreatSucceeded)
                {
                    operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                    sharedLocked = false;
                    Console.WriteLine("│ [下料] ST606已完成ZoneTS侧退避并写下料完成, 提前释放ZoneTS");
                }
                else
                {
                    _paused = true;
                    Console.WriteLine("│ [下料] ⚠ ST606 ZoneTS侧退避失败; 引擎已暂停, finally仍会释放ZoneTS和后天车锁");
                    throw new InvalidOperationException("ST606下料完成后ZoneTS侧退避失败；已暂停，finally将释放ZoneTS和后天车锁");
                }
            }

            bool isLong = wp.Length >= 800;
            bool needsBalancing = isLong || wp.ForceBalancing;
            string balancingReason = isLong ? "版长≥800" : "任务勾选动平衡";
            Console.WriteLine(
                $"│ [下料] ④ 分流 → {wp.IdentityText} 版长={wp.Length}mm {(needsBalancing ? $"{balancingReason}→ST020动平衡架" : "<800且未勾选→ST021短板中转位")}");
            if (needsBalancing)
            {
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheNotification, "M818动平衡缓存回调");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheMutation, "M818");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M819(ST020放料完成)");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST020/M818完整交接");
            }
            else
            {
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheNotification, "M821动平衡缓存回调");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheMutation, "M821");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M821(ST021放料完成)");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST021/M821完整交接");
            }
            // 获取平衡料架位置锁(防M2Flow/M3Flow同时操作)
            // 2号线后天车下料分流区统一拿两把锁: M818 + M821。
            // 长板目标是M818, 短板目标是M821; 但后天车飞行/放料路径都经过同一侧分流区域,
            // 因此不按长短省锁, 避免目标位附近设备或机械手3并行动作形成碰撞窗口。
            bool gotM818 = false;
            bool gotM821 = false;
            try
            {
                // ── 分流前确认本线已启动 ──
                // 必须放在拿 M818/M821 两把分流锁之前：等待期间只占着后天车锁，不会为了一件
                // 与本线启动无关的事去阻塞共享这两把锁的机械手2/3 与研磨天车。
                // 此时天车停在斜床上方安全Z并保持持件、未退磁、未进分流区，超时可安全作废本趟分流。
                // 必须在 try 内：若抛出而逃出本 try，外层 finally 不执行，后天车锁将无法释放。
                await EnsureLineRunningBeforeDivertAsync(bed, ct);

                if (needsBalancing)//需要动平衡加锁M818
                {
                    // 主循环已用M818快照做过预检。
                    // 工件已由后天车吸起后, 不能再因为前端快照瞬时无效直接停半路;
                    // 拿到位置锁后再读一次PLC真实M818, 以二次确认为最终放料依据。
                    Console.WriteLine("│ [分流] 目标: ST020(2号线动平衡下料架1)  拿锁后二次确认M818允许放料...");
                    // 注意: 长板也拿M821路径锁。多把锁从获取前就进入finally保护,
                    // 第二把等待异常/取消时也会释放第一把, 避免锁泄漏。
                    if (_lockM818 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M818)..."); await operation.AcquireAsync("M818", _lockM818, ct); action.RegisterLock("M818"); gotM818 = true; }
                    if (_lockM821 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M821路径保护)..."); await operation.AcquireAsync("M821", _lockM821, ct); action.RegisterLock("M821"); gotM821 = true; }
                    // 二次确认: M818=未允许放料时不释放分流锁、不移动、不退磁、不暂停，保持持件与安全Z
                    // 在锁内按轮询周期无限等待，直到 M818 恢复允许放料才允许进入分流区。
                    await EnsureM818CanPlaceBeforePlaceAsync(wp, ct);
                    // 锁内、目的地确认后再确认机械手2/3已离开料架区，之后才允许后天车进入分流区。
                    await WaitManipulatorsClearOfRackAsync(ct);
                }
                else//短板加锁M818 M821
                {
                    Console.WriteLine("│ [分流] 目标: ST021(2号线不需动平衡位置)  拿锁后二次确认M820允许放料...");
                    Console.WriteLine("│   M820允许放料 ✓ (前置步骤④已确认); M720由后续M3Flow搬运前再确认");
                    // 注意: 多把锁从获取前就进入finally保护, 第二把等待异常/取消时也会释放第一把, 避免死锁。
                    if (_lockM818 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M818)..."); await operation.AcquireAsync("M818", _lockM818, ct); action.RegisterLock("M818"); gotM818 = true; }
                    if (_lockM821 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M821路径保护)..."); await operation.AcquireAsync("M821", _lockM821, ct); action.RegisterLock("M821"); gotM821 = true; }
                    // 二次确认: M820=未允许放料时不释放分流锁、不移动、不退磁、不暂停，保持持件与安全Z
                    // 在锁内按轮询周期无限等待，直到 M820 恢复允许放料才允许进入分流区。
                    await EnsureM820CanPlaceBeforePlaceAsync(wp, ct);
                    // 锁内、目的地确认后再确认机械手2/3已离开料架区，之后才允许后天车进入分流区。
                    await WaitManipulatorsClearOfRackAsync(ct);
                }

                // ③ XY移动+放料(持锁中)
                if (needsBalancing)
                {
                    if (!TryCoords("ST020", out int dx, out int dy, out int dz)) throw new Exception("缺少ST020坐标");
                    Console.WriteLine($"│   XY到ST020({dx + _ox},{dy + _oy})");
                    action.SetTarget("ST020", "已选择M818/ST020作为实际分流目标");
                    action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(dx + _ox, dy + _oy, sz), "持件XY到ST020/M818");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(dx + _ox, dy + _oy, -1, ct: ct);
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST020目标XY到位");
                    action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST020放料前XY微调");
                    action.MarkCommandSent();
                    await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                        cr, _cfg, CraneRearNo, "ST020", "2号线后天车-ST020放料前",
                        reporter: _exceptionReporter,
                        failureContext: OperationalEventContextFactory.FineTuneFailure(
                            scope: "2号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: "ST020",
                            actionStage: "ST020动平衡架放料前XY微调", workpiece: wp,
                            source: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "本物理周期来源"),
                            target: OperationalEventContextFactory.ConfirmedLocation("ST020", "分流结果"),
                            owner: "2号线后天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后计算ST020放料dz2"),
                            physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "斜床取料X11=1且分流已完成", "天车/ST020上方")),
                        actionId: actionId, physicalTracker: physicalTracker, ct: ct);
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST020放料前XY微调完成");
                    int dz2 = Pz(dz, wp.Diameter);
                    Console.WriteLine($"│   Z下降到{dz2 + _oz}");
                    operationalTracker.BeginZDown(dz2 + _oz);
                    physicalTracker?.TryMarkZUnknown("ST020原始放料Z下降调用已开始，旧安全高度不能继承");
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST020下降放料");
                        action.MarkCommandSent();
                        await cr.MoveAbsoluteAsync(-1, -1, dz2 + _oz, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST020放料Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("ST020放料Z下降触发下压保护，恢复后位置等待安全高度确认");
                        physicalTracker?.TryMarkZUnknown("ST020放料Z下降触发下压保护，恢复后位置未知");
                        await cr.RecoverFromPressureStopAsync(ct);
                        throw;
                    }
                    catch (Exception)
                    {
                        operationalTracker.MarkZUnknown("ST020放料Z下降方法异常，命令实际结果和当前位置未知");
                        physicalTracker?.TryMarkZUnknown("ST020放料Z下降方法异常，旧安全高度不能继承");
                        throw;
                    }

                    operationalTracker.BeginPlacementMagnetOff("ST020");
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST020/M818退磁放料");
                        action.MarkCommandSent();
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompletePlacementMagnetOff("ST020");
                        action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST020退磁调用成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { Station = "ST020", ActionStage = "ST020目标位退磁放料" }, operationalTracker, ex,
                            "目标位退磁方法异常，工件是否释放未知", true,
                            holdingWorkpiece: holdingWorkpiece, placed: null, cacheNotified: null,
                            downstreamNotified: null, handoffClosed: false);
                        throw;
                    }
                    magnetOn = false;
                    operationalPlaced = true;
                    action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "ST020退磁放料成功返回，等待缓存、Z安全回升和M819通知");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST020/M818完整交接",
                        "ST020目标位退磁成功返回，放料推定成立；等待缓存回调、Z安全回升和M819通知");
                    Console.WriteLine($"│   ✓ ST020放料完成,退磁 {wp.IdentityText}");
                    
                    // 通知平衡引擎。工件已物理放下, 缓存回调必须成功, 不能静默跳过。
                    if (OnBalancingRackPlaced == null)
                        throw new InvalidOperationException("M818已放料但动平衡缓存回调未绑定");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "M818动平衡缓存回调", "动平衡缓存回调调用已开始，结果未知");
                    operationalTracker.BeginCacheMutation("M818",
                        EvidenceValue<string>.Unknown("回调前未确认M818缓存已写入本周期工件"),
                        "M818缓存写入调用已开始，修改后事实未知");
                    OnBalancingRackPlaced.Invoke("M818", wp);
                    operationalCacheNotified = true;
                    operationalTracker.CompleteCacheMutation("M818",
                        EvidenceValue<string>.Unknown("回调前未确认M818缓存已写入本周期工件"),
                        EvidenceValue<string>.Confirmed("回调成功后M818缓存已写入本周期工件", "缓存回调成功返回"),
                        "M818缓存写入成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "M818动平衡缓存回调", "动平衡缓存回调成功返回");
                    wp.ReportStage("2号线 动平衡下料架 ST020/M818");
                    action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST020放料后Z升安全高度");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST020放料后Z安全到位");
                    physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST020放料后Z升安全命令成功返回");
                    operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST020放料后Z升安全命令成功返回");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M819(ST020放料完成)", "M819写入调用已开始，结果未知");
                    action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(dx + _ox, dy + _oy, sz), "写M819通知ST020放料完成");
                    action.MarkCommandSent();
                    await WriteMc63HandshakeBitAsync(RackAddr.Bit_ST020_PlaceDone, "M819(ST020放料完成)", ct);
                    operationalDownstreamNotified = true;
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M819(ST020放料完成)", "M819写入成功返回");
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "M819放料完成通知成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST020/M818完整交接",
                        "ST020退磁、M818缓存回调、Z安全回升及M819通知均成功返回");
                    holdingWorkpiece = false;
                    placedToDestination = true;
                }
                else
                {
                    if (!TryCoords("ST021", out int gx, out int gy, out int gz)) throw new Exception("缺少ST021坐标");
                    Console.WriteLine($"│   XY到ST021({gx + _ox},{gy + _oy})");
                    action.SetTarget("ST021", "已选择M821/ST021作为实际分流目标");
                    action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(gx + _ox, gy + _oy, sz), "持件XY到ST021/M821");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(gx + _ox, gy + _oy, -1, ct: ct);
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST021目标XY到位");
                    action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST021放料前XY微调");
                    action.MarkCommandSent();
                    await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                        cr, _cfg, CraneRearNo, "ST021", "2号线后天车-ST021放料前",
                        reporter: _exceptionReporter,
                        failureContext: OperationalEventContextFactory.FineTuneFailure(
                            scope: "2号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: "ST021",
                            actionStage: "ST021不平衡架放料前XY微调", workpiece: wp,
                            source: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "本物理周期来源"),
                            target: OperationalEventContextFactory.ConfirmedLocation("ST021", "分流结果"),
                            owner: "2号线后天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后计算ST021放料gz2"),
                            physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "斜床取料X11=1且分流已完成", "天车/ST021上方")),
                        actionId: actionId, physicalTracker: physicalTracker, ct: ct);
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST021放料前XY微调完成");
                    int gz2 = Pz(gz, wp.Diameter);
                    Console.WriteLine($"│   Z下降到{gz2 + _oz}");
                    operationalTracker.BeginZDown(gz2 + _oz);
                    physicalTracker?.TryMarkZUnknown("ST021原始放料Z下降调用已开始，旧安全高度不能继承");
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST021下降放料");
                        action.MarkCommandSent();
                        await cr.MoveAbsoluteAsync(-1, -1, gz2 + _oz, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST021放料Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("ST021放料Z下降触发下压保护，恢复后位置等待安全高度确认");
                        physicalTracker?.TryMarkZUnknown("ST021放料Z下降触发下压保护，恢复后位置未知");
                        await cr.RecoverFromPressureStopAsync(ct);
                        throw;
                    }
                    catch (Exception)
                    {
                        operationalTracker.MarkZUnknown("ST021放料Z下降方法异常，命令实际结果和当前位置未知");
                        physicalTracker?.TryMarkZUnknown("ST021放料Z下降方法异常，旧安全高度不能继承");
                        throw;
                    }

                    operationalTracker.BeginPlacementMagnetOff("ST021");
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST021/M821退磁放料");
                        action.MarkCommandSent();
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompletePlacementMagnetOff("ST021");
                        action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST021退磁调用成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { Station = "ST021", ActionStage = "ST021目标位退磁放料" }, operationalTracker, ex,
                            "目标位退磁方法异常，工件是否释放未知", true,
                            holdingWorkpiece: holdingWorkpiece, placed: null, cacheNotified: null,
                            downstreamNotified: null, handoffClosed: false);
                        throw;
                    }
                    magnetOn = false;
                    operationalPlaced = true;
                    action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "ST021退磁放料成功返回，等待缓存、Z安全回升和M821通知");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST021/M821完整交接",
                        "ST021目标位退磁成功返回，放料推定成立；等待缓存回调、Z安全回升和M821通知");
                    Console.WriteLine($"│   ✓ ST021放料完成,退磁 {wp.IdentityText}");
                    // 通知平衡引擎: M821有工件了(M3Flow读缓存取直径)。物理已放下后回调必须成功。
                    if (OnBalancingRackPlaced == null)
                        throw new InvalidOperationException("M821已放料但动平衡缓存回调未绑定");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "M821动平衡缓存回调", "动平衡缓存回调调用已开始，结果未知");
                    operationalTracker.BeginCacheMutation("M821",
                        EvidenceValue<string>.Unknown("回调前未确认M821缓存已写入本周期工件"),
                        "M821缓存写入调用已开始，修改后事实未知");
                    OnBalancingRackPlaced.Invoke("M821", wp);
                    operationalCacheNotified = true;
                    operationalTracker.CompleteCacheMutation("M821",
                        EvidenceValue<string>.Unknown("回调前未确认M821缓存已写入本周期工件"),
                        EvidenceValue<string>.Confirmed("回调成功后M821缓存已写入本周期工件", "缓存回调成功返回"),
                        "M821缓存写入成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "M821动平衡缓存回调", "动平衡缓存回调成功返回");
                    wp.ReportStage("2号线 研磨中转位 ST021/M821");
                    action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST021放料后Z升安全高度");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST021放料后Z安全到位");
                    physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST021放料后Z升安全命令成功返回");
                    operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST021放料后Z升安全命令成功返回");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M821(ST021放料完成)", "M821写入调用已开始，结果未知");
                    action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(gx + _ox, gy + _oy, sz), "写M821通知ST021放料完成");
                    action.MarkCommandSent();
                    await WriteMc63HandshakeBitAsync(RackAddr.Bit_ST021_PlaceDone, "M821(ST021放料完成)", ct);
                    operationalDownstreamNotified = true;
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M821(ST021放料完成)", "M821写入成功返回");
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "M821放料完成通知成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST021/M821完整交接",
                        "ST021退磁、M821缓存回调、Z安全回升及M821通知均成功返回");
                    holdingWorkpiece = false;
                    placedToDestination = true;
                }

            }
            finally
            {
                // 长短板都可能持有M821路径锁; 只释放真正拿到的锁。
                if (gotM821)
                    Console.WriteLine(operation.TryRelease("M821", _lockM821)
                        ? "│ [分流] 释放 M821锁"
                        : "│ [分流] M821锁已由应急释放, 跳过重复释放");
                // 需要/不需要动平衡都会持有M818: 目标位或路径保护。
                if (gotM818)
                    Console.WriteLine(operation.TryRelease("M818", _lockM818)
                        ? "│ [分流] 释放 M818锁"
                        : "│ [分流] M818锁已由应急释放, 跳过重复释放");
            }

            Console.WriteLine("│ [下料] ⑤ 后天车回自定义位置 X→home Z回原点 并发执行");
            int homeX = _cfg.GetCraneHomeX(CraneRearNo); // 后天车归位X(配置文件, 默认-4000)
            var xTask = cr.MoveAbsoluteAsync(homeX, -1, -1, ct: ct);
            var zTask = cr.HomeZAsync(ct);
            //两个任务同时执行 回homex 回z轴原点   然后finally释放锁
            await Task.WhenAll(xTask, zTask);
            bed.St = SkewState.Idle;
            bed.Wp = null;
            ClearRearCraneTask();
            action.Complete("斜床下料、目标位交接和后天车回位均已完成");
            Console.WriteLine($"│ [下料] ✓ 完成 {wp.IdentityText} {bed.Code}→Idle");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine($"│ [下料] ⚠ {bed.Code} 当前动作已被应急取消, 不再继续写CNC/天车动作");
        }
        // 先拦住应急失效后的迟到超时，避免旧任务重新登记人工记录或写回本床工件。
        catch (CraneMotionTimeoutException ex)
        {
            if (!operation.IsValid) return;
            _paused = true;
            discardOnFailure = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnRearCraneSafetyAlarm?.Invoke($"2号线后天车{bed.Code}异常：{ex.Message}。线路已暂停，正在按本次动作作废收尾；请勿恢复或删除下一板。");
            action.MarkCommandResponseUnknown(ex.Message);
            action.PauseForManualResolution(ex.Message);
            operation.HoldForManualSafetyRecovery(ex.Message);
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = $"{action.Step}：天车运动超时" }, operationalTracker, ex,
                "后天车下料运动超时；动作账本已暂停，等待人工确认。", true,
                holdingWorkpiece: holdingWorkpiece, actionSnapshot: action.Snapshot());
            bed.St = SkewState.Unloading;
            if (bed.Wp is { } timeoutWp)
                SetRearCraneTask(timeoutWp, "运动超时，位置未知", bed.Code, "分流目的地", true,
                    $"{ex.Stage}超时；未到位轴={string.Join("/", ex.UnreachedAxes)}；等待人工确认");
            pendingSafetyAlarm = $"2号线后天车从{bed.Code}下料时{ex.Stage}运动超时。" +
                $"目标=({ex.XTarget},{ex.YTarget},{ex.ZTarget})，最后坐标={ex.LastKnownStatus?.XPos}/{ex.LastKnownStatus?.YPos}/{ex.LastKnownStatus?.ZPos}，" +
                $"未到位轴={string.Join("/", ex.UnreachedAxes)}，D4518停止={(ex.EmergencyStopCommandSucceeded ? "已发送" : "发送失败")}。" +
                "引擎已暂停；本板软件任务将在收尾作废，现场位置须人工确认，本动作软件锁将在收尾后释放，禁止自动重试。";
            Console.WriteLine($"│ [下料] ⚠ {pendingSafetyAlarm}");
        }
        // 下压保护也可能晚于应急取消返回；当前有效动作暂停作废，失效动作交给应急流程收尾。
        catch (PressureStopException ex)
        {
            if (!operation.IsValid) return;
            _paused = true;
            discardOnFailure = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnRearCraneSafetyAlarm?.Invoke($"2号线后天车{bed.Code}异常：{ex.Message}。线路已暂停，正在按本次动作作废收尾；请勿恢复或删除下一板。");
            action.MarkCommandResponseUnknown(ex.Message);
            action.PauseForManualResolution(ex.Message);
            requiresManualConfirmation = true;
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PRESSURE_STOP_DETECTED",
                operationalSite with { ActionStage = $"{action.Step}：下压保护" }, operationalTracker, ex,
                "后天车下料触发下压保护；动作账本已暂停，等待人工确认。", true,
                holdingWorkpiece: holdingWorkpiece, actionSnapshot: action.Snapshot());
            bed.St = SkewState.Unloading;
            if (bed.Wp is { } pressureWp)
                SetRearCraneTask(pressureWp, "下压保护触发，位置待确认", bed.Code, "分流目的地", true,
                    "Z未在目标容差内触发X2；报警已复位，禁止自动继续");
            pendingSafetyAlarm = $"2号线后天车从{bed.Code}下料时触发下压保护，Z未确认到位。" +
                "引擎已暂停，本动作软件锁将在收尾后释放；请人工确认天车、磁铁和工件位置。异常：" + ex.Message;
            Console.WriteLine($"│ [下料] ⚠ {pendingSafetyAlarm}");
        }
        // 仅处理仍有效的本轮分流等待超时，不对已应急失效的旧床执行清零或状态写回。
        catch (LineNotStartedTimeoutException ex)
        {
            if (!operation.IsValid) return;
            _paused = true;
            discardOnFailure = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnRearCraneSafetyAlarm?.Invoke($"2号线后天车{bed.Code}异常：{ex.Message}。线路已暂停，正在按本次动作作废收尾；请勿恢复或删除下一板。");
            // 线路未启动超时：本趟分流作废。
            // 抛点固定在拿 M818/M821 之前、任何 XY 移动与退磁之前，所以天车仍停在斜床上方安全Z、
            // 保持持件，两把分流锁根本没拿。因此这里只做三件事：
            //   ① 清该斜床 PLC 下发数据；② 内存状态回空闲；③ 暂停本线并告警。
            // 后天车锁由外层 finally 释放，本分支不需要任何手工释放（拿锁前的等待也不存在锁泄漏）。
            // 不设置 pendingSafetyAlarm：外层 finally 会在其非空时追加一条要求先走斜床应急的口径，
            // 而本场景斜床已回空闲，操作员应当直接启动线路继续，故由本分支自己发一条告警。
            action.MarkCommandResponseUnknown(ex.Message);
            action.PauseForManualResolution(ex.Message);
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = $"{action.Step}：分流前等待本线启动超时" }, operationalTracker, ex,
                "后天车在分流区外等待本线启动超时；本轮分流已作废，动作账本已暂停等待人工确认。", true,
                holdingWorkpiece: holdingWorkpiece, actionSnapshot: action.Snapshot());

            // ① 清该斜床 PLC 下发数据（复用斜床应急作废的同一方法，保证清除范围一致）
            string deviceClearLog;
            try
            {
                deviceClearLog = await ClearSkewDeviceEmergencyRegistersAsync(bed, ct);
            }
            catch (Exception clearEx)
            {
                deviceClearLog = $"设备侧清零失败: {clearEx.GetType().Name} - {clearEx.Message}；PLC下发数据可能部分残留，请人工核对";
            }

            // ② 内存状态回空闲（与 EmergencyClearSkewBedAsync 的清除清单逐项一致）
            lock (_wpLock)
            {
                bed.Wp = null;
                bed.St = SkewState.Idle;
                bed.RqData = bed.RqLoad = bed.RqUnload = bed.Clamped = false;
                bed.SignalFresh = false;
                bed.DoorOpen = false;
                bed.CompletionExported = false;
                bed.WpRecoveryNeeded = false;
                bed.X11UnloadMissWarningShown = false;
                Interlocked.Increment(ref bed.PollVersion);
            }
            ClearRearCraneTask();

            // ③ 告警：说清"本轮已作废、天车仍持件、下一步做什么"，不要误导操作员去走人工确认的应急路径
            bool deviceCleared = !deviceClearLog.StartsWith("设备侧清零失败", StringComparison.Ordinal);
            string abortAlarm =
                $"2号线后天车在{bed.Code}下料分流前等待本线启动超时（{ex.TimeoutMs / 1000}秒），天车保持持件与安全Z，" +
                $"未进入分流区、未退磁。本轮分流已作废：{bed.Code} PLC下发数据{(deviceCleared ? "已清除" : "清除失败")}、" +
                "内存状态已回空闲、后天车锁由收尾释放。" +
                "请人工放磁取下工件并把天车移回安全位置，然后启动线路继续。";
            Console.WriteLine($"│ [下料] ⚠ {abortAlarm}（{deviceClearLog}）");
            OnRearCraneSafetyAlarm?.Invoke(abortAlarm);
        }
        catch (Exception ex)
        {
            if (!operation.IsValid) return;
            _paused = true;
            discardOnFailure = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnRearCraneSafetyAlarm?.Invoke($"2号线后天车{bed.Code}异常：{ex.Message}。线路已暂停，正在按本次动作作废收尾；请勿恢复或删除下一板。");
            // 停止后禁止finally追加横移、下探或退磁；保留实际现场供人工处理。
            operation.HoldForManualSafetyRecovery(ex.Message);
            requiresManualConfirmation = true;
            if (!action.IsFinalized) action.PauseForManualResolution(ex.Message);
            pendingSafetyAlarm = $"2号线后天车{bed.Code}下料严重异常：{ex.Message}。本次板任务作废，设备内存将回Idle；请人工移走本板，确认天车安全及实际放磁后恢复。";
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, ex is PickupFailedException pickupFailure ? pickupFailure.EventCode : "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "严重异常作废" }, operationalTracker, ex,
                pendingSafetyAlarm, true, actionSnapshot: action.Snapshot());
        }
        finally
        {
            // 异常自动作废：只要这一轮没走到 action.Complete(Completed)，就清这台斜床的
            // PLC 下发数据和内存工件数据并回空闲。放在 finally 最前面，让后面的告警文案
            // 能反映"已自动作废"这个事实。中转架账本完全不碰。
            if (discardOnFailure && operation.IsValid && ReferenceEquals(bed.ActiveOperation, operation))
            {
                // 下料不清中转架来源或成功交付的动平衡/研磨缓存，只作废本床与本趟动作身份。
                await ClearSkewOnAbortAsync(bed, isLoad: false);
                action.Discard("异常本板已作废，设备内存Idle，旧人工续搬入口删除");
            }

            if (operation.RequiresManualSafetyRecovery)
            {
                // 位置未知时禁止finally再移动，但软件锁随已暂停的动作退出而释放。
                bool rearReleased = operation.TryRelease("RearCrane", _craneRearLock);
                bool mtReleased = operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                bool tsReleased = operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                bool m818Released = operation.TryRelease("M818", _lockM818);
                bool m821Released = operation.TryRelease("M821", _lockM821);
                string alarm = (pendingSafetyAlarm ?? operation.ManualSafetyHoldReason ?? "后天车运动超时") +
                    $" 本动作软件锁已释放(后天车={YN(rearReleased)} ZoneMT={YN(mtReleased)} ZoneTS={YN(tsReleased)} M818={YN(m818Released)} M821={YN(m821Released)})。" +
                    AbortClearInstruction;
                Console.WriteLine($"│ [下料] ⚠ {alarm}");
                OnRearCraneSafetyAlarm?.Invoke(alarm);
                _fastNextCycle = true;
            }
            else
            {
            string sharedCleanupText = string.Empty;
            if (sharedLocked)
            {
                if (!operation.IsHeld("ZoneTS"))
                {
                    sharedLocked = false;
                    sharedCleanupText = "ZoneTS已由斜床应急提前释放";
                    Console.WriteLine("│ [下料] ZoneTS已由应急释放, 跳过finally退避释放");
                }
                else
                {
                    if (sharedRetreatAttempted)
                    {
                        // 情景：正常安全点的提前X退避已经失败。禁止finally重复发送同一运动命令。
                        sharedCleanupText = "Z轴已回安全高度，但X轴+1000毫米提前退避失败，finally未重复运动";
                    }
                    else if (requiresManualConfirmation || zMayBeDown)
                    {
                        // 情景：Z下降、尾座张开等待、磁铁或Z回升阶段异常，实际Z位置可能不安全。
                        sharedCleanupText = requiresManualConfirmation
                            ? "下压保护触发且位置未确认，finally未发送自动退避命令"
                            : "Z轴已经或可能已经下降，程序未执行X轴+1000毫米退避";
                        Console.WriteLine("│ [下料] ⚠ Z轴已经或可能已经下降, 禁止X+1000低位横移");
                    }
                    else
                    {
                        // 情景：异常发生在Z下降前，或已确认Z回安全高度；按现场策略尝试一次X+1000。
                        sharedRetreatAttempted = true;
                        sharedRetreatSucceeded = await TryRetreatSharedAreaBeforeReleaseAsync(cr, bed.Code, "下料finally", ct);
                        sharedCleanupText = sharedRetreatSucceeded
                            ? "Z轴处于安全阶段，X轴+1000毫米退避成功"
                            : "Z轴处于安全阶段，但X轴+1000毫米退避失败或位置无法确认";
                    }

                    // 与ST606上料保持一致：无论退避成功、失败或因低Z跳过，
                    // 都释放本次下料动作持有的ZoneTS；弹窗明确说明锁已释放并要求立即人工处理。
                    operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                    sharedLocked = false;
                    sharedCleanupText += "；ZoneTS已释放";

                    if (!sharedRetreatSucceeded && pendingSafetyAlarm == null)
                    {
                        _paused = true;
                        pendingSafetyAlarm = $"2号线后天车在{bed.Code}下料异常收尾时未完成ZoneTS侧X退避。引擎已暂停，请立即人工确认天车位置";
                    }
                }
            }

            // 情景：ST606已正常完成X退避并提前释放ZoneTS，随后分流/目的位又发生异常。
            // finally无需再次释放，但最终弹窗仍应明确告诉操作员ZoneTS已经释放。
            if (sharedWasAcquired && !sharedLocked && string.IsNullOrWhiteSpace(sharedCleanupText))
                sharedCleanupText = "ZoneTS已在正常提前退避后释放";

            bool rearReleased = operation.TryRelease("RearCrane", _craneRearLock);
            string rearCleanupText = rearReleased ? "后天车锁已释放" : "后天车锁已由斜床应急提前释放";

            if (pendingSafetyAlarm != null)
            {
                string cleanupText = string.IsNullOrWhiteSpace(sharedCleanupText)
                    ? rearCleanupText
                    : $"{sharedCleanupText}；{rearCleanupText}";
                pendingSafetyAlarm += $"。异常收尾：{cleanupText}。" + AbortClearInstruction;
                Console.WriteLine($"│ [下料] ⚠ {pendingSafetyAlarm}");
                OnRearCraneSafetyAlarm?.Invoke(pendingSafetyAlarm);
            }
            
            _fastNextCycle = true;
            Console.WriteLine("│ [下料] 后天车锁收尾完成" + (!string.IsNullOrWhiteSpace(sharedCleanupText) ? " + ZoneTS收尾完成" : ""));
            Console.WriteLine("└── [下料] 结束 ──────────────────────────");
            }
            EndSkewOperation(bed, operation);
        }
    }

    /// <summary>
    /// ZoneTS锁兜底释放前的安全退避。
    /// finally里仍持有ZoneTS, 说明流程没有走到正常提前释放点, 后天车可能停在ST606上方。
    /// 这里直接读取天车当前X坐标并加配置退避距离后移动, 不套数据库偏移; 退避失败则由finally弹窗说明后释放ZoneTS。
    /// </summary>
    private async Task<bool> TryRetreatSharedAreaBeforeReleaseAsync(CraneService? cr, string stationCode, string flowName, CancellationToken ct)
    {
        try
        {
            if (cr == null)
            {
                Console.WriteLine($"│ [{flowName}] ⚠ ZoneTS侧退避失败: 后天车服务未创建");
                return false;
            }

            var status = await cr.ReadStatusAsync(ct);
            if (status == null)
            {
                Console.WriteLine($"│ [{flowName}] ⚠ ZoneTS侧退避失败: 后天车当前位置读取失败");
                return false;
            }

            int retreatX = _cfg.SkewBed.GetSharedAreaRetreatX(stationCode);
            if (retreatX <= 0)
            {
                Console.WriteLine($"│ [{flowName}] ZoneTS侧退避距离未配置或<=0: {stationCode}={retreatX}");
                return false;
            }

            int targetX = status.XPos + retreatX;
            Console.WriteLine($"│ [{flowName}] ZoneTS释放前退避: {stationCode} 当前X={status.XPos}, 目标X={targetX}(当前X+配置{retreatX}, 不加偏移)");
            await cr.MoveAbsoluteAsync(targetX, -1, -1, ct: ct);
            Console.WriteLine($"│ [{flowName}] ✓ 后天车已完成ZoneTS侧退避, 允许释放ZoneTS");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│ [{flowName}] ⚠ ZoneTS侧退避异常: {ex.Message}");
            return false;
        }
    }
/// <summary>
///得到斜床工艺
/// </summary>
/// <param name="process"></param>
/// <returns></returns>
    private static int ResolveFanucSkewMode(string? process)
    {
        string text = process?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return 1; // 默认粗精一体不倒角

        return text switch
        {
            "粗精一体不倒角" => 1,
            "粗精一体倒角" => 2,
            "精车" => 3,
            _ when int.TryParse(text, out var m) && m is 1 or 2 or 3 => m,
            _ => WarnAndDefaultFanuc(text)
        };
    }

    private static int WarnAndDefaultFanuc(string text)
    {
        Console.WriteLine($"│ [握手] ⚠ 斜床工艺[{text}]无法识别, 默认粗精一体不倒角(1)");
        return 1;
    }


    // ═══════════════════════════════════════════════════════════════
    //  Init — 引擎启动时执行一次
    //    ① 读后天车(ST904)数据库偏移量
    //    ② 并行连接5台斜床(整个Init硬超时5s, FANUC原生DLL阻塞也无妨)
    //    ③ 不连MC65 — 不做动平衡的短工件下料时按需连
    // ═══════════════════════════════════════════════════════════════
    /// <summary>初始化: 读后天车偏移→并行连接5台FANUC斜床</summary>
    private async Task Init(CancellationToken ct)
    {
        if (_stationCoords.TryGetValue("ST904", out var cr)) { _ox = (int)cr.XOffset; _oy = (int)cr.YOffset; _oz = (int)cr.ZOffset; }
        Console.WriteLine($"[初始化] 后天车偏移量: X={_ox} Y={_oy} Z={_oz}");

        Console.WriteLine("[初始化] 开始并行连接5台斜床...");
        var tasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            var c = codes[i]; var ctx = new SkewCtx { Code = c, St = SkewState.Idle };
            _beds[i] = ctx;
            if (TryIp(c, out var ip, out var port) && !string.IsNullOrWhiteSpace(ip))
            { Console.WriteLine($"[初始化]   {c} ip={ip}:{port}"); tasks[i] = TryConnectBed(ctx, ip, port, ct); }
            else
            { Console.WriteLine($"[初始化]   {c} 无IP配置"); tasks[i] = Task.CompletedTask; }
        }
        await Task.WhenAll(tasks);

        int connected = 0;
        for (int i = 0; i < 5; i++) if (_beds[i].Ok) connected++;
        Console.WriteLine($"[初始化] 斜床连接完成: {connected}/5台在线");
        // 2号线全FANUC, MC65不在此连接（不做动平衡的短工件下料时步骤④按需连）
    }

    /// <summary>连接单台斜床。2号线FANUC只启动独立Worker, 由Worker内部连接/重连并写入快照。</summary>
    private Task TryConnectBed(SkewCtx ctx, string ip, int port, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"[连接] {ctx.Code} 开始连接 {ip}:{port}...");
            // 2号线全FANUC — 启动独立Worker线程轮询
            var fanuc = new FanucSkewBedService(ip, port > 0 ? port : 8193);
            ctx.F?.Dispose();
            ctx.F = fanuc;
            fanuc.StartPollWorker();
            Console.WriteLine($"[连接] {ctx.Code} ✓ Worker已启动");
        }
        catch (Exception ex) { Console.WriteLine($"[连接] {ctx.Code} ✗ 失败: {ex.Message}"); }
        return Task.CompletedTask;
    }

    /// <summary>后台重连(fire-and-forget)。FANUC不走线程池, 只在Worker缺失时重建。
    /// 成功时清零 ConsecutiveReconnectFails, 失败时累加(UI通过DeviceStatus可见)。</summary>
    private Task TryConnectBedBg(SkewCtx ctx, string ip, int port)
    {
        try
        {
            Console.WriteLine($"[后台重连] {ctx.Code} {ip}:{port}...");
            // 2号线全FANUC — 重启Worker线程
            var fanuc = new FanucSkewBedService(ip, port > 0 ? port : 8193);
            ctx.F?.Dispose();
            ctx.F = fanuc;
            fanuc.StartPollWorker();
            ctx.ConsecutiveReconnectFails = 0; // 成功清零
            Console.WriteLine($"[后台重连] {ctx.Code} ✓ 成功 (连续失败次数已清零)");
        }
        catch (Exception ex)
        {
            ctx.ConsecutiveReconnectFails++; // 失败累加
            Console.WriteLine($"[后台重连] {ctx.Code} ✗ 失败(连续{ctx.ConsecutiveReconnectFails}次): {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>轮询等待CNC信号为true, 超时取 _cfg.Grinding.HandshakeTimeoutMs</summary>
    private async Task W(Func<Task<bool>> c, string n, CancellationToken ct)
    {
        int ms = _cfg.Grinding.HandshakeTimeoutMs;
        var dl = DateTime.UtcNow.AddMilliseconds(ms);
        int waited = 0;
        while (DateTime.UtcNow < dl)
        {
            ct.ThrowIfCancellationRequested();
            if (await c()) { Console.WriteLine($"│   ✓ 信号[{n}]已就绪(等待{waited}ms)"); return; }
            await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct); waited += _cfg.Grinding.SignalPollIntervalMs;
        }
        throw new TimeoutException($"等待信号[{n}]超时({ms}ms)");
    }

    /// <summary>
    /// ST020 放料前的目的地二次确认。
    /// 主循环只做预检；拿到ST020/ST021区域双锁后必须重新读PLC，避免旧快照导致误放。
    /// <para>
    /// M818=未允许放料时不移动天车、不退磁、不暂停、也不释放分流锁，保持持件和安全 Z
    /// 按轮询周期无限等待，直到 M818 恢复允许放料才允许进入分流区。
    /// 现场确认 M818 的"未允许"属于残留/不稳定读数，实际不会出现 ST020 上真有一块
    /// 等待机械手2取走的板，因此采用持锁等待，不引入额外的分流区进入窗口。
    /// </para>
    /// PLC 读取失败、报文异常或动作取消仍按既有安全异常路径抛出。
    /// </summary>
    private async Task EnsureM818CanPlaceBeforePlaceAsync(WorkpieceCache wp, CancellationToken ct)
    {
        int waitCount = 0;
        while (!await ReadM818CanPlaceAsync(ct, log: waitCount == 0))
        {
            waitCount++;
            if (waitCount == 1 || waitCount % 10 == 0)
                Console.WriteLine($"│ [分流] ⚠ {wp.IdentityText} M818二次确认=未允许放料, 保持持件和安全Z持锁等待（{waitCount * _cfg.Grinding.PollIntervalMs / 1000.0:F1}秒）");
            await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
        }

        if (waitCount > 0)
            Console.WriteLine($"│ [分流] ✓ {wp.IdentityText} M818恢复允许放料, 结束持锁等待, 继续放料");
    }

    /// <summary>
    /// ST021 放料前的目的地二次确认。
    /// M820=未允许放料时不移动天车、不退磁、不暂停、也不释放分流锁，保持持件和安全 Z
    /// 按轮询周期无限等待，直到 M820 恢复允许放料才允许进入分流区。处理方式与 ST020 相同。
    /// </summary>
    private async Task EnsureM820CanPlaceBeforePlaceAsync(WorkpieceCache wp, CancellationToken ct)
    {
        int waitCount = 0;
        while (!await ReadM820CanPlaceAsync(ct, log: waitCount == 0))
        {
            waitCount++;
            if (waitCount == 1 || waitCount % 10 == 0)
                Console.WriteLine($"│ [分流] ⚠ {wp.IdentityText} M820二次确认=未允许放料, 保持持件和安全Z持锁等待（{waitCount * _cfg.Grinding.PollIntervalMs / 1000.0:F1}秒）");
            await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
        }

        if (waitCount > 0)
            Console.WriteLine($"│ [分流] ✓ {wp.IdentityText} M820恢复允许放料, 结束持锁等待, 继续放料");
    }

    /// <summary>读取M818是否允许放料(=1)。读取失败和报文异常由调用方按安全异常处理。</summary>
    private async Task<bool> ReadM818CanPlaceAsync(CancellationToken ct, bool log)
        => await ReadRearHandshakeBitAsync(RackAddr.Bit_ST020_CanPlace, "M818", ct, log);

    /// <summary>读取M820是否允许放料(=1)。读取失败和报文异常由调用方按安全异常处理。</summary>
    private async Task<bool> ReadM820CanPlaceAsync(CancellationToken ct, bool log)
        => await ReadRearHandshakeBitAsync(RackAddr.Bit_ST021_CanPlace, "M820", ct, log);

    /// <summary>读取MC63后半握手字(M816~M831)的指定bit。报文变短视为错误，不按0继续。</summary>
    private async Task<bool> ReadRearHandshakeBitAsync(int bitOffset, string signalName, CancellationToken ct, bool log)
    {
        using var t = new CancellationTokenSource(3000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, t.Token);
        var mc63 = await EnsureMc63Async(linked.Token);
        var r = await mc63.ReadMAlignedWordAsync(800, 2, linked.Token);
        int m816 = r.IntValues.Length > 1
            ? r.IntValues[1]
            : throw new InvalidOperationException($"MC63 {signalName}二次确认返回字数不足");
        bool value = (m816 & (1 << bitOffset)) != 0;
        if (log)
            Console.WriteLine($"│ [分流] 二次确认{signalName}={(value ? "允许放料" : "未允许放料")} rawM816=0x{m816:X4}");
        return value;
    }

    private async Task WriteMc63HandshakeBitAsync(int bitOffset, string signalName, CancellationToken ct)
    {
        using var t = new CancellationTokenSource(3000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, t.Token);
        var mc63 = await EnsureMc63Async(linked.Token);
        await mc63.WriteMBitInWordAsync(RackAddr.M_RearHandshakeWordStart, bitOffset, true, linked.Token);
        Console.WriteLine($"│ [分流] ✓ {signalName}=1 已写入");
    }

    /// <summary>Z下降公式: 台面Z - Round[(d/2/zFactor1)+(d/2/zFactor2)] — 用于取料/放料</summary>
    private int Pz(int z, double d)
    {
        return z - (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));
    }

    /// <summary>从数据库取工位XYZ坐标(含数据库偏移量) 这个偏移量是工件自身的</summary>
    private bool TryCoords(string code, out int x, out int y, out int z)
    {
        x = y = z = 0; 
        if (!_stationCoords.TryGetValue(code, out var r)) return false;
        x = (int)(r.X + r.XOffset); y = (int)(r.Y + r.YOffset);
        z = (int)(r.Z + r.ZOffset); 
        return true;
    }

    /// <summary>从数据库取工位IP:端口</summary>
    private bool TryIp(string code, out string ip, out int port) { ip = ""; port = 0; if (!_stationCoords.TryGetValue(code, out var r)) return false; ip = r.Ip; port = r.Port; return !string.IsNullOrWhiteSpace(ip); }

    /// <summary>bool→"1"/"0" (日志用)</summary>
    private static string YN(bool b) => b ? "1" : "0";

    /// <summary>
    /// 异常自动作废：清该斜床的 PLC 下发数据、清内存维护的工件数据并回空闲、清后天车任务牌、
    /// 由调用方只结本动作快照，并更新实际收尾结果。
    /// <para>
    /// 现场模型：异常了这块板会被人工<b>抽走</b>，设备侧随即变空，下一轮派发拿别的板。
    /// 因此设备helper只清这台斜床，来源作废在调用方处理——本次来源预约/代次作废由调用方核对并处理。
    /// </para>
    /// 安全边界：X11残留检查不能证明非磁铁承载面空，恢复前必须人工处理实物并确认安全。
    /// <para>
    /// 使用独立10秒清理令牌：本方法在 finally 中执行，而动作的 ct 可能已被
    /// 应急取消；若沿用它会在此抛出 OperationCanceledException，掩盖真正的原始异常。
    /// </para>
    /// </summary>
    private async Task ClearSkewOnAbortAsync(SkewCtx bed, bool isLoad)
    {
        // 调用者已经验证严重失败、本轮租约及床归属；此方法不接管取消判断或其他床的任务。
        // 即使PLC清理失败，仍清本床软件工件和请求快照并回Idle，失败原因显示为可能残留。
        // 不写机床“请求数据=1”，不自动移板/退磁/回安全位；软件Idle不能作为现场空床证明。
        string deviceLog;
        try
        {
            using var cleanupCts = new CancellationTokenSource(10_000);
            deviceLog = await ClearSkewDeviceEmergencyRegistersAsync(bed, cleanupCts.Token);
        }
        catch (Exception ex)
        {
            deviceLog = $"设备侧清零失败: {ex.GetType().Name} - {ex.Message}；PLC下发数据可能部分残留，请人工核对";
        }

        lock (_wpLock)
        {
            bed.Wp = null;
            bed.St = SkewState.Idle;
            bed.RqData = bed.RqLoad = bed.RqUnload = bed.Clamped = false;
            bed.SignalFresh = false;
            bed.DoorOpen = false;
            bed.CompletionExported = false;
            bed.WpRecoveryNeeded = false;
            bed.X11UnloadMissWarningShown = false;
            Interlocked.Increment(ref bed.PollVersion);
        }
        ClearRearCraneTask();
        string ledgerLog = "本次严重动作由调用方作废结案；其他设备人工记录未删除";
        _fastNextCycle = true;

        FlowAlarmPresentationContext.RecordCleanup(deviceLog, "斜床已回Idle，工件与请求快照已清；后天车任务牌已清",
            "中转架来源账本未由此清理修改；已移交下游的缓存不回滚；请核对实物与来源预约状态");
        Console.WriteLine(
            $"│ [作废] 2号线后天车{bed.Code}{(isLoad ? "上料" : "下料")}异常：本轮已自动作废——" +
            $"内存工件已清并回空闲、任务牌已清；{ledgerLog}；设备清理实际结果：{deviceLog}。" +
            "请把本块板抽走后启动线路继续。");
    }

    /// <summary>异常自动作废时写给操作员的统一指引，替换原先要求人工走应急的口径。</summary>
    private const string AbortClearInstruction =
        "本轮软件任务已作废，斜床内存已回空闲；PLC清理和锁释放以本窗实际结果为准。" +
        "请移走本次板，确认斜床无残留、天车安全位置和实际放磁，再启动对应线路；勿删下一板任务。";

    /// <summary>
    /// 后天车下料分流前确认本线已启动。判据是后端引擎自身的暂停位 <c>_paused</c>：
    /// 主页"暂停2号线"会同时暂停前端与后端引擎，因此它与页面按钮状态天然同步；
    /// 引擎因自身异常自动暂停时同样会拦住。
    /// <para>
    /// 等待期间不移动天车、不退磁、不释放任何锁，保持持件与安全 Z。
    /// 超过 <c>grinding.lineStartWaitTimeoutMs</c> 仍未启动则抛
    /// <see cref="LineNotStartedTimeoutException"/>，由专用 catch 分支作废本趟分流。
    /// </para>
    /// </summary>
    private async Task EnsureLineRunningBeforeDivertAsync(SkewCtx bed, CancellationToken ct)
    {
        if (!_paused) return;

        int timeoutMs = _cfg.Grinding.LineStartWaitTimeoutMs;
        Console.WriteLine($"│ [分流] ⏸ 本线未启动，{bed.Wp?.IdentityText ?? "当前工件"} 保持持件与安全Z在分流区外等待" +
            $"（上限{timeoutMs / 1000}秒）...");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (_paused)
        {
            if (waited.ElapsedMilliseconds >= timeoutMs)
                throw new LineNotStartedTimeoutException("2号线", bed.Code, timeoutMs);
            await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
        }
        Console.WriteLine($"│ [分流] ▶ 本线已启动（等待{waited.ElapsedMilliseconds}ms），继续进入分流区");
    }

    /// <summary>
    /// ST020长板/强制动平衡及ST021短板在拿到M818、M821且目的地允许后，共用此锁内等待。
    /// 两台机械手都必须实际Y低于各自提前放锁阈值，且各自D5007严格等于1才允许进入分流区。
    /// 每轮Y与回原点值来自同一次实时状态读取，不沿用上次页面/轮询结果，也不写回原点信号。
    /// 未满足或状态读失败时无限轮询，不暂停、不移动、不退磁、不释放或重新获取位置锁。
    /// 原候选阶段报警门禁保持原位；动作取消仍向外抛出，由原finally处理本动作持锁。
    /// </summary>
    private async Task WaitManipulatorsClearOfRackAsync(CancellationToken ct)
    {
        int m2Below = _cfg.Balancing.M2ReleaseLocksBelowY;
        int m3Below = _cfg.Balancing.M3ReleaseLocksBelowY;
        int pollMs = Math.Max(_cfg.Grinding.SignalPollIntervalMs, 200);
        int waitCount = 0;
        Console.WriteLine($"│   分流前确认机械手2(Y<{m2Below}且D5007=1)、机械手3(Y<{m3Below}且D5007=1)...");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int? m2Y = null, m3Y = null;
            // 本轮先设为未知，读取失败不能复用上一轮的D5007=1。
            short? m2YHome = null, m3YHome = null;
            string m2Err = "", m3Err = "";
            try
            {
                var s = await _manipulatorCache.GetOrCreateService(2).ReadStatusAsync(ct);
                m2Y = s?.YPos;
                m2YHome = s?.YHomeCompleted; // D5007，与本轮实际Y来自同一个PLC快照。
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { m2Err = ex.Message; }
            try
            {
                var s = await _manipulatorCache.GetOrCreateService(3).ReadStatusAsync(ct);
                m3Y = s?.YPos;
                m3YHome = s?.YHomeCompleted; // 两台分别读取，不能用机械手2的标志代替机械手3。
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { m3Err = ex.Message; }

            // 原实际Y条件保留，再增加严格D5007==1；两台都通过才退出持锁等待。
            bool m2Clear = ManipulatorRackPositionGate.IsClear(m2Y, m2YHome, m2Below);
            bool m3Clear = ManipulatorRackPositionGate.IsClear(m3Y, m3YHome, m3Below);
            if (m2Clear && m3Clear)
            {
                Console.WriteLine($"│   机械手2 Y={m2Y}(<{m2Below}) D5007={m2YHome}、机械手3 Y={m3Y}(<{m3Below}) D5007={m3YHome} 均满足分流条件 ✓");
                return;
            }

            waitCount++;
            if (waitCount == 1 || waitCount % 10 == 0)
            {
                string m2Text = m2Y.HasValue ? m2Y.Value.ToString() : (string.IsNullOrEmpty(m2Err) ? "读失败" : $"读失败({m2Err})");
                string m3Text = m3Y.HasValue ? m3Y.Value.ToString() : (string.IsNullOrEmpty(m3Err) ? "读失败" : $"读失败({m3Err})");
                string m2HomeText = m2YHome?.ToString() ?? "未知";
                string m3HomeText = m3YHome?.ToString() ?? "未知";
                Console.WriteLine($"│   [分流等待] 机械手2 Y={m2Text}(需<{m2Below}) D5007={m2HomeText}(需=1) 机械手3 Y={m3Text}(需<{m3Below}) D5007={m3HomeText}(需=1)，继续等待；保持持件和位置锁");
            }
            await Task.Delay(pollMs, ct);
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _engineCts.Cancel(); _engineCts.Dispose(); _craneRearLock.Dispose();
        foreach (var bed in _beds)
        {
            try { bed?.F?.Dispose(); } catch { }
        }
    }
}


