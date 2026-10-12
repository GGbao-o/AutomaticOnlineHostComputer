using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Logging;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
///暂停按钮：暂停按钮就是一个"主循环开关"——关闭后主循环不再派发新任务，
/// 但已经在跑的任务（天车移动、斜床加工、打号机等待）会继续跑到完成。适合临时阻止新任务入队（比如操作工要进去检查设备），不适合紧急停止。
///
/// 
/// 1号线前端流程引擎 (S2~S6)。
///
/// 【主循环 — 500ms 一轮】
///   ① [_manipulatorLock] 读总上料架(192.168.2.63:9000) M800 → M800=1 + 缓存有料 + 无在途 + 货叉空闲 → 出队 → 机械手1取料送叉
///   ② [_manipulatorLock] 读货叉(192.168.2.88:9000) M900 → 待机位有版 + 货叉空闲 → 双头镗或SkipBoring
///   ③ [无锁]          货叉状态机: R6101写参数/R6102→R6103→M912送料回待机→R6104→R6107→M913取料到Pos3→R6108→天车
///   ④ [无锁]          无条件状态快照刷新 → UI 绑定
///
/// 【锁安全设计 — 固定锁顺序，避免反向持锁死锁】
///   _manipulatorLock: 串行化机械手 + 货叉分派（S2~S3）
///   _craneFrontLock:  串行化前天车流程（S4~S6）
    ///   ZoneMT/ZoneTS: 串行化打号机-中转架、中转架-ST108两个相邻碰撞区。
    ///   前天车锁顺序: _craneFrontLock → ZoneMT → ZoneTS。
    ///   _manipulatorLock 独立，不和Zone碰撞区锁嵌套。
///
/// 【下压信号处理 — 下压不是异常】
///   机械手取料/送叉时 Z 下降过程中下压信号(X2/D4523)亮 = 工件已接触台面，正常停止条件。
///   触发后: 停Z→恢复伺服→继续充磁/退磁→继续后续流程。
/// </summary>
public sealed class Line1FrontFlowEngine : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    //  依赖注入
    // ═══════════════════════════════════════════════════════════════
    private readonly CraneConnectionCache _craneCache;                  // 天车连接缓存(共享,避免重复TCP)
    private readonly ManipulatorConnectionCache _manipulatorCache;      // 机械手连接缓存(共享)
    private readonly MotionConfig _cfg;                                 // 运动参数配置(速度/Z公式/安全高度等)
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords; // 工位坐标字典(key=站号)
    private readonly IOperationalEventReporter _exceptionReporter;
    private readonly CancellationTokenSource _engineCts = new();        // 引擎取消令牌
    private Task? _engineTask;                                          // 引擎后台Task
    private bool _disposed;
    private volatile bool _paused;                                      // 暂停标志(volatile-多线程可见)
    private int _cycleCount;                                             // 循环计数(调试用)
    private string? _lastForkStateLogKey;
    private DateTime _lastForkStateLogAtUtc;
    private DateTime _lastSlowCycleLogAtUtc;
    private static readonly TimeSpan StateLogHeartbeat = TimeSpan.FromSeconds(10);
    private const int SlowCycleWarningMs = 1000;

    // ═══════════════════════════════════════════════════════════════
    //  工件缓存 (FIFO 队列 + 线程安全列表)
    // ═══════════════════════════════════════════════════════════════
    private readonly ConcurrentQueue<WorkpieceCache> _cacheQueue = new();   // FIFO，修改与显示列表共用_cacheLock
    private readonly List<WorkpieceCache> _cachedList = new();              // UI显示用
    private readonly object _cacheLock = new();                              // 保护来源队列与_cachedList
    public int CachedCount { get { lock (_cacheLock) return _cachedList.Count; } }

    /// <summary>供状态页显示的只读缓存快照；不读取或修改FIFO队列。</summary>
    public WorkpieceCache[] GetCachedWorkpiecesSnapshot()
    {
        lock (_cacheLock) return _cachedList.ToArray();
    }
    public int DispatchPressure { get { lock (_wpLock) return CachedCount + (_currentWp != null ? 1 : 0); } }

    /// <summary>在途工件 — 机械手取出后到货叉Pos3为止, 入天车队列后立即清空。
    /// 非null时主循环步骤①不出队新工件。lock(_wpLock)保护。</summary>
    private WorkpieceCache? _currentWp;
    private readonly object _wpLock = new();
    /// <summary>天车任务队列 — 货叉Pos3到位后入队, 天车独立循环消费(可同时处理多个工件)</summary>
    private readonly ConcurrentQueue<WorkpieceCache> _craneQueue = new();
    /// <summary>
    /// 前天车正在处理已出队工件的只读压力标志。
    /// 只用于全局分线“哪条线更忙”的判断；不能作为动作锁，也不能改变前天车正常流程。
    /// </summary>
    private int _frontCraneActive;
    // 前天车任务牌、入出队及异常残留删除共用这把锁，重建队列时保留其他板的相对顺序。
    // 它只保护软件集合，不替代货叉/天车物理区域锁，也不授权删除下一块_currentWp。
    private readonly object _frontCraneTaskLock = new();
    private CraneTaskSnapshot _frontCraneTask = CraneTaskSnapshot.Idle(1, "1号线前天车");
    // 仅在前天车进入人工确认任务牌时保留完整工件，供“已在中转架待交接”补缓存；
    // 页面快照只有显示字段，不能反向构造业务工件。
    private WorkpieceCache? _frontCraneManualWorkpiece;

    /// <summary>货叉-双头镗交互状态机；R6108置1并保持，随后立即清R6102/R6104。</summary>
    private enum ForkBoringPhase
    {
        Idle,
        // ── 上料 ──
        Load_WriteParams,    // 货叉在待机位: 等R6101→写参数→R6102=1
        WaitingRequestLoad,  // 参数已下发: 持续等R6103，不设置业务超时
        GoingToBoring,       // R6103=1后: M912=1送料并回待机 → 等M901=1且M900=0
        Load_WriteDone,      // 货叉已回待机且无板: R6104=1, 双头镗开始加工
        // ── 加工 ──
        WaitingMachine,      // 等R6107=1 (请求下料/加工完成)
        // ── 下料 ──
        Unload_GoPos3,       // M913取料并自动去Pos3 → 等M902=1+M900=1 → R6108=1 → 触发天车
        WaitingForkReturnFromBoring, // R6108已发, 等天车取走后货叉回待机; 此阶段禁止再写M913/M914
        // ── 跳过双头镗 ──
        SkipBoring_GoPos3,  // SkipBoring=true: M914直接送Pos3 → 触发天车(不碰Syntec)
        WaitingForkReturnFromSkip, // SkipBoring已触发天车, 等货叉回待机; 此阶段禁止再写M914
    }
    private ForkBoringPhase _forkPhase = ForkBoringPhase.Idle;
    /// <summary>握手子步骤是否正在执行(防重复触发)。</summary>
    private volatile bool _forkHandshakeInProgress;
    /// <summary>
    /// 当前在途工件是否已满足货叉放行条件。
    /// 只有退磁放板、Z回0、到达1号线首次安全Y且M801写入成功后才置true；
    /// 机械手随后回取板待机点时，货叉可依靠此门闩并行推进双头镗握手。
    /// </summary>
    private volatile bool _manipulatorClearedFork;
    /// <summary>R6108 下料完成信号是否已发送(防重复触发)。</summary>
    private volatile bool _boringUnloadDoneSent;
    /// <summary>M912 送料命令是否已发送。M912 是保持命令位，成功写入后不需要每轮重复写。</summary>
    private volatile bool _m912Sent;
    /// <summary>SkipBoring 天车触发是否已执行(防重复触发)。
    /// 与正常下料完成标志类似, 但 SkipBoring 不写双头镗信号, 需独立标志。</summary>
    private volatile bool _skipBoringTriggered;

    // ═══════════════════════════════════════════════════════════════
    //  信号量 — 固定锁顺序防死锁
    //    前天车锁顺序: _craneFrontLock → ZoneMT → ZoneTS。
    //    机械手锁和货叉分派锁独立, 不和Zone碰撞区锁嵌套。
    // ═══════════════════════════════════════════════════════════════
    private readonly SemaphoreSlim _manipulatorLock;                // 机械手1取料送叉(可与2号线共享)
    private readonly SemaphoreSlim _forkDispatchLock = new(1, 1);  // 货叉分派(本线独立,不与机械手锁竞争)
    private readonly SemaphoreSlim _craneFrontLock = new(1, 1);    // 前天车流程
    private readonly SemaphoreSlim _transferRackLock;               // 兼容旧构造/诊断；正常中转架业务互斥已由ZoneMT+ZoneTS承担
    /// <summary>仅串行化人工前端应急；正常主循环不取得此锁。</summary>
    private readonly SemaphoreSlim _frontEmergencyLock = new(1, 1);
    private readonly SafetyFlags _safety;                      // 前后天车共享安全标志

    private const int CraneFront1No = 1; // 1号线前天车编号
    private const string MarkerSharePath = @"\\192.168.2.67\1";
    private int _craneOffsetX, _craneOffsetY, _craneOffsetZ; // 天车1校准偏移量(来自ST901)

    // ═══════════════════════════════════════════════════════════════
    //  设备服务 (InitServicesAsync 中创建)
    // ═══════════════════════════════════════════════════════════════
    private CraneService? _manipulator1;        // 机械手1 (ModbusTCP, 复用ManipulatorCache)
    private ForkService? _forkSvc;              // 货叉 (MC协议, UseBitReadForM=true)
    private CenteringRackService? _rackSvc; // 总上料架+中转架 (共享连接,可重连)
    private BoringModbusService? _boringSvc;    // 双头镗 (Modbus TCP, R区地址=R*2+1)
    private readonly MarkerShareMonitor _markerMonitor = new("1号线打号机", MarkerSharePath, "ggbao", "123456", EngineLogRouter.Line1);
    private int _forkReconnectInProgress;       // 货叉后台重连占坑, 防止断线时每轮重复创建连接任务
    private int _boringReconnectInProgress;     // 双头镗后台重连占坑, 防止断线时堆积SDK连接任务

    /// <summary>前端放料到中转架时回调, 通知后端引擎工件数据。参数: (站号, 工件数据)</summary>
    public Action<string, WorkpieceCache>? OnRackPlaced;
    /// <summary>天车任务出队前连续两次读到XYZ全零时通知主页面暂停整条1号线并弹窗。</summary>
    public Action<CraneZeroPositionAlarm>? OnCraneZeroPositionDetected;
    /// <summary>前端进入人工确认暂停时通知主页面弹窗。</summary>
    public Action<string>? OnSafetyAlarm;
    /// <summary>机械手1为两线共享设备；回取板待机点时位置不确定，需要同步暂停两条线。</summary>
    public Action<string>? OnSharedManipulatorSafetyAlarm;

    /// <summary>物理工件已放到中转架, 但后端缓存回调未确认。用于外层catch区分“已在中转架”和“仍在天车上”。</summary>
    private sealed class TransferRackCacheException : Exception
    {
        public string RackStation { get; }
        public TransferRackCacheException(string rackStation, Exception inner)
            : base($"工件已物理放到中转架{rackStation}, 但后端缓存未确认", inner)
        {
            RackStation = rackStation;
        }
    }

    /// <summary>引擎是否正在运行 — _engineTask已创建且未完成。</summary>
    public bool IsRunning => _engineTask != null && !_engineTask.IsCompleted;
    public bool IsPaused => _paused;

    // ═══════════════════════════════════════════════════════════════
    //  DeviceStatus — 引擎持续更新，SyncLine1StatusToCardsAsync 1.5s刷到UI
    // ═══════════════════════════════════════════════════════════════
    public Line1DeviceStatus DeviceStatus { get; } = new();

    public CraneTaskSnapshot GetCraneTaskSnapshot()
    {
        lock (_frontCraneTaskLock) return _frontCraneTask;
    }

    /// <summary>
    /// 供“在制工件”页面使用的只读快照。
    /// 仅分别复制缓存、当前工件、天车队列和天车展示任务；不读取设备、不改变队列、不嵌套持锁。
    /// </summary>
    public InProcessWorkpieceSnapshot[] GetInProcessWorkpieceSnapshots()
    {
        var nowUtc = DateTime.UtcNow;
        var result = new List<InProcessWorkpieceSnapshot>();

        WorkpieceCache[] cached;
        lock (_cacheLock) cached = _cachedList.ToArray();
        foreach (var wp in cached)
            result.Add(CreateInProcessSnapshot(InProcessWorkpieceKind.FrontCache, "前端缓存",
                "等待总上料架物理板和取料条件", wp, "软件FIFO缓存", InProcessWorkpieceStatus.Normal, nowUtc));

        WorkpieceCache? current;
        lock (_wpLock) current = _currentWp;
        if (current.HasValue)
            result.Add(CreateInProcessSnapshot(InProcessWorkpieceKind.FrontCurrent, "前端_currentWp",
                GetCurrentFrontWorkpieceStage(), current.Value, "前端活动工件上下文",
                InProcessWorkpieceStatus.Normal, nowUtc));

        foreach (var wp in _craneQueue.ToArray())
            result.Add(CreateInProcessSnapshot(InProcessWorkpieceKind.FrontCraneQueue, "前天车队列",
                "等待前天车取件", wp, "线程安全天车队列", InProcessWorkpieceStatus.Normal, nowUtc));

        var craneTask = GetCraneTaskSnapshot();
        if (craneTask.IsActive && craneTask.Workpiece != null)
            result.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.CraneTask, "1号线", "1号线前端",
                craneTask.CraneName, craneTask.Stage, craneTask.Workpiece, "前天车活动任务",
                craneTask.NeedsManualConfirmation ? InProcessWorkpieceStatus.ManualConfirmation : InProcessWorkpieceStatus.Normal,
                FormatCraneTaskDetail(craneTask), nowUtc));

        return result.ToArray();
    }

    private static InProcessWorkpieceSnapshot CreateInProcessSnapshot(InProcessWorkpieceKind kind,
        string location, string stage, WorkpieceCache workpiece, string evidence,
        InProcessWorkpieceStatus status, DateTime nowUtc)
        => new(kind, "1号线", "1号线前端", location, stage, WorkpieceDisplaySnapshot.From(workpiece),
            evidence, status, "仅为软件内存位置，不代表额外读取到现场信号", nowUtc);

    /// <summary>把现有货叉状态机阶段翻译成页面文字；纯读取，不参与状态迁移。</summary>
    private string GetCurrentFrontWorkpieceStage()
    {
        if (!_manipulatorClearedFork) return "机械手取料/送叉中";
        return _forkPhase switch
        {
            ForkBoringPhase.Idle => "货叉待分派",
            ForkBoringPhase.Load_WriteParams => "等待双头镗参数下发",
            ForkBoringPhase.WaitingRequestLoad => "等待双头镗请求上料",
            ForkBoringPhase.GoingToBoring => "货叉向双头镗送料",
            ForkBoringPhase.Load_WriteDone => "确认双头镗上料完成",
            ForkBoringPhase.WaitingMachine => "双头镗加工中",
            ForkBoringPhase.Unload_GoPos3 => "货叉取回Pos3中",
            ForkBoringPhase.WaitingForkReturnFromBoring => "等待货叉从双头镗返回",
            ForkBoringPhase.SkipBoring_GoPos3 => "跳过双头镗，货叉去Pos3",
            ForkBoringPhase.WaitingForkReturnFromSkip => "等待跳过工艺货叉返回",
            _ => _forkPhase.ToString()
        };
    }

    private static string FormatCraneTaskDetail(CraneTaskSnapshot task)
    {
        string route = string.IsNullOrWhiteSpace(task.SourceStation) && string.IsNullOrWhiteSpace(task.TargetStation)
            ? ""
            : $"；{task.SourceStation}→{task.TargetStation}";
        string manual = string.IsNullOrWhiteSpace(task.ManualConfirmationText) ? "" : $"；{task.ManualConfirmationText}";
        return $"活动任务{route}{manual}";
    }

    private void SetFrontCraneTask(WorkpieceCache wp, string stage, string source, string target, bool manual = false, string manualText = "")
    {
        lock (_frontCraneTaskLock)
        {
            _frontCraneTask = new CraneTaskSnapshot(true, CraneFront1No, "1号线前天车", WorkpieceDisplaySnapshot.From(wp), stage, source, target, manual, manualText, DateTime.UtcNow);
            _frontCraneManualWorkpiece = manual ? wp : null;
        }
    }

    private void ClearFrontCraneTask()
    {
        lock (_frontCraneTaskLock)
        {
            _frontCraneTask = CraneTaskSnapshot.Idle(CraneFront1No, "1号线前天车");
            _frontCraneManualWorkpiece = null;
        }
    }

    /// <summary>仅处理前天车已退磁放到中转架、但OnRackPlaced缓存未闭环的人工结论。</summary>
    public string ResolveFrontCraneTargetPending(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线前天车取料-打号-中转" ||
            action.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff)
            return $"1号线前天车动作不是目标待交接结案: {operationId}";
        WorkpieceCache? wp;
        CraneTaskSnapshot task;
        lock (_frontCraneTaskLock) { wp = _frontCraneManualWorkpiece; task = _frontCraneTask; }
        if (!task.IsActive || !task.NeedsManualConfirmation || !wp.HasValue || string.IsNullOrWhiteSpace(task.TargetStation))
            return "1号线前天车缺少完整人工任务牌或真实目标站号，拒绝凭显示快照补中转架缓存。";
        if (OnRackPlaced == null) return "1号线后端中转架缓存回调未绑定，未修改前天车任务牌。";
        _paused = true;
        try
        {
            OnRackPlaced(task.TargetStation, wp.Value);
            ClearFrontCraneTask();
            FlowActionManualRegistry.Remove(operationId);
            string result = $"1号线前天车已确认工件在{task.TargetStation}：后端中转架缓存补写成功，前天车任务牌已结案；前端保持暂停。";
            Console.WriteLine($"[Line1Front] [人工账本结案] {result}");
            return result;
        }
        catch (Exception ex)
        {
            return $"1号线中转架缓存补写失败: {ex.Message}；任务牌与动作账本保留，前端保持暂停。";
        }
    }

    /// <summary>前天车尚未充磁时人工确认工件仍在货叉Pos3；仅在同一工件已实际回队列后消除快照。</summary>
    public string ResolveFrontCraneStillAtSource(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线前天车取料-打号-中转")
            return $"1号线前天车动作账本不存在: {operationId}";
        if (!_craneQueue.Any(x => x.IdentityText == action.WorkpieceIdentity))
            return "前天车来源结案拒绝：队列中未找到同一工件，不能假设工件仍在货叉Pos3。";
        _paused = true;
        FlowActionManualRegistry.Remove(operationId);
        return "1号线前天车确认工件仍在货叉Pos3：工件已在前天车队列，动作快照已结案；前端保持暂停，恢复后会按新任务重新取料。";
    }

    /// <summary>仅清已被人工移走的前天车手工任务牌；不触碰货叉/双头镗或队列。</summary>
    public string ResolveFrontCraneRemovedManually(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线前天车取料-打号-中转")
            return $"1号线前天车动作账本不存在: {operationId}";
        WorkpieceCache? wp;
        lock (_frontCraneTaskLock) wp = _frontCraneManualWorkpiece;
        if (!wp.HasValue || wp.Value.IdentityText != action.WorkpieceIdentity)
            return "前天车人工移走结案拒绝：缺少匹配的人工任务牌，未清任何状态。";
        _paused = true;
        ClearFrontCraneTask();
        FlowActionManualRegistry.Remove(operationId);
        return "1号线前天车已确认工件人工移走：仅清前天车人工任务牌和动作快照；前端保持暂停。";
    }

    /// <summary>机械手1尚未确认持件时，仅在同一工件已回总上料架缓存后结案。</summary>
    public string ResolveManipulator1StillAtSource(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线机械手1取料送叉" ||
            action.Ownership != FlowWorkpieceOwnership.ReservedAtSource)
            return "1号线机械手1来源结案拒绝：动作不是尚未确认取料的来源占用。";
        bool inCache;
        lock (_cacheLock) inCache = _cachedList.Any(x => x.IdentityText == action.WorkpieceIdentity);
        if (!inCache)
            return "1号线机械手1来源结案拒绝：总上料架缓存中未找到同一工件，不能假设仍在来源位。";
        _paused = true;
        FlowActionManualRegistry.Remove(operationId);
        return "1号线机械手1确认工件仍在总上料架：缓存已保留，动作快照已结案；前端保持暂停，恢复后按新任务重新取料。";
    }

    /// <summary>机械手1持件没有安全的自动转换；保留快照和暂停，等待专用现场处置。</summary>
    public string ResolveManipulator1OnCarrier(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线机械手1取料送叉" ||
            action.Ownership != FlowWorkpieceOwnership.OnCarrier)
            return "1号线机械手1持件结案拒绝：动作不是已由机械手确认持件。";
        _paused = true;
        return "1号线机械手1已确认仍持件：保留动作快照、在途工件和暂停；禁止自动派发或清缓存，请使用现场专用应急完成物理交接。";
    }

    /// <summary>现场确认人工移走后，只消费同一来源缓存/在途引用，不发送任何设备命令。</summary>
    public string ResolveManipulator1RemovedManually(string operationId)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot action) ||
            action.FlowScope != "1号线机械手1取料送叉")
            return "1号线机械手1人工移走结案拒绝：动作账本不存在。";
        if (!_manipulatorLock.Wait(0))
            return "机械手1仍被自动动作占用，未清缓存或快照；请等待旧动作完全退出后重试。";
        try
        {
            bool removed = false;
            lock (_cacheLock)
            {
                var retained = new List<WorkpieceCache>();
                while (_cacheQueue.TryDequeue(out WorkpieceCache queued))
                {
                    if (!removed && queued.IdentityText == action.WorkpieceIdentity) removed = true;
                    else retained.Add(queued);
                }
                foreach (WorkpieceCache queued in retained) _cacheQueue.Enqueue(queued);
                if (removed)
                {
                    int index = _cachedList.FindIndex(x => x.IdentityText == action.WorkpieceIdentity);
                    if (index >= 0) _cachedList.RemoveAt(index);
                }
            }
            lock (_wpLock)
            {
                if (_currentWp is WorkpieceCache current && current.IdentityText == action.WorkpieceIdentity)
                    _currentWp = null;
            }
            _paused = true;
            FlowActionManualRegistry.Remove(operationId);
            return removed
                ? "1号线机械手1已确认工件人工移走：已仅删除同一总上料架缓存项和动作快照；前端保持暂停。"
                : "1号线机械手1已确认工件人工移走：来源缓存中没有该工件，仅清匹配在途引用和动作快照；前端保持暂停。";
        }
        finally { _manipulatorLock.Release(); }
    }

    /// <summary>1号线设备在线状态快照（只读缓存，UI绑定不另建TCP连接）</summary>
    public sealed class Line1DeviceStatus
    {
        /// <summary>最近一次完成整轮页面状态维护的时间；仅供UI判断数据是否过期。</summary>
        public DateTime SnapshotAtUtc { get; set; }
        /// <summary>前端暂停时立即标记最后已知值；仅供后端快照/UI显示，不参与控制。</summary>
        public bool DisplaySnapshotPaused { get; set; }
        public bool Manipulator1Connected { get; set; }
        public int Manipulator1Y { get; set; }
        public bool Manipulator1Safe { get; set; }

        public bool ForkConnected { get; set; }
        public bool ForkHasPlate { get; set; }
        public bool ForkSnapshotValid { get; set; } // 最近一次货叉M900~M914是否读取成功；失败时全局分线必须保守拒绝
        public DateTime ForkSnapshotAtUtc { get; set; }
        public bool ForkAtStandby { get; set; } // 最近一次成功读取时M901待机位
        public string ForkPosition { get; set; } = "未知";
        public string ForkCommand { get; set; } = "无命令";

        public bool RackConnected { get; set; }
        public bool RackRequestPickup { get; set; }
        public int RackPlateLength { get; set; }
        public bool TransferRack1Free { get; set; }
        public bool TransferRack2Free { get; set; }
        public bool TransferRack3Free { get; set; }
        public bool M817_HasPlate { get; set; } // ST019动平衡下料架1 (192.168.2.63)
        public bool M817SnapshotValid { get; set; } // M817本轮是否读到有效PLC快照; 失败时后端必须保守拒绝放料

        public bool CraneFrontConnected { get; set; }
        public bool BoringConnected { get; set; }
        public bool BoringSnapshotValid { get; set; } // 仅供状态页面区分TCP在线与本轮信号读取成功
        public DateTime BoringSnapshotAtUtc { get; set; }
        public bool Boring_R6101 { get; set; } // 请求数据
        public bool Boring_R6102 { get; set; } // 数据下发完成
        public bool Boring_R6103 { get; set; } // 请求上料
        public bool Boring_R6104 { get; set; } // 上料完成
        public bool Boring_R6107 { get; set; } // 请求下料
        public bool Boring_R6108 { get; set; } // 下料完成
        public bool MarkerConnected { get; set; }
        public string MarkerStatusText { get; set; } = "尚未探测";

        public int CycleCount { get; set; }
        public int CacheCount { get; set; }
    }

    private readonly bool _ownsTransferRackLock;  // 是否是本引擎创建的锁(需要Dispose)
    private readonly bool _ownsManipulatorLock;    // 机械手锁是否自建(共享时不Dispose)

    /// <param name="transferRackLock">中转架互斥锁, 与后端引擎共享。为null时自建。</param>
    /// <param name="safety">前后天车共享安全标志。为null时自建。</param>
    /// <param name="manipulatorLock">机械手互斥锁(2号线共用时传入, null则自建)</param>
    public Line1FrontFlowEngine(CraneConnectionCache craneCache, ManipulatorConnectionCache manipulatorCache,
        MotionConfig cfg, Dictionary<string, MachineManagementRowVm> stationCoords, IOperationalEventReporter exceptionReporter,
        SemaphoreSlim? transferRackLock = null, SafetyFlags? safety = null,
        CenteringRackService? rackSvc = null, SemaphoreSlim? manipulatorLock = null)
    {
        _craneCache = craneCache;
        _manipulatorCache = manipulatorCache;
        _cfg = cfg;
        _stationCoords = stationCoords;
        _exceptionReporter = exceptionReporter;
        _ownsTransferRackLock = transferRackLock == null;
        _transferRackLock = transferRackLock ?? new SemaphoreSlim(1, 1);
        _ownsManipulatorLock = manipulatorLock == null;
        _manipulatorLock = manipulatorLock ?? new SemaphoreSlim(1, 1); // 共享或自建
        _safety = safety ?? new SafetyFlags();
        _rackSvc = rackSvc;
        Console.WriteLine("[Line1Front] 1号线前端引擎实例已创建");
    }

    /// <summary>
    /// 给总上料架全局分线使用的只读准入快照。
    /// 这里故意不读PLC、不等待机械手/货叉/前天车锁，只消费本引擎主循环已经刷新的状态；
    /// 真正动作前仍由原流程重新读取M800、货叉、双头镗和机械手状态。
    /// </summary>
    public FrontDispatchReadinessSnapshot GetFrontDispatchReadiness()
    {
        WorkpieceCache? current;
        lock (_wpLock)
            current = _currentWp;

        var ds = DeviceStatus;
        var input = new FrontDispatchReadinessInput(
            Line: 1,
            IsRunning: IsRunning,
            IsPaused: IsPaused,
            HasCurrentWorkpiece: current != null,
            ForkPhaseIdle: _forkPhase == ForkBoringPhase.Idle,
            CachedCount: CachedCount,
            ForkSnapshotValid: ds.ForkSnapshotValid,
            ForkSnapshotAtUtc: ds.ForkSnapshotAtUtc,
            ForkAtStandby: ds.ForkAtStandby,
            ForkHasPlate: ds.ForkHasPlate,
            BoringSnapshotValid: ds.BoringSnapshotValid,
            BoringSnapshotAtUtc: ds.BoringSnapshotAtUtc,
            BoringRequestData: ds.Boring_R6101,
            CraneQueueCount: _craneQueue.Count,
            FrontCraneActive: Volatile.Read(ref _frontCraneActive) != 0);

        return FrontDispatchReadinessEvaluator.Evaluate(input, DateTime.UtcNow, TimeSpan.FromSeconds(3));
    }

    private static void ApplyForkSnapshot(Line1DeviceStatus ds, ForkStatus fs)
    {
        ds.ForkConnected = true;
        ds.ForkHasPlate = fs.HasPlate;
        ds.ForkAtStandby = fs.AtStandbyPos;
        ds.ForkPosition = fs.CurrentPosition;
        ds.ForkCommand = fs.CommandText;
        ds.ForkSnapshotValid = true;
        ds.ForkSnapshotAtUtc = DateTime.UtcNow;
    }

    private static void MarkForkSnapshotFailed(Line1DeviceStatus ds)
    {
        ds.ForkConnected = false;
        ds.ForkSnapshotValid = false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  公开控制方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>启动引擎 — 创建后台Task运行主循环。幂等(重复调用忽略)。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            Console.WriteLine("[Line1Front] 引擎已在运行"); return;
        }
        using var logScope = EngineLogRouter.BeginScope(EngineLogRouter.Line1);
        _paused = false;
        DeviceStatus.DisplaySnapshotPaused = false;
        DeviceStatus.SnapshotAtUtc = default;
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  [Line1Front] 1号线前端流程引擎启动");
        Console.WriteLine($"  机械手1安全Y: 本线={_cfg.SkewBed.GetManipulator1SafeYForLine(1)}mm, 另一安全点={_cfg.SkewBed.GetManipulator1SafeYForLine(2)}mm  安全高度={_cfg.Grinding.SafeZHeight}mm");
        Console.WriteLine($"  zFactor1={_cfg.Grinding.ZFactor1} zFactor2={_cfg.Grinding.ZFactor2}");
        Console.WriteLine("══════════════════════════════════════════");
        _markerMonitor.Start(_engineCts.Token);
        _engineTask = Task.Run(() => EngineLoopAsync(_engineCts.Token));
    }

    /// <summary>停止引擎 — 取消CancellationToken，等待主循环退出。</summary>
    public void Stop() { Console.WriteLine("[Line1Front] ▶ 停止..."); _engineCts.Cancel(); }

    /// <summary>暂停 — 主循环跳过一次循环(可恢复)。</summary>
    public void Pause() { _paused = true; DeviceStatus.DisplaySnapshotPaused = true; Console.WriteLine("[Line1Front] ⏸ 暂停"); }

    /// <summary>恢复 — 清除暂停标志，主循环继续执行。</summary>
    public void Resume() { DeviceStatus.SnapshotAtUtc = default; DeviceStatus.DisplaySnapshotPaused = false; _paused = false; Console.WriteLine("[Line1Front] ▶ 恢复"); }

    // ═══════════════════════════════════════════════════════════════════
    //  前端在途应急（仅由人工应急中心调用；正常主循环不调用）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>普通双头镗清零是否会破坏当前软件在途阶段。</summary>
    public bool HasFrontInTransit => GetBoringClearBlockReason() != null;

    /// <summary>
    /// 返回普通双头镗清零的阻断原因。这里只读内存，不清状态、不释放锁。
    /// 前天车队列不属于双头镗当前握手，因此不作为阻断条件。
    /// </summary>
    public string? GetBoringClearBlockReason()
    {
        WorkpieceCache? wp;
        lock (_wpLock) wp = _currentWp;
        bool blocked = wp != null
                       || _forkPhase != ForkBoringPhase.Idle
                       || _forkHandshakeInProgress
                       || _manipulatorClearedFork
                       || _boringUnloadDoneSent
                       || _m912Sent
                       || _skipBoringTriggered;
        if (!blocked) return null;

        return $"1号线存在前端在途状态：当前工件={(wp?.IdentityText ?? "无")}，" +
               $"货叉阶段={_forkPhase}，握手执行中={_forkHandshakeInProgress}，机械手离叉门闩={_manipulatorClearedFork}，" +
               $"R6108已发送={_boringUnloadDoneSent}，M912已发送={_m912Sent}，" +
               $"跳镗已触发={_skipBoringTriggered}。";
    }

    /// <summary>
    /// 生成前端在途实时诊断。设备读取互相独立，某台设备读取失败不会用旧快照冒充实时状态。
    /// </summary>
    public async Task<string> GetFrontEmergencyInfoAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        WorkpieceCache? wp;
        lock (_wpLock) wp = _currentWp;
        _craneQueue.TryPeek(out var craneHead);

        sb.AppendLine("【1号线前端在途应急诊断】");
        sb.AppendLine($"引擎: 运行={IsRunning} 暂停={_paused}");
        sb.AppendLine($"当前工件 _currentWp: {(wp?.IdentityText ?? "无")}");
        sb.AppendLine($"货叉阶段 _forkPhase: {_forkPhase}");
        sb.AppendLine($"门闩: 机械手已安全离叉={_manipulatorClearedFork} 握手执行中={_forkHandshakeInProgress} R6108已发送={_boringUnloadDoneSent} M912已发送={_m912Sent} 跳镗已触发={_skipBoringTriggered}");
        sb.AppendLine($"锁: 机械手={LockText(_manipulatorLock)} 货叉分派={LockText(_forkDispatchLock)} 前天车={LockText(_craneFrontLock)} ZoneMT={LockText(_safety.MarkerTransferCollisionLock)} ZoneTS={LockText(_safety.TransferSkew1CollisionLock)}");
        sb.AppendLine($"前天车队列: 数量={_craneQueue.Count} 队头={(_craneQueue.IsEmpty ? "无" : craneHead.IdentityText)}（丢弃当前工件不会清此队列）");

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            if (_forkSvc == null || !_forkSvc.IsConnected)
                sb.AppendLine("货叉实时状态: 未连接，未使用缓存值");
            else
            {
                var fs = await _forkSvc.ReadAllStatusAsync(linked.Token);
                sb.Append("货叉实时 M900~M914:");
                for (int bit = 0; bit <= 14; bit++)
                    sb.Append($" M{900 + bit}={((fs.RawValue & (1 << bit)) != 0 ? 1 : 0)}");
                sb.AppendLine();
            }
        }
        catch (Exception ex) { sb.AppendLine($"货叉实时状态读取失败: {ex.GetType().Name} - {ex.Message}"); }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            if (_boringSvc == null || !_boringSvc.IsConnected)
                sb.AppendLine("双头镗实时状态: 未连接，未使用缓存值");
            else
            {
                var r = await _boringSvc.ReadCycleRawAsync(linked.Token);
                sb.AppendLine($"双头镗实时: R6101={r.R6101} R6102={r.R6102} R6103={r.R6103} R6104={r.R6104} R6105={r.R6105} R6106={r.R6106} R6107={r.R6107} R6108={r.R6108}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"双头镗实时状态读取失败: {ex.GetType().Name} - {ex.Message}"); }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            _manipulator1 ??= _manipulatorCache.GetOrCreateService(1);
            if (!_manipulator1.IsConnected)
                sb.AppendLine("机械手1实时状态: 未连接，未使用缓存值");
            else
            {
                var ms = await _manipulator1.ReadStatusAsync(linked.Token);
                if (ms == null) sb.AppendLine("机械手1实时状态: 读取返回空，安全状态未知");
                else
                {
                    int safeY = _cfg.SkewBed.GetManipulator1SafeYForLine(1);
                    bool ySafe = Math.Abs(ms.YPos - safeY) <= MotionConfig.SkewBedSection.Manipulator1SafeYTolerance;
                    sb.AppendLine($"机械手1实时: X={ms.XPos} Y={ms.YPos} Z={ms.ZPos} 本线安全Y={safeY} Y安全={ySafe} Z回零={ms.ZPos == 0} 持件={ms.HasRoller}");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine($"机械手1实时状态读取失败: {ex.GetType().Name} - {ex.Message}"); }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 板已放在货叉但门闩未打开时的人工续跑入口。
    /// 不移动设备、不改阶段；实时安全条件和M801全部成功后只打开本任务门闩。
    /// </summary>
    public async Task<string> EmergencyContinuePlateOnForkAsync(string? manualActionOperationId = null, CancellationToken ct = default)
    {
        _paused = true;
        await Task.Delay(600, ct); // 让已经进入的500ms主循环退出本轮，不影响正常流程。
        if (!await _frontEmergencyLock.WaitAsync(0, ct)) return "前端应急正在执行，请勿重复操作。";
        try
        {
            var (guards, guardError) = await TryAcquireFrontEmergencyGuardsAsync(ct);
            if (guards == null) return guardError;
            using (guards)
            {
                WorkpieceCache? wp;
                lock (_wpLock) wp = _currentWp;
                if (wp == null) return "继续失败：_currentWp为空，没有可继续的当前工件。";
                if (_forkPhase != ForkBoringPhase.Idle) return $"继续失败：仅允许Idle门闩卡死场景，当前阶段={_forkPhase}。";
                if (_manipulatorClearedFork) return "继续失败：机械手离叉门闩已经打开，无需重复操作。";
                if (_forkHandshakeInProgress) return "继续失败：货叉/双头镗握手仍在执行。";
                if (_forkSvc == null || !_forkSvc.IsConnected) return "继续失败：货叉未连接，无法实时确认板和位置。";
                if (_manipulator1 == null || !_manipulator1.IsConnected) return "继续失败：机械手1未连接，无法实时确认安全位置。";
                if (_rackSvc == null || !_rackSvc.IsConnected) return "继续失败：总上料架服务未连接，无法补发M801。";

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var fs = await _forkSvc.ReadAllStatusAsync(linked.Token);
                if (!fs.HasPlate || !fs.AtStandbyPos || fs.AtPos3)
                    return $"继续失败：货叉必须满足M900=1、M901=1、M902=0；当前M900={(fs.HasPlate ? 1 : 0)} M901={(fs.AtStandbyPos ? 1 : 0)} M902={(fs.AtPos3 ? 1 : 0)}。";

                var ms = await _manipulator1.ReadStatusAsync(linked.Token);
                if (ms == null) return "继续失败：机械手1实时状态读取为空，安全状态未知。";
                int safeY = _cfg.SkewBed.GetManipulator1SafeYForLine(1);
                bool ySafe = Math.Abs(ms.YPos - safeY) <= MotionConfig.SkewBedSection.Manipulator1SafeYTolerance;
                if (ms.ZPos != 0 || !ySafe)
                    return $"继续失败：机械手必须Z=0且位于1号线安全Y={safeY}±{MotionConfig.SkewBedSection.Manipulator1SafeYTolerance}；当前Y={ms.YPos} Z={ms.ZPos}。";
                if (ms.HasRoller != 0)
                    return $"继续失败：机械手持件状态HasRoller={ms.HasRoller}，不能确认板已安全留在货叉。";

                // 与正常流程保持相同安全承诺：M801成功以后才允许打开货叉门闩。
                await _rackSvc.SetPickupDoneAsync(linked.Token);
                _manipulatorClearedFork = true;
                if (!string.IsNullOrWhiteSpace(manualActionOperationId) &&
                    FlowActionManualRegistry.TryGet(manualActionOperationId, out FlowActionSnapshot action) &&
                    action.FlowScope == "1号线机械手1取料送叉" &&
                    action.Ownership == FlowWorkpieceOwnership.AtTargetPendingHandoff &&
                    action.WorkpieceIdentity == wp.Value.IdentityText)
                    FlowActionManualRegistry.Remove(manualActionOperationId);
                string result = $"1号线当前板已允许继续：{wp.Value.IdentityText}。已实时确认货叉有板且在待机位、机械手Y={ms.YPos}/Z=0且不持件，并成功补发M801；仅打开货叉门闩，阶段仍为Idle。线体保持暂停，请检查后手动恢复。";
                Console.WriteLine($"[Line1Front] [前端应急] {result}");
                return result;
            }
        }
        catch (Exception ex)
        {
            string result = $"继续失败：{ex.GetType().Name} - {ex.Message}。门闩和业务阶段保持不变，线体保持暂停。";
            Console.WriteLine($"[Line1Front] [前端应急] {result}");
            return result;
        }
        finally { _frontEmergencyLock.Release(); }
    }

    /// <summary>
    /// 人工确认实物已处理后的当前工件作废入口。
    /// 第一级先清设备输出再清软件；设备失败时保持软件状态，由UI二次确认后才能仅清软件。
    /// </summary>
    public async Task<string> EmergencyDiscardCurrentAsync(bool skipDeviceClear, CancellationToken ct = default)
    {
        _paused = true;
        await Task.Delay(600, ct);
        if (!await _frontEmergencyLock.WaitAsync(0, ct)) return "前端应急正在执行，请勿重复操作。";
        try
        {
            var (guards, guardError) = await TryAcquireFrontEmergencyGuardsAsync(ct);
            if (guards == null) return guardError;
            using (guards)
            {
                if (_forkHandshakeInProgress) return "丢弃失败：货叉/双头镗握手仍在执行，软件状态未修改。";
                WorkpieceCache? discarded;
                lock (_wpLock) discarded = _currentWp;

                if (!skipDeviceClear)
                {
                    try
                    {
                        if (_forkSvc == null || !_forkSvc.IsConnected)
                            throw new InvalidOperationException("货叉未连接，无法清M911~M914");
                        if (_boringSvc == null || !_boringSvc.IsConnected)
                            throw new InvalidOperationException("双头镗未连接，无法清R6108/R6104/R6102");
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                        await _forkSvc.ClearMotionCommandsAsync(linked.Token);
                        await _boringSvc.ClearEmergencyOutputsAsync(linked.Token);
                    }
                    catch (Exception ex)
                    {
                        string failed = $"设备侧清零失败: {ex.GetType().Name} - {ex.Message}。软件当前工件和阶段未清，线体保持暂停。设备写入可能部分完成，请核对M911~M914及R6102/R6104/R6108后再决定是否仅清软件。";
                        Console.WriteLine($"[Line1Front] [前端应急] {failed}");
                        return failed;
                    }
                }

                lock (_wpLock) _currentWp = null;
                _forkPhase = ForkBoringPhase.Idle;
                _manipulatorClearedFork = false;
                _forkHandshakeInProgress = false;
                _boringUnloadDoneSent = false;
                _m912Sent = false;
                _skipBoringTriggered = false;

                string mode = skipDeviceClear
                    ? "仅清软件；设备信号未由本次操作确认清除"
                    : "已清M911~M914及R6108/R6104/R6102，并清软件状态";
                string result = $"1号线前端当前工件已丢弃并回Idle：{(discarded?.IdentityText ?? "原本无_currentWp")}。{mode}。缓存FIFO和前天车队列未清，线体保持暂停，请检查后手动恢复。";
                Console.WriteLine($"[Line1Front] [前端应急] {result}");
                return result;
            }
        }
        catch (Exception ex)
        {
            string result = $"丢弃失败：{ex.GetType().Name} - {ex.Message}。软件状态未修改，线体保持暂停。";
            Console.WriteLine($"[Line1Front] [前端应急] {result}");
            return result;
        }
        finally { _frontEmergencyLock.Release(); }
    }

    private static string LockText(SemaphoreSlim semaphore) => semaphore.CurrentCount > 0 ? "空闲" : "占用";

    /// <summary>
    /// 以零等待方式实际取得三个关键锁，避免“检查CurrentCount后旧动作又进入”的竞态。
    /// 取得失败立即释放本方法已经取得的锁；绝不强行释放别的动作持有的锁。
    /// </summary>
    private async Task<(FrontEmergencyGuardLease? Lease, string Error)> TryAcquireFrontEmergencyGuardsAsync(CancellationToken ct)
    {
        bool manipulator = false, fork = false, crane = false;
        try
        {
            manipulator = await _manipulatorLock.WaitAsync(0, ct);
            if (!manipulator) return (null, "应急拒绝：机械手动作锁仍被占用，旧动作可能尚未退出。线体保持暂停。需要等待动作退出后刷新诊断。");
            fork = await _forkDispatchLock.WaitAsync(0, ct);
            if (!fork) return (null, "应急拒绝：货叉分派锁仍被占用，旧动作可能尚未退出。线体保持暂停。需要等待动作退出后刷新诊断。");
            crane = await _craneFrontLock.WaitAsync(0, ct);
            if (!crane) return (null, "应急拒绝：前天车锁仍被占用，旧动作可能尚未退出。线体保持暂停。需要等待动作退出后刷新诊断。");
            return (new FrontEmergencyGuardLease(_manipulatorLock, _forkDispatchLock, _craneFrontLock), "");
        }
        finally
        {
            if (!crane)
            {
                if (fork) _forkDispatchLock.Release();
                if (manipulator) _manipulatorLock.Release();
            }
        }
    }

    private sealed class FrontEmergencyGuardLease : IDisposable
    {
        private readonly SemaphoreSlim _manipulator;
        private readonly SemaphoreSlim _fork;
        private readonly SemaphoreSlim _crane;
        private int _released;

        public FrontEmergencyGuardLease(SemaphoreSlim manipulator, SemaphoreSlim fork, SemaphoreSlim crane)
        { _manipulator = manipulator; _fork = fork; _crane = crane; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _crane.Release();
            _fork.Release();
            _manipulator.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  工件缓存方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>工件入队 — 加入 FIFO 队列末尾，主循环出队时按顺序处理。</summary>
    public void EnqueueWorkpiece(WorkpieceCache wp)
    { lock (_cacheLock) { _cacheQueue.Enqueue(wp); _cachedList.Add(wp); } Console.WriteLine($"[Line1Front] 📥 入缓存 {wp.IdentityText} d={wp.Diameter} L={wp.Length} 队列={CachedCount}"); }

    /// <summary>清空缓存 — 丢弃所有未处理工件。</summary>
    public void ClearCache() { lock (_cacheLock) { while (_cacheQueue.TryDequeue(out _)) { } _cachedList.Clear(); } }

    /// <summary>出队 — 从 FIFO 队列头部取出一个工件。成功返回true，队列空返回false。</summary>
    private bool TryDequeueCache(out WorkpieceCache wp)
    { lock (_cacheLock) { if (_cacheQueue.TryDequeue(out wp)) { _cachedList.Remove(wp); return true; } wp = default; return false; } }

    /// <summary>
    /// 机械手1取板/送叉严重失败时，清匹配的_currentWp及前端缓存中的完整身份残留，不再回队重试。
    /// 只按本板号和序号删项，保护下一板和另一条线；队列重建与并发入队共用_cacheLock。
    /// 本方法不发PLC命令、不自动退磁或回位；设备输出和前端阶段由调用方检查周期归属后处理。
    /// </summary>
    private void PauseAndDiscardManipulatorWorkpiece(WorkpieceCache wp)
    {
        _paused = true;
        lock (_wpLock)
        {
            if (_currentWp is WorkpieceCache current &&
                (WorkpieceCacheIdentity.Matches(current, wp) || current.Equals(wp)))
                _currentWp = null;
        }
        lock (_cacheLock)
        {
            var retained = new List<WorkpieceCache>();
            while (_cacheQueue.TryDequeue(out WorkpieceCache queued))
            {
                if (!WorkpieceCacheIdentity.Matches(queued, wp)) retained.Add(queued);
            }
            foreach (WorkpieceCache queued in retained) _cacheQueue.Enqueue(queued);
            _cachedList.RemoveAll(queued => WorkpieceCacheIdentity.Matches(queued, wp));
        }
        Console.WriteLine($"[Line1Front] [作废] 机械手1 {wp.IdentityText}：本块软件任务已作废，当前工件及匹配来源缓存已清，不回缓存；引擎保持暂停。请人工移走本板并确认机械手/货叉安全后再恢复。");
    }

    private const string ManipulatorAbortInstruction =
        "本块软件任务已作废，不回缓存。请人工移走本板，确认机械手/货叉安全后再恢复；软件清理不代表已退磁或已回安全位置。";

    /// <summary>
    /// 独立刷新总上料架/中转架状态快照。
    /// 后端上料步骤⑤只看 DeviceStatus 中的 TransferRackXFree, 所以这个刷新不能被
    /// _currentWp 或 _manipulatorLock 限制；否则前端有在途工件时, 后端会一直看旧快照并认为中转架无版。
    /// </summary>
    private async Task RefreshRackSnapshotAsync(Line1DeviceStatus ds, CancellationToken ct)
    {
        if (_rackSvc == null)
        {
            ds.RackConnected = false;
            return;
        }

        if (!_rackSvc.IsConnected)
        {
            try
            {
                // 状态快照刷新不能依赖机械手取料分支；断线时在这里通过共享服务轻量重连。
                using var reconnectTimeoutCts = new CancellationTokenSource(5000);
                using var reconnectLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnectTimeoutCts.Token);
                await _rackSvc.ConnectAsync(reconnectLinked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ds.RackConnected = false;
                if (_cycleCount % 10 == 1) Console.WriteLine("[Line1Front] ⚠ 独立刷新中转架重连超时(5s)");
                return;
            }
            catch (Exception ex)
            {
                ds.RackConnected = false;
                if (_cycleCount % 10 == 1) Console.WriteLine($"[Line1Front] ⚠ 独立刷新中转架重连失败: {ex.Message}");
                return;
            }
        }

        try
        {
            // MC设备可能TCP已连但帧无响应, 保持原先5s保护, 防止状态刷新卡死整轮循环。
            using var timeoutCts = new CancellationTokenSource(5000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var rs = await _rackSvc.ReadAllStatusAsync(linked.Token);

            ds.RackConnected = true;
            ds.RackRequestPickup = rs.RequestPickup;
            ds.RackPlateLength = rs.PlateLength;
            // 1号线中转架: ST105/M811, ST101/M812, ST106/M813。
            ds.TransferRack1Free = !rs.Station1HasPlate;
            ds.TransferRack2Free = !rs.Station2HasPlate;
            ds.TransferRack3Free = !rs.Station3HasPlate;

            // M817在第二个字bit1。该信号直接决定长板能否下料到ST019, 读失败时不能沿用旧“空闲”。
            try
            {
                int m816 = rs.RawM816Word;
                ds.M817_HasPlate = (m816 & (1 << 1)) != 0;
                ds.M817SnapshotValid = true;
            }
            catch (Exception ex)
            {
                ds.M817SnapshotValid = false;
                if (_cycleCount % 10 == 1) Console.WriteLine($"[Line1Front] ⚠ M817快照读取失败: {ex.Message} → 后端禁止向M817放料");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ds.RackConnected = false;
            ds.M817SnapshotValid = false;
            if (_cycleCount % 10 == 1) Console.WriteLine("[Line1Front] ⚠ 独立刷新中转架状态超时(5s)");
        }
        catch (Exception ex)
        {
            ds.RackConnected = false;
            ds.M817SnapshotValid = false;
            if (_cycleCount % 10 == 1) Console.WriteLine($"[Line1Front] ⚠ 独立刷新中转架状态失败: {ex.Message}");
        }
    }

    private void ScheduleDeviceReconnects(CancellationToken ct)
    {
        if (_cycleCount % 20 != 1) return;

        if ((_forkSvc == null || !_forkSvc.IsConnected) &&
            System.Threading.Interlocked.CompareExchange(ref _forkReconnectInProgress, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!TryGetStationIp("ST711", out string fIp, out int fPort))
                    {
                        Console.WriteLine("[Line1Front] 货叉后台重连跳过: 未找到ST711 IP");
                        return;
                    }

                    using var timeoutCts = new CancellationTokenSource(5000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    _forkSvc ??= new ForkService("一号线货叉", fIp, fPort > 0 ? fPort : 9000);
                    Console.WriteLine($"[Line1Front] 货叉后台重连 {fIp}:{(fPort > 0 ? fPort : 9000)}...");
                    await _forkSvc.ConnectAsync(linked.Token);
                    Console.WriteLine("[Line1Front] 货叉后台重连 ✓");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Console.WriteLine("[Line1Front] 货叉后台重连超时(5s)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Line1Front] 货叉后台重连失败: {ex.Message}");
                }
                finally
                {
                    System.Threading.Volatile.Write(ref _forkReconnectInProgress, 0);
                }
            });
        }

        if ((_boringSvc == null || !_boringSvc.IsConnected) &&
            System.Threading.Interlocked.CompareExchange(ref _boringReconnectInProgress, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!TryGetStationIp("ST103", out string bIp, out _))
                    {
                        Console.WriteLine("[Line1Front] 双头镗后台重连跳过: 未找到ST103 IP");
                        return;
                    }

                    using var timeoutCts = new CancellationTokenSource(5000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    _boringSvc ??= new BoringModbusService("1号线双头镗", bIp);
                    Console.WriteLine($"[Line1Front] 双头镗Modbus后台重连 {bIp}:502...");
                    await _boringSvc.ConnectAsync(linked.Token);
                    Console.WriteLine("[Line1Front] 双头镗后台重连 ✓");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Console.WriteLine("[Line1Front] 双头镗后台重连超时(5s)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Line1Front] 双头镗后台重连失败: {ex.Message}");
                }
                finally
                {
                    System.Threading.Volatile.Write(ref _boringReconnectInProgress, 0);
                }
            });
        }
    }

    private void PauseForkBoringHandshake(string reason, Exception ex)
    {
        // 这是货叉独立推进双头镗时的异常入口，不是机械手1取板动作的作废catch。
        // 现行策略保留本板及中间阶段，不能仅凭这里的暂停弹窗认定_currentWp或PLC已清零。
        // 双头镗握手异常不能回Idle: CNC/货叉/工件可能已经处于中间状态。
        // 保持当前_forkPhase并暂停, 让人工按现场确认后再恢复, 避免下一块板进入同一区域。
        _paused = true;
        string message = $"1号线货叉/双头镗握手异常：{reason}：{ex.Message}。保持阶段={_forkPhase}，引擎已暂停，请人工确认货叉、双头镗和工件位置。";
        Console.WriteLine($"[Line1Front] [货叉] {message}");
        OnSafetyAlarm?.Invoke(message);
    }

    /// <summary>
    /// 机械手1允许取总上料架/送货叉前的货叉硬条件。
    /// 必须同时满足: ①货叉服务已连接 ②状态读取成功 ③货叉在待机位 ④货叉无板。
    /// 这里只做安全确认和UI快照刷新, 不改变原来的M800/缓存/双头镗/在途等业务条件。
    /// </summary>
    private async Task<bool> ConfirmForkReadyForManipulatorAsync(string context, CancellationToken ct, Line1DeviceStatus? ds = null)
    {
        if (_forkSvc == null || !_forkSvc.IsConnected)
        {
            if (ds != null) MarkForkSnapshotFailed(ds);
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[Line1Front] [{context}] ⚠ 货叉未连接 → 禁止机械手1取料/送叉");
            return false;
        }

        try
        {
            var fs = await _forkSvc.ReadAllStatusAsync(ct);
            if (ds != null) ApplyForkSnapshot(ds, fs);

            if (!fs.AtStandbyPos)
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line1Front] [{context}] ⚠ 货叉不在待机位(当前{fs.CurrentPosition}) → 禁止机械手1取料/送叉");
                return false;
            }

            if (fs.HasPlate)
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line1Front] [{context}] ⚠ 货叉已有板 → 禁止机械手1取料/送叉");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (ds != null) MarkForkSnapshotFailed(ds);
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[Line1Front] [{context}] ⚠ 读取货叉状态失败 → 禁止机械手1取料/送叉: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 机械手1派发前的通信硬检查。
    /// 这里不改变原来的取料业务条件, 只保证“读不到机械手状态”不会先出队、设置_currentWp、再进入动作异常。
    /// 通信未知必须保守拒绝本轮派发, 下轮继续重连/重试。
    /// </summary>
    private async Task<bool> EnsureManipulator1ReadyForPickupAsync(string context, CancellationToken ct)
    {
        try
        {
            _manipulator1 ??= _manipulatorCache.GetOrCreateService(1);

            if (!_manipulator1.IsConnected)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line1Front] [{context}] 机械手1未连接, 尝试重连后再判断是否允许取料...");
                await _manipulator1.ConnectAsync(linked.Token);
            }

            var status = await _manipulator1.ReadStatusAsync(ct);
            if (status == null)
            {
                // CraneService读失败返回null时, 底层连接通常已被置断开。
                // 这里再主动断开一次, 防止半连接导致后续轮次继续读旧socket。
                try { await _manipulator1.DisconnectAsync(); } catch { }
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line1Front] [{context}] 机械手1状态读取失败(status=null) → 本轮不出队、不启动机械手");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[Line1Front] [{context}] 机械手1通信不可用 → 本轮不出队、不启动机械手: {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  主循环 EngineLoopAsync — 500ms 一轮
    // 一号线前半段启动
    //  每轮 4 步:
    //    ① [_manipulatorLock] 读总上料架 → M800=1+缓存有料+无在途 → 出队 → 机械手取料送叉
    //    ② [_manipulatorLock] 读货叉 → 待机位有版 → 分派(Pos2双头镗 or Pos3天车)
    //    ③ [无锁]          读货叉 → Pos3有版 → 前天车S4~S6
    //    ④ [无锁]          无条件刷新所有设备状态到 DeviceStatus
    // ═══════════════════════════════════════════════════════════════════
    private async Task EngineLoopAsync(CancellationToken ct)
    {
        // ── 启动时先连接所有设备 机械手 货叉 总上料机啊 双头镗 ──
        try
        {
            await InitServicesAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line1Front] ⚠ 设备初始化异常(引擎继续运行): {ex.Message}");
        }
        // ── 主循环: 每 500ms 执行一轮 ──
        while (!ct.IsCancellationRequested)
        {
            // 暂停状态: 跳过本轮，500ms后重试
            if (_paused)
            {
                await Task.Delay(500, ct); continue;
            }
            try
            {
                long cycleIoStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                // 本轮普通观察只读一次。安全确认仍由专用方法实时读取，不能复用这里的快照。
                Task<ForkStatus>? forkCycleReadTask = null;
                Task<BoringModbusStatusSnapshot>? boringCycleReadTask = null;

                //循环计数
                _cycleCount++;
                // 获取 DeviceStatus 快照引用 (后续所有更新直接写入此对象)
                var ds = DeviceStatus;
                ds.CycleCount = _cycleCount;
                ds.CacheCount = CachedCount;
                // 货叉/双头镗如果断线, 不能只在启动时连一次后放弃。
                // 后台重连不改变任何取料条件；连不上仍按原逻辑保守拒绝动作。
                ScheduleDeviceReconnects(ct);
                // 中转架状态供后端步骤⑤选料使用, 必须每轮独立刷新, 不能绑在机械手取料条件里。
                await RefreshRackSnapshotAsync(ds, ct);
                // 每 10 轮输出一次状态摘要 (减少日志量)
                if (_cycleCount % 10 == 1)
                    Console.WriteLine(
                        $"[Line1Front] 轮次{_cycleCount} 缓存={CachedCount} 在途={(_currentWp != null ? 1 : 0)} 货叉={_forkPhase} 暂停={_paused}");

                // ═══════════════════════════════════════════════════════════
                //  ① 机械手取料: M800=1 + 缓存有料 + 无在途工件 → 出队 → 取料送叉
                //     _currentWp!=null时直接跳过(有工件在途), 不抢锁不阻塞步骤②
                // ═══════════════════════════════════════════════════════════
                WorkpieceCache? inTransit; lock (_wpLock) { inTransit = _currentWp; }
                bool boringIdleForPickup = false;
                if (inTransit == null && CachedCount > 0 && _forkPhase == ForkBoringPhase.Idle)
                {
                    if (_boringSvc?.IsConnected == true)
                    {
                        try
                        {
                            // SkipBoring 也必须等双头镗在线且 R6101=1，防止设备节拍冲突。
                            boringIdleForPickup = (await (boringCycleReadTask ??= _boringSvc.ReadAllSignalsAsync(ct))).RequestData;
                        }
                        catch (Exception ex)
                        {
                            if (_cycleCount % 10 == 1)
                                Console.WriteLine($"[Line1Front] ⚠ 锁外读取双头镗空闲状态失败 → 本轮不抢机械手锁: {ex.Message}");
                        }
                    }
                    else if (_cycleCount % 10 == 1)
                    {
                        Console.WriteLine("[Line1Front] ⚠ 双头镗未连接 → 本轮不抢机械手锁");
                    }

                    if (!boringIdleForPickup && _boringSvc?.IsConnected == true && _cycleCount % 10 == 1)
                        Console.WriteLine("[Line1Front] 双头镗忙或信号无效 → 本轮不抢机械手锁");
                }

                // 没有本线缓存时不参与机械手1竞争；双头镗通信也已在锁外完成。
                if (inTransit == null && CachedCount > 0 && boringIdleForPickup
                    && await _manipulatorLock.WaitAsync(0, ct))
                {
                    bool released = false; // true=锁已转交给 ProcessManipulatorPickupAsync
                    try
                    {
                        Console.WriteLine(
                            $"[Line1Front] 读总上料架前: SvcNull={_rackSvc == null} IsConnected={_rackSvc?.IsConnected}");
                        // 总上料架已连接 → 读 M800~M816 + D100
                        if (_rackSvc != null && _rackSvc.IsConnected)
                        {
                            CenteringRackStatus rs;
                            // MC 设备可能连上后无响应，加 5s 超时防止挂死整个引擎
                            using (var timeoutCts = new CancellationTokenSource(5000))
                            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                            {
                                try
                                {
                                    //一次性读取全部状态：M800~M816 位信号 + D100 板长数值。
                                    rs = await _rackSvc.ReadAllStatusAsync(linked.Token);
                                }
                                // 5s 超时: MC TCP连通但帧被拒/无响应 → 释放锁重新尝试
                                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                                {
                                    Console.WriteLine("[Line1Front] ⚠ 读 M800 超时(5s)，MC设备TCP通但无响应");
                                    ds.RackConnected = false;
                                    _manipulatorLock.Release();
                                    continue;
                                }
                                // 引擎取消: 正常退出
                                catch (OperationCanceledException)
                                {
                                    Console.WriteLine("[Line1Front] 引擎已取消");
                                    _manipulatorLock.Release();
                                    return;
                                }
                                // 其它异常时底层MC客户端会关闭半开socket。
                                // 这里仍持有两线共享机械手锁，禁止在锁内断开/重连共享MC63；
                                // 下一轮由锁外RefreshRackSnapshotAsync通过McConnectionCache单飞重连。
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Line1Front] ⚠ 读 M800 异常: {ex.GetType().Name} - {ex.Message} → 释放机械手锁,等待锁外重连");
                                    ds.RackConnected = false;
                                    _manipulatorLock.Release();
                                    continue;
                                }
                            }

                            // ── 刷新 UI 快照 ──
                            ds.RackConnected = true;
                            ds.RackRequestPickup = rs.RequestPickup;
                            ds.RackPlateLength = rs.PlateLength;
                            ds.TransferRack1Free = !rs.Station1HasPlate;
                            ds.TransferRack2Free = !rs.Station2HasPlate;
                            ds.TransferRack3Free = !rs.Station3HasPlate;
                            // 读M817(动平衡下料架1): M800起2字,第2字bit1。
                            // 该快照会被后端用来判断ST019是否允许放料, 读失败时必须显式标无效。
                            try
                            {
                                int m816 = rs.RawM816Word;
                                ds.M817_HasPlate = (m816 & (1 << 1)) != 0;
                                ds.M817SnapshotValid = true;
                            }
                            catch (Exception ex)
                            {
                                ds.M817SnapshotValid = false;
                                Console.WriteLine($"[Line1Front] ⚠ M817快照读取失败: {ex.Message} → 后端禁止向M817放料");
                            }

                            Console.WriteLine(
                                $"[Line1Front] M800={rs.RequestPickup} 板长={rs.PlateLength}mm 缓存={CachedCount} 货叉={_forkPhase} 中转1={(rs.Station1HasPlate ? "有" : "空")} 2={(rs.Station2HasPlate ? "有" : "空")} 3={(rs.Station3HasPlate ? "有" : "空")} {(rs.RequestPickup && CachedCount > 0 ? "→ 满足！" : "→ 等M800=1+缓存有料")}");

                            // 双头镗空闲已在共享机械手锁外确认；拿锁后只复核物理架、机械手和货叉条件。
                            if (rs.RequestPickup && inTransit == null && CachedCount > 0)
                            {
                                // 机械手从总上料架取料前, 必须先确认货叉可接料。
                                // 货叉未连接/读不到/不在待机位/已有板时, 不出队、不设置_currentWp、不启动机械手。
                                if (await EnsureManipulator1ReadyForPickupAsync("取料派发前", ct)
                                    && await ConfirmForkReadyForManipulatorAsync("取料派发前", ct, ds)
                                    && TryDequeueCache(out var wp))
                                {
                                    lock (_wpLock) { _currentWp = wp; }
                                    // 新工件必须重新完成“放板→Z安全→Y安全→M801”才能放行货叉，禁止沿用上一任务门闩。
                                    _manipulatorClearedFork = false;
                                    _forkHandshakeInProgress = false;
                                    _boringUnloadDoneSent = false;
                                    _m912Sent = false;
                                    _skipBoringTriggered = false;
                                    Console.WriteLine($"[Line1Front] [DEBUG] _currentWp SET {wp.IdentityText} d={wp.Diameter} L={wp.Length} SkipBoring={wp.SkipBoring}");
                                    Console.WriteLine($"[Line1Front] M800=1 缓存有料 {wp.IdentityText} d={wp.Diameter} L={wp.Length}{(wp.SkipBoring ? " ⚡SkipBoring" : "")} → 启动机械手1 在途=1");
                                    //机械手1开始执行流程
                                    _ = ProcessManipulatorPickupAsync(wp, ct);
                                    released = true;
                                }
                            }
                        }
                        else
                        {
                            if (_cycleCount % 10 == 1)
                                Console.WriteLine($"[Line1Front] ⚠ 总上料架未连接 IsConnected={_rackSvc?.IsConnected}");
                            ds.RackConnected = false;
                        }
                    }
                    catch
                    {
                        ds.RackConnected = false;
                    }

                    // released=false → ProcessManipulatorPickupAsync 未触发 → 主循环自己释放锁
                    if (!released) _manipulatorLock.Release();
                }

                // ═══════════════════════════════════════════════════════════
                //  ② 货叉分派: 待机位有版 → 统一送Pos2双头镗 (所有工件)
                //     M912=1→货叉送料到双头镗并自动回待机
                //     获取 _forkDispatchLock (非阻塞, 本线独立锁, 不与机械手锁竞争)
                // ═══════════════════════════════════════════════════════════
                // TcpClient.Connected不可靠, MC能通但属性可能false → 不检查IsConnected, ReadAllStatusAsync真断了会抛异常
                if (_forkSvc != null && await _forkDispatchLock.WaitAsync(0, ct))
                {
                    try
                    {
                        var fs = await (forkCycleReadTask ??= _forkSvc.ReadAllStatusAsync(ct));
                        ApplyForkSnapshot(ds, fs);

                        // ── 条件: 待机位 + 有版 + 有在途工件 + 货叉空闲 + 机械手已离开叉区 ──
                        //     必须确认机械手Y回到安全位后才分派, 防机械手还在叉区时货叉移动碰撞
                        if (fs.HasPlate && fs.AtStandbyPos && _currentWp != null
                            && _forkPhase == ForkBoringPhase.Idle)
                        {
                            // 门闩是“本任务已经完成退磁、Z=0、到达1号线释放安全点并成功写M801”的持久证据。
                            // 放行后机械手会立即从11500继续回1000，不能再要求“当前Y仍等于11500”，
                            // 否则主循环可能错过极短的到位窗口，导致货叉一直不启动。
                            bool manipSafe = _manipulatorClearedFork;
                            if (!manipSafe)
                            {
                                if (_cycleCount % 10 == 1)
                                    Console.WriteLine("[Line1Front] [货叉分派] ⚠ 机械手1尚未完成Z安全/首次安全Y/M801，等待门闩...");
                            }
                            //机械手在安全位置
                            else
                            {
                                Console.WriteLine("[Line1Front] [货叉分派] 机械手放行门闩=ON，允许货叉与机械手回1000并行");
                                var wp = _currentWp.Value;
                                if (wp.SkipBoring)
                                {
                                    Console.WriteLine($"[Line1Front] [货叉] ⚡ SkipBoring! L={wp.Length}mm → 跳过双头镗, 货叉直接伸Pos3");
                                    //更改货叉状态
                                    _forkPhase = ForkBoringPhase.SkipBoring_GoPos3;
                                }
                                else
                                {
                                    Console.WriteLine($"[Line1Front] [货叉] 待机位有版 L={wp.Length}mm → 先写参数等R6103再写M912送料");
                                    //更改货叉状态  方便进行下一步
                                    _forkPhase = ForkBoringPhase.Load_WriteParams;
                                }
                            }
                        }
                        else if (_cycleCount % 10 == 1)
                        {
                            // 诊断: 条件不满足时打印各字段
                            Console.WriteLine($"[Line1Front] [货叉分派] 条件不满足 HasPlate={fs.HasPlate} Standby={fs.AtStandbyPos} 在途={_currentWp != null} 机械手放行={_manipulatorClearedFork} 阶段={_forkPhase}");
                        }
                    }
                    catch (Exception ex)
                    {
                        MarkForkSnapshotFailed(ds);
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[Line1Front] [货叉分派] 读取/判断异常: {ex.GetType().Name} - {ex.Message}");
                    }
                    finally
                    {
                        _forkDispatchLock.Release();
                    }
                }
                else if (_cycleCount % 10 == 1)
                {
                    Console.WriteLine($"[Line1Front] [货叉分派] 未进锁 ForkSvcNull={_forkSvc == null} IsConnected={_forkSvc?.IsConnected} LockCnt={_manipulatorLock.CurrentCount}");
                }
                
                // ═══════════════════════════════════════════════════════════
                //  ③ 货叉-双头镗状态机 + 天车触发
                //     M912送料回待机→写R6104→等R6107加工→M913取料到Pos3→R6108→天车
                // ═══════════════════════════════════════════════════════════
                if (_currentWp != null && _forkSvc != null) // TcpClient.Connected不可靠, 不检查IsConnected
                {
                    try
                    {
                        var fs = await (forkCycleReadTask ??= _forkSvc.ReadAllStatusAsync(ct));
                        ApplyForkSnapshot(ds, fs);
                        if (ShouldLogForkState(fs))
                        {
                            Console.WriteLine(
                                $"[Line1Front] [货叉状态机] 在途=1 HasPlate={fs.HasPlate} AtPos3={fs.AtPos3} AtStandby={fs.AtStandbyPos} Cmd={fs.CommandText} 阶段={_forkPhase}");
                        }

                        switch (_forkPhase)
                        {
                            // ══════════ 上料阶段 ══════════

                            // ① Load_WriteParams: 货叉在待机位 → 等R6101→写参数→R6102=1
                            case ForkBoringPhase.Load_WriteParams:
                                if (_boringSvc != null && _boringSvc.IsConnected && !_forkHandshakeInProgress)
                                {
                                    BoringModbusStatusSnapshot snapshot;
                                    try
                                    {
                                        snapshot = await (boringCycleReadTask ??= _boringSvc.ReadAllSignalsAsync(ct));
                                    }
                                    catch (Exception ex)
                                    {
                                        // 被动等信号期间的偶发读失败不改变物理状态；保留阶段，让下一轮后台重连后继续读取。
                                        ds.BoringSnapshotValid = false;
                                        ds.BoringConnected = _boringSvc.IsConnected;
                                        Console.WriteLine($"[Line1Front] [货叉] 等待R6101读取异常: {ex.GetType().Name} - {ex.Message} → 保持阶段等待重连");
                                        break;
                                    }

                                    if (!snapshot.RequestData)
                                    {
                                        if (_cycleCount % 10 == 1)
                                            Console.WriteLine("[Line1Front] [货叉] ① 等R6101=1请求数据（无业务超时）...");
                                        break;
                                    }

                                    _forkHandshakeInProgress = true;
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var wp = _currentWp!.Value;
                                            double boringD = wp.Diameter + _cfg.GetDiameterOffset("boring1");
                                            Console.WriteLine(
                                                $"[Line1Front] [货叉] ② R6101=1，写Modbus参数 R2041={wp.Length} R2043={boringD}*100 R2044={wp.LeftPlugThickness}*100 R2045={wp.RightPlugThickness}*100 R2046={wp.InnerTaper}*100 R2047={wp.BoreType}*100 R2048={wp.CornerSize}*100");
                                            await _boringSvc.SendMachiningParamsAsync(wp.Length, boringD,
                                                wp.LeftPlugThickness, wp.RightPlugThickness, wp.InnerTaper,
                                                wp.BoreType, wp.CornerSize, ct);
                                            await _boringSvc.SetDataSentDoneAsync(ct);
                                            Console.WriteLine("[Line1Front] [货叉] ③ R6102=1 数据下发完成 ✓ → 持续等R6103=1（无业务超时）");
                                            _forkPhase = ForkBoringPhase.WaitingRequestLoad;
                                        }
                                        catch (Exception ex)
                                        {
                                            PauseForkBoringHandshake("上料握手异常", ex);
                                        }
                                        finally
                                        {
                                            _forkHandshakeInProgress = false;
                                        }
                                    }, ct);
                                }
                                break;

                            // ② WaitingRequestLoad: 参数已下发，持续等R6103；信号未到不暂停引擎。
                            case ForkBoringPhase.WaitingRequestLoad:
                                if (_boringSvc != null && _boringSvc.IsConnected && !_forkHandshakeInProgress)
                                {
                                    try
                                    {
                                        var snapshot = await (boringCycleReadTask ??= _boringSvc.ReadAllSignalsAsync(ct));
                                        if (snapshot.RequestLoad)
                                        {
                                            Console.WriteLine("[Line1Front] [货叉] ④ R6103=1 CNC请求上料 → 准备写M912送料并回待机");
                                            _forkPhase = ForkBoringPhase.GoingToBoring;
                                        }
                                        else if (_cycleCount % 10 == 1)
                                        {
                                            Console.WriteLine("[Line1Front] [货叉] 等R6103=1请求上料（无业务超时）...");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // R6102已经写出，但读取R6103失败不代表设备执行状态发生变化；保留阶段等待通信恢复。
                                        ds.BoringSnapshotValid = false;
                                        ds.BoringConnected = _boringSvc.IsConnected;
                                        Console.WriteLine($"[Line1Front] [货叉] 等待R6103读取异常: {ex.GetType().Name} - {ex.Message} → 保持阶段等待重连");
                                    }
                                }
                                break;

                            // ③ GoingToBoring: R6103=1后 → 写M912送料并自动回待机 → 等M901=1且M900=0
                            case ForkBoringPhase.GoingToBoring:
                                if (!_forkHandshakeInProgress)
                                {
                                    //其实这个if写的有问题 但是能跑 还是不改了   因为其实分派条件就要求 fs.HasPlate && fs.AtStandbyPos    时间 9.22
                                    if (!(fs.AtStandbyPos && !fs.HasPlate))
                                    {
                                        if (!_m912Sent)
                                        {
                                            Console.WriteLine("[Line1Front] [货叉] ⑤ M912=1 送料到双头镗并自动回待机");
                                            //写货叉M912=1
                                            await _forkSvc.GoFeedToBoringAsync(ct);
                                            forkCycleReadTask = null; // 命令写入后不得继续使用写入前状态
                                            _m912Sent = true;
                                        }
                                    }
                                    //货叉在待机位且无板
                                    else
                                    {
                                        Console.WriteLine("[Line1Front] [货叉] 送料完成: 待机位且无板 → R6104=1 上料完成");
                                        _m912Sent = false;
                                        _forkPhase = ForkBoringPhase.Load_WriteDone;
                                    }
                                }
                                break;

                            // ③ Load_WriteDone: M912自动回待机且无板后 → R6104=1, 双头镗开始加工
                            case ForkBoringPhase.Load_WriteDone:
                                if (_boringSvc != null && _boringSvc.IsConnected && !_forkHandshakeInProgress)
                                {
                                    _forkHandshakeInProgress = true;
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await _boringSvc!.SetLoadDoneAsync(ct);
                                            Console.WriteLine($"[Line1Front] [货叉] R6104=1 上料完成 ✓ → 等加工... {_currentWp?.IdentityText}");
                                            _currentWp?.ReportStage("1号线 双头镗加工中");
                                            _forkPhase = ForkBoringPhase.WaitingMachine;
                                        }
                                        catch (Exception ex) { PauseForkBoringHandshake("上料完成写入异常", ex); }
                                        finally { _forkHandshakeInProgress = false; }
                                    }, ct);
                                }
                                break;

                            // ══════════ 加工阶段 ══════════

                            // ④ WaitingMachine: 等R6107=1加工完成
                            case ForkBoringPhase.WaitingMachine:
                                if (_boringSvc != null && _boringSvc.IsConnected)
                                {
                                    bool boringDone = false;
                                    try { boringDone = (await (boringCycleReadTask ??= _boringSvc.ReadAllSignalsAsync(ct))).RequestUnload; }
                                    catch { }
                                    if (boringDone)
                                    {
                                        Console.WriteLine($"[Line1Front] [货叉] R6107=1 加工完成 → M913=1 货叉去双头镗取料 {_currentWp?.IdentityText}");
                                        await _forkSvc.GoPickFromBoringAsync(ct);
                                        forkCycleReadTask = null;
                                        _forkPhase = ForkBoringPhase.Unload_GoPos3;
                                    }
                                }
                                break;

                            // ══════════ 下料阶段 ══════════

                            // ⑤ Unload_GoPos3: M913会自动取料并去Pos3, 到位后写R6108=1并入天车队列
                            case ForkBoringPhase.Unload_GoPos3:
                                if (!_forkHandshakeInProgress)
                                {
                                    if (!fs.AtPos3)
                                    {
                                        Console.WriteLine("[Line1Front] [货叉] 等M913自动取料到Pos3...");
                                    }
                                    else if (fs.HasPlate && !_boringUnloadDoneSent)
                                    {
                                        if (_boringSvc == null || !_boringSvc.IsConnected)
                                        {
                                            if (_cycleCount % 10 == 1)
                                                Console.WriteLine("[Line1Front] [货叉] Pos3已到位, 但双头镗Modbus未连接 → 暂不写R6108, 等待重连");
                                            break;
                                        }

                                        Console.WriteLine("[Line1Front] [货叉] Pos3已到位 工件在叉上 → R6108=1 下料完成");
                                        try
                                        {
                                            await _boringSvc.SetUnloadDoneAndClearCycleAsync(ct);
                                            boringCycleReadTask = null; // R6108写入后重新读取显示/观察状态
                                        }
                                        catch (Exception ex)
                                        {
                                            PauseForkBoringHandshake("下料完成写入异常", ex);
                                            break;
                                        }

                                        _boringUnloadDoneSent = true;
                                        _forkPhase = ForkBoringPhase.WaitingForkReturnFromBoring;
                                        Console.WriteLine($"[Line1Front] [货叉] 下料完成已发送，R6108保持为1，R6102/R6104已清零 ✓ → 工件入天车队列 {_currentWp?.IdentityText}");
                                        EnqueueFrontCrane(_currentWp!.Value);
                                        lock (_wpLock)
                                        {
                                            _currentWp = null;
                                        } // 交付天车队列, 主循环可出队下一个
                                        _manipulatorClearedFork = false;
                                    }
                                }
                                break;

                            // ══════════ SkipBoring快速通道 ══════════

                            // ⑨ SkipBoring_GoPos3: M914从待机直接送Pos3(不碰Syntec) → 到位后触发前天车
                            //     _skipBoringTriggered 防止天车锁忙时主循环重复触发
                            case ForkBoringPhase.SkipBoring_GoPos3:
                                if (!_forkHandshakeInProgress)
                                {
                                    if (!fs.AtPos3)
                                    {
                                        Console.WriteLine("[Line1Front] [货叉] ⚡ SkipBoring → M914=1 直接去Pos3天车位");
                                        await _forkSvc.GoStandbyToPos3Async(ct);
                                        forkCycleReadTask = null;
                                    }
                                    else if (fs.HasPlate) // 每次循环都尝试(ProcessCraneFrontAsync内有_craneFrontLock防重入)
                                    {
                                        if (!_skipBoringTriggered)
                                        {
                                            Console.WriteLine($"[Line1Front] [货叉] ⚡ SkipBoring Pos3已到位 → 工件入天车队列 {_currentWp?.IdentityText}");
                                            _currentWp?.ReportStage("1号线 跳过双头镗");
                                            _skipBoringTriggered = true;
                                            _forkPhase = ForkBoringPhase.WaitingForkReturnFromSkip;
                                            EnqueueFrontCrane(_currentWp!.Value);
                                            lock (_wpLock) { _currentWp = null; } // 交付天车队列
                                            _manipulatorClearedFork = false;
                                        }
                                    }
                                }
                                break;

                            case ForkBoringPhase.WaitingForkReturnFromBoring:
                            case ForkBoringPhase.WaitingForkReturnFromSkip:
                                // 天车已接管Pos3取料, 这里只等底部状态刷新逻辑确认 M901=1 且 M900=0 后复位Idle。
                                // 不再写M913/M914, 防止和天车取板后的M911回待机命令互相覆盖。
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Line1Front] ⚠ 货叉状态机异常: {ex.Message}");
                    }
                }

                // ═══════════════════════════════════════════════════════════
                //  ③.5 天车独立调度 — fire-and-forget不阻塞主循环, 货叉状态机可并行推进
                // ═══════════════════════════════════════════════════════════
                if (_craneQueue.TryPeek(out _) && await _craneFrontLock.WaitAsync(0, ct))
                {
                    _ = Task.Run(async () =>
                    {
                        using var alarmDispatch = FrontCraneAlarmDispatchScope.Begin();
                        try
                        {
                            while (!ct.IsCancellationRequested && _craneQueue.TryPeek(out _))
                            {
                                // 必须在出队前检查。若疑似断电，保留队头工件，避免暂停后丢失任务身份。
                                var crane = _craneCache.GetOrCreateService(CraneFront1No);
                                var zeroAlarm = await CraneZeroPositionGuard.CheckAsync(
                                    crane, CraneFront1No, "1号线前天车", "前天车任务出队前", ct);
                                if (zeroAlarm != null)
                                {
                                    _paused = true;
                                    OnCraneZeroPositionDetected?.Invoke(zeroAlarm);
                                    break;
                                }

                                if (!TryDequeueFrontCrane(out var craneWp))
                                    break;

                                Console.WriteLine($"[Line1Front] [天车调度] ▶ 出队 {craneWp.IdentityText} d={craneWp.Diameter} L={craneWp.Length} 队列剩余={_craneQueue.Count}");
                                SetFrontCraneTask(craneWp, "前往货叉 Pos3 取料", "ST713", "ST107");
                                //1号线前天车取料->打号机->中转架->退磁->Z回0->释放ZoneTS+ZoneMT->X回安全位置
                                Interlocked.Exchange(ref _frontCraneActive, 1);
                                try
                                {
                                    await ProcessCraneFrontAsync(craneWp, ct);
                                }
                                finally
                                {
                                    // ProcessCraneFrontAsync 内部仍负责自己的锁和异常处理；
                                    // 这里的标志只告诉全局调度器“前天车通道正在忙”，避免压力统计漏掉已出队工件。
                                    Interlocked.Exchange(ref _frontCraneActive, 0);
                                }
                                if (_paused) break; // 当前任务已进入人工确认状态，禁止继续消费后续天车任务。
                            }
                            Console.WriteLine("[Line1Front] [天车调度] 队列空, 天车归位");
                        }
                        finally
                        {
                            //前天车锁释放
                            _craneFrontLock.Release();
                            FrontCraneAlarmDispatchScope.RecordReleased();
                            Console.WriteLine("[Line1Front] [天车调度] _craneFrontLock 已释放");
                        }
                    }, ct);
                }

                // ═══════════════════════════════════════════════════════════
                //  ④ 状态快照 — 无条件刷新，确保UI始终显示最新值
                //     不受任何锁或条件限制，避免卡片显示过期数据
                // ═══════════════════════════════════════════════════════════
                // ── 叉状态 (无条件读) + 天车取完料后货叉回待机位→复位Idle ──
                if (_forkSvc != null) // TcpClient.Connected不可靠, 不检查IsConnected
                {
                    try
                    {
                        var fs = await (forkCycleReadTask ??= _forkSvc.ReadAllStatusAsync(ct));
                        ApplyForkSnapshot(ds, fs);
                        // 天车接管后, 只等待货叉回待机且无板再复位Idle; 等待阶段不会再写M913/M914。
                        if (((_forkPhase == ForkBoringPhase.WaitingForkReturnFromBoring && _boringUnloadDoneSent) ||
                             (_forkPhase == ForkBoringPhase.WaitingForkReturnFromSkip && _skipBoringTriggered))
                            && fs.AtStandbyPos && !fs.HasPlate)
                        {
                            string tag = _forkPhase == ForkBoringPhase.WaitingForkReturnFromSkip ? "⚡SkipBoring" : "";
                            Console.WriteLine($"[Line1Front] [货叉] {tag} 已回待机位 无版 → 状态机复位 Idle");
                            _forkPhase = ForkBoringPhase.Idle;
                            _boringUnloadDoneSent = false; // 状态机复位: 清除R6108已发送标志
                            _m912Sent = false;
                            _skipBoringTriggered = false; // 状态机复位: 清除SkipBoring触发标志
                        }
                    }
                    catch (Exception ex)
                    {
                        MarkForkSnapshotFailed(ds);
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[Line1Front] [货叉状态机] 状态刷新异常: {ex.GetType().Name} - {ex.Message}");
                    }
                }

                // ── 天车前连接状态 (通过 CraneConnectionCache 共享连接) ──
                try
                {
                    ds.CraneFrontConnected = _craneCache.GetOrCreateService(CraneFront1No).IsConnected;
                }
                catch
                { }

                // ── 机械手1 状态 (Y坐标 + 安全位判断) ──
                ds.Manipulator1Connected = _manipulator1?.IsConnected == true;
                if (_manipulator1?.IsConnected == true)
                {
                    try
                    {
                        var s = await _manipulator1.ReadStatusAsync(ct);
                        ds.Manipulator1Y = s?.YPos ?? 0;
                        // 页面显示的是全局“已离开两条前天车危险区”；现场确认两个配置点都安全。
                        ds.Manipulator1Safe = s != null && _cfg.SkewBed.IsManipulator1AtAnySafeY(s.YPos);
                    }
                    catch
                    {
                        ds.Manipulator1Safe = false;
                    }
                }

                // ── 双头镗状态 (持续监视R6101, 供机械手判断是否可取料) ──
                ds.BoringConnected = _boringSvc?.IsConnected == true;
                ds.BoringSnapshotValid = false;
                if (_boringSvc?.IsConnected == true)
                {
                    try
                    {
                        var v = await (boringCycleReadTask ??= _boringSvc.ReadAllSignalsAsync(ct));
                        ds.Boring_R6101 = v.RequestData;
                        ds.Boring_R6102 = v.DataSentDone;
                        ds.Boring_R6103 = v.RequestLoad;
                        ds.Boring_R6104 = v.LoadDone;
                        ds.Boring_R6107 = v.RequestUnload;
                        ds.Boring_R6108 = v.UnloadDone;
                        ds.BoringSnapshotValid = true;
                        ds.BoringSnapshotAtUtc = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[Line1Front] ⚠ 双头镗状态快照失败: {ex.GetType().Name} - {ex.Message}");
                    }
                }
                // UNC探测由独立线程隔离；这里只读取新鲜快照，不能让共享目录离线拖慢主循环。
                var markerSnapshot = _markerMonitor.LatestSnapshot;
                ds.MarkerConnected = _markerMonitor.IsFreshAndConnected;
                ds.MarkerStatusText = ds.MarkerConnected
                    ? MarkerSharePath
                    : markerSnapshot.Error ?? "共享目录不可访问";

                // 只记录本轮页面状态已经维护完成；不触发额外设备读取，也不参与流程放行。
                // 若Pause恰好发生在本轮收尾，保留暂停标记，不能把旧物理信号重新标成当前证据。
                ds.DisplaySnapshotPaused = _paused;
                ds.SnapshotAtUtc = DateTime.UtcNow;

                // ── 等待下一轮 ──
                LogSlowCycleIfNeeded(System.Diagnostics.Stopwatch.GetElapsedTime(cycleIoStarted));
                await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            } // 引擎取消 → 退出
            catch (Exception ex)
            {
                Console.WriteLine($"[Line1Front] ✘ 主循环异常：{ex.GetType().Name} — {ex.Message}"); 
                await Task.Delay(2000, ct);
            }  // 未知异常 → 等2s继续
        }
        Console.WriteLine("[Line1Front] 引擎已停止");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S2: ProcessManipulatorPickupAsync — 机械手1 取料送叉
    //  完整流程:
    //    ① 读D100实测板长并校验任务长度，超差在运动前拒绝取料。
    //    ② ST007取料：确认放磁→首次XYZ→按本次配置探测X11→有板才充磁→独立确认持件。
    //      ②a. MoveAbsoluteAsync(rx, ry, ComputePickupZ(rz,d)) — 下压触发视为异常
    //      ②b. 只有有效X11=0才补偿下探，默认3位置/5mm步长，来源探测期间不反复充退磁。
    //      ②c. X11=1后 Z升安全高度
    //    ③ ST711 送叉: XY→公式Z → 退磁 → Z升 → Y回安全
    //      ③a~③c 不再依赖下压信号; 下压触发视为异常保护
    //      ③d. Z升安全
    //      ③e. Y轴回安全位
    //    ④ M801=1 通知上料架取料完成
    //
    //  异常处理:
    //    交叉未闭环的严重失败，不论是否充磁均暂停并作废本板；实际板/磁铁由人工确认。
    //    完整交叉后的空载回位失败保护货叉已接收板，不凭magnetOn推断实物归属。
    //
    //  锁: 在 finally 中释放 _manipulatorLock，无论成功或失败
    // ═══════════════════════════════════════════════════════════════════
    private async Task ProcessManipulatorPickupAsync(WorkpieceCache wp, CancellationToken ct)
    {
        int pickupStableDelayMs = _cfg.Grinding.X11StableDelayMs;
        string operationalActionId = OperationalEventContextFactory.NewActionId("L1-MANIPULATOR-ST007");
        var action = new FlowActionContext(operationalActionId, "1号线机械手1取料送叉", "机械手1",
            wp.IdentityText, "ST007", "ST711", "总上料架当前工件");
        using var logAction = OperationalLog.BeginAction("1号线前端", "机械手1", operationalActionId, wp.IdentityText, "ST007", "ST711");
        using var alarmPresentation = FlowAlarmPresentationContext.Begin(action, wp, "1号线", 0, "机械手1取料送叉");
        alarmPresentation.ObserveFinalState(() => _manipulatorLock.CurrentCount == 1 ? "机械手1本趟锁已释放" : "机械手1锁仍占用，请核对后续动作",
            readMemory: () => $"本线当前工件={_currentWp?.IdentityText ?? "无"}；货叉阶段={_forkPhase}");
        action.RegisterLock("Manipulator1");
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled($"{operationalActionId}:st007");
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "1号线", "前端引擎", "机械手", "1", "ST007", "ST007取料并放到ST711",
            operationalActionId, $"{operationalActionId}:st007", wp, "ST007", "ST711", "1号线机械手1", "ST007",
            "机械手取料任务已开始", "机械手锁"));
        bool magnetOn = false;          // 磁铁命令是否打开过
        bool holdingWorkpiece = false;  // X11确认吸住后才算工件真的在机械手上
        bool placedOnFork = false;      // 退磁放到货叉后, 才允许货叉状态机接管
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801取料完成通知");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST711货叉完整交接");
        try
        {
            Console.WriteLine($"[Line1Front] [机械手1] ═══ 取料送叉 {wp.IdentityText} d={wp.Diameter} L={wp.Length} ═══");
            wp.ReportStage("1号线 机械手取料/送叉");

            if (wp.Diameter <= 0 || wp.Length <= 0) throw new InvalidOperationException($"任务长度/直径无效：L={wp.Length}，D={wp.Diameter}");

            // ①.0 安全: 取料前检查磁铁是否已有工件(断电/急停重启后X11不受PLC内存影响)
            if (_manipulator1?.IsConnected == true)
            {
                operationalTracker.BeginX11Stage("取料前残留检查");
                operationalTracker.BeginX11Attempt();
                bool hasUnexpectedWorkpiece;
                try
                {
                    hasUnexpectedWorkpiece = await _manipulator1.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(hasUnexpectedWorkpiece, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = "ST007取料前X11残留检查" }, operationalTracker, ex,
                        "取料前X11读取失败，当前传感器值无效；保留最后一次成功值（如有）", true);
                    throw;
                }

                if (hasUnexpectedWorkpiece)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                        operationalSite with { ActionStage = "ST007取料前发现磁铁已有工件" }, operationalTracker, null,
                        "取料前X11成功读取为1，确认磁铁上存在身份未知的残留工件", false, holdingWorkpiece: true);
                    Console.WriteLine("[Line1Front] ⚠⚠⚠ 机械手1磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                    Console.WriteLine("[Line1Front]   拒绝取料, 请人工确认机械手1状态后手动处理");
                    throw new InvalidOperationException("机械手1 X11=1(磁铁已有工件), 拒绝取料防止碰撞");
                }
            }

            // ① 读 D100 板长，并校验它与任务板长是否一致
            //    D100 = 总上料架居中架在 ST007 测得的本块板实测长度，是取料前唯一的物理长度证据。
            //    任务板长来自 ERP/人工建单，是下游全局分线、斜床最大长度匹配、长短板动平衡分流
            //    与研磨机长度准入门槛的判决依据；两者不一致时继续生产会把错误长度一路带下去
            //    （例如把实际长板当短板，直接跳过动平衡送去 ST010）。
            //    读取异常仍按原有语义向外抛出（沿用既有安全收尾）；读到 0 表示本次没有可用测量值，
            //    按"校验不可用"放行，避免一次 MC 短报文就丢弃任务并停线。
            //    ⚠ 判定必须早于本方法后面的 action.MarkCommandSent()：只有在此之前抛出，
            //    外层收尾才会把它当"尚未下发物理命令"，从而允许丢弃本任务身份。
            int plateLen = 0;
            if (_rackSvc != null)
            {
                plateLen = await _rackSvc.ReadPlateLengthAsync(ct);
                Console.WriteLine($"[Line1Front] [机械手1] ① D100板长={plateLen}mm");
            }

            int plateLengthToleranceMm = _cfg.Safety.PlateLengthToleranceMm;
            PlateLengthVerdict plateLengthVerdict =
                PlateLengthVerification.Evaluate(plateLen, wp.Length, plateLengthToleranceMm);
            if (plateLengthVerdict == PlateLengthVerdict.Mismatch)
            {
                Console.WriteLine(
                    $"[Line1Front] [机械手1] ⚠ 板长校验不通过：D100实测={plateLen}mm 任务={wp.Length}mm " +
                    $"偏差={Math.Abs(plateLen - wp.Length)}mm 容差={plateLengthToleranceMm}mm → 拒绝取料并丢弃本任务身份");
                throw new PlateLengthMismatchException(plateLen, wp.Length, plateLengthToleranceMm);
            }
            if (plateLengthVerdict == PlateLengthVerdict.NotComparable)
            {
                // 不校验也放行，但必须留痕，便于事后判断校验是否一直在空转。
                string reason = wp.Length <= 0 ? $"任务板长无效({wp.Length})" : $"D100无有效读数({plateLen})";
                Console.WriteLine($"[Line1Front] [机械手1] ⚠ 板长校验不可用：{reason}，本次放行不校验");
            }

            // ② ST007 取料
            if (!TryGetStationCoords("ST007", out int rx, out int ry, out int rz))
                throw new InvalidOperationException("数据库未找到 ST007 坐标");
            //z轴距离
            int formulaZ = ComputePickupZ(rz, wp.Diameter);
            //z轴安全距离
            int safeZ = _cfg.Grinding.SafeZHeight;
            Console.WriteLine($"[Line1Front] [机械手1] ② ST007({rx},{ry},{rz}) 公式Z={formulaZ} 安全Z={safeZ} (d={wp.Diameter})");
            // 设置三轴绝对速度 (X/Y/Z 各不同)
            var m1Spd = _cfg.GetManipulatorSpeed(1);
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置机械手1任务速度");
            action.MarkCommandSent();
            await _manipulator1!.SetAbsSpeedAsync(m1Spd.X.Speed, m1Spd.X.Accel, m1Spd.X.Decel,
                m1Spd.Y.Speed, m1Spd.Y.Accel, m1Spd.Y.Decel, m1Spd.Z.Speed,
                m1Spd.Z.Accel, m1Spd.Z.Decel, ct);
            action.Confirm();
            // 按配置位置数/步长探测（默认实际Z0/+5/+10），充磁后再次确认，成功前不得提交来源。
            // formulaZ已包含ST007台面/直径计算；首次仍用原XYZ复合动作，不在补偿时叠加偏移。
            // 放磁确认只读X6/X7；配置在本次取板开始时固定，无板/未知反馈仍走本方法统一作废。
            var pickup = await PickupProbeHelper.PickAsync(_manipulator1!, formulaZ, pickupStableDelayMs,
                action, operationalTracker, ct,
                settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm),
                moveInitial: async token =>
                {
                    action.BeginStep(FlowActionStep.MoveXYToSource, new(rx, ry, formulaZ), "ST007 XYZ复合到来源取料位");
                    action.MarkCommandSent();
                    await _manipulator1!.MoveAbsoluteAsync(rx, ry, formulaZ, ct: token);
                });
            int pickupZ = pickup.ActualZ;
            magnetOn = true;
            holdingWorkpiece = true;

            // ②d. Z回原点0 (取料完成先回Z, 下压误触清除后重试最多3次)
            Console.WriteLine("[Line1Front] [机械手1] ②d Z回原点0");
            bool pickupZAtZero = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(rx, ry, 0), "ST007持件后Z回零");
                    action.MarkCommandSent();
                    await _manipulator1!.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
                    operationalTracker.ConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "ST007取料后Z回0命令成功返回");
                    action.Confirm(new FlowActionPosition(rx, ry, 0), "ST007持件后Z=0已确认");
                    pickupZAtZero = true;
                    break;
                }
                catch (PressureStopException)
                {
                    operationalTracker.MarkZUnknown("ST007取料后Z回0触发下压保护，当前位置需后续成功回0确认");
                    Console.WriteLine($"[Line1Front] [机械手1]   ⚠ Z升下压误触→清除→重试{retry+1}");
                    await RecoverFromPressureStopAsync(ct);
                }
            }
            if (!pickupZAtZero)
                throw new InvalidOperationException("机械手1取料后Z三次回0均失败，禁止横移到货叉");

            // ③ 送货叉 ST711 — 前置检查: 叉在待机位且无版, 防止叉在Pos2/Pos3时碰撞
            if (!TryGetStationCoords("ST711", out int f1x, out int f1y, out int f1z))
                throw new InvalidOperationException("数据库未找到 ST711");
            // 这里必须是硬检查: 货叉未连接/状态读不到时不能跳过, 否则机械手会盲目去ST711放料。
            if (!await ConfirmForkReadyForManipulatorAsync("送叉动作前", ct))
                throw new InvalidOperationException("货叉未连接/状态读取失败/不在待机位/已有板, 机械手拒绝送叉");
            Console.WriteLine($"[Line1Front] [机械手1]   货叉前置检查通过: 待机位 ✓ 无版 ✓");
            int forkPlaceZ = ComputePickupZ(f1z, wp.Diameter);
            Console.WriteLine($"[Line1Front] [机械手1] ③ 送叉 ST711({f1x},{f1y},{f1z}) 放料Z={forkPlaceZ}");

            // ③a. XY→叉位公式Z; 下压触发视为异常保护
            operationalTracker.BeginZDown(forkPlaceZ);
            try
            {
                action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(f1x, f1y, forkPlaceZ), "ST711 XYZ复合到货叉放料位");
                action.MarkCommandSent();
                await _manipulator1!.MoveAbsoluteAsync(f1x, f1y, forkPlaceZ, timeoutMs: 240_000, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(f1x, f1y, forkPlaceZ), "ST711 XYZ复合到位");
            }
                catch (PressureStopException ex)
                {
                    operationalTracker.MarkZUnknown("ST711放料Z下降触发下压保护，恢复后实际位置需人工确认");
                    await RecoverFromPressureStopAsync(ct);
                    throw new InvalidOperationException($"机械手1 ST711公式放料触发下压信号, Z={forkPlaceZ}, 已停止流程", ex);
                }
                catch (Exception)
                {
                    operationalTracker.MarkZUnknown("ST711放料Z下降调用异常，实际Z位置需人工确认");
                    throw;
                }

            // ③c. 退磁放下
            operationalTracker.BeginPlacementMagnetOff("ST711货叉");
            try
            {
                action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(f1x, f1y, forkPlaceZ), "ST711退磁放料");
                action.MarkCommandSent();
                await _manipulator1!.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff("ST711货叉");
                action.Confirm(new FlowActionPosition(f1x, f1y, forkPlaceZ), "ST711退磁调用成功返回");
                operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST711货叉完整交接",
                    "货叉目标位退磁成功返回；等待Z回安全、机械手安全退出和M801通知");
            }
            catch (Exception ex)
            {
                operationalTracker.FailMagnetOff();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                    operationalSite with { Station = "ST711", ActionStage = "ST711货叉放料退磁" }, operationalTracker, ex,
                    "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                throw;
            }
            magnetOn = false; // 标志置false → 工件已安全放下
            placedOnFork = true;
            holdingWorkpiece = false;
            action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "ST711货叉退磁放料成功，等待M801与货叉放行闭环");
            Console.WriteLine($"[Line1Front] [机械手1]   退磁放下 ✓ {wp.IdentityText}");

            // ③d. Z回原点0 (送完货叉先回Z, 下压误触清除后重试最多3次)
            Console.WriteLine("[Line1Front] [机械手1]   Z回原点0");
            bool placeZAtZero = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(f1x, f1y, 0), "ST711放料后Z回零");
                    action.MarkCommandSent();
                    await _manipulator1!.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
                    operationalTracker.ConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "ST711放料后Z回0命令成功返回");
                    action.Confirm(new FlowActionPosition(f1x, f1y, 0), "ST711放料后Z=0已确认");
                    placeZAtZero = true;
                    break;
                }
                catch (PressureStopException)
                {
                    operationalTracker.MarkZUnknown("ST711放料后Z回0触发下压保护，当前位置需后续成功回0确认");
                    Console.WriteLine($"[Line1Front] [机械手1]   ⚠ Z升下压误触→清除→重试{retry+1}");
                    await RecoverFromPressureStopAsync(ct);
                }
            }
            if (!placeZAtZero)
                throw new InvalidOperationException("机械手1放板后Z三次回0均失败，禁止放行货叉");

            // ④ 第一阶段：先到本线首次安全点并成功写M801，再打开货叉放行门闩。
            //    1号线现场确认首次安全点为Y=11500；门闩打开后，货叉主循环即可独立推进双头镗握手。
            int releaseSafeY = _cfg.SkewBed.GetManipulator1SafeYForLine(1);
            Console.WriteLine($"[Line1Front] [机械手1] ④ 第一阶段回位: Y→1号线货叉释放安全点 {releaseSafeY}");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, releaseSafeY, 0), "离开ST711至1号线货叉释放安全Y");
            action.MarkCommandSent();
            await _manipulator1!.MoveAbsoluteAsync(-1, releaseSafeY, -1, ct: ct);
            action.Confirm(new FlowActionPosition(null, releaseSafeY, 0), "已离开ST711货叉区");
            Console.WriteLine($"[Line1Front] [机械手1]   Y={releaseSafeY} Z=0，已离开1号线叉区 ✓");

            if (_rackSvc == null)
                throw new InvalidOperationException("总上料架服务为空，M801未写入，禁止放行货叉");
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801取料完成通知", "M801取料完成通知调用已开始，PLC写入结果未知");
            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(null, releaseSafeY, 0), "写M801通知总上料架取料完成");
            action.MarkCommandSent();
            await _rackSvc.SetPickupDoneAsync(ct);
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801取料完成通知", "M801取料完成通知成功返回");
            action.Confirm(new FlowActionPosition(null, releaseSafeY, 0), "M801取料完成通知成功返回");
            Console.WriteLine($"[Line1Front] [机械手1] ④ M801=1→0 取料完成 ✓ {wp.IdentityText}");

            // 门闩必须放在M801成功之后；主循环可能与本方法并行，提前置位会让货叉在M801失败时误启动。
            _manipulatorClearedFork = true;
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST711货叉完整交接",
                "退磁放料、Z回安全、机械手安全退出和M801通知均成功返回");
            Console.WriteLine($"[Line1Front] [机械手1]   货叉放行门闩=ON，双头镗可开始交互；机械手继续回取板待机点");

            // ⑤ 第二阶段：保持两线共享机械手锁，继续回总上料架附近的待机Y。
            //    不新建Task；本方法本身已在后台执行，货叉主循环会在门闩打开后与本移动自然并行。
            if (!await MoveManipulator1ToPickupStandbyAfterForkReleaseAsync(wp, action, ct))
                return; // 货叉门闩保持开启，但共享机械手位置不确定；告警已同步暂停两条线。

            Console.WriteLine($"[Line1Front] [机械手1] ═══ 取料送叉完成 {wp.IdentityText} ═══");
            action.Complete("机械手1送叉、M801通知和安全回位均已完成");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 普通取消不作废来源板；应急入口负责自身结案。
            throw;
        }
        catch (Exception ex)
        {
            _paused = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            OnSafetyAlarm?.Invoke($"1号线机械手1异常：{ex.Message}。线路已暂停，正在作废本趟任务并清理设备；请勿恢复或删除下一板。");
            if (placedOnFork && _manipulatorClearedFork)
            {
                // “交接完成”包含退磁放板、Z=0、安全Y退出及M801成功，不能只看板已落到货叉。
                // 放行后货叉可能已经独立推进；仅结束空载动作，不能回滚已交付板或清下游参数。
                FlowAlarmPresentationContext.RecordCleanup("工件已完整交接，跳过下游参数清理", "清空载回位动作，货叉/下游继续保留本板", "已交付任务不回滚");
            }
            else
            {
                bool ownsCycle;
                lock (_wpLock) ownsCycle = _currentWp.HasValue && WorkpieceCacheIdentity.Matches(_currentWp.Value, wp);
                string deviceResult = "归属已变化，跳过前端设备输出清理";
                // 当前周期仍是本板且没有独立货叉握手在执行时，才允许清本轮前端输出/阶段。
                // 归属变化或握手并发时跳过设备清理，避免写0破坏另一个已开始的周期。
                if (ownsCycle && !_forkHandshakeInProgress)
                {
                    var results = new List<string>();
                    using var cleanupCts = new CancellationTokenSource(10_000);
                    try { if (_forkSvc == null) throw new InvalidOperationException("货叉服务为空"); await _forkSvc.ClearMotionCommandsAsync(cleanupCts.Token); results.Add("M911~M914已清"); }
                    catch (Exception clearEx) { results.Add($"货叉输出清理失败/未知：{clearEx.Message}"); }
                    try { if (_boringSvc == null) throw new InvalidOperationException("双头镗服务为空"); await _boringSvc.ClearEmergencyOutputsAsync(cleanupCts.Token); results.Add("R6102/R6104/R6108已清"); }
                    catch (Exception clearEx) { results.Add($"双头镗输出清理失败/未知：{clearEx.Message}"); }
                    deviceResult = string.Join("；", results);
                    _forkPhase = ForkBoringPhase.Idle;
                    _manipulatorClearedFork = false;
                    _boringUnloadDoneSent = false;
                    _m912Sent = false;
                    _skipBoringTriggered = false;
                }
                // 物理板可能仍在ST007、机械手或尚未放行的货叉；这里只作废软件身份，现场人工处理。
                PauseAndDiscardManipulatorWorkpiece(wp);
                FlowAlarmPresentationContext.RecordCleanup(deviceResult, "本板_currentWp已清；本趟机械手动作退出", "仅删除本板完整身份残留，不回前端缓存");
            }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "机械手1严重异常作废" }, operationalTracker, ex,
                "本趟暂停，软件任务作废；实体及磁铁仍需人工确认", true, actionSnapshot: action.Snapshot());
            action.Discard("严重异常已软件结案，禁止续搬本板");
            OnSafetyAlarm?.Invoke($"1号线机械手1处理{wp.IdentityText}严重异常：{ex.Message}。线路已暂停，本趟任务已作废，不回缓存。" + "\n" + ManipulatorAbortInstruction);
        }
        finally
        {
            // 无论如何释放锁
            Console.WriteLine("[Line1Front] [DEBUG] 机械手释放 _manipulatorLock");
            _manipulatorLock.Release();
            action.TryReleaseLock("Manipulator1");
        }  // 无论如何释放锁
    }

    /// <summary>
    /// 1号线货叉已安全放行后，把机械手1预定位到总上料架附近，缩短下一次取板空行程。
    /// 调用期间仍持有两线共享的_manipulatorLock；货叉依靠独立门闩和状态机并行工作。
    /// </summary>
    private async Task<bool> MoveManipulator1ToPickupStandbyAfterForkReleaseAsync(
        WorkpieceCache wp, FlowActionContext action, CancellationToken ct)
    {
        int targetY = _cfg.SkewBed.Manipulator1PickupStandbyY;
        int releaseY = _cfg.SkewBed.GetManipulator1SafeYForLine(1);
        if (Math.Abs(targetY - releaseY) <= MotionConfig.SkewBedSection.Manipulator1SafeYTolerance)
        {
            Console.WriteLine($"[Line1Front] [机械手1] ⑤ 取板待机Y={targetY}与货叉释放点相同，无需第二段移动");
            return true;
        }

        try
        {
            Console.WriteLine($"[Line1Front] [机械手1] ⑤ 货叉已开始独立工作，保持共享机械手锁并预定位 Y:{releaseY}→{targetY}");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, targetY, 0), "货叉放行后机械手1回取板待机Y");
            action.MarkCommandSent();
            await _manipulator1!.MoveAbsoluteAsync(-1, targetY, -1, ct: ct);

            // MoveAbsoluteAsync正常返回后再读一次实际Y，避免通信/PLC异常把“命令完成”误当成“到位完成”。
            var status = await _manipulator1.ReadStatusAsync(ct);
            if (status == null || Math.Abs(status.YPos - targetY) > MotionConfig.SkewBedSection.Manipulator1SafeYTolerance)
                throw new InvalidOperationException(status == null
                    ? $"回取板待机Y={targetY}后状态读取为空"
                    : $"回取板待机Y未到位，实际Y={status.YPos}，目标Y={targetY}");

            Console.WriteLine($"[Line1Front] [机械手1]   取板待机Y={status.YPos}到位 ✓，下一块板可从近端取料");
            action.Confirm(new FlowActionPosition(null, status.YPos, 0), "货叉放行后取板待机Y已由状态读取确认");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception moveEx)
        {
            int? actualY = null;
            string readDetail;
            try
            {
                var status = await _manipulator1!.ReadStatusAsync(ct);
                actualY = status?.YPos;
                readDetail = status == null ? "实际Y读取为空" : $"实际Y={status.YPos}";
            }
            catch (Exception readEx)
            {
                readDetail = $"实际Y读取失败: {readEx.Message}";
            }

            // 若移动命令失败但机械手仍停在1000/11500任一已确认安全端点，货叉和下一任务均不存在位置不确定风险。
            // 此时只记录警告，不回退已打开的货叉门闩；下一次取板可能多走一段，但业务可继续。
            if (actualY.HasValue && _cfg.SkewBed.IsManipulator1AtAnySafeY(actualY.Value))
            {
                Console.WriteLine($"[Line1Front] [机械手1] ⚠ 回取板待机点失败，但仍在已确认安全端点({readDetail})，货叉继续工作。异常：{moveEx.Message}");
                return true;
            }

            _paused = true;
            string alarm = $"共享机械手1在1号线货叉已放行后回取板待机Y={targetY}时异常，{readDetail}。" +
                           $"工件{wp.IdentityText}已在货叉且货叉状态不回退；机械手位置不确定，1/2号线新派发必须暂停。异常：{moveEx.Message}";
            Console.WriteLine($"[Line1Front] [机械手1] ❌ {alarm}");
            FlowAlarmPresentationContext.RecordFailure(moveEx);
            FlowAlarmPresentationContext.RecordCleanup("本板已完整交接，未清货叉或双头镗输出", "仅空载回位动作作废；共享机械手位置需人工确认", "已交接本板及后续任务保留");
            action.Discard("货叉放行后的空载回位失败，禁止续跑旧动作，保留下游本板");
            OnSharedManipulatorSafetyAlarm?.Invoke(alarm);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S3: DispatchForkAsync — 货叉分派 (M912送料到双头镗并回待机)
    // ═══════════════════════════════════════════════════════════════════
    public async Task DispatchForkAsync(int workpieceLength, CancellationToken ct = default)
    {
        if (_forkSvc == null || !_forkSvc.IsConnected) throw new InvalidOperationException("货叉未连接");
        Console.WriteLine($"[Line1Front] [货叉] L={workpieceLength}mm → M912送料到双头镗并回待机");
        await _forkSvc.GoFeedToBoringAsync(ct);
    }

    
    // ═══════════════════════════════════════════════════════════════════
    //  S4~S6: ProcessCraneFrontAsync — 前天车: Pos3取料→打号机→中转架
    //   货叉已处理完镗孔, 天车不再参与双头镗握手
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>前天车单工件任务: Pos3取料→打号机→中转架。锁由调度层持有, 本方法不管。</summary>
    private void EnqueueFrontCrane(WorkpieceCache wp)
    {
        lock (_frontCraneTaskLock) _craneQueue.Enqueue(wp);
    }

    private bool TryDequeueFrontCrane(out WorkpieceCache wp)
    {
        lock (_frontCraneTaskLock) return _craneQueue.TryDequeue(out wp);
    }

    /// <summary>
    /// 前天车严重失败后只删除本趟完整身份的队列残留，并保留其他板的顺序。
    /// 不清整条前端缓存，不清下一板货叉周期；已成功交接下游时调用方不执行此删除。
    /// </summary>
    private int DiscardFrontCraneQueueResidues(WorkpieceCache wp)
    {
        lock (_frontCraneTaskLock)
        {
            int count = _craneQueue.Count, removed = 0;
            for (int i = 0; i < count && _craneQueue.TryDequeue(out var candidate); i++)
                if (WorkpieceCacheIdentity.Matches(candidate, wp)) removed++;
                else _craneQueue.Enqueue(candidate);
            return removed;
        }
    }

    private async Task ProcessCraneFrontAsync(WorkpieceCache wp, CancellationToken ct = default)
    {
        int pickupStableDelayMs = _cfg.Grinding.X11StableDelayMs;
        string actionId = OperationalEventContextFactory.NewActionId("L1-FRONT");
        // 任务一旦从前天车队列出队，就用动作账本记录真实工件归属；不能再凭磁铁调用推断持件。
        var action = new FlowActionContext(actionId, "1号线前天车取料-打号-中转", $"前天车{CraneFront1No}",
            wp.IdentityText, "ST713", "1号线中转架", "1号线前天车队列");
        using var logAction = OperationalLog.BeginAction("1号线前天车", $"{CraneFront1No}号天车", actionId, wp.IdentityText, "ST713", "1号线中转架");
        using var alarmPresentation = FlowAlarmPresentationContext.BeginFront(action, wp, "1号线", CraneFront1No);
        alarmPresentation.ObserveFinalState(() => FlowAlarmPresentationContext.DescribeFrontActionLocks(action),
            readMemory: () =>
            {
                WorkpieceCache? current;
                lock (_wpLock) current = _currentWp;
                return $"货叉阶段={_forkPhase}；当前前端工件={current?.IdentityText ?? "无"}（可能为下一块板，按原处理保留）；机械手离叉门闩={_manipulatorClearedFork}；R6108已发送={_boringUnloadDoneSent}；M912已发送={_m912Sent}；跳镗已触发={_skipBoringTriggered}。各字段为退出时软件读取。";
            });
        FlowAlarmPresentationContext.RecordTransferPhase("ST713", "ST107", "货叉Pos3取料");
        action.RegisterLock("FrontCrane#1");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        var pos3OperationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled($"{actionId}:pos3");
        var pos3OperationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "1号线", "前端引擎", "前天车", CraneFront1No.ToString(), "ST713", "货叉Pos3取料并放到打号机",
            actionId, $"{actionId}:pos3", wp, "ST713", "ST107", "1号线前天车", "ST713",
            "前天车任务已开始", "前天车锁/ZoneMT"));
        var markerOperationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled($"{actionId}:marker-pickup");
        var markerOperationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "1号线", "前端引擎", "前天车", CraneFront1No.ToString(), "ST107", "打号机取回并放到中转架",
            actionId, $"{actionId}:marker-pickup", wp, "ST107", "中转架", "1号线前天车", "ST107",
            "打号机工件等待取回", "前天车锁/ZoneMT/ZoneTS"));
        pos3OperationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST107打号机放料与文件握手");
        bool magnetOn = false;
        bool holdingWorkpiece = false;
        // 只在中转架放料和下游软件交接成功返回后置true；末尾空载失败不能删除已交付的板。
        bool downstreamHandoffClosed = false;
        try
        {
            wp.ReportStage("1号线 前天车/打号机");
            SetFrontCraneTask(wp, "前天车取料并经过打号机", "ST713", "中转架");
            var crane = _craneCache.GetOrCreateService(CraneFront1No);
            if (!crane.IsConnected)
            {
                Console.WriteLine($"[Line1Front] [前天车] 连接天车{CraneFront1No}...");
                await crane.ConnectAsync(ct);
            }

            int safeZ = _cfg.Grinding.SafeZHeight;
            Console.WriteLine($"[Line1Front] [前天车] ═══ 天车流程开始 {wp.IdentityText} d={wp.Diameter} L={wp.Length} ═══");

            // ① 当前现场确认机械手1只在ST711/ST712待机端工作, 前天车只进Pos3(ST713)取料,
            //    两者物理区域不重叠; 前天车不再等待机械手1到安全Y端点, 避免机械手回位/通信阻塞Pos3取料。
            Console.WriteLine("[Line1Front] [前天车] ① 跳过机械手1安全Y等待: ST711/ST712与Pos3(ST713)不重叠");

            // ①.5 安全: 检查天车磁铁上是否已有工件(断电重启后可能残留)
            // 用X11物理线圈(63497)而非D5029: PLC断电重启后D5029可能清零, X11不受影响
            pos3OperationalTracker.BeginX11Stage("取料前残留检查");
            pos3OperationalTracker.BeginX11Attempt();
            bool hasRoller;
            try
            {
                hasRoller = await crane.ReadXBitAsync(63497, ct);
                pos3OperationalTracker.CompleteX11(hasRoller, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pos3OperationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    pos3OperationalSite with { ActionStage = "货叉Pos3取料前X11残留检查" }, pos3OperationalTracker, ex,
                    "取料前X11读取失败，当前传感器值无效；保留最后一次成功值（如有）", true);
                throw;
            }
            physicalTracker?.TryObserveX11(hasRoller);
            if (hasRoller)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    pos3OperationalSite with { ActionStage = "货叉Pos3取料前发现磁铁已有工件" }, pos3OperationalTracker, null,
                    "取料前X11成功读取为1，确认磁铁上存在身份未知的残留工件", false, holdingWorkpiece: true);
                Console.WriteLine("[Line1Front] ⚠⚠⚠ 天车磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[Line1Front]   拒绝取料, 请人工确认天车状态后手动处理");
                throw new InvalidOperationException("天车X11=1(磁铁有工件), 拒绝取料防止碰撞");
            }

            // ② 货叉Pos3 Z降取料 → 充磁 → Z升
            if (!TryGetStationCoords("ST713", out int f2x, out int f2y, out int f2z))
                throw new InvalidOperationException("数据库未找到 ST713");
            int f2PickupZ = ComputePickupZ(f2z, wp.Diameter);
            int f2xOff = f2x + _craneOffsetX, f2yOff = f2y + _craneOffsetY, f2zOff = f2PickupZ + _craneOffsetZ;
            Console.WriteLine(
                $"[Line1Front] [前天车] ② XY→货叉Pos3 ST713({f2x},{f2y},{f2z}) +偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ}) Z取料={f2PickupZ}");
            var crSpd = _cfg.GetCraneSpeed(1);
            //设置速度
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置前天车本任务绝对速度");
            action.MarkCommandSent();
            await crane.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel, crSpd.Y.Speed, crSpd.Y.Accel,
                crSpd.Y.Decel, crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);
            action.Confirm();
            // 新任务可能承接上一次异常/急停后的天车位置；首次去Pos3横移前必须实时确认Z已回零。
            Console.WriteLine("[Line1Front] [前天车] 去货叉Pos3前确认Z=0±5mm");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "去ST713前确认Z回零");
            action.MarkCommandSent();
            await crane.EnsureZAtZeroAsync(5, ct);
            action.Confirm(new FlowActionPosition(null, null, 0), "取料前Z=0已确认");
            physicalTracker?.TryConfirmSafeZ(0, 5, "去ST713取料前EnsureZAtZeroAsync成功返回");
            // X绝对编码器微调必须在Z下降前执行: 先XY到货叉Pos3上方, 复核D5014~D5015, 合格后才下降。
            action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(f2xOff, f2yOff, 0), "XY到货叉Pos3取料位");
            action.MarkCommandSent();
            await crane.MoveAbsoluteAsync(f2xOff, f2yOff, -1, ct: ct);
            action.Confirm(new FlowActionPosition(f2xOff, f2yOff, 0), "ST713上方XY到位");
            action.BeginStep(FlowActionStep.FineTuneSource, new FlowActionPosition(f2xOff, f2yOff, 0), "ST713取料前XY微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                crane, _cfg, CraneFront1No, "ST713", "1号线前天车-货叉Pos3取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "1号线", engine: "前端引擎", deviceNo: CraneFront1No.ToString(), station: "ST713",
                    actionStage: "货叉Pos3取料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation("ST713", "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation("ST107", "本物理周期目标"),
                    owner: "1号线前天车", targetZ: EvidenceValue<int>.Confirmed(f2zOff, "已有取料Z公式与偏移"),
                    physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, true, "XY已到ST713上方", "ST713")),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(f2xOff, f2yOff, 0), "ST713取料前XY微调完成");
            // Pos3首次Z已由本入口算好偏移。先只读放磁反馈，再下降/探测/充磁/后确认；
            // helper成功以后才能按原顺序回升并通知货叉回待机，不能因充磁前X11=1提前交接。
            var pos3Pickup = await PickupProbeHelper.PickAsync(crane, f2zOff, pickupStableDelayMs,
                action, pos3OperationalTracker, ct,
                settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm));
            int f2PickZ = pos3Pickup.ActualZ;
            magnetOn = true;
            holdingWorkpiece = true;
            physicalTracker?.TryObserveX11(true);

            // ②.5 X11已经确认吸住: 现场确认Pos3低位持板时货叉回待机不会碰天车/工件。
            //      Z升安全和M911回待机并行执行, 两边都完成后才继续去打号机; 任一失败仍按"工件在天车上"暂停。
            Console.WriteLine("[Line1Front] [前天车] ②.5 Z升安全高度 与 M911货叉回待机 并行");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(f2xOff, f2yOff, safeZ), "持件后Z上升安全高度");
            action.MarkCommandSent();
            FlowAlarmPresentationContext.RecordTransferPhase("ST713", "ST107", "货叉Pos3取料后回升与货叉回待机", "Z回安全高度与货叉回待机（并行，具体失败见原因）");
            var zSafeTask = crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            Task forkStandbyTask = Task.CompletedTask;
            if (_forkSvc != null) // TcpClient.Connected不可靠, 不检查IsConnected
            {
                Console.WriteLine("[Line1Front] [前天车]   并行通知货叉回待机位 M911=1");
                // 清除2/3位置, 通知货叉去待机位置。
                forkStandbyTask = _forkSvc.GoStandbyAsync(ct);
            }
            //z升0点和货叉回待机位并行
            await Task.WhenAll(zSafeTask, forkStandbyTask);
            action.Confirm(new FlowActionPosition(f2xOff, f2yOff, safeZ), "持件后Z已升安全高度");
            pos3OperationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST713取料后Z升安全命令成功返回");
            physicalTracker?.TryConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST713取料后Z升安全命令成功返回");
            Console.WriteLine("[Line1Front] [前天车]   Z安全高度与货叉回待机命令均已完成");

            FlowAlarmPresentationContext.RecordTransferPhase("ST713", "ST107", "等待中转架空位及打号机碰撞区");
            // ③ 等中转架空位 → ZoneMT打号机区 → ZoneTS中转架区 → 放中转架。
            //    预检: 锁外先等中转架物理空位, 只做候选判断。
            //    新碰撞区模型:
            //      ZoneMT = 打号机↔中转架；ZoneTS = 中转架↔斜床1。
            //      打号期间只持ZoneMT, 后天车仍可在ST108侧持ZoneTS工作。
            //      真正进入中转架前再拿ZoneTS; 放料二次判断只看物理空位信号。
            Console.WriteLine("[Line1Front] [前天车] ③ 等中转架空位...");
            while (_rackSvc?.IsConnected == true)
            {
                try
                {
                    //读取中转架是否有空位置
                    var crs = await _rackSvc.ReadAllStatusAsync(ct);
                    if (!crs.Station1HasPlate || !crs.Station2HasPlate || !crs.Station3HasPlate) break;
                    Console.WriteLine("[Line1Front] [前天车] 中转架1/2/3全满, 等后天车取走...");
                }
                catch { /* 读失败→等下轮 */ }
                await Task.Delay(500, ct);
            }
            Console.WriteLine("[Line1Front] [前天车] ③ 获取ZoneMT(打号机↔中转架碰撞区)...");
            while (!await _safety.MarkerTransferCollisionLock.WaitAsync(TimeSpan.FromSeconds(1), ct))
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine("[Line1Front] [前天车] 等ZoneMT...");
            }
            Console.WriteLine("[Line1Front] [前天车] ZoneMT已获取 ✓");
            bool zoneMtLocked = true;
            action.RegisterLock("ZoneMT");
            bool zoneTsLocked = false;
            try
            {
                //打号机流程
                await MarkingHandshakeAsync(crane, wp, safeZ, actionId, physicalTracker,
                    pos3OperationalTracker, pos3OperationalSite, markerOperationalTracker, markerOperationalSite, action, ct, pickupStableDelayMs);
                FlowAlarmPresentationContext.RecordTransferPhase("ST107", "中转架（尚未选择）", "等待进入中转架");

                Console.WriteLine("[Line1Front] [前天车] ③.5 获取ZoneTS(中转架↔ST108碰撞区), 准备进入中转架...");
                while (!await _safety.TransferSkew1CollisionLock.WaitAsync(TimeSpan.FromSeconds(1), ct))
                {
                    if (_cycleCount % 10 == 1)
                        Console.WriteLine("[Line1Front] [前天车] 等ZoneTS...");
                }
                zoneTsLocked = true;
                action.RegisterLock("ZoneTS");
                Console.WriteLine("[Line1Front] [前天车] ZoneTS已获取 ✓，当前持有ZoneMT+ZoneTS，可进入中转架");

                //中转架放料流程  
                await PlaceOnTransferRackAsync(crane, wp, safeZ, actionId, physicalTracker,
                    markerOperationalTracker, markerOperationalSite, action, ct);
                downstreamHandoffClosed = true;
                _safety.TransferSkew1CollisionLock.Release();
                zoneTsLocked = false;
                action.TryReleaseLock("ZoneTS");
                _safety.MarkerTransferCollisionLock.Release();
                zoneMtLocked = false;
                action.TryReleaseLock("ZoneMT");
                Console.WriteLine($"[Line1Front] [前天车] 中转架放料完成且Z已安全, ZoneTS+ZoneMT已释放 ✓ {wp.IdentityText}");
                magnetOn = false; // 放料成功才清标志

                FlowAlarmPresentationContext.RecordTransferPhase(action.Target, "前天车安全归位", "中转架交接后前天车归位", "前天车X归位与Z回原点（并行）");
                int homeX = _cfg.GetCraneHomeX(CraneFront1No);
                Console.WriteLine($"[Line1Front] [前天车] X→{homeX + _craneOffsetX} Z回原点 并发执行");
                //回homex 回z轴原点 并发执行你    第二个参数f2y是货叉3号位的坐标
                var xTask = crane.MoveAbsoluteAsync(homeX + _craneOffsetX, f2y, -1, ct: ct);
                var zTask = crane.HomeZAsync(ct);
                //两个任务同时执行  homex和回原点并发执行
                await Task.WhenAll(xTask, zTask);
                ClearFrontCraneTask();
                action.Complete("前天车已完成中转架交接并回到安全位");
                Console.WriteLine($"[Line1Front] [前天车] ═══ 天车流程完成 {wp.IdentityText} ═══");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _paused = true;
                FlowAlarmPresentationContext.RecordFailure(ex);
                OnSafetyAlarm?.Invoke($"1号线前天车异常：{ex.Message}。线路暂停，正在收尾。");
                throw;
            }
            finally
            {
                if (zoneTsLocked)
                {
                    try { _safety.TransferSkew1CollisionLock.Release(); } catch (SemaphoreFullException) { }
                    action.TryReleaseLock("ZoneTS");
                    Console.WriteLine("[Line1Front] [前天车] 异常兜底: ZoneTS已释放");
                }
                if (zoneMtLocked)
                {
                    try { _safety.MarkerTransferCollisionLock.Release(); } catch (SemaphoreFullException) { }
                    action.TryReleaseLock("ZoneMT");
                    Console.WriteLine("[Line1Front] [前天车] 异常兜底: ZoneMT已释放");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _paused = true;
            FlowAlarmPresentationContext.RecordFailure(ex);
            // 本趟已经出队；禁止回队。下一板可能正在货叉/双头镗，不能清整条前端周期。
            // 严重失败结束当前天车身份及旧人工续搬入口；已成功交付下游的板保留，锁在原持锁层收尾。
            int removed = downstreamHandoffClosed ? 0 : DiscardFrontCraneQueueResidues(wp);
            ClearFrontCraneTask();
            FlowAlarmPresentationContext.RecordCleanup("前天车本趟退出；未改可能属于下一板的货叉/双头镗输出，也未强制退磁或回位",
                "本趟前天车任务牌和人工工件已清，前端下一板状态保留",
                downstreamHandoffClosed ? "本板已完成下游交接，仅空载回位失败，保留下游缓存" : $"本板不回队；匹配残留删除={removed}；已移交下游的缓存不回滚");
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, ex is PickupFailedException pickupFailure ? pickupFailure.EventCode : "ENGINE_FINAL_FAILURE",
                pos3OperationalSite with { ActionStage = "前天车严重异常作废" }, pos3OperationalTracker, ex,
                "线路暂停，当前搬运动作作废；现场板及实际磁铁待人工确认", true, actionSnapshot: action.Snapshot());
            action.Discard("前天车严重异常本趟已作废，旧人工续搬记录已删除");
            OnSafetyAlarm?.Invoke($"1号线前天车处理{wp.IdentityText}严重异常：{ex.Message}。线路已暂停，本趟任务已清，不回队。" +
                (downstreamHandoffClosed ? "工件已完整交接下游，本次为空载回位失败，保留下游任务。" : "请人工移走本次板。") +
                "确认天车安全位置和实际放磁后恢复；下一板货叉/双头镗任务未清。");
        }

    }

    /// <summary>
    /// 打号机文件握手:
    ///   Z降(公式)→退磁放下工件→Z升安全→写 D:\job1\A (内容=刻印+直径)
    ///   →轮询等 D:\job1\B 出现(1小时超时)→Z降→充磁取料→Z升→删B
    /// </summary>
    private async Task MarkingHandshakeAsync(CraneService crane, WorkpieceCache wp, int safeZ, string actionId,
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker,
        OperationalPhysicalCycleTracker pos3OperationalTracker,
        OperationalEventContextFactory.OperationalPhysicalEventSite pos3OperationalSite,
        OperationalPhysicalCycleTracker markerOperationalTracker,
        OperationalEventContextFactory.OperationalPhysicalEventSite markerOperationalSite,
        FlowActionContext action,
        CancellationToken ct, int pickupStableDelayMs)
    {
        FlowAlarmPresentationContext.RecordTransferPhase("ST713", "ST107", "打号机放料");
        int markerZUp = 0;
        string fileB = string.Empty;
        if (!TryGetStationCoords("ST107", out int mx, out int my, out int mz)) throw new InvalidOperationException("数据库未找到 ST107");
        Console.WriteLine($"[Line1Front] [打号机] ④ ST107({mx},{my},{mz})");

        // ── ④a. Z降(公式)→退磁放下 ──
        int markerZ = ComputePickupZ(mz, wp.Diameter);
        int mxOff = mx + _craneOffsetX, myOff = my + _craneOffsetY, mzOff = markerZ + _craneOffsetZ;
        Console.WriteLine($"[Line1Front] [前天车] Z降={markerZ} +偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ}) 退磁放下");
        action.SetTarget("ST107", "进入打号机放料/取回交接阶段");
        action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(mxOff, myOff, safeZ), "XY到ST107打号机上方");
        action.MarkCommandSent();
        await crane.MoveAbsoluteAsync(mxOff, myOff, -1, ct: ct);
        action.Confirm(new FlowActionPosition(mxOff, myOff, safeZ), "ST107目标XY命令成功返回");
        action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(mxOff, myOff, safeZ), "ST107放料前XY微调");
        action.MarkCommandSent();
        await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
            crane, _cfg, CraneFront1No, "ST107", "1号线前天车-打号机放料前",
            reporter: _exceptionReporter,
            failureContext: OperationalEventContextFactory.FineTuneFailure(
                scope: "1号线", engine: "前端引擎", deviceNo: CraneFront1No.ToString(), station: "ST107",
                actionStage: "打号机放料前XY微调", workpiece: wp,
                source: OperationalEventContextFactory.ConfirmedLocation("ST713", "本物理周期来源"),
                target: OperationalEventContextFactory.ConfirmedLocation("ST107", "本物理周期目标"),
                owner: "1号线前天车", targetZ: EvidenceValue<int>.Confirmed(mzOff, "已有打号机放料Z公式与偏移"),
                physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(true, true, physicalTracker, "ST713取料X11=1且充磁已返回", "天车/路径中")),
            actionId: actionId, physicalTracker: physicalTracker, ct: ct);
        action.Confirm(new FlowActionPosition(mxOff, myOff, safeZ), "ST107放料前XY微调完成");
        action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(mxOff, myOff, mzOff), "ST107打号机下降放料");
        action.MarkCommandSent();
        pos3OperationalTracker.BeginZDown(mzOff);
        try
        {
            await crane.MoveAbsoluteAsync(-1, -1, mzOff, ct: ct);
            pos3OperationalTracker.CompleteZDown();
            action.Confirm(new FlowActionPosition(mxOff, myOff, mzOff), "ST107放料Z下降命令成功返回");
        }
        catch (PressureStopException)
        {
            pos3OperationalTracker.MarkZUnknown("打号机放料Z下降触发下压保护，恢复后实际位置需后续安全高度确认");
            Console.WriteLine("[Line1Front] [打号机] ⚡ 下压触发(已接触)→恢复"); 
            await crane.RecoverFromPressureStopAsync(ct);
            throw;
        }
        catch (Exception)
        {
            pos3OperationalTracker.MarkZUnknown("打号机放料Z下降调用异常，实际Z位置需人工确认");
            throw;
        }
        pos3OperationalTracker.BeginPlacementMagnetOff("ST107打号机");
        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(mxOff, myOff, mzOff), "ST107退磁放下工件");
        action.MarkCommandSent();
        try
        {
            await crane.MagnetOffAsync(ct);
            pos3OperationalTracker.CompletePlacementMagnetOff("ST107打号机");
            action.Confirm(new FlowActionPosition(mxOff, myOff, mzOff), "ST107退磁命令成功返回");
            action.RecordFeedback(FlowActionPosition.Unknown, false, false, "打号机退磁放料成功，当前载具不持件");
            action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "工件已在ST107，等待打号文件握手和取回");
            pos3OperationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST107打号机放料与文件握手",
                "打号机目标位退磁成功返回；等待Z回升和文件握手完成");
        }
        catch (Exception ex)
        {
            pos3OperationalTracker.FailMagnetOff();
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                pos3OperationalSite with { Station = "ST107", ActionStage = "打号机放料退磁" }, pos3OperationalTracker, ex,
                "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: true);
            throw;
        }
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "ST107", "打号机放料后Z回升与文件握手", "打号机Z回升／写A文件／等待B回执（具体失败见原因）");
        pos3OperationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.FileHandshake, "ST107打号机文件握手");
        try
        {
        Console.WriteLine($"[Line1Front] [打号机] 退磁完成 ✓ {wp.IdentityText}");

        // ── ④b. 退磁后立即启动: Z升安全(并发) + 打号机文件握手 ──
        //    天车Z升和打号机打号同时进行, 不再串行等待
        const string markerDir = MarkerSharePath;
        // 连接网络共享
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("net", @"use \\192.168.2.67\1 /user:ggbao 123456")
            {
                CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line1Front] [打号机] 连接共享异常(可忽略): {ex.Message}");
        }

        if (!Directory.Exists(markerDir))
        {
            Console.WriteLine($"[Line1Front] [打号机] ✘ 共享目录不存在: {markerDir}"); 
            throw new DirectoryNotFoundException($"打标机共享目录不存在: {markerDir}");
        }
        Console.WriteLine($"[Line1Front] [打号机] ✓ 共享目录已连接: {markerDir}");

        var file3 = Path.Combine(markerDir, "3.txt");
        var fileA = Path.Combine(markerDir, "A.txt");
        fileB = Path.Combine(markerDir, "B.txt");
        string content = $"{wp.MarkingContent}\r\n{wp.Diameter}";

        // 清旧B.txt(防上次残留导致打号失败)
        if (File.Exists(fileB)) { File.Delete(fileB); Console.WriteLine("[Line1Front] [打号机] ⚠ 发现旧B.txt已删除"); }

        // Z升+写A.txt 并发启动
        markerZUp = markerZ - 300 + _craneOffsetZ; // 上升目标Z = 取料位-200+天车偏移
        pos3OperationalTracker.BeginSafeZReturn(markerZUp, "打号机放料后Z升命令已启动，尚未取得成功返回");
        async Task MoveMarkerZUpWithEvidenceAsync()
        {
            await crane.MoveAbsoluteAsync(-1, -1, markerZUp, ct: ct);
            pos3OperationalTracker.ConfirmSafeZ(markerZUp, _cfg.AbsMove.Tolerance, "打号机放料后Z升命令自身成功返回");
            physicalTracker?.TryConfirmSafeZ(markerZUp, _cfg.AbsMove.Tolerance, "打号机放料后Z升命令自身成功返回");
        }
        var zUpTask = MoveMarkerZUpWithEvidenceAsync();
        Console.WriteLine($"[Line1Front] [打号机] Z升+写A.txt 并发启动... Z升目标={markerZUp}");
        // 写A.txt立即启动(不等Z升) → 打号机开始打标
        Console.WriteLine($"[Line1Front] [打号机] 写A.txt: '{content.Replace("\n", " | ")}'");
        pos3OperationalTracker.BeginMonitorStep(OperationalMonitorStepKind.FileHandshake, "ST107打号机文件握手", "打号机A文件写入与B文件完成回执等待已开始，结果未知");
        var writeATask = File.WriteAllTextAsync(fileA, content, Encoding.ASCII, ct);
        await Task.WhenAll(zUpTask, writeATask);  // 等Z升+写A都完成
        Console.WriteLine($"[Line1Front] [打号机] Z升完成 + A.txt已写入 ✓ {wp.IdentityText}");

        // ── 轮询等 B.txt (打标完成, 1小时超时) ──
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "ST107", "打号机文件握手", "等待打号机B文件完成回执");
        const int markerTimeoutSeconds = 60 * 60;
        Console.WriteLine("[Line1Front] [打号机] 等待B.txt (超时1小时/3600s)...");
        var dl = DateTime.UtcNow.AddSeconds(markerTimeoutSeconds);
        int pollCount = 0;
        while (!File.Exists(fileB) && DateTime.UtcNow < dl)
        {
            await Task.Delay(500, ct);
            pollCount++;
            if (pollCount % 20 == 0) Console.WriteLine($"[Line1Front] [打号机]   轮询中...({pollCount * 500 / 1000}s)");
        }
        if (!File.Exists(fileB)) throw new TimeoutException("打标机超时：1小时(3600s)未生成 B.txt");
        pos3OperationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.FileHandshake, "ST107打号机文件握手", "打号机B文件已出现，文件握手成功返回");
        pos3OperationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST107打号机放料与文件握手",
            "退磁放料、Z回升及打号文件握手均成功返回");
        Console.WriteLine($"[Line1Front] [打号机] B.txt已生成 ✓ {wp.IdentityText}");
        }
        catch (Exception ex)
        {
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                pos3OperationalSite with { Station = "ST107", ActionStage = "ST107退磁放料后、取回前交接未闭环", LastConfirmedLocation = "ST107" },
                pos3OperationalTracker, ex,
                "ST107退磁成功返回后、后续取回尚未开始时发生异常；工件位置推定为ST107，需人工确认", true,
                holdingWorkpiece: false, placed: true, handoffClosed: false);
            throw;
        }

        // Z降充磁取料→X11检测→Z升→删B.txt
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "中转架（尚未选择）", "打号机取回", "打号机取回前XY绝对编码器微调");
        int markerZPickup = markerZ + _craneOffsetZ; // 取料Z = 公式Z + 天车偏移
        Console.WriteLine($"[Line1Front] [前天车] Z降={markerZPickup} 充磁取料 → X11检测 → 删B.txt");
        await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
            crane, _cfg, CraneFront1No, "ST107", "1号线前天车-打号机取回前",
            reporter: _exceptionReporter,
            failureContext: OperationalEventContextFactory.FineTuneFailure(
                scope: "1号线", engine: "前端引擎", deviceNo: CraneFront1No.ToString(), station: "ST107",
                actionStage: "打号机取回前XY微调", workpiece: wp,
                source: OperationalEventContextFactory.ConfirmedLocation("ST107", "本物理周期来源"),
                target: EvidenceValue<string>.Unknown("中转架位置尚未选择"),
                owner: "1号线前天车", targetZ: EvidenceValue<int>.Confirmed(markerZPickup, "已有打号机取料Z公式与偏移"),
                physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, false, "打号机B文件已生成", "ST107")),
            actionId: actionId, physicalTracker: physicalTracker, ct: ct);
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "中转架（尚未选择）", "打号机取回");
        // 打号机取回是同板的第二次独立取板，重新检查放磁及X11，不沿用Pos3那次持件反馈。
        // markerZPickup保留原打号机台面/直径/偏移公式，补偿以这次实际停稳Z重新计算。
        var markerPickup = await PickupProbeHelper.PickAsync(crane, markerZPickup, pickupStableDelayMs,
            action, markerOperationalTracker, ct,
            settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm));
        int mPickZ = markerPickup.ActualZ;
        physicalTracker?.TryObserveX11(true);
        // 回安全位置
        action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(mxOff, myOff, markerZUp), "ST107取回后Z上升安全高度");
        action.MarkCommandSent();
        await crane.MoveAbsoluteAsync(-1, -1, markerZUp, ct: ct);
        action.Confirm(new FlowActionPosition(mxOff, myOff, markerZUp), "ST107取回后Z安全回升命令成功返回");
        markerOperationalTracker.ConfirmSafeZ(markerZUp, _cfg.AbsMove.Tolerance, "打号机取回后Z升命令成功返回");
        physicalTracker?.TryConfirmSafeZ(markerZUp, _cfg.AbsMove.Tolerance, "打号机取回后Z升命令成功返回");
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "中转架（尚未选择）", "打号机取回后文件清理", "删除打号机B文件");
        File.Delete(fileB);
        Console.WriteLine($"[Line1Front] [打号机] ✓ 完成 {wp.IdentityText}");
    }

    /// <summary>
    /// 中转架放料:
    ///   已在外层持有ZoneMT+ZoneTS → 读M811~M813物理信号 → 选第一个空位(ST105→ST101→ST106)
    ///   → XY到中转架 → Z降(公式) → 退磁放下 → Z升安全
    ///   注意：二次判断只看物理有板信号；软件缓存残留不能阻止放料，缓存异常由放料后的OnRackPlaced生命线处理。
    /// </summary>
    private async Task PlaceOnTransferRackAsync(CraneService crane, WorkpieceCache wp, int safeZ, string actionId,
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker,
        OperationalPhysicalCycleTracker operationalTracker,
        OperationalEventContextFactory.OperationalPhysicalEventSite operationalSite,
        FlowActionContext action,
        CancellationToken ct, bool transferRackLockAlreadyHeld = false)
    {
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "中转架（尚未选择）", "选择中转架放料位");
        bool placedOnRack = false;
        bool rackCacheNotified = false;
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnRackPlaced中转架缓存通知");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "中转架放料与缓存通知");
        string? notifiedRackStation = null;
        try
        {
            if (_rackSvc == null || !_rackSvc.IsConnected) throw new InvalidOperationException("中转架服务未连接");
            // ── 读三个中转架状态 ──
            var status = await _rackSvc.ReadAllStatusAsync(ct);
            Console.WriteLine(
                $"[Line1Front] [中转架] M811={status.Station1HasPlate} M812={status.Station2HasPlate} M813={status.Station3HasPlate}");

            // ── 按优先级选空位 (M811→ST105, M812→ST101, M813→ST106) ──
            //    现场策略：前端放料的二次判断只看物理信号。即使软件缓存残留，只要物理信号无板就允许放；
            //    放料后的OnRackPlaced必须成功，否则暂停弹窗要求人工补缓存。
            string? rackStation = null;
            if (!status.Station1HasPlate) rackStation = "ST105";
            else if (!status.Station2HasPlate) rackStation = "ST101";
            else if (!status.Station3HasPlate) rackStation = "ST106";
            else throw new InvalidOperationException("1号线3个中转架全满，无法放料！");//抛出异常  finally会释放ZoneTS+ZoneMT

            FlowAlarmPresentationContext.RecordTransferPhase("ST107", rackStation, "中转架放料");
            if (!TryGetStationCoords(rackStation, out int rx, out int ry, out int rz))
                throw new InvalidOperationException($"未找到 {rackStation}");
            Console.WriteLine($"[Line1Front] [中转架] 选中 {rackStation}({rx},{ry},{rz})");

            // ── XY先到位 → Z降(公式+偏移) → 退磁 → Z升 ──   
            int rxOff = rx + _craneOffsetX, ryOff = ry + _craneOffsetY;
            action.SetTarget(rackStation, $"已选择实际中转架目标{rackStation}");
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(rxOff, ryOff, safeZ), $"XY到{rackStation}中转架上方");
            action.MarkCommandSent();
            await crane.MoveAbsoluteAsync(rxOff, ryOff, -1, ct: ct); // XY到中转架上方
            action.Confirm(new FlowActionPosition(rxOff, ryOff, safeZ), $"{rackStation}目标XY命令成功返回");
            action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(rxOff, ryOff, safeZ), $"{rackStation}放料前XY微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                crane, _cfg, CraneFront1No, rackStation, $"1号线前天车-{rackStation}放料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "1号线", engine: "前端引擎", deviceNo: CraneFront1No.ToString(), station: rackStation,
                    actionStage: $"{rackStation}中转架放料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation("ST107", "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation(rackStation, "已选择的空中转架"),
                    owner: "1号线前天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后计算中转架放料rackZ"),
                    physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(true, true, physicalTracker, "打号机取回X11=1且充磁已返回", "天车/路径中")),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(rxOff, ryOff, safeZ), $"{rackStation}放料前XY微调完成");
            int rackZ = ComputePickupZ(rz, wp.Diameter) + _craneOffsetZ; // Z公式 + 天车Z偏移
            Console.WriteLine($"[Line1Front] [前天车] Z降={rackZ}(公式+偏移{_craneOffsetZ}) 退磁放下");
            action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(rxOff, ryOff, rackZ), $"{rackStation}下降放料");
            action.MarkCommandSent();
            operationalTracker.BeginZDown(rackZ);
            try
            {
                await crane.MoveAbsoluteAsync(-1, -1, rackZ, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(rxOff, ryOff, rackZ), $"{rackStation}放料Z下降命令成功返回");
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("中转架放料Z下降触发下压保护，恢复后实际位置需后续安全高度确认");
                Console.WriteLine("[Line1Front] [中转架] ⚡ 下压触发(已接触)→恢复");
                await crane.RecoverFromPressureStopAsync(ct);
                throw;
            }
            catch (Exception)
            {
                operationalTracker.MarkZUnknown("中转架放料Z下降调用异常，实际Z位置需人工确认");
                throw;
            }

            operationalTracker.BeginPlacementMagnetOff(rackStation);
            action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(rxOff, ryOff, rackZ), $"{rackStation}退磁放下工件");
            action.MarkCommandSent();
            try
            {
                await crane.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff(rackStation);
                action.Confirm(new FlowActionPosition(rxOff, ryOff, rackZ), $"{rackStation}退磁命令成功返回");
                action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, $"工件已在{rackStation}，等待缓存通知闭环");
                operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "中转架放料与缓存通知",
                    $"{rackStation}目标位退磁成功返回；等待Z回安全和缓存通知");
            }
            catch (Exception ex)
            {
                operationalTracker.FailMagnetOff();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                    operationalSite with { Station = rackStation, ActionStage = $"{rackStation}中转架放料退磁", PlannedTarget = rackStation },
                    operationalTracker, ex, "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: true);
                throw;
            }
            // 退磁成功后, 物理工件已经离开天车落在中转架上; 后续任何异常都不能再按“仍在天车/叉上”自动重试。
            placedOnRack = true;
            FlowAlarmPresentationContext.RecordCacheState($"{rackStation}退磁放料成功返回；后端缓存通知尚未确认，未自动回滚下游缓存");
            // z 回升
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(rxOff, ryOff, safeZ), $"{rackStation}放料后Z上升安全高度");
            action.MarkCommandSent();
            await crane.MoveAbsoluteAsync(-1, -1, safeZ , ct: ct);
            action.Confirm(new FlowActionPosition(rxOff, ryOff, safeZ), $"{rackStation}放料后Z安全回升命令成功返回");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "中转架放料后Z升安全命令成功返回");
            Console.WriteLine($"[Line1Front] [中转架] ✓ 放料完成 {rackStation} {wp.IdentityText}");
            // 通知后端引擎: 中转架上有工件了
            notifiedRackStation = rackStation;
            FlowAlarmPresentationContext.RecordTransferPhase("ST107", rackStation, "中转架缓存交接", $"通知后端写入{rackStation}中转架缓存");
            // 工件已经物理放到中转架, 软件缓存写入是生命线: 未绑定或写入失败都必须进入异常处理。
            if (OnRackPlaced == null)
                throw new InvalidOperationException($"前天车已放到{rackStation}, 但中转架缓存回调未绑定");
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnRackPlaced中转架缓存通知", $"OnRackPlaced({rackStation})调用已开始，缓存写入结果未知");
            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(rxOff, ryOff, safeZ), $"通知后端写入{rackStation}中转架缓存");
            OnRackPlaced.Invoke(rackStation, wp);
            FlowAlarmPresentationContext.RecordCacheState($"{rackStation}后端缓存通知成功返回；下游缓存不回滚，当前缓存可能已被后端消费");
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnRackPlaced中转架缓存通知", $"OnRackPlaced({rackStation})成功返回");
            action.Confirm(new FlowActionPosition(rxOff, ryOff, safeZ), $"{rackStation}中转架缓存通知成功返回");
            rackCacheNotified = true;
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "中转架放料与缓存通知",
                $"{rackStation}退磁放料、Z回安全和缓存通知均成功返回");
            wp.ReportStage($"1号线 中转架 {rackStation}");
            // 物理放料和缓存通知已经完成, 先更新本地快照, 避免PLC有版信号下一轮才刷新导致后端暂时看不到。
            if (rackStation == "ST105") DeviceStatus.TransferRack1Free = false;
            else if (rackStation == "ST101") DeviceStatus.TransferRack2Free = false;
            else if (rackStation == "ST106") DeviceStatus.TransferRack3Free = false;
        }
        catch (Exception ex)
        {
            if (placedOnRack && !rackCacheNotified)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with
                    {
                        Station = notifiedRackStation ?? "中转架(未知)",
                        ActionStage = "中转架已退磁放料但缓存通知未闭环",
                        PlannedTarget = notifiedRackStation ?? operationalSite.PlannedTarget
                    },
                    operationalTracker, ex,
                    "退磁成功返回并推定工件已放到中转架，但OnRackPlaced缓存通知未确认", true,
                    holdingWorkpiece: false, placed: true,
                    handoffClosed: false);
                // 物理已放下但后端_wps没有确认写入, 继续运行会形成“架上有板但无缓存”的危险状态。
                _paused = true;
                Console.WriteLine($"[Line1Front] ⚠ 工件已放到中转架{notifiedRackStation ?? "(未知)"}但后端缓存未确认, 前端引擎已暂停, 需人工补缓存/确认现场; 异常={ex.Message}");
                throw new TransferRackCacheException(notifiedRackStation ?? "(未知)", ex);
            }
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Z公式 / 伺服恢复 / 信号等待
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>下压急停后完整恢复 — 委托给 CraneService.RecoverFromPressureStopAsync。
    /// 恢复步骤: 清D4523→强制手动D4001/D4500→清报警D4514→伺服通电D4516→复位三轴触发D4520~D4522→等500ms</summary>
    private async Task RecoverFromPressureStopAsync(CancellationToken ct)
    {
        try
        {
            await _manipulator1!.RecoverFromPressureStopAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line1Front] [机械手1]   ⚠ 恢复异常(忽略): {ex.Message}");
        }
    }

    /// <summary>Z下降公式 — 通用取料/放料用。
    /// Z目标 = 台面Z - Round[(d/2/zFactor1)+(d/2/zFactor2)]</summary>
    private int ComputePickupZ(int rackZ, double diameter)
    {
        double d = diameter;
        int descent = (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));
        int z = rackZ - descent;
        Console.WriteLine($"[Line1Front]   Z公式: {rackZ} - [({d}/2/{_cfg.Grinding.ZFactor1})+({d}/2/{_cfg.Grinding.ZFactor2})] = {rackZ}-{descent}={z}");
        return z;
    }

    /// <summary>初始化设备连接 — 引擎启动时执行一次。
    /// 机械手1复用 ManipulatorConnectionCache(与主页面共享TCP)，货叉和上料架独立连接。</summary>
    private async Task InitServicesAsync(CancellationToken ct)
    {
        // ── 机械手1(ModbusTCP 192.168.2.85:502) — 复用缓存，不建重复连接 ──
        _manipulator1 = _manipulatorCache.GetOrCreateService(1);
        try { if (!_manipulator1.IsConnected) await _manipulator1.ConnectAsync(ct); Console.WriteLine($"[Line1Front] 机械手1 已连接 (复用ManipulatorCache) ✓"); }
        catch (Exception ex) { Console.WriteLine($"[Line1Front] ⚠ 机械手1 连接失败: {ex.Message}"); }

        // ── 货叉 ST711 (MC协议, UseBitReadForM=true, nibble编码) ──
        try
        {
            if (TryGetStationIp("ST711", out string fIp, out int fPort))
            { Console.WriteLine($"[Line1Front] 正在连接货叉 {fIp}:{fPort}..."); _forkSvc = new ForkService("一号线货叉", fIp, fPort > 0 ? fPort : 9000); await _forkSvc.ConnectAsync(ct); Console.WriteLine("[Line1Front] 货叉 已连接"); }
            else { Console.WriteLine("[Line1Front] 未找到 ST711 IP"); }
        }
        catch (Exception ex) { Console.WriteLine($"[Line1Front] 货叉连接失败: {ex.Message}"); }

        // ── 总上料架+中转架 192.168.2.63:9000 ──
        try
        {
            if (_rackSvc == null)
            { _rackSvc = new CenteringRackService("总上料架+中转架", "192.168.2.63", 9000); await _rackSvc.ConnectAsync(ct); }
            else if (!_rackSvc.IsConnected)
            { await _rackSvc.ConnectAsync(ct); }
            Console.WriteLine($"[Line1Front] 总上料架+中转架 已连接 192.168.2.63:9000 (MC) OK");
        }
        catch (Exception ex) { Console.WriteLine($"[Line1Front] 总上料架连接失败: {ex.Message}"); }

        // ── 双头镗 Modbus TCP (提前连接, 机械手取料前需要一直监视R6101状态) ──
        if (TryGetStationIp("ST103", out string bIp, out _))
        {
            try
            {
                _boringSvc = new BoringModbusService("1号线双头镗", bIp);
                await _boringSvc.ConnectAsync(ct);
                Console.WriteLine($"[Line1Front] 双头镗Modbus 已连接 {bIp}:502 ✓ (持续监视R6101)");
            }
            catch (Exception ex) { Console.WriteLine($"[Line1Front] ⚠ 双头镗 连接失败: {ex.Message} (保守拒绝机械手取料, 不跳过R6101检查)"); }
        }
        else { Console.WriteLine("[Line1Front] ⚠ 未找到 ST103 IP，双头镗无法连接"); }

        // ── 读取1号天车校准偏移量 (ST901的x_dis/y_dis/z_dis) ──
        if (_stationCoords.TryGetValue("ST901", out var craneRow))
        { _craneOffsetX = (int)craneRow.XOffset; _craneOffsetY = (int)craneRow.YOffset; _craneOffsetZ = (int)craneRow.ZOffset; }

        Console.WriteLine($"[Line1Front] 设备初始化完成: 机械手1✓ 货叉✓ 上料架✓ 双头镗✓ 天车偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ})");
    }

    /// <summary>等待机械手1回到任一已确认安全Y位 — 轮询 ReadStatusAsync, 容差±10mm, 默认60s超时。
    /// 1000和11500均已由现场确认对两条前天车安全；接受任一位置可避免另一条线刚完成任务后永久等待。</summary>
    private async Task WaitForManipulatorSafeAsync(CancellationToken ct)
    {
        var dl = DateTime.UtcNow.AddMilliseconds(_cfg.Grinding.HandshakeTimeoutMs);
        var nextReconnectAt = DateTime.MinValue;
        var reconnectInterval = TimeSpan.FromSeconds(3);
        bool forceReconnect = false;
        Console.WriteLine($"[Line1Front] 机械手1 安全检查: 任一目标Y={_cfg.SkewBed.Manipulator1Line1SafeY}/{_cfg.SkewBed.Manipulator1Line2SafeY} " +
                          $"容差=±{MotionConfig.SkewBedSection.Manipulator1SafeYTolerance} 超时={_cfg.Grinding.HandshakeTimeoutMs / 1000}s");
        while (DateTime.UtcNow < dl)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _manipulator1 ??= _manipulatorCache.GetOrCreateService(1);

                if (!_manipulator1.IsConnected || forceReconnect)
                {
                    // 读不到机械手状态绝不能当成安全；这里只负责限频恢复通信。
                    // 恢复后仍必须重新读到Y安全位，前天车才允许进入货叉区域。
                    if (DateTime.UtcNow >= nextReconnectAt)
                    {
                        string reason = forceReconnect ? "状态读取失败" : "未连接";
                        await TryReconnectManipulator1Async(reason, forceReconnect, ct);
                        nextReconnectAt = DateTime.UtcNow.Add(reconnectInterval);
                        forceReconnect = false;
                    }

                    await Task.Delay(Math.Max(_cfg.Grinding.SignalPollIntervalMs, 200), ct);
                    continue;
                }

                var s = await _manipulator1.ReadStatusAsync(ct);
                if (s == null)
                {
                    // CraneService读失败时返回null；把它当作通信不可用，下一轮强制断开重连。
                    Console.WriteLine("[Line1Front] 机械手1 状态读取返回null, 将限频重连后重新确认安全位");
                    forceReconnect = true;
                    continue;
                }

                // 前天车只关心机械手是否已离开危险区域，因此两个现场确认安全点均可通过。
                if (_cfg.SkewBed.IsManipulator1AtAnySafeY(s.YPos)) { Console.WriteLine($"[Line1Front] 机械手1 Y={s.YPos} 在安全位 ✓"); return; }
                Console.WriteLine($"[Line1Front] 机械手1 Y={s.YPos} 安全Y={_cfg.SkewBed.Manipulator1Line1SafeY}/{_cfg.SkewBed.Manipulator1Line2SafeY} 等待中...");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.WriteLine($"[Line1Front] 读机械手1异常：{ex.Message}"); }
            await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct); // 紧轮询(50ms)
        }
        throw new TimeoutException($"等待机械手1回安全位超时({_cfg.Grinding.HandshakeTimeoutMs / 1000}s)");
    }

    /// <summary>
    /// 机械手1连接恢复。只恢复通信，不放宽安全条件；调用方必须重新读到安全Y位才可继续动作。
    /// forceDisconnect用于处理IsConnected仍为true但ReadStatus返回null的半断开连接。
    /// </summary>
    private async Task<bool> TryReconnectManipulator1Async(string reason, bool forceDisconnect, CancellationToken ct)
    {
        try
        {
            _manipulator1 ??= _manipulatorCache.GetOrCreateService(1);

            if (forceDisconnect && _manipulator1.IsConnected)
            {
                try { await _manipulator1.DisconnectAsync(); }
                catch (Exception ex) { Console.WriteLine($"[Line1Front] 机械手1 重连前断开旧连接异常: {ex.Message}"); }
            }

            if (_manipulator1.IsConnected) return true;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            Console.WriteLine($"[Line1Front] 机械手1 {reason}, 尝试重连...");
            await _manipulator1.ConnectAsync(linkedCts.Token);
            Console.WriteLine("[Line1Front] 机械手1 重连成功, 继续确认安全位");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line1Front] 机械手1 重连失败({reason}): {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  坐标 / IP 查找 — 从 stationCoords 字典取
    // ═══════════════════════════════════════════════════════════════════
    private bool ShouldLogForkState(ForkStatus status)
    {
        string key = $"{status.RawValue:X4}|{_forkPhase}";
        var now = DateTime.UtcNow;
        bool changed = !string.Equals(_lastForkStateLogKey, key, StringComparison.Ordinal);
        bool heartbeat = now - _lastForkStateLogAtUtc >= StateLogHeartbeat;
        if (!changed && !heartbeat) return false;
        _lastForkStateLogKey = key;
        _lastForkStateLogAtUtc = now;
        return true;
    }

    private void LogSlowCycleIfNeeded(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < SlowCycleWarningMs) return;
        var now = DateTime.UtcNow;
        if (now - _lastSlowCycleLogAtUtc < StateLogHeartbeat) return;
        _lastSlowCycleLogAtUtc = now;
        Console.WriteLine($"[Line1Front] 慢轮告警: IO/业务耗时={elapsed.TotalMilliseconds:F0}ms 轮次={_cycleCount} 货叉阶段={_forkPhase} 缓存={CachedCount}");
    }

    private bool TryGetStationCoords(string stationCode, out int x, out int y, out int z)
    { x = y = z = 0; if (!_stationCoords.TryGetValue(stationCode, out var row)) return false; x = (int)(row.X + row.XOffset); y = (int)(row.Y + row.YOffset); z = (int)(row.Z + row.ZOffset); return true; }

    private bool TryGetStationIp(string stationCode, out string ip, out int port)
    { ip = string.Empty; port = 0; if (!_stationCoords.TryGetValue(stationCode, out var row)) return false; ip = row.Ip; port = row.Port; return !string.IsNullOrWhiteSpace(ip); }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _engineCts.Cancel();
        _markerMonitor.Dispose();
        _engineCts.Dispose();
        _boringSvc?.Dispose();
        _forkSvc?.Dispose();
        if (_ownsManipulatorLock) _manipulatorLock.Dispose();
        _frontEmergencyLock.Dispose();
        _forkDispatchLock.Dispose();
        _craneFrontLock.Dispose();
        if (_ownsTransferRackLock) _transferRackLock.Dispose();
        Console.WriteLine("[Line1Front] 引擎已释放");
    }
}
