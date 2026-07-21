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
    private readonly ConcurrentQueue<WorkpieceCache> _cacheQueue = new();   // 无锁FIFO
    private readonly List<WorkpieceCache> _cachedList = new();              // UI显示用
    private readonly object _cacheLock = new();                              // 保护 _cachedList
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
    private readonly object _frontCraneTaskLock = new();
    private CraneTaskSnapshot _frontCraneTask = CraneTaskSnapshot.Idle(1, "1号线前天车");

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
    private readonly MarkerShareMonitor _markerMonitor = new("1号线打号机", MarkerSharePath, "ggbao", "123456");
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
            _frontCraneTask = new CraneTaskSnapshot(true, CraneFront1No, "1号线前天车", WorkpieceDisplaySnapshot.From(wp), stage, source, target, manual, manualText, DateTime.UtcNow);
    }

    private void ClearFrontCraneTask()
    {
        lock (_frontCraneTaskLock) _frontCraneTask = CraneTaskSnapshot.Idle(CraneFront1No, "1号线前天车");
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
    public async Task<string> EmergencyContinuePlateOnForkAsync(CancellationToken ct = default)
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
    { _cacheQueue.Enqueue(wp); lock (_cacheLock) _cachedList.Add(wp); Console.WriteLine($"[Line1Front] 📥 入缓存 {wp.IdentityText} d={wp.Diameter} L={wp.Length} 队列={CachedCount}"); }

    /// <summary>清空缓存 — 丢弃所有未处理工件。</summary>
    public void ClearCache() { while (_cacheQueue.TryDequeue(out _)) { } lock (_cacheLock) _cachedList.Clear(); }

    /// <summary>出队 — 从 FIFO 队列头部取出一个工件。成功返回true，队列空返回false。</summary>
    private bool TryDequeueCache(out WorkpieceCache wp)
    { if (_cacheQueue.TryDequeue(out wp)) { lock (_cacheLock) _cachedList.Remove(wp); return true; } wp = default; return false; }

    /// <summary>
    /// 机械手取料尚未确认吸住前发生异常时, 把刚出队的数据退回缓存。
    /// 这里追加到队尾, 牺牲极少见异常场景下的顺序, 换取不丢工件数据和不盲动。
    /// </summary>
    private void RequeueFrontCache(WorkpieceCache wp)
    {
        _cacheQueue.Enqueue(wp);
        lock (_cacheLock) _cachedList.Add(wp);
    }

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
                                    if (!(fs.AtStandbyPos && !fs.HasPlate))
                                    {
                                        if (!_m912Sent)
                                        {
                                            Console.WriteLine("[Line1Front] [货叉] ⑤ M912=1 送料到双头镗并自动回待机");
                                            await _forkSvc.GoFeedToBoringAsync(ct);
                                            forkCycleReadTask = null; // 命令写入后不得继续使用写入前状态
                                            _m912Sent = true;
                                        }
                                    }
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
                                        _craneQueue.Enqueue(_currentWp!.Value);
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
                                            _craneQueue.Enqueue(_currentWp!.Value);
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

                                if (!_craneQueue.TryDequeue(out var craneWp))
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
    //    ① 读 D100 板长 (仅日志)
    //    ② ST007 取料: XY→公式Z → 充磁 → X11确认, 未吸住最多下探2次
    //      ②a. MoveAbsoluteAsync(rx, ry, ComputePickupZ(rz,d)) — 下压触发视为异常
    //      ②b. X11=0 → 退磁 → Z每次下探5mm → 再充磁确认
    //      ②c. X11=1后 Z升安全高度
    //    ③ ST711 送叉: XY→公式Z → 退磁 → Z升 → Y回安全
    //      ③a~③c 不再依赖下压信号; 下压触发视为异常保护
    //      ③d. Z升安全
    //      ③e. Y轴回安全位
    //    ④ M801=1 通知上料架取料完成
    //
    //  异常处理:
    //    magnetOn=true → 工件在机械手上悬空 → 清 _currentWp 避免死锁 → 人工介入
    //    magnetOn=false → 工件已安全放下 → 正常
    //
    //  锁: 在 finally 中释放 _manipulatorLock，无论成功或失败
    // ═══════════════════════════════════════════════════════════════════
    private async Task ProcessManipulatorPickupAsync(WorkpieceCache wp, CancellationToken ct)
    {
        string operationalActionId = OperationalEventContextFactory.NewActionId("L1-MANIPULATOR-ST007");
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

            // ① 读 D100 板长 — 仅日志输出，不影响流程
            int plateLen = 0;
            if (_rackSvc != null)
            {
                plateLen = await _rackSvc.ReadPlateLengthAsync(ct);
                Console.WriteLine($"[Line1Front] [机械手1] ① D100板长={plateLen}mm");
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
            await _manipulator1!.SetAbsSpeedAsync(m1Spd.X.Speed, m1Spd.X.Accel, m1Spd.X.Decel,
                m1Spd.Y.Speed, m1Spd.Y.Accel, m1Spd.Y.Decel, m1Spd.Z.Speed,
                m1Spd.Z.Accel, m1Spd.Z.Decel, ct);
            // ②a~②c. XY→料架公式Z, 充磁后用X11确认; X11=0最多下探2次
            int pickupZ = formulaZ;
            Console.WriteLine($"[Line1Front] [机械手1] ②a XY→ST007 Z→公式位{pickupZ}");
            operationalTracker.BeginX11Stage("充磁后持件确认");
            for (int retry = 0; retry <= 2; retry++)
            {
                try
                {
                    if (retry == 0)
                    {
                        operationalTracker.BeginZDown(pickupZ);
                        await _manipulator1!.MoveAbsoluteAsync(rx, ry, pickupZ, ct: ct);
                    }
                    else
                    {
                        pickupZ += 5;
                        Console.WriteLine($"[Line1Front] [机械手1]   X11=0 → 退磁后Z下探第{retry}次 → {pickupZ}");
                        operationalTracker.BeginZDown(pickupZ);
                        await _manipulator1!.MoveAbsoluteAsync(-1, -1, pickupZ, timeoutMs: 10_000, ct: ct);
                    }
                    operationalTracker.CompleteZDown();
                }
                catch (PressureStopException ex)
                {
                    operationalTracker.MarkZUnknown("ST007取料Z下降触发下压保护，恢复后实际位置需人工确认");
                    await RecoverFromPressureStopAsync(ct);
                    throw new InvalidOperationException($"机械手1 ST007公式取料触发下压信号, Z={pickupZ}, 已停止流程", ex);
                }

                Console.WriteLine($"[Line1Front] [机械手1] ====== ②b 充磁 START (第{retry + 1}次) ======");
                operationalTracker.BeginMagnetOn();
                try
                {
                    await _manipulator1!.MagnetOnAsync(ct);
                    operationalTracker.CompleteMagnetOn();
                }
                catch (Exception ex)
                {
                    operationalTracker.FailMagnetOn();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                        operationalSite with { ActionStage = $"ST007第{retry + 1}次充磁" }, operationalTracker, ex,
                        "充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                    throw;
                }
                magnetOn = true;
                Console.WriteLine($"[Line1Front] [机械手1] ====== ②b 充磁 DONE ✓ {wp.IdentityText} ======");
                Console.WriteLine($"[Line1Front] [机械手1]   等X11确认吸住... {wp.IdentityText}");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct);
                operationalTracker.BeginX11Attempt();
                try
                {
                    holdingWorkpiece = await _manipulator1.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(holdingWorkpiece, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"ST007充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后一次成功值（如有）", true);
                    throw;
                }
                Console.WriteLine($"[Line1Front] [机械手1]   X11={(holdingWorkpiece ? "1(已吸住)" : "0(未吸住)")} @Z={pickupZ} {wp.IdentityText}");
                if (holdingWorkpiece)
                    break;

                operationalTracker.BeginMagnetOff();
                try
                {
                    await _manipulator1.MagnetOffAsync(ct);
                    operationalTracker.CompleteMagnetOff();
                }
                catch (Exception ex)
                {
                    operationalTracker.FailMagnetOff();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                        operationalSite with { ActionStage = $"ST007第{retry + 1}次X11未确认后的退磁" }, operationalTracker, ex,
                        "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                    throw;
                }
                magnetOn = false;
            }

            if (!holdingWorkpiece)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                    operationalSite with { ActionStage = "ST007三次X11均未确认持件" }, operationalTracker, null,
                    "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在ST007", true,
                    holdingWorkpiece: false);
                throw new InvalidOperationException($"机械手1充磁后X11仍为0, 已按公式Z+2次下探尝试, 最终Z={pickupZ}, 拒绝继续送叉");
            }

            // ②d. Z回原点0 (取料完成先回Z, 下压误触清除后重试最多3次)
            Console.WriteLine("[Line1Front] [机械手1] ②d Z回原点0");
            bool pickupZAtZero = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    await _manipulator1!.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
                    operationalTracker.ConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "ST007取料后Z回0命令成功返回");
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
                await _manipulator1!.MoveAbsoluteAsync(f1x, f1y, forkPlaceZ, timeoutMs: 240_000, ct: ct);
                operationalTracker.CompleteZDown();
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
                await _manipulator1!.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff("ST711货叉");
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
            Console.WriteLine($"[Line1Front] [机械手1]   退磁放下 ✓ {wp.IdentityText}");

            // ③d. Z回原点0 (送完货叉先回Z, 下压误触清除后重试最多3次)
            Console.WriteLine("[Line1Front] [机械手1]   Z回原点0");
            bool placeZAtZero = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    await _manipulator1!.MoveAbsoluteAsync(-1, -1, 0, ct: ct);
                    operationalTracker.ConfirmSafeZ(0, _cfg.AbsMove.Tolerance, "ST711放料后Z回0命令成功返回");
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
            await _manipulator1!.MoveAbsoluteAsync(-1, releaseSafeY, -1, ct: ct);
            Console.WriteLine($"[Line1Front] [机械手1]   Y={releaseSafeY} Z=0，已离开1号线叉区 ✓");

            if (_rackSvc == null)
                throw new InvalidOperationException("总上料架服务为空，M801未写入，禁止放行货叉");
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801取料完成通知", "M801取料完成通知调用已开始，PLC写入结果未知");
            await _rackSvc.SetPickupDoneAsync(ct);
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M801取料完成通知", "M801取料完成通知成功返回");
            Console.WriteLine($"[Line1Front] [机械手1] ④ M801=1→0 取料完成 ✓ {wp.IdentityText}");

            // 门闩必须放在M801成功之后；主循环可能与本方法并行，提前置位会让货叉在M801失败时误启动。
            _manipulatorClearedFork = true;
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST711货叉完整交接",
                "退磁放料、Z回安全、机械手安全退出和M801通知均成功返回");
            Console.WriteLine($"[Line1Front] [机械手1]   货叉放行门闩=ON，双头镗可开始交互；机械手继续回取板待机点");

            // ⑤ 第二阶段：保持两线共享机械手锁，继续回总上料架附近的待机Y。
            //    不新建Task；本方法本身已在后台执行，货叉主循环会在门闩打开后与本移动自然并行。
            if (!await MoveManipulator1ToPickupStandbyAfterForkReleaseAsync(wp, ct))
                return; // 货叉门闩保持开启，但共享机械手位置不确定；告警已同步暂停两条线。

            Console.WriteLine($"[Line1Front] [机械手1] ═══ 取料送叉完成 {wp.IdentityText} ═══");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line1Front] [机械手1] ✘ 异常：{ex.Message}");
            if (holdingWorkpiece && !placedOnFork)
            {
                // X11已确认吸住, 但还没有安全放到货叉; 此时最危险, 必须停住等人工确认。
                lock (_wpLock) { _currentWp = null; }
                _paused = true;
                Console.WriteLine("[Line1Front] ⚠ 机械手1已吸住工件但未放到货叉！引擎已暂停, 需人工确认机械手/工件位置！");
                OnSafetyAlarm?.Invoke($"1号线机械手1处理{wp.IdentityText}时发生异常：已吸住工件但未放到货叉。引擎已暂停，请人工确认机械手和工件位置。异常：{ex.Message}");
            }
            else if (placedOnFork)
            {
                // 板已放在货叉不等于允许启动：Z、首次安全Y或M801任一步失败时，门闩仍为false。
                if (!_manipulatorClearedFork)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                        operationalSite with { Station = "ST711", ActionStage = "ST711已退磁放料但安全退出/M801未闭环" },
                        operationalTracker, ex,
                        "退磁成功返回并推定工件已放到货叉，但Z回0、机械手安全Y或M801通知未完整成功", true,
                        holdingWorkpiece: false, placed: true,
                        handoffClosed: false);
                    _paused = true;
                    Console.WriteLine("[Line1Front] ⚠ 工件已放在货叉，但机械手安全退出/M801未完整完成；货叉门闩保持关闭");
                    OnSafetyAlarm?.Invoke($"1号线机械手1已将{wp.IdentityText}放到货叉，但Z回0、Y到货叉释放安全点或M801通知未完整完成。货叉未启动，引擎已暂停，请人工确认。异常：{ex.Message}");
                }
                else
                {
                    // 第二阶段回待机由专用方法自行判断安全并告警；门闩已开时不回退货叉状态。
                    Console.WriteLine("[Line1Front] [机械手1] 工件已安全放在货叉且门闩已开，保持 _currentWp 让 ForkBoringPhase 接管");
                }
            }
            else
            {
                // 尚未确认从总上料架吸起: 不能让_currentWp继续占着在途, 否则货叉会永远等一块不存在的板。
                if (magnetOn)
                {
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        await _manipulator1!.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                    }
                    catch (Exception offEx)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = "ST007取料异常后的尽力退磁" }, operationalTracker, offEx,
                            "停机前尽力退磁异常，实际退磁结果未知；保持原吞错路径", true,
                            holdingWorkpiece: holdingWorkpiece);
                    }
                }
                lock (_wpLock) { _currentWp = null; }
                RequeueFrontCache(wp);
                _paused = true;
                Console.WriteLine("[Line1Front] ⚠ 机械手1未确认吸住工件, 已退回工件缓存并暂停, 请人工确认总上料架/机械手状态");
                OnSafetyAlarm?.Invoke($"1号线机械手1处理{wp.IdentityText}时发生异常：未确认吸住工件，任务已退回缓存。引擎已暂停，请确认总上料架和机械手状态。异常：{ex.Message}");
            }
        }
        finally
        {
            // 无论如何释放锁
            Console.WriteLine("[Line1Front] [DEBUG] 机械手释放 _manipulatorLock");
            _manipulatorLock.Release();
        }  // 无论如何释放锁
    }

    /// <summary>
    /// 1号线货叉已安全放行后，把机械手1预定位到总上料架附近，缩短下一次取板空行程。
    /// 调用期间仍持有两线共享的_manipulatorLock；货叉依靠独立门闩和状态机并行工作。
    /// </summary>
    private async Task<bool> MoveManipulator1ToPickupStandbyAfterForkReleaseAsync(
        WorkpieceCache wp, CancellationToken ct)
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
            await _manipulator1!.MoveAbsoluteAsync(-1, targetY, -1, ct: ct);

            // MoveAbsoluteAsync正常返回后再读一次实际Y，避免通信/PLC异常把“命令完成”误当成“到位完成”。
            var status = await _manipulator1.ReadStatusAsync(ct);
            if (status == null || Math.Abs(status.YPos - targetY) > MotionConfig.SkewBedSection.Manipulator1SafeYTolerance)
                throw new InvalidOperationException(status == null
                    ? $"回取板待机Y={targetY}后状态读取为空"
                    : $"回取板待机Y未到位，实际Y={status.YPos}，目标Y={targetY}");

            Console.WriteLine($"[Line1Front] [机械手1]   取板待机Y={status.YPos}到位 ✓，下一块板可从近端取料");
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
    private async Task ProcessCraneFrontAsync(WorkpieceCache wp, CancellationToken ct = default)
    {
        string actionId = OperationalEventContextFactory.NewActionId("L1-FRONT");
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
            await crane.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel, crSpd.Y.Speed, crSpd.Y.Accel,
                crSpd.Y.Decel, crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);
            // 新任务可能承接上一次异常/急停后的天车位置；首次去Pos3横移前必须实时确认Z已回零。
            Console.WriteLine("[Line1Front] [前天车] 去货叉Pos3前确认Z=0±5mm");
            await crane.EnsureZAtZeroAsync(5, ct);
            physicalTracker?.TryConfirmSafeZ(0, 5, "去ST713取料前EnsureZAtZeroAsync成功返回");
            // X绝对编码器微调必须在Z下降前执行: 先XY到货叉Pos3上方, 复核D5014~D5015, 合格后才下降。
            await crane.MoveAbsoluteAsync(f2xOff, f2yOff, -1, ct: ct);
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
            pos3OperationalTracker.BeginZDown(f2zOff);
            try
            {
                await crane.MoveAbsoluteAsync(-1, -1, f2zOff, ct: ct);
                pos3OperationalTracker.CompleteZDown();
            }
            catch (PressureStopException)
            {
                pos3OperationalTracker.MarkZUnknown("货叉Pos3取料Z下降触发下压保护，恢复后实际位置需后续安全高度确认");
                Console.WriteLine("[Line1Front] [前天车]   ⚡ 下压触发→恢复");
                await crane.RecoverFromPressureStopAsync(ct);
            }

            // X11检测: 充磁→等3s→查X11(63497)→没吸到就退磁→Z↓5mm→充磁→再查, 最多2次
            Console.WriteLine("[Line1Front] [前天车]   充磁→等3s→X11检测");
            int f2PickZ = f2zOff;
            pos3OperationalTracker.BeginX11Stage("充磁后持件确认");
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("[Line1Front] [前天车]   充磁");
                    pos3OperationalTracker.BeginMagnetOn();
                    try
                    {
                        await crane.MagnetOnAsync(ct);
                        pos3OperationalTracker.CompleteMagnetOn();
                    }
                    catch (Exception ex)
                    {
                        pos3OperationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            pos3OperationalSite with { ActionStage = "货叉Pos3首次充磁" }, pos3OperationalTracker, ex,
                            "充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                        throw;
                    }
                    magnetOn = true;
                }
                else
                {
                    Console.WriteLine($"[Line1Front] [前天车]   退磁→Z↓到{f2PickZ}→充磁");
                    pos3OperationalTracker.BeginMagnetOff();
                    try
                    {
                        await crane.MagnetOffAsync(ct);
                        pos3OperationalTracker.CompleteMagnetOff();
                    }
                    catch (Exception ex)
                    {
                        pos3OperationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            pos3OperationalSite with { ActionStage = "货叉Pos3 X11重试前退磁" }, pos3OperationalTracker, ex,
                            "退磁方法异常，实际退磁结果未知", true);
                        throw;
                    }
                    magnetOn = false;
                    pos3OperationalTracker.BeginZDown(f2PickZ);
                    try
                    {
                        await crane.MoveAbsoluteAsync(-1, -1, f2PickZ, ct: ct);
                        pos3OperationalTracker.CompleteZDown();
                    }
                    catch (PressureStopException)
                    {
                        pos3OperationalTracker.MarkZUnknown("货叉Pos3重试下探触发下压保护，恢复后实际位置需后续安全高度确认");
                        await crane.RecoverFromPressureStopAsync(ct);
                    }

                    pos3OperationalTracker.BeginMagnetOn();
                    try
                    {
                        await crane.MagnetOnAsync(ct);
                        pos3OperationalTracker.CompleteMagnetOn();
                    }
                    catch (Exception ex)
                    {
                        pos3OperationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            pos3OperationalSite with { ActionStage = "货叉Pos3下探后再次充磁" }, pos3OperationalTracker, ex,
                            "重试充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                        throw;
                    }
                    magnetOn = true;
                }

                Console.WriteLine("[Line1Front] [前天车]   等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                pos3OperationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await crane.ReadXBitAsync(63497, ct);
                    pos3OperationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    pos3OperationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        pos3OperationalSite with { ActionStage = $"货叉Pos3充磁后X11第{retry + 1}次读取" }, pos3OperationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后一次成功值（如有）", true);
                    throw;
                }
                physicalTracker?.TryObserveX11(x11);
                Console.WriteLine($"[Line1Front] [前天车]   X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    Console.WriteLine($"[Line1Front] [前天车]   ✓ X11=1 已吸到(保持在取料位Z={f2PickZ})");
                    break;
                }

                if (retry > 1)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                        pos3OperationalSite with { ActionStage = "货叉Pos3三次X11均未确认持件" }, pos3OperationalTracker, null,
                        "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在货叉Pos3", true,
                        holdingWorkpiece: false);
                    throw new Exception("货叉Pos3取料失败: 2次充磁后X11仍=0");
                }
                f2PickZ += 5;
                Console.WriteLine($"[Line1Front] [前天车]   ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ②.5 X11已经确认吸住: 现场确认Pos3低位持板时货叉回待机不会碰天车/工件。
            //      Z升安全和M911回待机并行执行, 两边都完成后才继续去打号机; 任一失败仍按"工件在天车上"暂停。
            Console.WriteLine("[Line1Front] [前天车] ②.5 Z升安全高度 与 M911货叉回待机 并行");
            var zSafeTask = crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            Task forkStandbyTask = Task.CompletedTask;
            if (_forkSvc != null) // TcpClient.Connected不可靠, 不检查IsConnected
            {
                Console.WriteLine("[Line1Front] [前天车]   并行通知货叉回待机位 M911=1");
                // 清除2/3位置, 通知货叉去待机位置。
                forkStandbyTask = _forkSvc.GoStandbyAsync(ct);
            }
            await Task.WhenAll(zSafeTask, forkStandbyTask);
            pos3OperationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST713取料后Z升安全命令成功返回");
            physicalTracker?.TryConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST713取料后Z升安全命令成功返回");
            Console.WriteLine("[Line1Front] [前天车]   Z安全高度与货叉回待机命令均已完成");

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
            bool zoneTsLocked = false;
            try
            {
                //打号机流程
                await MarkingHandshakeAsync(crane, wp, safeZ, actionId, physicalTracker,
                    pos3OperationalTracker, pos3OperationalSite, markerOperationalTracker, markerOperationalSite, ct);

                Console.WriteLine("[Line1Front] [前天车] ③.5 获取ZoneTS(中转架↔ST108碰撞区), 准备进入中转架...");
                while (!await _safety.TransferSkew1CollisionLock.WaitAsync(TimeSpan.FromSeconds(1), ct))
                {
                    if (_cycleCount % 10 == 1)
                        Console.WriteLine("[Line1Front] [前天车] 等ZoneTS...");
                }
                zoneTsLocked = true;
                Console.WriteLine("[Line1Front] [前天车] ZoneTS已获取 ✓，当前持有ZoneMT+ZoneTS，可进入中转架");

                //中转架放料  放完回升了
                await PlaceOnTransferRackAsync(crane, wp, safeZ, actionId, physicalTracker,
                    markerOperationalTracker, markerOperationalSite, ct);
                _safety.TransferSkew1CollisionLock.Release();
                zoneTsLocked = false;
                _safety.MarkerTransferCollisionLock.Release();
                zoneMtLocked = false;
                Console.WriteLine($"[Line1Front] [前天车] 中转架放料完成且Z已安全, ZoneTS+ZoneMT已释放 ✓ {wp.IdentityText}");
                magnetOn = false; // 放料成功才清标志

                int homeX = _cfg.GetCraneHomeX(CraneFront1No);
                Console.WriteLine($"[Line1Front] [前天车] X→{homeX + _craneOffsetX} Z回原点 并发执行");
                //回homex 回z轴原点 并发执行
                var xTask = crane.MoveAbsoluteAsync(homeX + _craneOffsetX, f2y, -1, ct: ct);
                var zTask = crane.HomeZAsync(ct);
                //两个任务同时执行
                await Task.WhenAll(xTask, zTask);
                ClearFrontCraneTask();
                Console.WriteLine($"[Line1Front] [前天车] ═══ 天车流程完成 {wp.IdentityText} ═══");
            }
            finally
            {
                if (zoneTsLocked)
                {
                    try { _safety.TransferSkew1CollisionLock.Release(); } catch (SemaphoreFullException) { }
                    Console.WriteLine("[Line1Front] [前天车] 异常兜底: ZoneTS已释放");
                }
                if (zoneMtLocked)
                {
                    try { _safety.MarkerTransferCollisionLock.Release(); } catch (SemaphoreFullException) { }
                    Console.WriteLine("[Line1Front] [前天车] 异常兜底: ZoneMT已释放");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line1Front] [前天车] ✘ 异常：{ex.GetType().Name} — {ex.Message}");
            if (ex is TransferRackCacheException rackEx)
            {
                // 工件已经不在天车/叉上, 不能回队列; 保持暂停, 等人工确认中转架缓存。
                // 前天车处理的是上一块工件；此时下一块工件可能已经进入货叉/双头镗。
                // 禁止在前天车异常中复位_forkPhase或清握手门闩，否则会把下一块工件
                // 从WaitingMachine等有效阶段错误改成Idle，造成R6107=1也不写M913的永久卡停。
                _paused = true;
                SetFrontCraneTask(wp, "中转架缓存未确认", "ST107", rackEx.RackStation, true, "工件已放到中转架，请人工补缓存并确认现场");
                Console.WriteLine($"[Line1Front] ⚠⚠⚠ 工件已在{rackEx.RackStation}, 但后端缓存未确认；不回队列，等待人工补缓存/确认现场");
                Console.WriteLine($"[Line1Front] [前天车] 货叉—双头镗阶段保持={_forkPhase}，相关门闩未清除；ZoneMT/ZoneTS已按finally释放，前天车锁将在调度finally释放");
                OnSafetyAlarm?.Invoke($"1号线前天车处理{wp.IdentityText}时，工件已放到{rackEx.RackStation}，但后端缓存未确认。引擎已暂停，货叉—双头镗阶段保持={_forkPhase}、相关门闩未清除。ZoneMT/ZoneTS碰撞区锁已释放，前天车锁将在任务退出时释放。请人工补缓存，确认旧工件和前天车处于安全位置后再恢复。");
            }
            else if (magnetOn)
            {
                // magnetOn=true表示本任务已经进入充磁后的不确定区间；若异常来自打号机超时，
                // 工件可能已经在ST107退磁放下。无论旧工件实际在哪，都不能改动可能属于
                // 下一块工件的货叉/双头镗阶段和门闩，只暂停并交由人工确认现场。
                _paused = true;
                SetFrontCraneTask(wp, "工件位置待人工确认", "流程中", "未知", true, "充磁后异常，工件可能在天车、打号机或中转架");
                Console.WriteLine("[Line1Front] ⚠⚠⚠ 前天车在充磁后的流程区间中断！工件可能在天车、打号机或后续位置，引擎已暂停,需人工处理！");
                Console.WriteLine($"[Line1Front] [前天车] 货叉—双头镗阶段保持={_forkPhase}，相关门闩未清除；ZoneMT/ZoneTS已按finally释放，前天车锁将在调度finally释放");
                OnSafetyAlarm?.Invoke($"1号线前天车处理{wp.IdentityText}时流程中断，工件可能在天车、打号机或后续位置。引擎已暂停，货叉—双头镗阶段保持={_forkPhase}、相关门闩未清除。ZoneMT/ZoneTS碰撞区锁已释放，前天车锁将在任务退出时释放。请人工退磁/处理旧工件并将前天车升到安全位置后再恢复。异常：{ex.Message}");
            }
            else
            {
                // 未充磁→工件还在叉Pos3上→放回天车队列等重试
                //   _forkPhase保持WaitingForkReturnFromBoring/Skip: 叉在Pos3带工件, 阻塞新取料
                //   R6108若已发送则不重发，天车重试只负责把Pos3工件取走
                //   天车重试时从Pos3正常取走工件, 通知货叉回待机, 状态自动恢复
                _craneQueue.Enqueue(wp);
                ClearFrontCraneTask();
                Console.WriteLine($"[Line1Front] [前天车] 未充磁,工件仍在叉Pos3 → 放回天车队列等待重试(队列={_craneQueue.Count})");
            }
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
        CancellationToken ct)
    {
        int markerZUp = 0;
        string fileB = string.Empty;
        if (!TryGetStationCoords("ST107", out int mx, out int my, out int mz)) throw new InvalidOperationException("数据库未找到 ST107");
        Console.WriteLine($"[Line1Front] [打号机] ④ ST107({mx},{my},{mz})");

        // ── ④a. Z降(公式)→退磁放下 ──
        int markerZ = ComputePickupZ(mz, wp.Diameter);
        int mxOff = mx + _craneOffsetX, myOff = my + _craneOffsetY, mzOff = markerZ + _craneOffsetZ;
        Console.WriteLine($"[Line1Front] [前天车] Z降={markerZ} +偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ}) 退磁放下");
        await crane.MoveAbsoluteAsync(mxOff, myOff, -1, ct: ct);
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
        pos3OperationalTracker.BeginZDown(mzOff);
        try
        {
            await crane.MoveAbsoluteAsync(-1, -1, mzOff, ct: ct);
            pos3OperationalTracker.CompleteZDown();
        }
        catch (PressureStopException)
        {
            pos3OperationalTracker.MarkZUnknown("打号机放料Z下降触发下压保护，恢复后实际位置需后续安全高度确认");
            Console.WriteLine("[Line1Front] [打号机] ⚡ 下压触发(已接触)→恢复"); 
            await crane.RecoverFromPressureStopAsync(ct);
        }
        catch (Exception)
        {
            pos3OperationalTracker.MarkZUnknown("打号机放料Z下降调用异常，实际Z位置需人工确认");
            throw;
        }
        pos3OperationalTracker.BeginPlacementMagnetOff("ST107打号机");
        try
        {
            await crane.MagnetOffAsync(ct);
            pos3OperationalTracker.CompletePlacementMagnetOff("ST107打号机");
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
        markerOperationalTracker.BeginZDown(markerZPickup);
        try
        {
            await crane.MoveAbsoluteAsync(-1, -1, markerZPickup, ct: ct);
            markerOperationalTracker.CompleteZDown();
        }
        catch (PressureStopException)
        {
            markerOperationalTracker.MarkZUnknown("打号机取回Z下降触发下压保护，恢复后实际位置需后续安全高度确认");
            Console.WriteLine("[Line1Front] [打号机] ⚡ 下压触发(取料)→恢复"); 
            await crane.RecoverFromPressureStopAsync(ct);
        }

        // X11检测: 充磁→等3s→查X11, 没吸到就退磁→Z↓5mm→充磁→再查, 最多2次
        Console.WriteLine("[Line1Front] [打号机] 充磁→等3s→X11检测");
        int mPickZ = markerZPickup;
        markerOperationalTracker.BeginX11Stage("充磁后持件确认");
        for (int retry = 0; retry <= 2; retry++)
        {
            if (retry == 0)
            {
                Console.WriteLine("[Line1Front] [打号机] 充磁");
                markerOperationalTracker.BeginMagnetOn();
                try
                {
                    await crane.MagnetOnAsync(ct);
                    markerOperationalTracker.CompleteMagnetOn();
                }
                catch (Exception ex)
                {
                    markerOperationalTracker.FailMagnetOn();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                        markerOperationalSite with { ActionStage = "打号机取回首次充磁" }, markerOperationalTracker, ex,
                        "充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                    throw;
                }
            }
            else
            {
                Console.WriteLine($"[Line1Front] [打号机] 退磁→Z↓到{mPickZ}→充磁");
                markerOperationalTracker.BeginMagnetOff();
                try
                {
                    await crane.MagnetOffAsync(ct);
                    markerOperationalTracker.CompleteMagnetOff();
                }
                catch (Exception ex)
                {
                    markerOperationalTracker.FailMagnetOff();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                        markerOperationalSite with { ActionStage = "打号机取回X11重试前退磁" }, markerOperationalTracker, ex,
                        "退磁方法异常，实际退磁结果未知", true);
                    throw;
                }
                markerOperationalTracker.BeginZDown(mPickZ);
                try
                {
                    await crane.MoveAbsoluteAsync(-1, -1, mPickZ, ct: ct);
                    markerOperationalTracker.CompleteZDown();
                }
                catch (PressureStopException)
                {
                    markerOperationalTracker.MarkZUnknown("打号机取回重试下探触发下压保护，恢复后实际位置需后续安全高度确认");
                    await crane.RecoverFromPressureStopAsync(ct);
                }
                markerOperationalTracker.BeginMagnetOn();
                try
                {
                    await crane.MagnetOnAsync(ct);
                    markerOperationalTracker.CompleteMagnetOn();
                }
                catch (Exception ex)
                {
                    markerOperationalTracker.FailMagnetOn();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                        markerOperationalSite with { ActionStage = "打号机取回下探后再次充磁" }, markerOperationalTracker, ex,
                        "重试充磁方法异常，不能证明命令是否发送或磁铁实际状态", true);
                    throw;
                }
            }
            Console.WriteLine("[Line1Front] [打号机] 等3s让X11稳定...");
            await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
            //判断是否有板  63794位置
            markerOperationalTracker.BeginX11Attempt();
            bool x11;
            try
            {
                x11 = await crane.ReadXBitAsync(63497, ct);
                markerOperationalTracker.CompleteX11(x11, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                markerOperationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    markerOperationalSite with { ActionStage = $"打号机取回X11第{retry + 1}次读取" }, markerOperationalTracker, ex,
                    "充磁后X11读取失败；本次值无效并保留最后一次成功值（如有）", true);
                throw;
            }
            physicalTracker?.TryObserveX11(x11);
            Console.WriteLine($"[Line1Front] [打号机] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
            if (x11) { Console.WriteLine($"[Line1Front] [打号机] ✓ X11=1 已吸到(保持在取料位Z={mPickZ}) {wp.IdentityText}"); break; }
            if (retry >1)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                    markerOperationalSite with { ActionStage = "打号机取回三次X11均未确认持件" }, markerOperationalTracker, null,
                    "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在打号机", true,
                    holdingWorkpiece: false);
                throw new Exception("打号机取料失败: 2次充磁后X11仍=0");
            }
            mPickZ += 5;
            Console.WriteLine($"[Line1Front] [打号机] ⚠ X11=0 未吸到, 准备下探5mm重试");
        }
        //回安全位置
        await crane.MoveAbsoluteAsync(-1, -1, markerZUp, ct: ct);
        markerOperationalTracker.ConfirmSafeZ(markerZUp, _cfg.AbsMove.Tolerance, "打号机取回后Z升命令成功返回");
        physicalTracker?.TryConfirmSafeZ(markerZUp, _cfg.AbsMove.Tolerance, "打号机取回后Z升命令成功返回");
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
        CancellationToken ct, bool transferRackLockAlreadyHeld = false)
    {
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

            if (!TryGetStationCoords(rackStation, out int rx, out int ry, out int rz))
                throw new InvalidOperationException($"未找到 {rackStation}");
            Console.WriteLine($"[Line1Front] [中转架] 选中 {rackStation}({rx},{ry},{rz})");

            // ── XY先到位 → Z降(公式+偏移) → 退磁 → Z升 ──   
            int rxOff = rx + _craneOffsetX, ryOff = ry + _craneOffsetY;
            await crane.MoveAbsoluteAsync(rxOff, ryOff, -1, ct: ct); // XY到中转架上方
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
            int rackZ = ComputePickupZ(rz, wp.Diameter) + _craneOffsetZ; // Z公式 + 天车Z偏移
            Console.WriteLine($"[Line1Front] [前天车] Z降={rackZ}(公式+偏移{_craneOffsetZ}) 退磁放下");
            operationalTracker.BeginZDown(rackZ);
            try
            {
                await crane.MoveAbsoluteAsync(-1, -1, rackZ, ct: ct);
                operationalTracker.CompleteZDown();
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("中转架放料Z下降触发下压保护，恢复后实际位置需后续安全高度确认");
                Console.WriteLine("[Line1Front] [中转架] ⚡ 下压触发(已接触)→恢复");
                await crane.RecoverFromPressureStopAsync(ct);
            }
            catch (Exception)
            {
                operationalTracker.MarkZUnknown("中转架放料Z下降调用异常，实际Z位置需人工确认");
                throw;
            }

            operationalTracker.BeginPlacementMagnetOff(rackStation);
            try
            {
                await crane.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff(rackStation);
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
            // z 回升
            await crane.MoveAbsoluteAsync(-1, -1, safeZ , ct: ct);
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "中转架放料后Z升安全命令成功返回");
            Console.WriteLine($"[Line1Front] [中转架] ✓ 放料完成 {rackStation} {wp.IdentityText}");
            // 通知后端引擎: 中转架上有工件了
            notifiedRackStation = rackStation;
            // 工件已经物理放到中转架, 软件缓存写入是生命线: 未绑定或写入失败都必须进入异常处理。
            if (OnRackPlaced == null)
                throw new InvalidOperationException($"前天车已放到{rackStation}, 但中转架缓存回调未绑定");
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnRackPlaced中转架缓存通知", $"OnRackPlaced({rackStation})调用已开始，缓存写入结果未知");
            OnRackPlaced.Invoke(rackStation, wp);
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnRackPlaced中转架缓存通知", $"OnRackPlaced({rackStation})成功返回");
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
