using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 1号线后端流程引擎 — 后天车(Crane#2) + 5台斜床(ST108~ST112)
/// 
/// 【主循环 500ms/轮 — 5步骤】
///   步骤① 轮询斜床PLC信号(请求数据/上料/夹紧/下料/张开) → 同步引擎状态
///   步骤② 斜床断线重连(每20轮, 仅斜床不连MC65)
///   步骤③ 状态汇总(纯日志)
///   步骤④/⑤ 动态调度 — 先生成上/下料候选，再按中转架压力、下料压力、长度匹配决定本轮动作
///
/// 【锁安全】
///   _craneRearLock(后天车) → ZoneMT(打号机-中转架) → ZoneTS(中转架-ST108)
///
/// 【MC65策略】
///   Init不连 / 主循环不连 / 重连也不连
///   仅步骤④不做动平衡的短工件下料到研磨架M720时按需连接(3s超时)
///
/// 【斜床协议】
///   ST108~ST111: 锦州迅捷PLC ModbusTCP (级联清零: 写新信号前清上一个)
///   ST112:       沈阳 FANUC FOCAS (级联清零: SafeSetMacro)
/// </summary>
public sealed class Line1RearFlowEngine : IDisposable
{
    private readonly CraneConnectionCache _craneCache;
    private readonly ManipulatorConnectionCache _manipulatorCache;
    private readonly MotionConfig _cfg;
    private readonly SemaphoreSlim _transferRackLock;
    private readonly SafetyFlags _safety;
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords;
    private readonly IOperationalEventReporter _exceptionReporter;
    private readonly Line1FrontFlowEngine.Line1DeviceStatus? _frontDs;
    private readonly CancellationTokenSource _engineCts = new();
    private Task? _engineTask;
    private bool _disposed;
    private volatile bool _paused;
    private volatile bool _fastNextCycle; // DoLoad/DoUnload完成后设true, 主循环缩短延迟
    private int _cycleCount;
    private readonly RearSkewDispatchLogGate _dispatchLogGate = new();

    // ── 后天车 ──
    private readonly SemaphoreSlim _craneRearLock = new(1, 1); // 串行化后天车操作
    private const int CraneRearNo = 2;                           // 后天车编号(对应192.168.2.82)
    private int _ox, _oy, _oz;                                   // 后天车数据库偏移量(mm)
    private readonly object _rearCraneTaskLock = new();
    private CraneTaskSnapshot _rearCraneTask = CraneTaskSnapshot.Idle(CraneRearNo, "1号线后天车");

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
    /// <summary>下料放至研磨上料架ST010时回调, 通知研磨引擎入缓存(完整工件信息)</summary>
    public Action<WorkpieceCache>? OnGrindingRackPlaced;
    /// <summary>后天车连续两次读到XYZ全零时通知主页面暂停整条1号线并弹窗。</summary>
    public Action<CraneZeroPositionAlarm>? OnCraneZeroPositionDetected;
    /// <summary>后天车异常恢复失败、现场位置不再可确认时通知主页面暂停整条1号线并弹窗。</summary>
    public Action<string>? OnRearCraneSafetyAlarm;
    /// <summary>X11三次失败已有最终结构化事件；此回调只复用整线暂停和弹窗，避免重复Legacy事件。</summary>
    public Action<string>? OnRearCraneX11FinalSafetyAlarm;
    /// <summary>X11连续无板但已安全退磁回升时通知主页面一次，不暂停引擎。</summary>
    public Action<string>? OnRearCraneWarning;

    /// <summary>单台斜床运行时上下文</summary>
    private sealed class SkewCtx
    {
        public string Code = "";                         // 站号 ST108~ST112
        private SkewState _st;                             // 当前引擎状态
        public SkewState St
        {
            get => _st;
            set { if (_st != value) { _st = value; StateChangedAtUtc = DateTime.UtcNow; } }
        }
        public DateTime StateChangedAtUtc { get; private set; } = DateTime.UtcNow;
        public ModbusSkewBedService? M;                   // 锦州Modbus服务 (ST108~ST111)
        public FanucSkewBedService? F;                    // 沈阳FANUC服务 (ST112)
        public WorkpieceCache? Wp;                        // 正在加工的工件数据(上料时从_wps取出绑定)
        public bool Ok => (M?.IsConnected == true) || (F?.IsConnected == true);
        public bool Idle => St == SkewState.Idle && Ok;
        /// <summary>最新轮询的CNC信号快照</summary>
        public bool RqData, RqLoad, RqUnload, Clamped;
        /// <summary>本轮CNC信号是否新鲜。轮询失败时禁止用旧RqData/RqUnload派发动作。</summary>
        public bool SignalFresh;
        /// <summary>状态轮询版本。超时/新一轮启动会递增, 防止旧轮询晚返回覆盖新状态。</summary>
        public long PollVersion;
        /// <summary>是否已有一个状态读取在飞。FANUC同步SDK异常阻塞时, 禁止每轮继续堆积新读取。</summary>
        public int PollInFlight;
        /// <summary>是否已有重连任务在执行。每台斜床只允许一个重连入口，避免重复创建连接。</summary>
        public int ReconnectInProgress;
        /// <summary>当前工件斜床完工记录是否已写入job2。只防重复导出, 不参与运动流程。</summary>
        public bool CompletionExported;
        /// <summary>后台重连连续失败次数(成功时清零, UI可查看)</summary>
        public int ConsecutiveReconnectFails;
        /// <summary>引擎重启后检测到斜床上有工件但Wp=null → 需人工确认</summary>
        public bool WpRecoveryNeeded;
        /// <summary>同一故障周期只弹一次X11连续无板警告，X11成功或应急清除后复位。</summary>
        public bool X11UnloadMissWarningShown;
        /// <summary>当前上/下料动作租约。锁归属只属于该动作, 不会被后续动作覆盖。</summary>
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
    public Line1RearDeviceStatus DeviceStatus => DS;
    public Line1RearDeviceStatus DS { get; } = new();

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
            CreateRackSnapshot("ST105"), CreateRackSnapshot("ST101"), CreateRackSnapshot("ST106")
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
            _rearCraneTask = new CraneTaskSnapshot(true, CraneRearNo, "1号线后天车", WorkpieceDisplaySnapshot.From(wp),
                stage, source, target, manual, manualText, DateTime.UtcNow);
    }

    private void ClearRearCraneTask()
    {
        lock (_rearCraneTaskLock) _rearCraneTask = CraneTaskSnapshot.Idle(CraneRearNo, "1号线后天车");
    }

    public sealed class Line1RearDeviceStatus
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

    /// <param name="lockM817">M817位置锁: Line1后天车放料 ↔ M2Flow取料 互斥</param>
    /// <param name="lockM720">M720位置锁: Line1后天车放料 ↔ M3Flow放料 ↔ 研磨天车取料 互斥</param>
    public Line1RearFlowEngine(CraneConnectionCache cc, ManipulatorConnectionCache mc,
        McConnectionCache mcc, MotionConfig cfg, Dictionary<string, MachineManagementRowVm> sc,
        SemaphoreSlim trl, SafetyFlags sf, IOperationalEventReporter exceptionReporter,
        Line1FrontFlowEngine.Line1DeviceStatus? fd = null,
        SemaphoreSlim? lockM817 = null, SemaphoreSlim? lockM720 = null)
    {
        _craneCache = cc; _manipulatorCache = mc; _mcCache = mcc; _cfg = cfg; _stationCoords = sc;
        _transferRackLock = trl; _safety = sf; _exceptionReporter = exceptionReporter; _frontDs = fd;
        _lockM817 = lockM817; _lockM720 = lockM720;
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  [后引擎] 1号线后端流程引擎 已创建");
        Console.WriteLine("  天车编号=2号  斜床=ST108~ST112");
        Console.WriteLine($"  平衡锁: M817={(_lockM817 != null ? "✓" : "✗(无锁!)")} M720={(_lockM720 != null ? "✓" : "✗(无锁!)")}");
        Console.WriteLine("══════════════════════════════════════════");
    }

    // ── 平衡料架位置锁(防止天车和机械手同时操作同位置) ──
    private readonly SemaphoreSlim? _lockM817; // M817位置: 后天车(放) vs M2Flow(取)
    private readonly SemaphoreSlim? _lockM720; // M720位置: 后天车(放) vs M3Flow(放) vs 研磨(取)

    public void Start()
    {
        if (IsRunning) return;
        _paused = false;
        Console.WriteLine("[后引擎] ▶ 启动");
        _engineTask = Task.Run(() => Loop(_engineCts.Token));
    }
    public void Stop() { Console.WriteLine("[后引擎] ■ 停止"); _engineCts.Cancel(); }
    public void Pause() { _paused = true; Console.WriteLine("[后引擎] ⏸ 暂停"); }
    public void Resume() { _paused = false; Console.WriteLine("[后引擎] ▶ 继续"); }

    public void EnqueueWp(string rs, WorkpieceCache wp) { _rackLedger.Enqueue(rs, wp, out var seq); Console.WriteLine($"[后引擎] 工件缓存写入: 中转架={rs} 顺序={seq} {wp.IdentityText} 直径={wp.Diameter}mm 版长={wp.Length}mm 缓存总数={CachedCount}"); }
    public void EnqueueWorkpiece(string rs, WorkpieceCache wp) => EnqueueWp(rs, wp);
    public void SetRackWorkpiece(string code, WorkpieceCache wp)
    {
        _rackLedger.Enqueue(code, wp, out var seq);
        Console.WriteLine($"[后引擎] 工件缓存: 中转架={code} 顺序={seq} {wp.IdentityText} 直径={wp.Diameter}mm 版长={wp.Length}mm 堵孔={wp.BoreType}mm 缓存总数={CachedCount}");
    }

    public bool TrySetManualRackWorkpiece(string code, WorkpieceCache wp, out string message)
    {
        // 人工入口也必须走同一账本，不能覆盖一个正在被后天车预约的来源位。
        if (!_rackLedger.TryEnqueueIfEmpty(code, wp, out var seq))
        {
            message = $"1号线{code}已有工件缓存或正在取料预约中, 拒绝覆盖";
            return false;
        }
        message = string.Empty;
        Console.WriteLine($"[后引擎] 人工中转架缓存写入: 中转架={code} 顺序={seq} {wp.IdentityText} 直径={wp.Diameter}mm 版长={wp.Length}mm 缓存总数={CachedCount}");
        return true;
    }

    /// <summary>
    /// 仅处理已被冻结的中转架来源账本。调用方必须先在现场确认真实位置；
    /// 本方法不发送任何天车、磁铁或CNC命令，也不会自动恢复引擎。
    /// </summary>
    public string ResolveManualSourceReservation(string rackCode, FlowActionManualResolution resolution)
    {
        if (!_rackLedger.IsHeldForManualResolution(rackCode, out string operationId, out string reason))
            return $"1号线{rackCode}没有待人工结案的来源缓存";
        if (!_rackLedger.TryResolveManualReservation(rackCode, operationId, resolution, out WorkpieceCache wp, out _))
            return $"1号线{rackCode}来源缓存结案失败；请刷新后重试";

        string result = resolution switch
        {
            FlowActionManualResolution.StillAtSource => "确认工件仍在来源位：保留缓存，解除冻结，允许后续重新派发",
            FlowActionManualResolution.OnCarrier => "确认工件在后天车：已从来源账本移除，保留人工任务牌，禁止自动派发",
            FlowActionManualResolution.AtTargetPendingHandoff => "确认工件已在目标待交接：已从来源账本移除，保留人工任务牌，禁止自动派发",
            FlowActionManualResolution.RemovedManually => "确认工件已人工移走：已从来源账本移除",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null)
        };
        if (resolution is FlowActionManualResolution.OnCarrier or FlowActionManualResolution.AtTargetPendingHandoff)
            SetRearCraneTask(wp, resolution == FlowActionManualResolution.OnCarrier ? "人工确认天车持件" : "人工确认目标待交接",
                rackCode, "人工确认目标", true, result);
        else if (GetCraneTaskSnapshot().SourceStation == rackCode)
            ClearRearCraneTask();

        // 先完成来源缓存和任务牌的真实结案，再移除仅供诊断的暂停快照；
        // 不能反过来先删快照，否则页面会失去人工正在处理哪一板的证据。
        FlowActionManualRegistry.Remove(operationId);

        string message = $"[Line1Rear] {rackCode} 动作={operationId} 原因={reason}；{result}。后端保持暂停，需人工复核后再恢复。";
        Console.WriteLine(message);
        return message;
    }

    /// <summary>后天车上料已在斜床待交接时，仅转换软件账本；不补发CNC握手或恢复引擎。</summary>
    public string ResolveManualLoadTargetPending(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线后天车上料" || action.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff)
            return "1号线后天车上料目标结案拒绝：动作不是斜床目标待交接。";
        SkewCtx? bed = _beds.FirstOrDefault(x => x?.Code == action.Target);
        if (bed == null || !_rackLedger.TryResolveManualReservation(action.Source, operationId,
                FlowActionManualResolution.AtTargetPendingHandoff, out WorkpieceCache wp, out _))
            return "1号线后天车上料目标结案失败：斜床或来源预约与动作账本不一致。";
        bed.Wp = wp;
        bed.St = SkewState.Loading;
        bed.CompletionExported = false;
        _paused = true;
        SetRearCraneTask(wp, "人工确认斜床待交接", action.Source, bed.Code, true, "不自动补发CNC握手");
        FlowActionManualRegistry.Remove(operationId);
        return $"1号线后天车已确认工件在{bed.Code}待交接：来源账本已消费，斜床置Loading并保留人工任务牌；后端保持暂停。";
    }

    /// <summary>
    /// 后天车斜床下料动作的人工结案。这里只做不需要伪造 PLC/缓存事实的状态转换：
    /// “仍在斜床”恢复 WaitingUnload；“天车持件”保留 Unloading 和任务牌；
    /// “人工移走”才清斜床工件账本。目标待交接必须由目标缓存的专用结案器处理，
    /// 因为仅凭人工文字不能判断 M817/M720 缓存或 PLC 通知是否已经成功。
    /// </summary>
    public string ResolveManualUnloadAction(string operationId, FlowActionManualResolution resolution)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot) ||
            snapshot.FlowScope != "1号线后天车下料")
            return $"1号线后天车下料动作账本不存在: {operationId}";
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
                return $"1号线后天车确认{bed.Code}工件仍在斜床：保留工件账本并回WaitingUnload；引擎保持暂停，恢复后会重新下料派发。";
            case FlowActionManualResolution.OnCarrier:
                bed.St = SkewState.Unloading;
                SetRearCraneTask(wp, "人工确认天车持件", bed.Code, snapshot.Target, true,
                    "工件仍由后天车持有；禁止自动重试，等待后续专用应急处理。");
                FlowActionManualRegistry.Remove(operationId);
                return $"1号线后天车确认工件在天车上：斜床账本和人工任务牌保留，后端保持暂停。";
            case FlowActionManualResolution.RemovedManually:
                bed.St = SkewState.Idle;
                bed.Wp = null;
                ClearRearCraneTask();
                FlowActionManualRegistry.Remove(operationId);
                return $"1号线后天车确认{bed.Code}工件已人工移走：斜床工件账本已清，后端保持暂停。";
            case FlowActionManualResolution.AtTargetPendingHandoff:
                return "后天车目标待交接不能直接结案：请先按实际目标补齐/核对M817、M720及PLC通知；动作账本、斜床账本和暂停状态均已保留。";
            default:
                return $"不支持的人工结论: {resolution}";
        }
    }

    /// <summary>
    /// 人工已确认工件在分流目标位时，补齐正常流程尚未完成的目标缓存/PLC握手。
    /// 仅在每一步成功返回后才清斜床账本和动作快照；失败保留全部证据，绝不假装完成。
    /// </summary>
    public async Task<string> ResolveManualUnloadTargetPendingAsync(string operationId, CancellationToken ct = default)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot) ||
            snapshot.FlowScope != "1号线后天车下料" ||
            snapshot.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff)
            return $"1号线后天车目标待交接动作账本不存在: {operationId}";
        SkewCtx? bed = _beds.FirstOrDefault(x => x != null && x.Code == snapshot.Source);
        if (bed?.Wp is not { } wp || wp.IdentityText != snapshot.WorkpieceIdentity)
            return "目标待交接结案拒绝：斜床当前工件与动作账本不一致，未改动任何状态。";
        _paused = true;
        try
        {
            switch (snapshot.Target)
            {
                case "ST019":
                    if (OnBalancingRackPlaced == null) return "M817动平衡缓存回调未绑定；账本和斜床状态保留。";
                    OnBalancingRackPlaced("M817", wp);
                    break;
                case "ST010":
                    var mc65 = await EnsureMc65Async(ct);
                    await mc65.WriteMBitInWordAsync(720, 1, true, ct);
                    if (OnGrindingRackPlaced == null) return "M720研磨缓存回调未绑定；M721已成功写入，账本和斜床状态保留。";
                    wp.BoreType = 1;
                    OnGrindingRackPlaced(wp);
                    break;
                default:
                    return $"1号线目标待交接站号无效: {snapshot.Target}；未改动账本。";
            }
            bed.St = SkewState.Idle;
            bed.Wp = null;
            ClearRearCraneTask();
            FlowActionManualRegistry.Remove(operationId);
            return $"1号线后天车确认工件已在{snapshot.Target}：目标缓存/必要PLC通知补齐成功，斜床账本已结案；后端保持暂停。";
        }
        catch (Exception ex)
        {
            return $"1号线{snapshot.Target}目标待交接补齐失败: {ex.Message}；动作账本、斜床工件和人工任务牌均保留，后端保持暂停。";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  主循环
    // ═══════════════════════════════════════════════════════════════════
    private async Task Loop(CancellationToken ct)
    {
        // 不得因暂停后重新启动而清空来源账本；否则PLC仍显示有板、软件却失去身份，
        // 后天车会永久无法取料。人工应急只能通过明确的结案结果改变缓存。
        Console.WriteLine($"[后引擎] 保留来源工件账本，当前缓存={CachedCount}");
        Console.WriteLine("[后引擎] ═══ 开始初始化设备连接 ═══");
        await Init(ct);
        Console.WriteLine("[后引擎] ══════ 主循环启动 ══════");

        while (!ct.IsCancellationRequested)
        {
            if (_paused) { await Task.Delay(500, ct); continue; }
            try
            {
                _cycleCount++; 
                var ds = DS;
                ds.CycleCount = _cycleCount;
                if (_cycleCount % 50 == 1) Console.WriteLine($"\n┌────── [后引擎] 第 {_cycleCount} 轮开始 ──────");

                // ═══════════════════════════════════════════════════════════
                //  步骤① 轮询斜床PLC信号
                //    读每台已连接斜床的5个信号: 请求数据/请求上料/夹紧完成/请求下料/尾座张开
                //    同步引擎状态(Idle→Machining→WaitingUnload)
                // ═══════════════════════════════════════════════════════════
                if (_cycleCount % 10 == 1) Console.WriteLine("│ [步骤1] 轮询斜床PMC信号...");
                // 5台斜床并行轮询。直接启动异步读, 不用Task.Run占线程池;
                // FlowStatus页面吃DeviceStatus缓存, 这里不能被离线设备拖成5~20s才刷新。
                var pollTasks = new Task[5];
                var pollCts = new CancellationTokenSource?[5];
                var pollBeds = new SkewCtx?[5];
                for (int i = 0; i < 5; i++)
                {
                    var b = _beds[i];
                    if (b == null || !b.Ok) { pollTasks[i] = Task.CompletedTask; continue; }
                    b.SignalFresh = false; // 每轮先标无效, PollBed成功后再置true, 防止沿用上一轮请求信号。
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
                        // 每台设备只允许一个后台重连任务。动作线程和轮询线程始终复用同一服务实例。
                        if (Interlocked.CompareExchange(ref b.ReconnectInProgress, 1, 0) != 0)
                        {
                            if (_cycleCount % 100 == 1)
                                Console.WriteLine($"│   {b.Code} 已有重连任务在执行,跳过重复创建");
                            continue;
                        }

                        if (b.F != null)
                            Console.WriteLine($"│   {b.Code} FANUC Worker已退出, 准备重建...");
                        if (TryIp(b.Code, out var rip, out var rp) && !string.IsNullOrWhiteSpace(rip))
                        {
                            Console.WriteLine($"│   后台重连 {b.Code} {rip}:{rp}...");
                            _ = TryConnectBedBg(b, rip, rp);
                        }
                        else
                        {
                            Volatile.Write(ref b.ReconnectInProgress, 0);
                        }
                    }
                    // 同步重连失败计数到 DeviceStatus(合并到重连检查块内, 避免每轮5次字段赋值)
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
                    Console.WriteLine($"│   中转架: T1(105)={(fds != null && !fds.TransferRack1Free ? "有版" : "空")} T2(101)={(fds != null && !fds.TransferRack2Free ? "有版" : "空")} T3(106)={(fds != null && !fds.TransferRack3Free ? "有版" : "空")}");
                    Console.WriteLine($"│   动平衡下料架 M817={(fds != null && fds.M817_HasPlate ? "有版" : "空")}  研磨上料架 M720=按需连接");
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
                var decision = RearSkewDispatchPlanner.Decide(
                    loadScan.Candidates.Count,
                    unloadScan.Candidates.Count,
                    loadScan.PhysicalRackPlateCount,
                    loadScan.MatchableRackPlateCount,
                    unloadScan.UsableBedCount);

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
                    await TryDispatchLoadAsync(ct);
                else if (decision.Action == RearSkewDispatchAction.Unload)
                    await TryDispatchUnloadAsync(fds, ct);

                // ── 刷新后天车连接状态到UI ──
                try { ds.CraneRearConnected = _craneCache.GetOrCreateService(CraneRearNo).IsConnected; } catch { }
                // 展示时间戳不参与调度、锁、握手或设备动作。
                ds.SnapshotAtUtc = DateTime.UtcNow;
                if (_cycleCount % 50 == 1) Console.WriteLine($"└────── [后引擎] 第 {_cycleCount} 轮结束 ──────");
                // DoLoad/DoUnload刚完成→缩短延迟, 快速响应新任务; 否则正常500ms
                int delay = _fastNextCycle ? 50 : _cfg.Grinding.PollIntervalMs;
                _fastNextCycle = false;
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[后引擎] 主循环异常: {ex.Message}\n{ex.StackTrace}"); await Task.Delay(2000, ct); }
        }
        Console.WriteLine("[后引擎] 主循环退出");
    }

    private async Task<bool> EnsureRearCranePositionReadyAsync(string context, CancellationToken ct)
    {
        var crane = _craneCache.GetOrCreateService(CraneRearNo);
        var alarm = await CraneZeroPositionGuard.CheckAsync(crane, CraneRearNo, "1号线后天车", context, ct);
        if (alarm == null) return true;

        _paused = true;
        OnCraneZeroPositionDetected?.Invoke(alarm);
        return false;
    }

    private string StCn(int i) { var b = _beds[i]; return b == null ? "?" : $"{b.St}({(b.Ok ? "连" : "断")})"; }
    private static readonly string[] codes = { "ST108", "ST109", "ST111", "ST110", "ST112" }; // 1~5号斜床显示顺序: ST110为4号, ST112为5号

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

        if (!fd.TransferRack1Free) racks.Add("ST105");
        if (!fd.TransferRack2Free) racks.Add("ST101");
        if (!fd.TransferRack3Free) racks.Add("ST106");
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
            "ST105" => !fd.TransferRack1Free,
            "ST101" => !fd.TransferRack2Free,
            "ST106" => !fd.TransferRack3Free,
            _ => false
        };
    }

    private static int TransferRackPhysicalPriority(string rackCode) => rackCode switch
    {
        "ST105" => 0,
        "ST101" => 1,
        "ST106" => 2,
        _ => 99
    };

    // ═══════════════════════════════════════════════════════════════════
    private List<SkewCtx> GetReadyBedsByPriority()
    {
        var result = new List<SkewCtx>(5);
        // ST108靠近中转架-斜床1碰撞区。ZoneTS忙时临时优先其它斜床,
        // 减少后天车拿着后天车锁等待ZoneTS; 无其它选择时仍允许ST108兜底等待。
        if (_safety.TransferSkew1CollisionLock.CurrentCount == 0)
        {
            for (int i = 1; i < _beds.Length; i++)
                if (IsReadyForLoad(_beds[i]))
                    result.Add(_beds[i]);
            if (IsReadyForLoad(_beds[0]))
            {
                if (result.Count == 0)
                    Console.WriteLine("│   ZoneTS忙且无其它斜床可用, 仍允许ST108等待ZoneTS");
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
                Console.WriteLine("│   [上料候选] 无可用斜床(需同时满足:空闲+已连接+CNC请求数据)");
                for (int i = 0; i < 5; i++)
                {
                    var b = _beds[i];
                    if (b == null) continue;
                    Console.WriteLine($"│     {b.Code}: St={b.St} Ok={b.Ok} Fresh={b.SignalFresh} RqData={b.RqData} RqLoad={b.RqLoad} → {(IsReadyForLoad(b) ? "可用" : "不可用")}");
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
    private async Task<UnloadCandidateScan> BuildUnloadCandidateScanAsync(Line1FrontFlowEngine.Line1DeviceStatus? fds, bool logDetails, CancellationToken ct)
    {
        var scan = new UnloadCandidateScan();
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

            if (!await CanUnloadDestinationAcceptAsync(fds, wp, ct))
                continue;

            scan.Candidates.Add(new UnloadCandidate { Bed = b, Wp = wp });
            if (logDetails) Console.WriteLine($"│   [下料候选] ✓ {b.Code} {wp.IdentityText} L={wp.Length}mm");
        }

        if (logDetails)
            Console.WriteLine($"│   [下料候选] 有效斜床={scan.UsableBedCount} 候选={scan.Candidates.Count} 正在下料={scan.HasActiveUnloading}");
        return scan;
    }

    private async Task<bool> CanUnloadDestinationAcceptAsync(Line1FrontFlowEngine.Line1DeviceStatus? fds, WorkpieceCache wp, CancellationToken ct)
    {
        bool needsBalancing = wp.Length >= 800 || wp.ForceBalancing;
        if (needsBalancing)
        {
            if (fds == null)
            {
                Console.WriteLine("│   目的地不可用: frontDs=null");
                return false;
            }
            if (!fds.M817SnapshotValid)
            {
                Console.WriteLine("│   目的地不可用: M817状态本轮无效(禁止按旧空闲放料)");
                return false;
            }
            if (fds.M817_HasPlate)
            {
                Console.WriteLine("│   目的地不可用: M817=有版(动平衡架未取走)");
                return false;
            }
            return true;
        }

        try
        {
            using var mcts = new CancellationTokenSource(3000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, mcts.Token);
            var mc65 = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, linked.Token);
            var r = await mc65.ReadMAlignedWordAsync(720, 1, linked.Token);
            bool m720CanPlace = (r.IntValues[0] & 1) != 0;
            Console.WriteLine($"│   M720(研磨上料架1号位)={(m720CanPlace ? "可放料" : "不可放料")}");
            if (!m720CanPlace)
            {
                Console.WriteLine("│   目的地不可用: M720=不可放料(研磨上料架1号位未允许)");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│   M720按需连MC65失败: {ex.Message},下轮重试");
            return false;
        }
    }

    private async Task TryDispatchLoadAsync(CancellationToken ct)
    {
        Console.WriteLine($"│ [后调度] 尝试派发上料, 抢后天车锁(当前={_craneRearLock.CurrentCount})...");
        if (!await _craneRearLock.WaitAsync(0, ct))
        {
            Console.WriteLine("│ [后调度] 后天车被占用, 本轮不改派下料");
            return;
        }

        bool triggered = false;
        try
        {
            if (!await EnsureRearCranePositionReadyAsync("斜床上料任务派发前", ct))
                return;

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

    private async Task TryDispatchUnloadAsync(Line1FrontFlowEngine.Line1DeviceStatus? fds, CancellationToken ct)
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
            if (!await EnsureRearCranePositionReadyAsync("斜床下料任务派发前", ct))
                return;

            var scan = await BuildUnloadCandidateScanAsync(fds, logDetails: true, ct);
            if (scan.Candidates.Count == 0)
            {
                Console.WriteLine("│ [后调度] 下料复核无候选, 释放后天车锁");
                return;
            }

            var candidate = scan.Candidates[0];
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

    private static bool IsReadyForLoad(SkewCtx? b) => b != null && b.Idle && b.SignalFresh && b.RqData;

    // ═══════════════════════════════════════════════════════════════════
    private async Task PollBedWithCleanupAsync(SkewCtx b, CancellationTokenSource bedCts,
        CancellationTokenSource linkedCts, Line1RearDeviceStatus ds, int idx, long pollVersion)
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
    private async Task PollBed(SkewCtx b, CancellationToken ct, Line1RearDeviceStatus ds, int idx, long pollVersion)
    {
        string sg = "--", st = "?", fail = "本轮信号未刷新"; bool ok = false;
        bool rqData = false, rqLoad = false, rqUnload = false, clamped = false;
        b.SignalFresh = false;
        try
        {
            if (b.Code == "ST112")
            {
                var snap = b.F!.LatestSnapshot;
                if (snap.Connected)
                {
                    rqData = snap.RqData; rqLoad = snap.RqLoad; clamped = snap.Clamped; rqUnload = snap.RqUnload;
                    sg = $"请求数据={YN(snap.RqData)} 请求上料={YN(snap.RqLoad)} 夹紧={YN(snap.Clamped)} 请求下料={YN(snap.RqUnload)} 张开={YN(snap.Opened)}";
                    if (snap.RqUnload) st = "请求下料"; else if (snap.RqLoad) st = "请求上料"; else if (snap.Clamped) st = "尾座顶紧"; else if (snap.RqData) st = "请求数据"; else st = "空闲";
                    ok = true;
                }
                else
                {
                    fail = snap.Error ?? "FANUC Worker未就绪";
                }
            }
            else
            {
                var s = await b.M!.ReadSignalsAsync(ct);
                rqData = s.rqData; rqLoad = s.rqLoad; clamped = s.clamped; rqUnload = s.rqUnload;
                sg = $"请求数据={YN(s.rqData)} 请求上料={YN(s.rqLoad)} 夹紧={YN(s.clamped)} 请求下料={YN(s.rqUnload)} 张开={YN(s.opened)}";
                if (s.rqUnload) st = "请求下料"; else if (s.rqLoad) st = "请求上料"; else if (s.clamped) st = "尾座顶紧"; else if (s.rqData) st = "请求数据"; else st = "空闲";
                ok = true;
            }
        }
        catch (Exception ex) { fail = ex.Message; Console.WriteLine($"│   {b.Code} 轮询异常: {ex.Message}"); }
        if (Interlocked.Read(ref b.PollVersion) != pollVersion)
        {
            Console.WriteLine($"│   {b.Code} 丢弃过期斜床轮询结果 poll={pollVersion}, current={Interlocked.Read(ref b.PollVersion)}");
            return;
        }

        if (ok)
        {
            b.RqData = rqData; b.RqLoad = rqLoad; b.Clamped = clamped; b.RqUnload = rqUnload;
            if (clamped && b.St == SkewState.Idle) { b.St = SkewState.Machining; Console.WriteLine($"│   ⚡ {b.Code} 同步: CNC已夹紧 → Machining"); }
            else if (clamped && rqUnload && b.St == SkewState.Machining) { b.St = SkewState.WaitingUnload; Console.WriteLine($"│   ⚡ {b.Code} 同步: 请求下料 → WaitingUnload"); }
            else if (!clamped && rqUnload && b.St == SkewState.Idle) { b.St = SkewState.WaitingUnload; Console.WriteLine($"│   ⚡ {b.Code} 同步: 空闲+请求下料 → WaitingUnload"); }
            else if (!clamped && !rqLoad && !rqData && b.St == SkewState.Machining) { b.St = SkewState.Idle; Console.WriteLine($"│   ⚡ {b.Code} 同步: CNC已释放 → Idle"); }
        }
        else
        {
            b.RqData = b.RqLoad = b.RqUnload = b.Clamped = false;
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
    }

    private static void MarkSkewPollFailed(Line1RearDeviceStatus ds, int idx, bool needsManualWp, string reason)
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
        if (bed == null) return $"1号线未找到斜床 {bedCode}";

        return string.Join(Environment.NewLine,
            $"线体: 1号线",
            $"斜床: {bed.Code}",
            $"当前斜床状态: {StateText(bed.St)}",
            $"当前工件: {FormatWorkpiece(bed.Wp)}",
            $"当前动作: {ActiveOperationText(bed)}",
            $"后天车锁: {LockText(_craneRearLock)}",
            $"ZoneMT(打号机-中转架): {LockText(_safety.MarkerTransferCollisionLock)}",
            $"ZoneTS(中转架-ST108): {LockText(_safety.TransferSkew1CollisionLock)}",
            $"下料分流锁: M817={YN(bed.ActiveOperation?.IsHeld("M817") == true)} M720={YN(bed.ActiveOperation?.IsHeld("M720") == true)}",
            $"CNC信号: 请求数据={YN(bed.RqData)} 请求上料={YN(bed.RqLoad)} 夹紧={YN(bed.Clamped)} 请求下料={YN(bed.RqUnload)}",
            $"信号新鲜: {(bed.SignalFresh ? "是" : "否")}");
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
        if (bed == null) return $"1号线未找到斜床 {bedCode}";

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
                string timeoutMessage = $"[Line1Rear] [斜床应急作废] {bed.Code} 旧动作6秒内未退出; " +
                    $"{string.Join("; ", logs)}; 后端保持暂停, 未释放软件锁, 未清软件缓存/设备寄存器, 请稍后重试";
                Console.WriteLine(timeoutMessage);
                return timeoutMessage;
            }
            logs.Add("旧动作=已确认退出");

            // 必须先确认旧动作退出，再释放finally仍未能释放的残留锁。
            // 先释放会让其它引擎在旧SDK调用尚未返回时进入同一物理区域。
            logs.Add(ReleaseLeaseLock(operation, "RearCrane", _craneRearLock, "后天车锁"));
            logs.Add(ReleaseLeaseLock(operation, "ZoneMT", _safety.MarkerTransferCollisionLock, "ZoneMT(打号机-中转架)"));
            logs.Add(ReleaseLeaseLock(operation, "ZoneTS", _safety.TransferSkew1CollisionLock, "ZoneTS(中转架-ST108)"));
            logs.Add(ReleaseLeaseLock(operation, "M720", _lockM720, "M720分流锁"));
            logs.Add(ReleaseLeaseLock(operation, "M817", _lockM817, "M817分流锁"));
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
                Console.WriteLine($"[Line1Rear] [斜床应急作废] {bed.Code} {failure.Replace(Environment.NewLine, " ")}");
                return failure;
            }
        }

        lock (_wpLock)
        {
            bed.Wp = null;
            bed.St = SkewState.Idle;
            bed.RqData = bed.RqLoad = bed.RqUnload = bed.Clamped = false;
            bed.SignalFresh = false;
            bed.CompletionExported = false;
            bed.WpRecoveryNeeded = false;
            bed.X11UnloadMissWarningShown = false;
            Interlocked.Increment(ref bed.PollVersion);
        }

        // 仅应急软件状态已成功清空后才清展示任务；超时/失败路径会在此前return，保留旧身份供人工确认。
        ClearRearCraneTask();
        MarkSkewManualCleared(bed);
        logs.Add(deviceClearLog);
        if (resumeAfterClear && IsRunning) _paused = false;
        _fastNextCycle = true;

        string message = $"[Line1Rear] [斜床应急作废] {DateTime.Now:yyyy-MM-dd HH:mm:ss} 操作员=本机 斜床={bed.Code} 原状态={StateText(oldState)} 工件={wpText ?? "无"}; {string.Join("; ", logs)}; 后端={(IsPaused ? "保持暂停" : "已恢复")}";
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
        if (bed.M != null)
        {
            await bed.M.ClearEmergencyRegistersAsync(ct);
            return "设备寄存器=Modbus 10300~10305/10370~10374 已清零";
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
        if (bed.M != null)
        {
            await bed.M.ClearUpperComputerCommandOutputsAsync(ct);
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
            : $"{bed.ActiveOperation.Name} v{bed.ActiveOperation.Version}(后天车锁={YN(bed.ActiveOperation.IsHeld("RearCrane"))} ZoneMT={YN(bed.ActiveOperation.IsHeld("ZoneMT"))} ZoneTS={YN(bed.ActiveOperation.IsHeld("ZoneTS"))} 分流M817={YN(bed.ActiveOperation.IsHeld("M817"))} 分流M720={YN(bed.ActiveOperation.IsHeld("M720"))} 超时待人工确认={YN(bed.ActiveOperation.RequiresManualSafetyRecovery)})";

    private static EmergencyActionLease BeginSkewOperation(SkewCtx bed, string name, CancellationToken parent, bool holdsTransferRack)
    {
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
    //  1号线后天车 DoLoad  取料放斜床
    // ═══════════════════════════════════════════════════════════════════
    private async Task DoLoad(SkewCtx bed, string rs, CancellationToken ct)
    {
        string actionId = OperationalEventContextFactory.NewActionId("L1-REAR-LOAD");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        bool transferLocked = false; // 新Zone模型下不再使用_transferRackLock作为中转架业务互斥
        WorkpieceCache wp;
        long rackArrivalSeq;
        // 预约来源位不是取走：在X11确认前，缓存仍属于中转架，异常不会丢失工件身份。
        if (!_rackLedger.TryReserve(rs, actionId, out wp, out rackArrivalSeq))
        {
            _craneRearLock.Release();
            bed.St = SkewState.Idle;
            Console.WriteLine($"[上料] ❌ 失败: 中转架{rs}没有可用工件缓存（为空或已被其它动作预约）");
            return;
        }
        // 预约成功后才拥有真实工件身份；后续异常一律以这个动作账本的事实为准。
        var action = new FlowActionContext(actionId, "1号线后天车上料", "2号天车", wp.IdentityText, rs, bed.Code, rs);
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
        try
        {
            Console.WriteLine($"[上料] {bed.Code} 已选中，天车取板前清理上位机命令输出...");
            await ClearSkewLoadCommandOutputsAsync(bed, ct);
        }
        catch (Exception ex)
        {
            _rackLedger.ReleaseReservation(rs, actionId);
            _craneRearLock.Release();
            bed.St = SkewState.Idle;
            Console.WriteLine($"[上料] ❌ {bed.Code} 上料前命令清理失败，未取中转架{rs}工件，缓存已恢复：{ex.Message}");
            return;
        }
        wp.ReportStage($"1号线 后端上斜床 {bed.Code}");
        bool mag = false;
        bool holdingWorkpiece = false; // 只有X11确认后才算工件已经离开中转架并由后天车持有
        // 物理命令一旦开始，后续通信异常的结果必须视为未知，不能自动重试或自动退避。
        bool physicalCommandStarted = false;
        bool requiresManualConfirmation = false;
        bool sharedLocked = false; // ST108上料取完中转架后继续持有ZoneTS, 直到斜床1交互后退避释放
        var loadState = new LoadHandshakeState();
        string? pendingSafetyAlarm = null; // 异常正文先保留，等finally完成退避和锁释放后只弹一次完整信息。
        CraneService? cr = null;
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(actionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "1号线", "后端引擎", "后天车", CraneRearNo.ToString(), rs,
            $"{rs}取料并放入{bed.Code}", actionId, $"{actionId}:rear-load", wp,
            rs, bed.Code, "1号线后天车", rs, "中转架工件缓存已取出",
            "ZoneMT/ZoneTS/后天车锁"));
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, $"{bed.Code}CNC上料完成");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{bed.Code}上料完整交接");
        using var operation = BeginSkewOperation(bed, "上料", ct, transferLocked);
        ct = operation.Token;
        Console.WriteLine($"\n┌── [上料] 开始 ──────────────────────────");
        Console.WriteLine($"│ 源={rs} → 目标={bed.Code}  {wp.IdentityText}  直径={wp.Diameter}mm  版长={wp.Length}mm  堵孔={wp.BoreType}mm");
        //此时已经获得工件的信息
        try
        {
            // 中转架位于打号机和斜床1中间，进入中转架取料必须同时持有ZoneMT+ZoneTS。
            // 取料完成且Z轴升到安全高度后：
            //   - 普通斜床：释放ZoneMT+ZoneTS；
            //   - ST108：释放ZoneMT，继续持有ZoneTS到斜床1交互和X+1000退避结束。
            Console.WriteLine("│ [上料] 获取ZoneMT(打号机↔中转架)...");
            await operation.AcquireAsync("ZoneMT", _safety.MarkerTransferCollisionLock, ct);
            action.RegisterLock("ZoneMT");
            Console.WriteLine("│ ZoneMT已获取 ✓");
            Console.WriteLine("│ [上料] 获取ZoneTS(中转架↔ST108)...");
            await operation.AcquireAsync("ZoneTS", _safety.TransferSkew1CollisionLock, ct);
            action.RegisterLock("ZoneTS");
            sharedLocked = bed.Code == "ST108";
            Console.WriteLine("│ ZoneTS已获取 ✓，当前可进入中转架取料");

            if (!RackHasPhysicalPlateSnapshot(rs))
                throw new InvalidOperationException($"中转架{rs}物理信号无板, 禁止后天车取料; 软件缓存将恢复等待人工/下轮确认");
            cr = _craneCache.GetOrCreateService(CraneRearNo);
            if (!cr.IsConnected)
            {
                Console.WriteLine("│ 后天车未连接,连接中...");
                await cr.ConnectAsync(ct);
            }

            int sz = _cfg.Grinding.SafeZHeight;
            //获得中转架坐标
            if (!TryCoords(rs, out int rx, out int ry, out int rz)) throw new Exception($"缺少中转架{rs}坐标");
            int pz = Pz(rz, wp.Diameter);
            Console.WriteLine($"│ [取料] 中转架{rs} 坐标=({rx},{ry},{rz}) 取料Z={pz + _oz} 安全高度={sz}");
            //设置天车速度 (缓存避免4次字典查找)
            var crSpd = _cfg.GetCraneSpeed(CraneRearNo);
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置后天车绝对速度");
            physicalCommandStarted = true;
            action.MarkCommandSent();
            await cr.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel,
                crSpd.Y.Speed, crSpd.Y.Accel, crSpd.Y.Decel,
                crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);
            action.Confirm();
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
                    "取料前X11读取失败，当前传感器值无效；保留最后一次成功值（如有）", true);
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
                cr, _cfg, CraneRearNo, rs, $"1号线后天车-{rs}取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "1号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: rs,
                    actionStage: $"{rs}中转架取料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation(rs, "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "已选斜床目标"),
                    owner: "1号线后天车", targetZ: EvidenceValue<int>.Confirmed(pz + _oz, "已有中转架取料Z公式与偏移"),
                    physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, true, "Z回原点命令已返回", rs)),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            Console.WriteLine($"│ [取料] step3: Z下降到{pz + _oz}");
            action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, 0), "来源位XY微调完成");
            action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(rx + _ox, ry + _oy, pz + _oz), "Z下降取料");
            operationalTracker.BeginZDown(pz + _oz);
            try
            {
                action.MarkCommandSent();
                await cr.MoveAbsoluteAsync(-1, -1, pz + _oz, ct: ct);
                action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, pz + _oz), "来源位取料Z到位");
                operationalTracker.CompleteZDown();
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("Z下降触发下压保护，恢复后实际位置需由后续安全高度命令确认");
                await cr.RecoverFromPressureStopAsync(ct);
                throw;
            }

            // step4: Z↓到位→充磁→等3s→查X11(63497)→有版就Z↑, 没版就退磁→Z↓5mm→充磁→等3s→再查, 最多2次
            Console.WriteLine("│ [取料] step4: 充磁→等3s→X11检测");
            int currentZ = pz + _oz;
            operationalTracker.BeginX11Stage("充磁后持件确认");
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("│ [取料] 充磁");
                    action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "来源位首次充磁");
                    action.MarkCommandSent();
                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        await cr.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{rs}首次充磁" }, operationalTracker, ex,
                            "充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                        throw;
                    }
                    mag = true;
                    action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "首次充磁调用成功返回");
                    action.RecordFeedback(FlowActionPosition.Unknown, null, true, "首次充磁调用成功返回");
                }
                else
                {
                    Console.WriteLine($"│ [取料] 退磁→Z↓到{currentZ}→充磁");
                    action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11重试前退磁");
                    action.MarkCommandSent();
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = $"{rs}X11重试前退磁" }, operationalTracker, ex,
                            "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                        throw;
                    }
                    mag = false;
                    action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11重试前退磁成功返回");
                    action.RecordFeedback(FlowActionPosition.Unknown, null, false, "X11重试前退磁成功返回");
                    try
                    {
                        operationalTracker.BeginZDown(currentZ);
                        action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11重试下探Z");
                        action.MarkCommandSent();
                        await cr.MoveAbsoluteAsync(-1, -1, currentZ, ct: ct);
                        action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11重试下探Z到位");
                    }
                    catch (PressureStopException)
                    {
                        await cr.RecoverFromPressureStopAsync(ct);
                        throw;
                    }

                    operationalTracker.BeginMagnetOn();
                    action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11重试后充磁");
                    action.MarkCommandSent();
                    try
                    {
                        await cr.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{rs}下探后再次充磁" }, operationalTracker, ex,
                            "重试充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                        throw;
                    }
                    mag = true;
                    action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11重试后充磁调用成功返回");
                    action.RecordFeedback(FlowActionPosition.Unknown, null, true, "X11重试后充磁调用成功返回");
                }

                Console.WriteLine("│ [取料] 等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                operationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await cr.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"{rs}充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后一次成功值（如有）", true);
                    throw;
                }
                physicalTracker?.TryObserveX11(x11);
                action.BeginStep(FlowActionStep.ConfirmPickup, new FlowActionPosition(rx + _ox, ry + _oy, currentZ), $"充磁后X11第{retry + 1}次读取");
                action.RecordFeedback(new FlowActionPosition(rx + _ox, ry + _oy, currentZ), x11, mag, $"X11={x11}");
                Console.WriteLine($"│ [取料] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    holdingWorkpiece = true;
                    // X11是来源所有权转移的唯一证据；只有此刻才允许从中转架账本删除。
                    if (!_rackLedger.CommitPickup(rs, actionId, out _, out _))
                        throw new InvalidOperationException($"中转架{rs}工件预约提交失败，不能确认软件身份已随天车转移");
                    action.SetOwnership(FlowWorkpieceOwnership.OnCarrier);
                    action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, currentZ), "X11=1确认工件已由后天车持有");
                    action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece);
                    Console.WriteLine($"│ [取料] ✓ X11=1 吸到! Z↑到安全高度{sz}");
                    //x11=1 z回安全位置
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
                    action.Confirm(new FlowActionPosition(rx + _ox, ry + _oy, sz), "取料后Z已升安全高度");
                    physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "中转架取料后Z升安全命令成功返回");
                    break;
                }

                if (retry > 1)
                {
                    loadState.X11PickupFailed = true;
                    // 最终结构化事件延后到finally：一次记录退磁、Z、退避、锁和暂停的最终结果。
                    throw new Exception("取料失败: 3次充磁后X11仍=0");
                }
                currentZ += 5;
                loadState.ZMayBeDown = true; // Z已降到低位，即使后续异常也不能横移X
                Console.WriteLine($"│ [取料] ⚠ X11=0 未吸到, 准备下探5mm重试");
            }
            //天车这时候在安全位置
            Console.WriteLine("│ [上料] 工件已取走且Z已安全, 按现场策略释放中转架侧Zone锁");
            if (!TryCoords(bed.Code, out int bx, out int by, out int bz)) throw new Exception($"缺少斜床{bed.Code}坐标");
            Console.WriteLine($"│ [移动] 天车到斜床{bed.Code} XY=({bx + _ox},{by + _oy}) Z保持{sz}");
            if (bed.Code == "ST108")
            {
                operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                Console.WriteLine("│ [上料] ST108目标: Z已安全, 释放ZoneMT, 继续持有ZoneTS到斜床1退避完成");
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
                cr, _cfg, CraneRearNo, bed.Code, $"1号线后天车-{bed.Code}上料放斜床前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "1号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: bed.Code,
                    actionStage: $"{bed.Code}上料放斜床前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation(rs, "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "已选斜床目标"),
                    owner: "1号线后天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后的DoHandshake中计算bz-Round(d/2)+_oz"),
                    physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(true, holdingWorkpiece, physicalTracker, "中转架取料X11=1且Z已升安全", "天车/斜床上方")),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床目标XY微调完成");

            // 到这里才绑定斜床工件: X11已确认吸住、Z已升安全、后天车已到目标斜床上方。
            // 在此之前异常按“工件仍在中转架/后天车”处理, 避免Idle斜床残留旧Wp。
            bed.Wp = wp;
            SetRearCraneTask(wp, "斜床上料握手中", rs, bed.Code);
            bed.CompletionExported = false; // 新工件开始加工前清除job2导出标志, 完工时只追加一次。
            Console.WriteLine("│ [握手] 天车到位,开始与CNC通讯");
            //开始上料握手流程
            await DoHandshake(cr, bed, wp, sz, bx, by, bz, loadState, action, operationalTracker, operationalSite, ct);
            Console.WriteLine("│ [上料] Z回原点");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "上料完成后Z回原点");
            action.MarkCommandSent();
            await cr.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
            action.Confirm(new FlowActionPosition(null, null, 0), "上料完成后Z已回原点");
            bed.St = SkewState.Machining;
            wp.ReportStage($"1号线 斜床加工中 {bed.Code}");
            ClearRearCraneTask();
            action.Complete("斜床上料、CNC通知和天车回位均已完成");
            Console.WriteLine($"│ [上料] ✓ 完成 {wp.IdentityText} {bed.Code}→加工中");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine($"│ [上料] ⚠ {bed.Code} 当前动作已被应急取消, 不再继续写CNC/天车动作");
        }
        catch (CraneMotionTimeoutException ex)
        {
            // 运动超时意味着位置未知：暂停且禁止自动恢复动作；软件锁在finally释放，
            // 工件身份保留在账本/斜床任务牌中，等待人工确认真实现场状态。
            _paused = true;
            requiresManualConfirmation = true;
            action.PauseForManualResolution(ex.Message);
            _rackLedger.HoldForManualResolution(rs, actionId, ex.Message);
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = $"{action.Step}：天车运动超时" }, operationalTracker, ex,
                "后天车运动超时，动作账本已暂停并保留来源预约，等待人工确认。", true,
                holdingWorkpiece: holdingWorkpiece, actionSnapshot: action.Snapshot());
            bed.St = SkewState.Loading;
            bed.Wp = wp;
            SetRearCraneTask(wp, "运动超时，位置未知", rs, bed.Code, true,
                $"{ex.Stage}超时；未到位轴={string.Join("/", ex.UnreachedAxes)}；等待人工确认");
            pendingSafetyAlarm = $"1号线后天车给{bed.Code}上料时{ex.Stage}运动超时。" +
                $"目标=({ex.XTarget},{ex.YTarget},{ex.ZTarget})，最后坐标={ex.LastKnownStatus?.XPos}/{ex.LastKnownStatus?.YPos}/{ex.LastKnownStatus?.ZPos}，" +
                $"未到位轴={string.Join("/", ex.UnreachedAxes)}，D4518停止={(ex.EmergencyStopCommandSucceeded ? "已发送" : "发送失败")}。" +
                "引擎已暂停；本动作软件锁将在收尾后释放，工件状态保留待人工确认，禁止自动重试。";
            Console.WriteLine($"│ [上料] ⚠ {pendingSafetyAlarm}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│ [上料] ❌ 异常: {wp.IdentityText} {ex.Message}");
            if (loadState.X11PickupFailed) loadState.X11FailureException = ex;
            // 已开始物理命令后的普通异常也可能是命令响应未知，不能落入旧的自动恢复分支。
            if (physicalCommandStarted && !loadState.X11PickupFailed)
            {
                _paused = true;
                requiresManualConfirmation = true;
                action.MarkCommandResponseUnknown(ex.Message);
                action.PauseForManualResolution(ex.Message);
                _rackLedger.HoldForManualResolution(rs, actionId, ex.Message);
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                    operationalSite with { ActionStage = $"{action.Step}：命令响应未知" }, operationalTracker, ex,
                    "物理命令已开始且响应未知，动作账本已暂停并保留来源预约，等待人工确认。", true,
                    holdingWorkpiece: holdingWorkpiece, actionSnapshot: action.Snapshot());
                bed.St = SkewState.Loading;
                bed.Wp = wp;
                SetRearCraneTask(wp, holdingWorkpiece ? "持件状态待确认" : "来源位/动作结果待确认", rs, bed.Code, true,
                    "物理命令已开始，异常响应未知；已暂停等待人工确认");
                pendingSafetyAlarm = $"1号线后天车给{bed.Code}上料时发生命令响应未知异常。" +
                    "引擎已暂停，未执行自动退避或缓存恢复；软件锁将在收尾后释放，工件状态等待人工确认。异常：" + ex.Message;
            }
            else if (loadState.PlacedOnBed && !loadState.LoadDoneNotified)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = bed.Code, ActionStage = $"{bed.Code}已退磁放料但CNC上料完成未确认" },
                    operationalTracker, ex,
                    "退磁成功返回并推定工件已放入斜床，但CNC上料完成通知未确认", true,
                    holdingWorkpiece: false, placed: true, cacheNotified: null,
                    downstreamNotified: null, handoffClosed: false);
            }
            if (!requiresManualConfirmation && loadState.PlacedOnBed)
            {
                _paused = true;
                bed.St = loadState.LoadDoneNotified ? SkewState.Machining : SkewState.Loading;
                Console.WriteLine(loadState.LoadDoneNotified
                    ? "│ [上料] ⚠ 工件已放入斜床且CNC已收到上料完成, 但后续动作异常; 引擎已暂停, 请确认后天车安全位置"
                    : "│ [上料] ⚠ 工件已物理放到斜床但CNC上料完成未确认; 引擎已暂停, 禁止把斜床改Idle");
                pendingSafetyAlarm = $"1号线后天车给{bed.Code}上料时发生异常。工件{wp.IdentityText}已放入斜床，" +
                    (loadState.LoadDoneNotified ? "CNC已收到上料完成" : "CNC上料完成尚未确认") +
                    $"。引擎已暂停，请确认后天车、斜床和工件状态。异常：{ex.Message}";
            }
            else if (!requiresManualConfirmation && holdingWorkpiece)
            {
                _paused = true;
                bed.St = SkewState.Loading;
                SetRearCraneTask(wp, "已持件，尚未放入斜床", rs, bed.Code, true, "X11已确认持件，请人工确认天车和工件位置");
                Console.WriteLine("│ [上料] ⚠ X11已确认工件在后天车上但未放到斜床; 引擎已暂停, 需人工确认后天车和工件位置");
                pendingSafetyAlarm = $"1号线后天车给{bed.Code}上料时发生异常，X11已确认工件{wp.IdentityText}在天车上但尚未放入斜床。引擎已暂停，请人工确认位置。异常：{ex.Message}";
            }
            else if (!requiresManualConfirmation)
            {
                if (mag && cr != null)
                {
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                        mag = false;
                        if (loadState.X11PickupFailed)
                        {
                            loadState.X11MagnetOffState = RecoveryStepState.Succeeded;
                            loadState.X11MagnetOffDetail = "退磁方法成功返回；不等于现场已确认释放";
                            loadState.X11RecoverySummary = "退磁成功";
                        }
                    }
                    catch (Exception offEx)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = $"{rs}异常收尾尽量退磁" }, operationalTracker, offEx,
                            "原业务在异常收尾中吞掉退磁异常；实际退磁结果未知", true,
                            holdingWorkpiece: holdingWorkpiece);
                        if (loadState.X11PickupFailed)
                        {
                            loadState.X11MagnetOffState = RecoveryStepState.Failed;
                            loadState.X11MagnetOffDetail = $"退磁方法异常，实际退磁结果未知({offEx.Message})";
                            loadState.X11RecoverySummary = $"退磁失败，结果未知({offEx.Message})";
                        }
                        Console.WriteLine($"│ [上料] ⚠ X11未确认有版,退磁失败: {offEx.Message}");
                    }
                }
                // X11未确认吸住时Z还在取料低位。退磁后先将Z拉回原点，
                // 防止finally做X+1000退避时低位横移。
                if (cr != null)
                {
                    try
                    {
                        operationalTracker.BeginSafeZReturn(0, "X11三次失败异常收尾开始Z回原点");
                        await cr.MoveAbsoluteAsync(-1, -1, 0, ct: CancellationToken.None);
                        operationalTracker.ConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "X11三次失败异常收尾Z回原点命令成功返回");
                        physicalTracker?.TryConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "X11三次失败异常收尾Z回原点命令成功返回");
                        loadState.ZMayBeDown = false; // Z已安全，finally可以退避
                        if (loadState.X11PickupFailed)
                        {
                            loadState.X11SafeZState = RecoveryStepState.Succeeded;
                            loadState.X11SafeZDetail = "Z回原点命令成功返回";
                            loadState.X11RecoverySummary += "；Z已回原点";
                        }
                        Console.WriteLine("│ [上料] X11=0恢复: Z已回原点");
                    }
                    catch (Exception zEx)
                    {
                        operationalTracker.MarkZUnknown($"X11三次失败异常收尾Z回原点失败({zEx.Message})");
                        physicalTracker?.TryMarkZUnknown($"X11三次失败异常收尾Z回原点失败({zEx.Message})");
                        loadState.ZMayBeDown = true; // Z回升失败，禁止退避
                        if (loadState.X11PickupFailed)
                        {
                            loadState.X11SafeZState = RecoveryStepState.Failed;
                            loadState.X11SafeZDetail = $"Z回原点失败({zEx.Message})";
                            loadState.X11RecoverySummary += $"；Z回原点失败({zEx.Message})";
                        }
                        Console.WriteLine($"│ [上料] X11=0恢复: Z→0失败({zEx.Message}), 禁止X退避, 已直接释放锁");
                    }
                }
                bed.St = SkewState.Idle;
                ClearRearCraneTask();
                bed.Wp = null;
                // X11未确认吸住时来源缓存从未删除；只解除预约，不能通过“恢复”伪造交接。
                _rackLedger.HoldForManualResolution(rs, actionId, "X11连续三次为0，已完成局部安全恢复，等待人工确认来源位");
                if (loadState.X11PickupFailed) loadState.X11CacheRestored = true;
                Console.WriteLine(loadState.X11PickupFailed
                    ? $"│ [上料] X11连续三次为0 → 来源缓存[{rs}]未删除，预约已解除(原顺序{rackArrivalSeq})，等待人工确认后恢复运行"
                    : $"│ [上料] X11未确认工件离开中转架 → 来源缓存[{rs}]未删除，预约已解除(原顺序{rackArrivalSeq})，下轮可重试");
            }

            try
            {
                if (transferLocked)
                {
                    // 兼容旧变量；新Zone模型下不再持有_transferRackLock。
                    transferLocked = false;
                }
            } catch (SemaphoreFullException) { }
        }
        finally
        {
            if (operation.RequiresManualSafetyRecovery)
            {
                // 超时已经暂停引擎；软件锁不承担长期现场安全隔离职责，动作退出即释放。
                bool rearReleased = operation.TryRelease("RearCrane", _craneRearLock);
                bool mtReleased = operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                bool tsReleased = operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                bool m817Released = operation.TryRelease("M817", _lockM817);
                bool m720Released = operation.TryRelease("M720", _lockM720);
                string alarm = (pendingSafetyAlarm ?? operation.ManualSafetyHoldReason ?? "后天车运动超时") +
                    $" 本动作软件锁已释放(后天车={YN(rearReleased)} ZoneMT={YN(mtReleased)} ZoneTS={YN(tsReleased)} M817={YN(m817Released)} M720={YN(m720Released)})。" +
                    "请先现场确认，再执行斜床应急恢复；恢复前不得直接启动引擎。";
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
            string x11RetreatDetail = bed.Code == "ST108"
                ? "ZoneTS退避尚未执行"
                : $"目标{bed.Code}不需要ZoneTS侧X退避";
            bool releasedSharedInFinally = false;
            string sharedCleanupText = string.Empty;
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
                        retreatSucceeded = await TryRetreatSharedAreaBeforeReleaseAsync(cr, "ST108", "上料", ct);
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

                    if (!retreatSucceeded && pendingSafetyAlarm == null)
                    {
                        _paused = true;
                        pendingSafetyAlarm = $"1号线后天车在{bed.Code}上料异常收尾时未完成ZoneTS侧X退避。引擎已暂停，请立即人工确认天车位置";
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
                string x11Alarm = $"1号线后天车从中转架{rs}取料时，X11连续三次为0。" +
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
                pendingSafetyAlarm += $"。异常收尾：{lockAndRetreat}。禁止直接点击启动；请先人工处理现场，确认安全后执行斜床应急恢复。";
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
            
            _fastNextCycle = true; // 刚完成上料, 通知主循环快速检查下料任务
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
        bool isF = bed.Code == "ST112";
        int lz = bz - (int)Math.Round(wp.Diameter / 2.0);
        //公式计算yt  天车下降到斜床中心位置 需要再向左走一段距离
        int yT = _cfg.SkewBed.ComputeYOffset(bed.Code, wp.Length, wp.BoreType);
        Console.WriteLine($"│ [握手] 斜床Z={bz} 装料Z={lz}(Z-半径) Y目标={yT}");
        Console.WriteLine("│ [握手] ① 等待CNC请求数据(F1=1)...");
        action.BeginStep(FlowActionStep.NotifyDownstream, FlowActionPosition.Unknown, "等待CNC请求数据");
        await W(async () => isF ? await bed.F!.IsRequestDataAsync(ct) : await bed.M!.IsRequestDataAsync(ct), "请求数据", ct);
        double skewD = wp.Diameter + _cfg.GetDiameterOffset(bed.Code);
        if (skewD != wp.Diameter)
            Console.WriteLine($"│ [握手] ② 写入加工参数 {wp.IdentityText} 直径={skewD}(原{wp.Diameter} + 偏移{_cfg.GetDiameterOffset(bed.Code)}) 版长={wp.Length} 堵孔={wp.BoreType}");
        else
            Console.WriteLine($"│ [握手] ② 写入加工参数 {wp.IdentityText} 直径={wp.Diameter} 版长={wp.Length} 堵孔={wp.BoreType}");
        // 斜床加工模式: 正常任务工艺统一映射为 1/2/3。
        // 设备级对刀模式只按当前斜床站号覆盖, 不修改任务里的原始工艺, 便于追溯。
        int skewMode = ResolveSkewMode(wp.SkewBedProcess);
        if (_cfg.SkewBed.IsToolSettingEnabled(bed.Code))
        {
            skewMode = 6;
            Console.WriteLine($"│ [握手] ⚠ {bed.Code}已启用对刀模式, 覆盖任务工艺[{wp.SkewBedProcess}] → 加工模式=6");
        }

        action.MarkCommandSent();
        if (!isF)//今洲斜床 ModbusTCP
            await bed.M!.SendMachiningParamsAsync(wp.Length, wp.BoreType, skewD, skewMode, ct);
        else//法兰克系统 FANUC FOCAS
            await bed.F!.SendMachiningParamsAsync(wp.Length, skewD, wp.BoreType, skewMode, ct);
        action.Confirm(FlowActionPosition.Unknown, "CNC加工参数写入成功返回");
        Console.WriteLine("│ [握手] ③ 等待CNC请求上料(F2=1)...");
        await W(async () => isF ? await bed.F!.IsRequestLoadAsync(ct) : await bed.M!.IsRequestLoadAsync(ct), "请求上料", ct);
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
        if (isF) bed.F!.SafeSetMacro(1101, 0); // 清#1101
        else { 
            //写10374=0
            await bed.M!.ClearTailstockStopAsync(ct);
            //写10373=0
            await bed.M!.ClearTailstockOpenAsync(ct); 
        } // 清10373=0(防锦州默认张开=1冲突)
        //今洲斜床写10371=1
        if (!isF) await bed.M!.ClampTailstockAsync(ct); 
        //沈阳斜床写1102=1
        else await bed.F!.SetCraneLoadInPlaceAsync(true, ct);
        await W(async () => isF ? await bed.F!.IsTailstockClampedAsync(ct) : await bed.M!.IsTailstockClampedAsync(ct), "夹紧完成", ct);
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
                holdingWorkpiece: true, placed: null, cacheNotified: null, downstreamNotified: null, handoffClosed: false);
            throw;
        }
        loadState.PlacedOnBed = true; // 退磁成功后工件已经离开后天车, 后续异常必须人工确认斜床状态
        action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "工件已退磁放入斜床，等待CNC上料完成通知");
        action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(bx + _ox, yAbs, sz), "放料后Z升安全高度");
        action.MarkCommandSent();
        await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
        action.Confirm(new FlowActionPosition(bx + _ox, yAbs, sz), "放料后Z已升安全高度");
        operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "斜床上料退磁后Z升安全命令成功返回");
        // 只有Z回安全运动成功返回，异常finally才允许ST108执行X+1000退避。
        loadState.ZMayBeDown = false;
        loadState.ZSafeAfterPlace = true;
        Console.WriteLine("│ [握手] ⑧ 清上一步信号 → 写天车上料完成,CNC启动加工");
        if (isF) bed.F!.SafeSetMacro(1102, 0); // 清#1102
        else await bed.M!.ClearTailstockClampAsync(ct); // 清10371=0
        operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, $"{bed.Code}CNC上料完成", "CNC上料完成通知调用已开始，结果未知");
        action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(bx + _ox, yAbs, sz), "CNC上料完成通知");
        action.MarkCommandSent();
        if (!isF) await bed.M!.RemoteStartAsync(ct); else await bed.F!.SetCraneLoadDoneAsync(ct);
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
    private async Task DoUnload(SkewCtx bed, CancellationToken ct)
    {
        string actionId = OperationalEventContextFactory.NewActionId("L1-REAR-UNLOAD");
        WorkpieceCache initialWorkpiece = bed.Wp ?? throw new InvalidOperationException($"{bed.Code}下料缺少工件账本");
        var action = new FlowActionContext(actionId, "1号线后天车下料", "2号天车",
            initialWorkpiece.IdentityText, bed.Code, "ST019/ST010分流目标", bed.Code);
        action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "斜床下料前检查");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        bool magnetOn = false;
        bool holdingWorkpiece = false;       // X11确认有版后才算工件已经在后天车上, 不能只看是否发过充磁命令
        bool placedToDestination = false;    // 退磁放到目标位 + Z升安全 + 必要通知完成后, 才允许斜床Idle并清Wp
        bool operationalPlaced = false;      // 仅供旁路证据，不参与任何业务判断
        bool operationalCacheNotified = false;
        bool operationalDownstreamNotified = false;
        bool operationalRequiresDownstreamNotification = false;
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
            "1号线", "后端引擎", "后天车", CraneRearNo.ToString(), bed.Code,
            $"{bed.Code}下料并送分流目标", actionId, $"{actionId}:rear-unload", bed.Wp,
            bed.Code, "ST019/ST010分流目标", "1号线后天车", bed.Code,
            "斜床加工完成等待后天车下料", "ZoneTS/后天车锁/M817/M720"));
        using var operation = BeginSkewOperation(bed, "下料", ct, holdsTransferRack: false);
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

            // 仅ST108紧邻中转架-斜床1碰撞区, 下料取料时只需持有ZoneTS。
            if (bed.Code == "ST108")
            {
                Console.WriteLine("│ [下料] 获取ZoneTS(ST108↔中转架)...");
                await operation.AcquireAsync("ZoneTS", _safety.TransferSkew1CollisionLock, ct);
                action.RegisterLock("ZoneTS");
                sharedLocked = true;
                sharedWasAcquired = true;
                Console.WriteLine("│ [下料] ZoneTS已获取(ST108) ✓");
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
            bool isF = bed.Code == "ST112";
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
            //进行微调
            action.BeginStep(FlowActionStep.FineTuneSource, new FlowActionPosition(bx + _ox, yPick, 0), "斜床取料位XY微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                cr, _cfg, CraneRearNo, bed.Code, $"1号线后天车-{bed.Code}下料取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "1号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: bed.Code,
                    actionStage: $"{bed.Code}下料取料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "本物理周期来源"),
                    target: EvidenceValue<string>.Unknown("ST019/ST010分流尚未确定"),
                    owner: "1号线后天车", targetZ: EvidenceValue<int>.Confirmed(lz + _oz, "已有斜床取料Z公式与偏移"),
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
            operationalTracker.BeginZDown(zDown);
            action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(bx + _ox, yPick, zDown), "Z下降到斜床取料高度");
            try
            {
                action.MarkCommandSent();
                await cr.MoveAbsoluteAsync(-1, -1, zDown, ct: ct);
                action.Confirm(new FlowActionPosition(bx + _ox, yPick, zDown), "斜床取料Z已到位");
                operationalTracker.CompleteZDown();
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("Z下降触发下压保护，恢复后位置等待后续安全高度确认");
                await cr.RecoverFromPressureStopAsync(ct);
                throw;
            }

            // Z↓到位后最多读取3次X11；后两次各下探5mm重试。
            Console.WriteLine("│ [下料] 充磁→等3s→X11检测");
            int unlz = zDown;
            operationalTracker.BeginX11Stage("充磁后持件确认");
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("│ [下料] 充磁");
                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(bx + _ox, yPick, unlz), $"{bed.Code}首次充磁取料");
                        action.MarkCommandSent();
                        await cr.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                        action.Confirm(new FlowActionPosition(bx + _ox, yPick, unlz), "斜床首次充磁调用成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{bed.Code}首次充磁取料" }, operationalTracker, ex,
                            "充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    magnetOn = true;
                }
                //第一次吸版
                else
                {
                    Console.WriteLine($"│ [下料] 退磁→Z↓到{unlz}→充磁");
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(bx + _ox, yPick, unlz), $"{bed.Code}X11重试前退磁");
                        action.MarkCommandSent();
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                        action.Confirm(new FlowActionPosition(bx + _ox, yPick, unlz), "X11重试前退磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = $"{bed.Code}X11重试前退磁" }, operationalTracker, ex,
                            "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                        throw;
                    }
                    magnetOn = false;
                    try
                    {
                        operationalTracker.BeginZDown(unlz);
                        action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(bx + _ox, yPick, unlz), $"{bed.Code}X11重试下探Z");
                        action.MarkCommandSent();
                        await cr.MoveAbsoluteAsync(-1, -1, unlz, ct: ct);
                        action.Confirm(new FlowActionPosition(bx + _ox, yPick, unlz), "X11重试下探Z到位");
                    }
                    catch (PressureStopException)
                    {
                        await cr.RecoverFromPressureStopAsync(ct);
                        throw;
                    }

                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(bx + _ox, yPick, unlz), $"{bed.Code}X11重试后充磁");
                        action.MarkCommandSent();
                        await cr.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                        action.Confirm(new FlowActionPosition(bx + _ox, yPick, unlz), "X11重试后充磁调用成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{bed.Code}下探后再次充磁" }, operationalTracker, ex,
                            "重试充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    magnetOn = true;
                }

                Console.WriteLine("│ [下料] 等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                operationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await cr.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"{bed.Code}充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后成功值（如有）", true);
                    throw;
                }
                physicalTracker?.TryObserveX11(x11);
                Console.WriteLine($"│ [下料] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    bed.X11UnloadMissWarningShown = false;
                    holdingWorkpiece = true; // X11=1才确认工件已由后天车持有, 异常时必须暂停人工处理
                    action.BeginStep(FlowActionStep.ConfirmPickup, new FlowActionPosition(bx + _ox, yPick, unlz), "X11确认后天车已从斜床持件");
                    action.RecordFeedback(new FlowActionPosition(bx + _ox, yPick, unlz), true, true, "斜床取料X11=1");
                    action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "斜床取料X11=1，工件已由后天车持有");
                    action.Confirm(new FlowActionPosition(bx + _ox, yPick, unlz), "X11=1确认工件已由后天车持有");
                    Console.WriteLine($"│ [下料] ✓ X11=1 已吸到(保持在取料位Z={unlz})");
                    break;
                }

                if (retry > 1)
                {
                    // X11连续三次均未确认有板：工件按“仍在斜床”处理。
                    // 必须在仍持有后天车锁时先退磁、再把Z升到安全高度，成功后才允许释放锁并自动重试。
                    var recoveryErrors = new List<string>();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                        operationalSite with { ActionStage = $"{bed.Code}三次X11均未确认持件" }, operationalTracker, null,
                        "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在斜床", true,
                        holdingWorkpiece: false);
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                        magnetOn = false;
                        Console.WriteLine("│ [下料恢复] X11三次均为0，退磁成功");
                    }
                    catch (Exception offEx)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = $"{bed.Code}X11三次0安全恢复退磁" }, operationalTracker, offEx,
                            "X11三次0后的安全恢复退磁失败，实际退磁结果未知", true,
                            holdingWorkpiece: false);
                        if (offEx is OperationCanceledException && ct.IsCancellationRequested)
                            throw;
                        recoveryErrors.Add($"退磁失败: {offEx.Message}");
                        Console.WriteLine($"│ [下料恢复] ❌ 退磁失败: {offEx.Message}");
                    }

                    try
                    {
                        await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
                        zMayBeDown = false; // X11失败恢复已确认Z回安全高度，异常收尾才允许尝试X退避
                        operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "X11三次0安全恢复Z回升成功返回");
                        Console.WriteLine($"│ [下料恢复] Z已回安全高度 {sz}");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception zEx)
                    {
                        recoveryErrors.Add($"Z回安全高度{sz}失败: {zEx.Message}");
                        Console.WriteLine($"│ [下料恢复] ❌ Z回安全高度{sz}失败: {zEx.Message}");
                    }

                    if (recoveryErrors.Count > 0)
                    {
                        // 使旧动作失效并暂停。若Z回升失败，finally会依据zMayBeDown禁止X横移；
                        // 按现场确认策略，异常收尾仍释放本动作的ZoneTS和后天车锁，并在弹窗中写明。
                        operation.CancelAndInvalidate();
                        _paused = true;
                        string alarm = $"1号线后天车从{bed.Code}下料取料时，X11连续三次为0，且安全恢复失败：" +
                                       string.Join("；", recoveryErrors) +
                                       $"。工件{wp.IdentityText}仍保留在斜床等待下料，后端已暂停。请人工确认磁铁和Z轴位置。";
                        Console.WriteLine($"│ [下料恢复] ⚠ {alarm}");
                        pendingSafetyAlarm = alarm;
                        throw new InvalidOperationException(alarm);
                    }

                    if (!bed.X11UnloadMissWarningShown)
                    {
                        bed.X11UnloadMissWarningShown = true;
                        string warning = $"1号线后天车在{bed.Code}下料取料时，X11连续三次为0。" +
                                         $"工件{wp.IdentityText}仍按留在斜床处理；后天车已退磁并将Z升到安全高度{sz}，引擎未暂停，将继续自动重试。";
                        Console.WriteLine($"│ [下料恢复] ⚠ {warning}");
                        OnRearCraneWarning?.Invoke(warning);
                    }

                    throw new InvalidOperationException("下料取料失败: 3次充磁后X11仍=0；已退磁并将Z升到安全高度，保留工件等待下轮重试");
                }
                unlz += 5;
                Console.WriteLine($"│ [下料] ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            Console.WriteLine("│ [下料] ② 清上一步信号 → 写天车下料到位 → 等CNC张开尾座(F5=1)...");
            if (isF) bed.F!.SafeSetMacro(1103, 0); // 清#1103
            else
            {
                await bed.M!.ClearRemoteStartAsync(ct);
                await bed.M!.ClearTailstockClampAsync(ct);
            } // 清10372=0, 清10371=0(防夹紧残留)

            await WaitTailstockOpenedForUnloadAsync(bed, isF, ct);
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
            else await bed.M!.ClearTailstockOpenAsync(ct); // 清10373=0  今洲斜床
            if (!isF)
            {
                //今洲斜床 不需要写10374=1了 没用 直接写10373=1 远程尾座张开
                await bed.M!.OpenTailstockAsync(ct);
            }
            else
            {
                action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(bx + _ox, by + _oy, sz), "写斜床天车下料完成通知");
                action.MarkCommandSent();
                await bed.F!.SetCraneUnloadDoneAsync(ct);
                action.Confirm(new FlowActionPosition(bx + _ox, by + _oy, sz), "斜床天车下料完成通知成功返回");
            }

            if (sharedLocked)
            {
                // ST108下料已完成: 尾座张开、Y回数据库坐标、Z已升安全、CNC已收到下料完成。
                // 下料仍按原策略先执行X+1000/配置退避，再释放ZoneTS。
                sharedRetreatAttempted = true;
                sharedRetreatSucceeded = await TryRetreatSharedAreaBeforeReleaseAsync(cr, bed.Code, "下料", ct);
                if (sharedRetreatSucceeded)
                {
                    operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                    sharedLocked = false;
                    Console.WriteLine("│ [下料] ST108已完成ZoneTS侧退避并写下料完成, 提前释放ZoneTS");
                }
                else
                {
                    _paused = true;
                    Console.WriteLine("│ [下料] ⚠ ST108 ZoneTS侧退避失败; 引擎已暂停, finally仍会释放ZoneTS和后天车锁");
                    throw new InvalidOperationException("ST108下料完成后ZoneTS侧退避失败；已暂停，finally将释放ZoneTS和后天车锁");
                }
            }

            bool isLong = wp.Length >= 800;
            bool needsBalancing = isLong || wp.ForceBalancing;
            operationalRequiresDownstreamNotification = !needsBalancing;
            string balancingReason = isLong ? "版长≥800" : "任务勾选动平衡";
            Console.WriteLine(
                $"│ [下料] ④ 分流 → {wp.IdentityText} 版长={wp.Length}mm {(needsBalancing ? $"{balancingReason}→ST0191号线动平衡下料架1" : "<800且未勾选→ST010研磨上料架1号位")}");
            if (needsBalancing)
            {
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheNotification, "M817动平衡缓存回调");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheMutation, "M817");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST019/M817完整交接");
            }
            else
            {
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721(ST010放料完成)");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheNotification, "M720研磨缓存回调");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheMutation, "M720");
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST010/M720完整交接");
            }
            //   获取平衡料架位置锁(防M2Flow/M3Flow/研磨天车同时操作)
            //   1号线后天车下料分流区统一拿两把锁: M817 + M720。
            //   长板目标是M817, 短板目标是M720; 但后天车飞行/放料路径都经过同一侧分流区域,
            //   因此不按长短省锁, 避免目标位附近设备或研磨天车并行动作形成碰撞窗口。
            bool gotM817 = false;
            bool gotM720 = false;
            try
            {
                //需要动平衡
                if (needsBalancing)
                {
                    // 需要动平衡: 主循环已用M817快照做过预检。
                    // 工件已由后天车吸起后, 不能再因为前端快照瞬时无效直接停半路;
                    // 拿到位置锁后再读一次PLC真实M817, 以二次确认为最终放料依据。
                    Console.WriteLine("│ [分流] 目标: ST019(动平衡下料架1)  拿锁后二次确认M817...");
                    // 注意: 长板也拿M720路径锁。多把锁从获取前就进入finally保护,
                    // 第二把等待异常/取消时也会释放第一把, 避免锁泄漏。
                    if (_lockM817 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M817)..."); await operation.AcquireAsync("M817", _lockM817, ct); action.RegisterLock("M817"); gotM817 = true; }
                    if (_lockM720 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M720路径保护)..."); await operation.AcquireAsync("M720", _lockM720, ct); action.RegisterLock("M720"); gotM720 = true; }
                    //二次确认
                    await ConfirmM817EmptyBeforePlaceAsync(ct);
                }
                else
                {
                    // 短工件: 检查目的地 + 拿两把锁(M817→M720, 路径必经M817区域)
                    Console.WriteLine("│ [分流] 目标: ST010(研磨上料架1号位)  检查M720...");
                    Console.WriteLine("│   M720可放料 ✓ (前置步骤④已确认)");
                    //加两把锁
                    // 注意: 多把锁从获取前就进入finally保护, 第二把等待异常/取消时也会释放第一把, 避免死锁。
                    if (_lockM817 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M817)..."); await operation.AcquireAsync("M817", _lockM817, ct); action.RegisterLock("M817"); gotM817 = true; }
                    if (_lockM720 != null) { Console.WriteLine("│ [分流] 等待平衡锁(M720)..."); await operation.AcquireAsync("M720", _lockM720, ct); action.RegisterLock("M720"); gotM720 = true; }
                    await WaitForM720CanPlaceBeforePlaceAsync(wp, ct);
                }

                // ③ XY移动+放料(持锁中)
                if (needsBalancing) // 需要动平衡 → ST019动平衡架
                {
                    // 去ST019前检查机械手2/3安全位, 防天车与机械手碰撞
                    //   ST019(M817)与ST021(M821)相邻, M3Flow可能正在附近操作
                    // Console.WriteLine("│   等机械手安全位...");
                    // await WaitManipulators(2, 3, ct);
                    // Console.WriteLine("│   机械手已在安全位 ✓");

                    if (!TryCoords("ST019", out int dx, out int dy, out int dz)) throw new Exception("缺少ST019坐标");
                    Console.WriteLine($"│   XY到ST019({dx + _ox},{dy + _oy})");
                    action.SetTarget("ST019", "已选择M817/ST019作为实际分流目标");
                    action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(dx + _ox, dy + _oy, sz), "持件XY到ST019/M817");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(dx + _ox, dy + _oy, -1, ct: ct);
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST019目标XY到位");
                    action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST019放料前XY微调");
                    action.MarkCommandSent();
                    await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                        cr, _cfg, CraneRearNo, "ST019", "1号线后天车-ST019放料前",
                        reporter: _exceptionReporter,
                        failureContext: OperationalEventContextFactory.FineTuneFailure(
                            scope: "1号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: "ST019",
                            actionStage: "ST019动平衡架放料前XY微调", workpiece: wp,
                            source: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "本物理周期来源"),
                            target: OperationalEventContextFactory.ConfirmedLocation("ST019", "分流结果"),
                            owner: "1号线后天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后计算ST019放料dz2"),
                            physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "斜床取料X11=1且分流已完成", "天车/ST019上方")),
                        actionId: actionId, physicalTracker: physicalTracker, ct: ct);
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST019放料前XY微调完成");
                    int dz2 = Pz(dz, wp.Diameter);
                    Console.WriteLine($"│   Z下降到{dz2 + _oz}");
                    operationalTracker.BeginZDown(dz2 + _oz);
                    physicalTracker?.TryMarkZUnknown("ST019原始放料Z下降调用已开始，旧安全高度不能继承");
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST019下降放料");
                        action.MarkCommandSent();
                        await cr.MoveAbsoluteAsync(-1, -1, dz2 + _oz, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST019放料Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("ST019放料Z下降触发下压保护，恢复后位置等待安全高度确认");
                        physicalTracker?.TryMarkZUnknown("ST019放料Z下降触发下压保护，恢复后位置未知");
                        await cr.RecoverFromPressureStopAsync(ct);
                        throw;
                    }
                    catch (Exception)
                    {
                        operationalTracker.MarkZUnknown("ST019放料Z下降方法异常，命令实际结果和当前位置未知");
                        physicalTracker?.TryMarkZUnknown("ST019放料Z下降方法异常，旧安全高度不能继承");
                        throw;
                    }
                    operationalTracker.BeginPlacementMagnetOff("ST019");
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST019/M817退磁放料");
                        action.MarkCommandSent();
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompletePlacementMagnetOff("ST019");
                        action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, dz2 + _oz), "ST019退磁调用成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { Station = "ST019", ActionStage = "ST019目标位退磁放料" }, operationalTracker, ex,
                            "目标位退磁方法异常，工件是否释放未知", true,
                            holdingWorkpiece: holdingWorkpiece, placed: null, cacheNotified: null, handoffClosed: false);
                        throw;
                    }
                    magnetOn = false;
                    operationalPlaced = true;
                    action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "ST019退磁放料成功返回，等待缓存和Z安全回升");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST019/M817完整交接",
                        "ST019目标位退磁成功返回，放料推定成立；等待缓存回调和Z安全回升");
                    Console.WriteLine($"│   ✓ ST019放料完成,退磁 {wp.IdentityText}");
                    // 通知平衡引擎。工件已物理放下, 缓存回调必须成功, 不能静默跳过。
                    if (OnBalancingRackPlaced == null)
                        throw new InvalidOperationException("M817已放料但动平衡缓存回调未绑定");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "M817动平衡缓存回调", "动平衡缓存回调调用已开始，结果未知");
                    operationalTracker.BeginCacheMutation("M817",
                        EvidenceValue<string>.Unknown("回调前未确认M817缓存已写入本周期工件"),
                        "M817缓存写入调用已开始，修改后事实未知");
                    OnBalancingRackPlaced.Invoke("M817", wp);
                    operationalCacheNotified = true;
                    operationalTracker.CompleteCacheMutation("M817",
                        EvidenceValue<string>.Unknown("回调前未确认M817缓存已写入本周期工件"),
                        EvidenceValue<string>.Confirmed("回调成功后M817缓存已写入本周期工件", "缓存回调成功返回"),
                        "M817缓存写入成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "M817动平衡缓存回调", "动平衡缓存回调成功返回");
                    wp.ReportStage("1号线 动平衡下料架 M817");
                    //移动z
                    action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST019放料后Z升安全高度");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
                    action.Confirm(new FlowActionPosition(dx + _ox, dy + _oy, sz), "ST019放料后Z安全到位");
                    physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST019放料后Z升安全命令成功返回");
                    operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST019放料后Z升安全命令成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST019/M817完整交接",
                        "ST019退磁、M817缓存回调及Z安全回升均成功返回");
                    holdingWorkpiece = false;
                    placedToDestination = true;
                }
                else //短板  去M720
                {
                    // Console.WriteLine("│   等机械手安全位...");
                    // await WaitManipulators(2, 3, ct);
                    // Console.WriteLine("│   机械手已在安全位 ✓");

                    if (!TryCoords("ST010", out int gx, out int gy, out int gz)) throw new Exception("缺少ST010坐标");
                    Console.WriteLine($"│   XY到ST010({gx + _ox},{gy + _oy})");
                    action.SetTarget("ST010", "已选择M720/ST010作为实际分流目标");
                    action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(gx + _ox, gy + _oy, sz), "持件XY到ST010/M720");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(gx + _ox, gy + _oy, -1, ct: ct);
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST010目标XY到位");
                    action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST010放料前XY微调");
                    action.MarkCommandSent();
                    await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                        cr, _cfg, CraneRearNo, "ST010", "1号线后天车-ST010放料前",
                        reporter: _exceptionReporter,
                        failureContext: OperationalEventContextFactory.FineTuneFailure(
                            scope: "1号线", engine: "后端引擎", deviceNo: CraneRearNo.ToString(), station: "ST010",
                            actionStage: "ST010研磨上料架放料前XY微调", workpiece: wp,
                            source: OperationalEventContextFactory.ConfirmedLocation(bed.Code, "本物理周期来源"),
                            target: OperationalEventContextFactory.ConfirmedLocation("ST010", "分流结果"),
                            owner: "1号线后天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后计算ST010放料gz2"),
                            physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "斜床取料X11=1且分流已完成", "天车/ST010上方")),
                        actionId: actionId, physicalTracker: physicalTracker, ct: ct);
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST010放料前XY微调完成");
                    int gz2 = Pz(gz, wp.Diameter);
                    Console.WriteLine($"│   Z下降到{gz2 + _oz}");
                    operationalTracker.BeginZDown(gz2 + _oz);
                    physicalTracker?.TryMarkZUnknown("ST010原始放料Z下降调用已开始，旧安全高度不能继承");
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST010下降放料");
                        action.MarkCommandSent();
                        await cr.MoveAbsoluteAsync(-1, -1, gz2 + _oz, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST010放料Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("ST010放料Z下降触发下压保护，恢复后位置等待安全高度确认");
                        physicalTracker?.TryMarkZUnknown("ST010放料Z下降触发下压保护，恢复后位置未知");
                        await cr.RecoverFromPressureStopAsync(ct);
                        throw;
                    }
                    catch (Exception)
                    {
                        operationalTracker.MarkZUnknown("ST010放料Z下降方法异常，命令实际结果和当前位置未知");
                        physicalTracker?.TryMarkZUnknown("ST010放料Z下降方法异常，旧安全高度不能继承");
                        throw;
                    }

                    operationalTracker.BeginPlacementMagnetOff("ST010");
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST010/M720退磁放料");
                        action.MarkCommandSent();
                        await cr.MagnetOffAsync(ct);
                        operationalTracker.CompletePlacementMagnetOff("ST010");
                        action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, gz2 + _oz), "ST010退磁调用成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { Station = "ST010", ActionStage = "ST010目标位退磁放料" }, operationalTracker, ex,
                            "目标位退磁方法异常，工件是否释放未知", true,
                            holdingWorkpiece: holdingWorkpiece, placed: null, cacheNotified: null,
                            downstreamNotified: null, handoffClosed: false);
                        throw;
                    }
                    magnetOn = false;
                    operationalPlaced = true;
                    action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "ST010退磁放料成功返回，等待M721和研磨缓存通知");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST010/M720完整交接",
                        "ST010目标位退磁成功返回，放料推定成立；等待Z安全回升、M721和研磨缓存回调");
                    Console.WriteLine("│   ✓ ST010放料完成,退磁");
                    //退磁完成先回安全位置
                    action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST010放料后Z升安全高度");
                    action.MarkCommandSent();
                    await cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);
                    action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "ST010放料后Z安全到位");
                    physicalTracker?.TryConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST010放料后Z升安全命令成功返回");
                    operationalTracker.ConfirmSafeZ(sz, _cfg.AbsMove.Tolerance, "ST010放料后Z升安全命令成功返回");
                    // ── M721=1: 通知PLC放料到1号位完成 → PLC将启动传送带旋转 ──
                    try
                    {
                        operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721(ST010放料完成)", "M721写入调用已开始，结果未知");
                        var mc65 = await EnsureMc65Async(ct);
                        action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(gx + _ox, gy + _oy, sz), "写M721通知ST010放料完成");
                        action.MarkCommandSent();
                        await mc65.WriteMBitInWordAsync(720, 1, true, ct);
                        operationalDownstreamNotified = true;
                        operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721(ST010放料完成)", "M721写入成功返回");
                        action.Confirm(new FlowActionPosition(gx + _ox, gy + _oy, sz), "M721放料完成通知成功返回");
                        Console.WriteLine($"│   ✓ M721=1 通知PLC放料完成(传送带启动) {wp.IdentityText}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"│   ⚠ M721写入失败: {ex.Message}");
                        throw new InvalidOperationException("ST010/M720已放料但M721写入失败, 需人工确认或补写M721", ex);
                    }

                    // ── 通知研磨引擎: 工件已放到1号位, 入FIFO缓存 ──
                    wp.BoreType = 1; // bore固定1(大孔), 研磨机不需要版孔区分
                    if (OnGrindingRackPlaced == null)
                        throw new InvalidOperationException("M720/ST010已放料但研磨缓存回调未绑定");
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "M720研磨缓存回调", "研磨缓存回调调用已开始，结果未知");
                    operationalTracker.BeginCacheMutation("M720",
                        EvidenceValue<string>.Unknown("回调前未确认M720缓存已写入本周期工件"),
                        "M720缓存写入调用已开始，修改后事实未知");
                    OnGrindingRackPlaced.Invoke(wp);
                    operationalCacheNotified = true;
                    operationalTracker.CompleteCacheMutation("M720",
                        EvidenceValue<string>.Unknown("回调前未确认M720缓存已写入本周期工件"),
                        EvidenceValue<string>.Confirmed("回调成功后M720缓存已写入本周期工件", "缓存回调成功返回"),
                        "M720缓存写入成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "M720研磨缓存回调", "研磨缓存回调成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST010/M720完整交接",
                        "ST010退磁、Z安全回升、M721及M720研磨缓存回调均成功返回");
                    wp.ReportStage("1号线 研磨上料架 ST010/M720");
                    Console.WriteLine($"│   ✓ 通知研磨引擎入缓存 {wp.IdentityText} d={wp.Diameter} L={wp.Length}");
                    
                    holdingWorkpiece = false;
                    placedToDestination = true;
                }

            }
            finally
            {
                // 长短板都可能持有M720路径锁; 只释放真正拿到的锁。
                if (gotM720)
                {
                    if (operation.TryRelease("M720", _lockM720)) Console.WriteLine("│ [分流] 释放 M720锁");
                    else Console.WriteLine("│ [分流] M720锁已由应急释放, 跳过重复释放");
                }
                if (gotM817)
                {
                    if (operation.TryRelease("M817", _lockM817)) Console.WriteLine("│ [分流] 释放 M817锁");
                    else Console.WriteLine("│ [分流] M817锁已由应急释放, 跳过重复释放");
                }
            }

            
            Console.WriteLine("│ [下料] ⑤ 后天车回自定义位置 X→home Z回原点 并发执行");
            int homeX = _cfg.GetCraneHomeX(CraneRearNo); // 后天车归位X(配置文件, 默认-4000)
            var xTask = cr.MoveAbsoluteAsync(homeX, -1, -1, ct: ct);
            //回z轴原点
            var zTask = cr.HomeZAsync(ct);
            //并发执行
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
        catch (CraneMotionTimeoutException ex)
        {
            _paused = true;
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
            pendingSafetyAlarm = $"1号线后天车从{bed.Code}下料时{ex.Stage}运动超时。" +
                $"目标=({ex.XTarget},{ex.YTarget},{ex.ZTarget})，最后坐标={ex.LastKnownStatus?.XPos}/{ex.LastKnownStatus?.YPos}/{ex.LastKnownStatus?.ZPos}，" +
                $"未到位轴={string.Join("/", ex.UnreachedAxes)}，D4518停止={(ex.EmergencyStopCommandSucceeded ? "已发送" : "发送失败")}。" +
                "引擎已暂停；工件状态保留待人工确认，本动作软件锁将在收尾后释放，禁止自动重试。";
            Console.WriteLine($"│ [下料] ⚠ {pendingSafetyAlarm}");
        }
        catch (PressureStopException ex)
        {
            _paused = true;
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
            pendingSafetyAlarm = $"1号线后天车从{bed.Code}下料时触发下压保护，Z未确认到位。" +
                "引擎已暂停，本动作软件锁将在收尾后释放；请人工确认天车、磁铁和工件位置。异常：" + ex.Message;
            Console.WriteLine($"│ [下料] ⚠ {pendingSafetyAlarm}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│ [下料] ❌ 异常: {bed.Wp?.IdentityText ?? "版号=未知"} {ex.Message}");
            FlowActionExceptionConclusion exceptionConclusion = FlowActionExceptionConclusion.WaitRetry;
            if (operationalPlaced && (!operationalCacheNotified ||
                (operationalRequiresDownstreamNotification && !operationalDownstreamNotified)))
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { ActionStage = "目标位退磁放料后通知闭环未完成" }, operationalTracker, ex,
                    "退磁已成功返回并推定工件已放到目标位，但缓存或PLC通知未完整成功", true,
                    holdingWorkpiece: false, placed: operationalPlaced,
                    cacheNotified: null,
                    downstreamNotified: null,
                    handoffClosed: false);
            }
            if (!placedToDestination)
            {
                exceptionConclusion = FlowActionExceptionResolver.Resolve(action, ex);
            }
            if (exceptionConclusion == FlowActionExceptionConclusion.PauseLineManual)
            {
                _paused = true;
                requiresManualConfirmation = true;
            }
            // magnetOn 只表示发过充磁命令, 不等于工件已经吸住。
            // holdingWorkpiece 由X11=1确认, 这是判断异常后是否需要暂停人工处理的安全边界。
            if (holdingWorkpiece && !placedToDestination)
            {
                _paused = true;
                bed.St = SkewState.Unloading;
                if (bed.Wp is { } heldWp)
                    SetRearCraneTask(heldWp, "已持件，目标位未完成放料", bed.Code, "分流目的地", true, "X11已确认持件，请人工确认天车和工件位置");
                Console.WriteLine("│ ⚠ X11已确认工件离开斜床但未完成目标位放料/通知, 引擎已暂停, 请人工确认后天车和工件位置");
                pendingSafetyAlarm = $"1号线后天车从{bed.Code}下料时发生异常，X11已确认工件在天车上，但目标位放料或通知尚未完成。引擎已暂停，请人工确认天车和工件位置。异常：{ex.Message}";
            }
            else if (exceptionConclusion == FlowActionExceptionConclusion.PauseLineManual && !placedToDestination)
            {
                bed.St = SkewState.Unloading;
                if (bed.Wp is { } unknownWp)
                    SetRearCraneTask(unknownWp, "物理命令响应未知", bed.Code, "分流目的地", true,
                        "命令已发送但X11/目标交接未确认，禁止自动重试");
                pendingSafetyAlarm = $"1号线后天车从{bed.Code}下料时，物理命令已发送但X11、工件位置或目标交接未确认。" +
                    $"引擎已暂停，禁止自动重试；请人工确认天车、磁铁、工件和目标位。异常：{ex.Message}";
                Console.WriteLine($"│ ⚠ {pendingSafetyAlarm}");
            }
            else if (magnetOn)
            {
                bed.St = SkewState.WaitingUnload;
                Console.WriteLine("│ 磁铁曾充磁但X11未确认有版→不判定后天车持件, 保持等待下料/下轮重试");
            }
            else if (placedToDestination)
            {
                bed.St = SkewState.Idle;
                bed.Wp = null;
                ClearRearCraneTask();
                Console.WriteLine("│ 工件已完成目标位放料确认→斜床可复位Idle");
            }
            else
            {
                bed.St = SkewState.WaitingUnload;
                ClearRearCraneTask();
                Console.WriteLine("│ 工件仍在斜床→保持等待下料");
            }

            // DoUnload不进入中转架, 不处理_transferRackLock；ST108/ST606只按任务牌处理ZoneTS。
        }
        finally
        {
            if (operation.RequiresManualSafetyRecovery)
            {
                // 位置未知时禁止finally再移动，但软件锁随已暂停的动作退出而释放。
                bool rearReleased = operation.TryRelease("RearCrane", _craneRearLock);
                bool mtReleased = operation.TryRelease("ZoneMT", _safety.MarkerTransferCollisionLock);
                bool tsReleased = operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                bool m817Released = operation.TryRelease("M817", _lockM817);
                bool m720Released = operation.TryRelease("M720", _lockM720);
                string alarm = (pendingSafetyAlarm ?? operation.ManualSafetyHoldReason ?? "后天车运动超时") +
                    $" 本动作软件锁已释放(后天车={YN(rearReleased)} ZoneMT={YN(mtReleased)} ZoneTS={YN(tsReleased)} M817={YN(m817Released)} M720={YN(m720Released)})。" +
                    "请先现场确认，再执行斜床应急恢复；恢复前不得直接启动引擎。";
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

                    // 与ST108上料保持一致：无论退避成功、失败或因低Z跳过，
                    // 都释放本次下料动作持有的ZoneTS；弹窗明确说明锁已释放并要求立即人工处理。
                    operation.TryRelease("ZoneTS", _safety.TransferSkew1CollisionLock);
                    sharedLocked = false;
                    sharedCleanupText += "；ZoneTS已释放";

                    if (!sharedRetreatSucceeded && pendingSafetyAlarm == null)
                    {
                        _paused = true;
                        pendingSafetyAlarm = $"1号线后天车在{bed.Code}下料异常收尾时未完成ZoneTS侧X退避。引擎已暂停，请立即人工确认天车位置";
                    }
                }
            }

            // 情景：ST108已正常完成X退避并提前释放ZoneTS，随后分流/目的位又发生异常。
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
                pendingSafetyAlarm += $"。异常收尾：{cleanupText}。禁止直接点击启动；请先人工处理现场，确认安全后执行斜床应急恢复。";
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
    /// finally里仍持有ZoneTS, 说明流程没有走到正常提前释放点, 后天车可能停在ST108上方。
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
            //目标位置
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

    private static int ResolveSkewMode(string? process)
    {
        string text = process?.Trim() ?? string.Empty;
        const int defaultMode = 1; // 默认粗精一体不倒角
        if (string.IsNullOrWhiteSpace(text)) return defaultMode;

        return text switch
        {
            "粗精一体不倒角" => 1,
            "粗精一体倒角" => 2,
            "精车" => 3,
            _ when int.TryParse(text, out var m) && m is 1 or 2 or 3 => m,
            _ => WarnAndDefault(text, defaultMode)
        };
    }

    private static int WarnAndDefault(string text, int defaultMode)
    {
        Console.WriteLine($"│ [握手] ⚠ 斜床工艺[{text}]无法识别, 默认粗精一体不倒角({defaultMode})");
        return defaultMode;
    }


    // ═══════════════════════════════════════════════════════════════
    //  Init — 引擎启动时执行一次
    //    ① 读后天车(ST102)数据库偏移量
    //    ② 并行连接5台斜床(整个Init硬超时5s, FANUC原生DLL阻塞也无妨)
    //    ③ 不连MC65 — 不做动平衡的短工件下料时按需连
    // ═══════════════════════════════════════════════════════════════
    private async Task Init(CancellationToken ct)
    {
        if (_stationCoords.TryGetValue("ST102", out var cr)) { _ox = (int)cr.XOffset; _oy = (int)cr.YOffset; _oz = (int)cr.ZOffset; }
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
        // MC65不在此连接, ST112连不上由主循环步骤②后台重连
    }

    /// <summary>连接单台斜床。FANUC只启动独立Worker, 由Worker内部连接/重连并写入快照。</summary>
    private async Task TryConnectBed(SkewCtx ctx, string ip, int port, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"[连接] {ctx.Code} 开始连接 {ip}:{port}...");
            if (ctx.Code == "ST112")
            {
                var fanuc = new FanucSkewBedService(ip, port > 0 ? port : 8193);
                ctx.F?.Dispose();
                ctx.F = fanuc;
                fanuc.StartPollWorker(); // 独立线程轮询, 不占线程池
                Console.WriteLine($"[连接] {ctx.Code} ✓ FANUC Worker已启动");
            }
            else
            {
                var modbus = new ModbusSkewBedService(ip, port > 0 ? port : 502);
                await ConnectModbusWithTimeoutAsync(modbus, ct);
                ctx.M?.Dispose();
                ctx.M = modbus;
                Console.WriteLine($"[连接] {ctx.Code} ✓ 连接成功");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[连接] {ctx.Code} ✗ 失败: {ex.Message}"); }
    }

    /// <summary>后台重连(fire-and-forget)。FANUC不走线程池, 只在Worker缺失时重建。
    /// 成功时清零 ConsecutiveReconnectFails, 失败时累加(UI通过DeviceStatus可见)。</summary>
    private async Task TryConnectBedBg(SkewCtx ctx, string ip, int port)
    {
        try
        {
            Console.WriteLine($"[后台重连] {ctx.Code} {ip}:{port}...");
            if (ctx.Code == "ST112")
            {
                var fanuc = new FanucSkewBedService(ip, port > 0 ? port : 8193);
                ctx.F?.Dispose();
                ctx.F = fanuc;
                fanuc.StartPollWorker(); // Worker线程内部处理连接+退避重连
            }
            else
            {
                // Modbus轮询、动作握手和故障恢复共用同一服务实例。
                // 底层客户端自带串行锁和断线清理；这里不能替换/释放动作线程正在使用的服务。
                var modbus = ctx.M;
                if (modbus == null)
                {
                    modbus = new ModbusSkewBedService(ip, port > 0 ? port : 502);
                    ctx.M = modbus;
                }
                await ConnectModbusWithTimeoutAsync(modbus, CancellationToken.None);
            }
            ctx.ConsecutiveReconnectFails = 0; // 成功清零
            Console.WriteLine($"[后台重连] {ctx.Code} ✓ 成功 (连续失败次数已清零)");
        }
        catch (Exception ex)
        {
            ctx.ConsecutiveReconnectFails++; // 失败累加
            Console.WriteLine($"[后台重连] {ctx.Code} ✗ 失败(连续{ctx.ConsecutiveReconnectFails}次): {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref ctx.ReconnectInProgress, 0);
        }
    }

    private static async Task ConnectModbusWithTimeoutAsync(ModbusSkewBedService svc, CancellationToken ct)
    {
        // 底层ConnectAsync已有5秒硬超时；这里只负责把调用方取消令牌链接进去。
        // 不Dispose服务，失败连接会由底层关闭套接字，同一实例可在下一轮安全重连。
        using var timeoutCts = new CancellationTokenSource(5000);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await svc.ConnectAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Modbus连接超时5s");
        }
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
            await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct); waited += _cfg.Grinding.SignalPollIntervalMs; // 紧轮询
        }
        throw new TimeoutException($"等待信号[{n}]超时({ms}ms)");
    }

    /// <summary>
    /// 下料已吸住工件后等待尾座张开。Modbus斜床通信抖动时允许短次数重连重写命令,
    /// 但必须重新确认尾座张开后才允许Y轴移动。
    /// </summary>
    private async Task WaitTailstockOpenedForUnloadAsync(SkewCtx bed, bool isFanuc, CancellationToken ct)
    {
        if (isFanuc)
        {
            await bed.F!.SetCraneUnloadInPlaceAsync(true, ct);
            await W(async () => await bed.F!.IsTailstockOpenedAsync(ct), "尾座张开", ct);
            return;
        }

        const int maxRecoveries = 3;
        int ms = _cfg.Grinding.HandshakeTimeoutMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        int recoveries = 0;
        int waited = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await bed.M!.OpenTailstockAsync(ct);
                if (await bed.M!.IsTailstockOpenedAsync(ct))
                {
                    Console.WriteLine($"│   ✓ 信号[尾座张开]已就绪(等待{waited}ms, 通信恢复{recoveries}次)");
                    return;
                }
            }
            catch (Exception ex) when (recoveries < maxRecoveries && !ct.IsCancellationRequested)
            {
                recoveries++;
                Console.WriteLine($"│   ⚠ 尾座张开读写异常({recoveries}/{maxRecoveries}): {ex.Message} → 重连斜床后重写张开命令");
                if (!await TryReconnectModbusBedAsync(bed, "尾座张开确认", ct))
                    await Task.Delay(Math.Max(_cfg.Grinding.SignalPollIntervalMs, 200), ct);
                continue;
            }

            await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct);
            waited += _cfg.Grinding.SignalPollIntervalMs;
        }

        throw new TimeoutException($"等待信号[尾座张开]超时({ms}ms, 通信恢复{recoveries}次)");
    }

    private async Task<bool> TryReconnectModbusBedAsync(SkewCtx bed, string reason, CancellationToken ct)
    {
        if (!TryIp(bed.Code, out var ip, out var port) || string.IsNullOrWhiteSpace(ip))
        {
            Console.WriteLine($"│   ⚠ {reason}: {bed.Code} 未配置IP, 无法重连");
            return false;
        }

        // 与定时后台重连共用同一互斥标记，动作恢复不能再创建第二条连接。
        if (Interlocked.CompareExchange(ref bed.ReconnectInProgress, 1, 0) != 0)
        {
            // 后台重连单次最多5秒。动作侧等待同一重连结果，避免200ms快速返回后
            // 连续耗尽恢复次数；等待期间不持有新的连接锁，也不改变任何业务状态。
            var waitDeadline = DateTime.UtcNow.AddMilliseconds(5500);
            Console.WriteLine($"│   ↻ {reason}: {bed.Code} 已有重连任务,最多等待5.5s");
            while (Volatile.Read(ref bed.ReconnectInProgress) != 0 && DateTime.UtcNow < waitDeadline)
            {
                await Task.Delay(100, ct);
            }

            bool connected = bed.M?.IsConnected == true;
            Console.WriteLine(connected
                ? $"│   ✓ {reason}: {bed.Code} 后台重连已完成"
                : $"│   ⚠ {reason}: {bed.Code} 后台重连未成功或等待超时");
            return connected;
        }

        try
        {
            var svc = bed.M;
            if (svc == null)
            {
                svc = new ModbusSkewBedService(ip, port > 0 ? port : 502);
                bed.M = svc;
            }
            await ConnectModbusWithTimeoutAsync(svc, ct);
            bed.ConsecutiveReconnectFails = 0;
            Console.WriteLine($"│   ✓ {reason}: {bed.Code} Modbus重连成功 {ip}:{(port > 0 ? port : 502)}");
            return true;
        }
        catch (Exception ex)
        {
            bed.ConsecutiveReconnectFails++;
            Console.WriteLine($"│   ⚠ {reason}: {bed.Code} Modbus重连失败(连续{bed.ConsecutiveReconnectFails}次): {ex.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref bed.ReconnectInProgress, 0);
        }
    }

    /// <summary>
    /// 二次确认M817物理空闲。
    /// 主循环的目的地检查只能算预检; 分流锁拿到后、XY去目标位前必须再读真实PLC信号,
    /// 防止人工放板/PLC状态变化/旧快照导致叠料。
    /// </summary>
    private async Task ConfirmM817EmptyBeforePlaceAsync(CancellationToken ct)
    {
        using var t = new CancellationTokenSource(3000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, t.Token);
        var mc63 = await EnsureMc63Async(linked.Token);
        var r = await mc63.ReadMAlignedWordAsync(800, 2, linked.Token);
        int m816 = r.IntValues.Length > 1 ? r.IntValues[1] : throw new InvalidOperationException("MC63 M817二次确认返回字数不足");
        bool busy = (m816 & (1 << 1)) != 0;
        Console.WriteLine($"│ [分流] 二次确认M817={(busy ? "有版" : "空闲")} rawM816=0x{m816:X4}");
        if (busy) throw new InvalidOperationException("M817二次确认=有版, 禁止放料");
    }

    /// <summary>
    /// 后天车已持板并取得分流锁后，M720=0只是目标位暂时不可用：保持持板等待，
    /// 不移动、不退磁、不暂停。MC65读取失败仍向外抛出，走既有安全异常路径。
    /// </summary>
    private async Task WaitForM720CanPlaceBeforePlaceAsync(WorkpieceCache wp, CancellationToken ct)
    {
        int waitCount = 0;
        while (!await ReadM720CanPlaceBeforePlaceAsync(ct))
        {
            waitCount++;
            if (waitCount == 1 || waitCount % 10 == 0)
                Console.WriteLine($"│ [分流] ⚠ {wp.IdentityText} M720二次确认=不可放料，保持持板等待（{waitCount * _cfg.Grinding.PollIntervalMs / 1000.0:F1}秒）");
            await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
        }

        if (waitCount > 0)
            Console.WriteLine($"│ [分流] ✓ {wp.IdentityText} M720恢复可放料，结束持板等待，继续放料");
    }

    /// <summary>读取M720允许放版状态。读取失败和报文异常由调用方按安全异常处理。</summary>
    private async Task<bool> ReadM720CanPlaceBeforePlaceAsync(CancellationToken ct)
    {
        using var t = new CancellationTokenSource(3000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, t.Token);
        var mc65 = await EnsureMc65Async(linked.Token);
        var r = await mc65.ReadMAlignedWordAsync(720, 1, linked.Token);
        int m720 = r.IntValues.Length > 0 ? r.IntValues[0] : throw new InvalidOperationException("MC65 M720二次确认返回字数不足");
        bool canPlace = (m720 & 1) != 0;
        return canPlace;
    }

    /// <summary>Z下降公式: 台面Z - Round[(d/2/zFactor1)+(d/2/zFactor2)] — 用于取料/放料</summary>
    private int Pz(int z, double d) { return z - (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2)); }

    /// <summary>从数据库取工位XYZ坐标(含数据库偏移量)</summary>
    private bool TryCoords(string code, out int x, out int y, out int z) { x = y = z = 0; if (!_stationCoords.TryGetValue(code, out var r)) return false; x = (int)(r.X + r.XOffset); y = (int)(r.Y + r.YOffset); z = (int)(r.Z + r.ZOffset); return true; }

    /// <summary>从数据库取工位IP:端口</summary>
    private bool TryIp(string code, out string ip, out int port) { ip = ""; port = 0; if (!_stationCoords.TryGetValue(code, out var r)) return false; ip = r.Ip; port = r.Port; return !string.IsNullOrWhiteSpace(ip); }

    /// <summary>bool→"1"/"0" (日志用)</summary>
    private static string YN(bool b) => b ? "1" : "0";

    /// <summary>等待机械手n1+n2到达安全Y位(下料分流到研磨架前, 防碰撞)</summary>
    private async Task WaitManipulators(int n1, int n2, CancellationToken ct)
    {
        int ms = _cfg.Grinding.HandshakeTimeoutMs; var dl = DateTime.UtcNow.AddMilliseconds(ms);
        int sy1 = n1 == 2 ? _cfg.SkewBed.Manipulator2SafeY : _cfg.SkewBed.Manipulator3SafeY;
        int sy2 = n2 == 2 ? _cfg.SkewBed.Manipulator2SafeY : _cfg.SkewBed.Manipulator3SafeY;
        Console.WriteLine($"│   等待机械手{n1}号(Y={sy1})和{n2}号(Y={sy2})到达安全位...");
        while (DateTime.UtcNow < dl)
        {
            ct.ThrowIfCancellationRequested();
            bool o1 = false, o2 = false;
            try { var s = await _manipulatorCache.GetOrCreateService(n1).ReadStatusAsync(ct); o1 = s != null && Math.Abs(s.YPos - sy1) <= 10; } catch { }
            try { var s = await _manipulatorCache.GetOrCreateService(n2).ReadStatusAsync(ct); o2 = s != null && Math.Abs(s.YPos - sy2) <= 10; } catch { }
            if (o1 && o2) { Console.WriteLine("│   机械手已在安全位"); return; }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("等待机械手安全位超时");
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _engineCts.Cancel(); _engineCts.Dispose(); _craneRearLock.Dispose();
        foreach (var bed in _beds)
        {
            try { bed?.M?.Dispose(); } catch { }
            try { bed?.F?.Dispose(); } catch { }
        }
    }
}
