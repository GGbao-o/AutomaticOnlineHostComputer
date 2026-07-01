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
using RackAddr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.CenteringRackAddress;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 2号线前端流程引擎 (S2~S6) — 与1号线共用机械手1
///
/// 【设备清单】
///   机械手1: 与1号线共享(_manipulatorLock由HomeViewModel传入)
///   货叉:    ST712 (192.168.2.89:9000, MC协议)
///   前天车:  ST104/CraneNo=3 (192.168.2.83:502, ModbusTCP)
///   双头镗:  ST402 (192.168.2.73, Syntec CNC SDK)
///   打号机:  ST502 (192.168.2.74, 文件握手)
///   中转架:  ST016/M814, ST017/M815, ST018/M816 (192.168.2.63:9000, MC协议)
///
/// 【主循环 500ms/轮 — 与1号线完全相同】
///   ① 检查M800+缓存→出队→机械手1取料送叉
///   ② 货叉待机位有版→SkipBoring?伸Pos3:M912送料回待机后进双头镗
///   ③ ForkBoringPhase状态机(双头镗Syntec握手+天车触发)
///   ④ 状态快照刷新→UI
///
/// 【与1号线的差异】
///   - 机械手锁共享: _manipulatorLock由HomeViewModel传入同一实例
///   - 中转架寄存器: M814/M815/M816(非M811/M812/M813)
///   - 平衡引擎: 共用Line1BalancingFlowEngine
///   - 其他流程逻辑完全相同
/// </summary>
public sealed class Line2FrontFlowEngine : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    //  依赖注入
    // ═══════════════════════════════════════════════════════════════
    private readonly CraneConnectionCache _craneCache;                  // 天车连接缓存(共享,避免重复TCP)
    private readonly ManipulatorConnectionCache _manipulatorCache;      // 机械手连接缓存(共享)
    private readonly MotionConfig _cfg;                                 // 运动参数配置(速度/Z公式/安全高度等)
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords; // 工位坐标字典(key=站号)
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
    public int DispatchPressure { get { lock (_wpLock) return CachedCount + (_currentWp != null ? 1 : 0); } }

    /// <summary>在途工件 — 机械手取出后到货叉Pos3为止, 入天车队列后立即清空。
    /// 非null时主循环步骤①不出队新工件。lock(_wpLock)保护。</summary>
    private WorkpieceCache? _currentWp;
    private readonly object _wpLock = new();
    /// <summary>天车任务队列 — 货叉Pos3到位后入队, 天车独立循环消费</summary>
    private readonly ConcurrentQueue<WorkpieceCache> _craneQueue = new();

    /// <summary>货叉-双头镗交互状态机 — 完整 clear-before-set 握手。</summary>
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
    /// <summary>R6108 下料完成信号是否已发送(防重复触发)。</summary>
    private volatile bool _boringUnloadDoneSent;
    /// <summary>M912 送料命令是否已发送。M912 是保持命令位，成功写入后不需要每轮重复写。</summary>
    private volatile bool _m912Sent;
    /// <summary>SkipBoring 天车触发是否已执行(防重复触发)。
    /// 与正常下料完成标志类似, 但 SkipBoring 不写双头镗信号, 需独立标志。</summary>
    private volatile bool _skipBoringTriggered;

    // ═══════════════════════════════════════════════════════════════
    //  四把信号量 — 固定锁顺序防死锁
    //    前天车锁顺序: _craneFrontLock → SharedAreaLock → _transferRackLock。
    //    机械手锁和货叉分派锁独立, 不和共享区/中转架锁嵌套。
    // ═══════════════════════════════════════════════════════════════
    private readonly SemaphoreSlim _manipulatorLock;                // 机械手1取料送叉(与1号线共享)
    private readonly SemaphoreSlim _forkDispatchLock = new(1, 1);  // 货叉分派(本线独立,不与机械手锁竞争)
    private readonly SemaphoreSlim _craneFrontLock = new(1, 1);    // 前天车流程
    private readonly SemaphoreSlim _transferRackLock;               // 中转架操作 (与后端引擎共享)
    private readonly SafetyFlags _safety;                      // 前后天车共享安全标志

    private const int CraneFront2No = 3; // 2号线前天车编号(ST104)
    private const string MarkerSharePath = @"\\192.168.2.74\1";
    private int _craneOffsetX, _craneOffsetY, _craneOffsetZ; // 2号线前天车校准偏移量(来自ST104)

    // ═══════════════════════════════════════════════════════════════
    //  设备服务 (InitServicesAsync 中创建)
    // ═══════════════════════════════════════════════════════════════
    private CraneService? _manipulator1;        // 机械手1 (ModbusTCP, 复用ManipulatorCache)
    private ForkService? _forkSvc;              // 货叉 (MC协议, UseBitReadForM=true)
    private CenteringRackService? _rackSvc; // 总上料架+中转架 (共享连接,可重连)
    private BoringModbusService? _boringSvc;    // 双头镗 (Modbus TCP, R区地址=R*2+1)
    private readonly MarkerShareMonitor _markerMonitor = new("2号线打号机", MarkerSharePath, "ggbao", "123456");
    private int _forkReconnectInProgress;       // 货叉后台重连占坑, 防止断线时每轮重复创建连接任务
    private int _boringReconnectInProgress;     // 双头镗后台重连占坑, 防止断线时堆积SDK连接任务

    /// <summary>前端放料到中转架时回调, 通知后端引擎工件数据。参数: (站号, 工件数据)</summary>
    public Action<string, WorkpieceCache>? OnRackPlaced;
    /// <summary>天车任务出队前连续两次读到XYZ全零时通知主页面暂停整条2号线并弹窗。</summary>
    public Action<CraneZeroPositionAlarm>? OnCraneZeroPositionDetected;
    /// <summary>前端进入人工确认暂停时通知主页面弹窗。</summary>
    public Action<string>? OnSafetyAlarm;

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
    //  DeviceStatus — 引擎持续更新，SyncLine2StatusToCardsAsync 1.5s刷到UI
    // ═══════════════════════════════════════════════════════════════
    public Line2DeviceStatus DeviceStatus { get; } = new();

    /// <summary>1号线设备在线状态快照（只读缓存，UI绑定不另建TCP连接）</summary>
    public sealed class Line2DeviceStatus
    {
        public bool Manipulator1Connected { get; set; }
        public int Manipulator1Y { get; set; }
        public bool Manipulator1Safe { get; set; }

        public bool ForkConnected { get; set; }
        public bool ForkHasPlate { get; set; }
        public string ForkPosition { get; set; } = "未知";
        public string ForkCommand { get; set; } = "无命令";
        public bool RackConnected { get; set; }
        public bool RackRequestPickup { get; set; }
        public int RackPlateLength { get; set; }
        public bool TransferRack4Free { get; set; }
        public bool TransferRack5Free { get; set; }
        public bool TransferRack6Free { get; set; }
        public bool M818CanPlace { get; set; } // ST020允许后天车放料 (192.168.2.63 M818)
        public bool M819PlaceDone { get; set; } // ST020后天车放料完成 (M819)
        public bool M820CanPlace { get; set; } // ST021允许后天车放料 (M820)
        public bool M821PlaceDone { get; set; } // ST021后天车放料完成 (M821)
        public bool M823CanPick { get; set; } // ST020允许机械手2取料 (M823)
        public bool M824PickDone { get; set; } // ST020机械手2取料完成 (M824)
        public bool M825CanPick { get; set; } // ST021允许机械手3取料 (M825)
        public bool M826PickDone { get; set; } // ST021机械手3取料完成 (M826)
        public bool DropRackSnapshotValid { get; set; } // 2号线后端下料架握手快照有效; 失败时后端禁止放料
        public bool CraneFrontConnected { get; set; }
        public bool BoringConnected { get; set; }
        public bool BoringSnapshotValid { get; set; } // 仅供状态页面区分TCP在线与本轮信号读取成功
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
    private readonly bool _ownsManipulatorLock;    // 机械手锁是否自建(共享时不Dispose, 2号线永远false)

    /// <param name="transferRackLock">中转架互斥锁, 与后端引擎共享。为null时自建。</param>
    /// <param name="safety">前后天车共享安全标志。为null时自建。</param>
    /// <param name="manipulatorLock">机械手互斥锁(必须与1号线共享同一个实例!)</param>
    public Line2FrontFlowEngine(CraneConnectionCache craneCache, ManipulatorConnectionCache manipulatorCache,
        MotionConfig cfg, Dictionary<string, MachineManagementRowVm> stationCoords,
        SemaphoreSlim? transferRackLock = null, SafetyFlags? safety = null,
        CenteringRackService? rackSvc = null, SemaphoreSlim? manipulatorLock = null)
    {
        _craneCache = craneCache;
        _manipulatorCache = manipulatorCache;
        _cfg = cfg;
        _stationCoords = stationCoords;
        _ownsTransferRackLock = transferRackLock == null;
        _transferRackLock = transferRackLock ?? new SemaphoreSlim(1, 1);
        _ownsManipulatorLock = false; // 2号线永远共享机械手锁
        _manipulatorLock = manipulatorLock ?? throw new InvalidOperationException("2号线必须共享1号线的机械手锁");
        _safety = safety ?? new SafetyFlags();
        _rackSvc = rackSvc;
        Console.WriteLine("[Line2Front] 2号线前端引擎实例已创建");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  公开控制方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>启动引擎 — 创建后台Task运行主循环。幂等(重复调用忽略)。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            Console.WriteLine("[Line2Front] 引擎已在运行"); return;
        }
        _paused = false;
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  [Line2Front] 2号线前端流程引擎启动");
        Console.WriteLine($"  机械手1安全Y={_cfg.SkewBed.Manipulator1SafeY}mm  安全高度={_cfg.Grinding.SafeZHeight}mm");
        Console.WriteLine($"  zFactor1={_cfg.Grinding.ZFactor1} zFactor2={_cfg.Grinding.ZFactor2}");
        Console.WriteLine("══════════════════════════════════════════");
        _markerMonitor.Start(_engineCts.Token);
        _engineTask = Task.Run(() => EngineLoopAsync(_engineCts.Token));
    }

    /// <summary>停止引擎 — 取消CancellationToken，等待主循环退出。</summary>
    public void Stop() { Console.WriteLine("[Line2Front] ▶ 停止..."); _engineCts.Cancel(); }

    /// <summary>暂停 — 主循环跳过一次循环(可恢复)。</summary>
    public void Pause() { _paused = true; Console.WriteLine("[Line2Front] ⏸ 暂停"); }

    /// <summary>恢复 — 清除暂停标志，主循环继续执行。</summary>
    public void Resume() { _paused = false; Console.WriteLine("[Line2Front] ▶ 恢复"); }

    // ═══════════════════════════════════════════════════════════════════
    //  工件缓存方法
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>工件入队 — 加入 FIFO 队列末尾，主循环出队时按顺序处理。</summary>
    public void EnqueueWorkpiece(WorkpieceCache wp)
    { _cacheQueue.Enqueue(wp); 
        lock (_cacheLock) _cachedList.Add(wp);
        Console.WriteLine($"[Line2Front] 📥 入缓存 {wp.IdentityText} d={wp.Diameter} L={wp.Length} 队列={CachedCount}"); 
    }

    /// <summary>清空缓存 — 丢弃所有未处理工件。</summary>
    public void ClearCache() { while (_cacheQueue.TryDequeue(out _)) { } lock (_cacheLock) _cachedList.Clear(); }

    /// <summary>出队 — 从 FIFO 队列头部取出一个工件。成功返回true，队列空返回false。</summary>
    private bool TryDequeueCache(out WorkpieceCache wp)
    { 
        if (_cacheQueue.TryDequeue(out wp)) 
        { 
            lock (_cacheLock) _cachedList.Remove(wp); 
            return true; 
        } 
        wp = default; 
        return false;
    }

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
    private async Task RefreshRackSnapshotAsync(Line2DeviceStatus ds, CancellationToken ct)
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
                // 状态快照刷新必须独立恢复连接，否则_currentWp或机械手锁会挡住中转架有板信号更新。
                using var reconnectTimeoutCts = new CancellationTokenSource(5000);
                using var reconnectLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnectTimeoutCts.Token);
                await _rackSvc.ConnectAsync(reconnectLinked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ds.RackConnected = false;
                if (_cycleCount % 10 == 1) Console.WriteLine("[Line2Front] ⚠ 独立刷新中转架重连超时(5s)");
                return;
            }
            catch (Exception ex)
            {
                ds.RackConnected = false;
                if (_cycleCount % 10 == 1) Console.WriteLine($"[Line2Front] ⚠ 独立刷新中转架重连失败: {ex.Message}");
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
            // 2号线中转架: ST016/M814, ST017/M815, ST018/M816。
            ds.TransferRack4Free = !rs.Station4HasPlate;
            ds.TransferRack5Free = !rs.Station5HasPlate;
            ds.TransferRack6Free = !rs.Station6HasPlate;

            // ST020/ST021后端下料架已改为完整握手: M818/M820=允许后天车放料,
            // M823/M825=允许机械手取料。读失败时不能沿用旧“允许”。
            try
            {
                int m816 = rs.RawM816Word;
                ApplyDropRackHandshakeSnapshot(ds, m816);
                ds.DropRackSnapshotValid = true;
            }
            catch (Exception ex)
            {
                ds.DropRackSnapshotValid = false;
                if (_cycleCount % 10 == 1) Console.WriteLine($"[Line2Front] ⚠ ST020/ST021握手快照读取失败: {ex.Message} → 后端禁止向2号线下料架放料");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ds.RackConnected = false;
            ds.DropRackSnapshotValid = false;
            if (_cycleCount % 10 == 1) Console.WriteLine("[Line2Front] ⚠ 独立刷新中转架状态超时(5s)");
        }
        catch (Exception ex)
        {
            ds.RackConnected = false;
            ds.DropRackSnapshotValid = false;
            if (_cycleCount % 10 == 1) Console.WriteLine($"[Line2Front] ⚠ 独立刷新中转架状态失败: {ex.Message}");
        }
    }

    private static void ApplyDropRackHandshakeSnapshot(Line2DeviceStatus ds, int m816)
    {
        ds.M818CanPlace = (m816 & (1 << RackAddr.Bit_ST020_CanPlace)) != 0;
        ds.M819PlaceDone = (m816 & (1 << RackAddr.Bit_ST020_PlaceDone)) != 0;
        ds.M820CanPlace = (m816 & (1 << RackAddr.Bit_ST021_CanPlace)) != 0;
        ds.M821PlaceDone = (m816 & (1 << RackAddr.Bit_ST021_PlaceDone)) != 0;
        ds.M823CanPick = (m816 & (1 << RackAddr.Bit_ST020_CanPick)) != 0;
        ds.M824PickDone = (m816 & (1 << RackAddr.Bit_ST020_PickDone)) != 0;
        ds.M825CanPick = (m816 & (1 << RackAddr.Bit_ST021_CanPick)) != 0;
        ds.M826PickDone = (m816 & (1 << RackAddr.Bit_ST021_PickDone)) != 0;
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
                    if (!TryGetStationIp("ST712", out string fIp, out int fPort))
                    {
                        Console.WriteLine("[Line2Front] 货叉后台重连跳过: 未找到ST712 IP");
                        return;
                    }

                    using var timeoutCts = new CancellationTokenSource(5000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    _forkSvc ??= new ForkService("二号线货叉", fIp, fPort > 0 ? fPort : 9000);
                    Console.WriteLine($"[Line2Front] 货叉后台重连 {fIp}:{(fPort > 0 ? fPort : 9000)}...");
                    await _forkSvc.ConnectAsync(linked.Token);
                    Console.WriteLine("[Line2Front] 货叉后台重连 ✓");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Console.WriteLine("[Line2Front] 货叉后台重连超时(5s)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Line2Front] 货叉后台重连失败: {ex.Message}");
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
                    if (!TryGetStationIp("ST402", out string bIp, out _))
                    {
                        Console.WriteLine("[Line2Front] 双头镗后台重连跳过: 未找到ST402 IP");
                        return;
                    }

                    using var timeoutCts = new CancellationTokenSource(5000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    _boringSvc ??= new BoringModbusService("2号线双头镗", bIp);
                    Console.WriteLine($"[Line2Front] 双头镗Modbus后台重连 {bIp}:502...");
                    await _boringSvc.ConnectAsync(linked.Token);
                    Console.WriteLine("[Line2Front] 双头镗后台重连 ✓");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Console.WriteLine("[Line2Front] 双头镗后台重连超时(5s)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Line2Front] 双头镗后台重连失败: {ex.Message}");
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
        string message = $"2号线货叉/双头镗握手异常：{reason}：{ex.Message}。保持阶段={_forkPhase}，引擎已暂停，请人工确认货叉、双头镗和工件位置。";
        Console.WriteLine($"[Line2Front] [货叉] {message}");
        OnSafetyAlarm?.Invoke(message);
    }

    /// <summary>
    /// 机械手1允许取总上料架/送货叉前的货叉硬条件。
    /// 必须同时满足: ①货叉服务已连接 ②状态读取成功 ③货叉在待机位 ④货叉无板。
    /// 这里只做安全确认和UI快照刷新, 不改变原来的M800/缓存/双头镗/在途等业务条件。
    /// </summary>
    private async Task<bool> ConfirmForkReadyForManipulatorAsync(string context, CancellationToken ct, Line2DeviceStatus? ds = null)
    {
        if (_forkSvc == null || !_forkSvc.IsConnected)
        {
            if (ds != null) ds.ForkConnected = false;
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[Line2Front] [{context}] ⚠ 货叉未连接 → 禁止机械手1取料/送叉");
            return false;
        }

        try
        {
            var fs = await _forkSvc.ReadAllStatusAsync(ct);
            if (ds != null)
            {
                ds.ForkConnected = true;
                ds.ForkHasPlate = fs.HasPlate;
                ds.ForkPosition = fs.CurrentPosition;
                ds.ForkCommand = fs.CommandText;
            }

            if (!fs.AtStandbyPos)
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line2Front] [{context}] ⚠ 货叉不在待机位(当前{fs.CurrentPosition}) → 禁止机械手1取料/送叉");
                return false;
            }

            if (fs.HasPlate)
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line2Front] [{context}] ⚠ 货叉已有板 → 禁止机械手1取料/送叉");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (ds != null) ds.ForkConnected = false;
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[Line2Front] [{context}] ⚠ 读取货叉状态失败 → 禁止机械手1取料/送叉: {ex.Message}");
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
                    Console.WriteLine($"[Line2Front] [{context}] 机械手1未连接, 尝试重连后再判断是否允许取料...");
                await _manipulator1.ConnectAsync(linked.Token);
            }

            var status = await _manipulator1.ReadStatusAsync(ct);
            if (status == null)
            {
                // CraneService读失败返回null时, 底层连接通常已被置断开。
                // 这里再主动断开一次, 防止半连接导致后续轮次继续读旧socket。
                try { await _manipulator1.DisconnectAsync(); } catch { }
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[Line2Front] [{context}] 机械手1状态读取失败(status=null) → 本轮不出队、不启动机械手");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[Line2Front] [{context}] 机械手1通信不可用 → 本轮不出队、不启动机械手: {ex.Message}");
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
            Console.WriteLine($"[Line2Front] ⚠ 设备初始化异常(引擎继续运行): {ex.Message}");
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
                        $"[Line2Front] 轮次{_cycleCount} 缓存={CachedCount} 在途={(_currentWp != null ? 1 : 0)} 货叉={_forkPhase} 暂停={_paused}");

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
                            // Syntec同步SDK可能慢或半开，必须在共享机械手锁外确认。
                            // SkipBoring 也必须等双头镗在线且 R6101=1，防止设备节拍冲突。
                            boringIdleForPickup = (await (boringCycleReadTask ??= _boringSvc.ReadAllSignalsAsync(ct))).RequestData;
                        }
                        catch (Exception ex)
                        {
                            if (_cycleCount % 10 == 1)
                                Console.WriteLine($"[Line2Front] ⚠ 锁外读取双头镗空闲状态失败 → 本轮不抢机械手锁: {ex.Message}");
                        }
                    }
                    else if (_cycleCount % 10 == 1)
                    {
                        Console.WriteLine("[Line2Front] ⚠ 双头镗未连接 → 本轮不抢机械手锁");
                    }

                    if (!boringIdleForPickup && _boringSvc?.IsConnected == true && _cycleCount % 10 == 1)
                        Console.WriteLine("[Line2Front] 双头镗忙或信号无效 → 本轮不抢机械手锁");
                }

                // 没有本线缓存时不参与机械手1竞争；双头镗通信也已在锁外完成。
                if (inTransit == null && CachedCount > 0 && boringIdleForPickup
                    && await _manipulatorLock.WaitAsync(0, ct))
                {
                    bool released = false; // true=锁已转交给 ProcessManipulatorPickupAsync
                    try
                    {
                        Console.WriteLine(
                            $"[Line2Front] 读总上料架前: SvcNull={_rackSvc == null} IsConnected={_rackSvc?.IsConnected}");
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
                                    Console.WriteLine("[Line2Front] ⚠ 读 M800 超时(5s)，MC设备TCP通但无响应");
                                    ds.RackConnected = false;
                                    _manipulatorLock.Release();
                                    continue;
                                }
                                // 引擎取消: 正常退出
                                catch (OperationCanceledException)
                                {
                                    Console.WriteLine("[Line2Front] 引擎已取消");
                                    _manipulatorLock.Release();
                                    return;
                                }
                                // 其它异常时底层MC客户端会关闭半开socket。
                                // 这里仍持有两线共享机械手锁，禁止在锁内断开/重连共享MC63；
                                // 下一轮由锁外RefreshRackSnapshotAsync通过McConnectionCache单飞重连。
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Line2Front] ⚠ 读 M800 异常: {ex.GetType().Name} - {ex.Message} → 释放机械手锁,等待锁外重连");
                                    ds.RackConnected = false;
                                    _manipulatorLock.Release();
                                    continue;
                                }
                            }

                            // ── 刷新 UI 快照 ──
                            ds.RackConnected = true;
                            ds.RackRequestPickup = rs.RequestPickup;
                            ds.RackPlateLength = rs.PlateLength;
                            ds.TransferRack4Free = !rs.Station4HasPlate;
                            ds.TransferRack5Free = !rs.Station5HasPlate;
                            ds.TransferRack6Free = !rs.Station6HasPlate;
                            // 同步刷新2号线后端下料架握手位, 供后端放料预检和页面显示使用。
                            try
                            {
                                int m816 = rs.RawM816Word;
                                ApplyDropRackHandshakeSnapshot(ds, m816);
                                ds.DropRackSnapshotValid = true;
                            }
                            catch (Exception ex)
                            {
                                ds.DropRackSnapshotValid = false;
                                Console.WriteLine($"[Line2Front] ⚠ ST020/ST021握手快照读取失败: {ex.Message} → 后端禁止向2号线下料架放料");
                            }

                            Console.WriteLine(
                                $"[Line2Front] M800={rs.RequestPickup} 板长={rs.PlateLength}mm 缓存={CachedCount} 货叉={_forkPhase} 中转4={(rs.Station4HasPlate ? "有" : "空")} 2={(rs.Station5HasPlate ? "有" : "空")} 3={(rs.Station6HasPlate ? "有" : "空")} {(rs.RequestPickup && CachedCount > 0 ? "→ 满足！" : "→ 等M800=1+缓存有料")}");

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
                                    _forkHandshakeInProgress = false;
                                    _boringUnloadDoneSent = false;
                                    _m912Sent = false;
                                    _skipBoringTriggered = false;
                                    Console.WriteLine($"[Line2Front] [DEBUG] _currentWp SET {wp.IdentityText} d={wp.Diameter} L={wp.Length} SkipBoring={wp.SkipBoring}");
                                    Console.WriteLine($"[Line2Front] M800=1 缓存有料 {wp.IdentityText} d={wp.Diameter} L={wp.Length}{(wp.SkipBoring ? " ⚡SkipBoring" : "")} → 启动机械手1 在途=1");
                                    _ = ProcessManipulatorPickupAsync(wp, ct);
                                    released = true;
                                }
                            }
                        }
                        else
                        {
                            if (_cycleCount % 10 == 1)
                                Console.WriteLine($"[Line2Front] ⚠ 总上料架未连接 IsConnected={_rackSvc?.IsConnected}");
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
                        ds.ForkConnected = true;
                        ds.ForkHasPlate = fs.HasPlate;
                        ds.ForkPosition = fs.CurrentPosition;
                        ds.ForkCommand = fs.CommandText;

                        // ── 条件: 待机位 + 有版 + 有在途工件 + 货叉空闲 + 机械手已离开叉区 ──
                        //     必须确认机械手Y回到安全位后才分派, 防机械手还在叉区时货叉移动碰撞
                        if (fs.HasPlate && fs.AtStandbyPos && _currentWp != null && _forkPhase == ForkBoringPhase.Idle)
                        {
                            // 前置安全: 机械手1必须在安全Y位 (Z↑完成不代表Y也移开了)
                            bool manipSafe = false;
                            if (_manipulator1?.IsConnected == true)
                            {
                                try
                                {
                                    var ms = await _manipulator1.ReadStatusAsync(ct);
                                    manipSafe = ms != null && Math.Abs(ms.YPos - _cfg.SkewBed.Manipulator1SafeY) <= 10;
                                }
                                catch { /* 读失败→保守不触发 */ }
                            }
                            if (!manipSafe)
                            {
                                if (_cycleCount % 10 == 1)
                                    Console.WriteLine("[Line2Front] [货叉分派] ⚠ 机械手1未回安全Y位(可能在叉区),等待...");
                            }
                            else
                            {
                                var wp = _currentWp.Value;
                                if (wp.SkipBoring)
                                {
                                    Console.WriteLine($"[Line2Front] [货叉] ⚡ SkipBoring! L={wp.Length}mm → 跳过双头镗, 货叉直接伸Pos3");
                                    _forkPhase = ForkBoringPhase.SkipBoring_GoPos3;
                                }
                                else
                                {
                                    Console.WriteLine($"[Line2Front] [货叉] 待机位有版 L={wp.Length}mm → 先写参数等R6103再写M912送料");
                                    _forkPhase = ForkBoringPhase.Load_WriteParams;
                                }
                            }
                        }
                        else if (_cycleCount % 10 == 1)
                        {
                            // 诊断: 条件不满足时打印各字段
                            Console.WriteLine($"[Line2Front] [货叉分派] 条件不满足 HasPlate={fs.HasPlate} Standby={fs.AtStandbyPos} 在途={_currentWp != null} 阶段={_forkPhase}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ds.ForkConnected = false;
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[Line2Front] [货叉分派] 读取/判断异常: {ex.GetType().Name} - {ex.Message}");
                    }
                    finally { _forkDispatchLock.Release(); }
                }
                else if (_cycleCount % 10 == 1)
                {
                    Console.WriteLine($"[Line2Front] [货叉分派] 未进锁 ForkSvcNull={_forkSvc == null} IsConnected={_forkSvc?.IsConnected} LockCnt={_manipulatorLock.CurrentCount}");
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
                        ds.ForkHasPlate = fs.HasPlate;
                        ds.ForkPosition = fs.CurrentPosition;
                        ds.ForkCommand = fs.CommandText;
                        if (ShouldLogForkState(fs))
                        {
                            Console.WriteLine(
                                $"[Line2Front] [货叉状态机] 在途=1 HasPlate={fs.HasPlate} AtPos3={fs.AtPos3} AtStandby={fs.AtStandbyPos} Cmd={fs.CommandText} 阶段={_forkPhase}");
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
                                        Console.WriteLine($"[Line2Front] [货叉] 等待R6101读取异常: {ex.GetType().Name} - {ex.Message} → 保持阶段等待重连");
                                        break;
                                    }

                                    if (!snapshot.RequestData)
                                    {
                                        if (_cycleCount % 10 == 1)
                                            Console.WriteLine("[Line2Front] [货叉] ① 等R6101=1请求数据（无业务超时）...");
                                        break;
                                    }

                                    _forkHandshakeInProgress = true;
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var wp = _currentWp!.Value;
                                            double boringD = wp.Diameter + _cfg.GetDiameterOffset("boring2");
                                            Console.WriteLine(
                                                $"[Line2Front] [货叉] ② R6101=1，写Modbus参数 R2041={wp.Length} R2043={boringD}*100 R2044={wp.LeftPlugThickness}*100 R2045={wp.RightPlugThickness}*100 R2046={wp.InnerTaper}*100 R2047={wp.BoreType}*100 R2048={wp.CornerSize}*100");
                                            await _boringSvc.SendMachiningParamsAsync(wp.Length, boringD,
                                                wp.LeftPlugThickness, wp.RightPlugThickness, wp.InnerTaper,
                                                wp.BoreType, wp.CornerSize, ct);
                                            await _boringSvc.SetDataSentDoneAsync(ct);
                                            Console.WriteLine("[Line2Front] [货叉] ③ R6102=1 数据下发完成 ✓ → 持续等R6103=1（无业务超时）");
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
                                            Console.WriteLine("[Line2Front] [货叉] ④ R6103=1 CNC请求上料 → 准备写M912送料并回待机");
                                            _forkPhase = ForkBoringPhase.GoingToBoring;
                                        }
                                        else if (_cycleCount % 10 == 1)
                                        {
                                            Console.WriteLine("[Line2Front] [货叉] 等R6103=1请求上料（无业务超时）...");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // R6102已经写出，但读取R6103失败不代表设备执行状态发生变化；保留阶段等待通信恢复。
                                        ds.BoringSnapshotValid = false;
                                        ds.BoringConnected = _boringSvc.IsConnected;
                                        Console.WriteLine($"[Line2Front] [货叉] 等待R6103读取异常: {ex.GetType().Name} - {ex.Message} → 保持阶段等待重连");
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
                                            Console.WriteLine("[Line2Front] [货叉] ⑤ M912=1 送料到双头镗并自动回待机");
                                            await _forkSvc.GoFeedToBoringAsync(ct);
                                            forkCycleReadTask = null; // 命令写入后不得继续使用写入前状态
                                            _m912Sent = true;
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("[Line2Front] [货叉] 送料完成: 待机位且无板 → R6104=1 上料完成");
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
                                            Console.WriteLine($"[Line2Front] [货叉] R6104=1 上料完成 ✓ → 等加工... {_currentWp?.IdentityText}");
                                            _currentWp?.ReportStage("2号线 双头镗加工中");
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
                                        Console.WriteLine($"[Line2Front] [货叉] R6107=1 加工完成 → M913=1 货叉去双头镗取料 {_currentWp?.IdentityText}");
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
                                        Console.WriteLine("[Line2Front] [货叉] 等M913自动取料到Pos3...");
                                    }
                                    else if (fs.HasPlate && !_boringUnloadDoneSent)
                                    {
                                        if (_boringSvc == null || !_boringSvc.IsConnected)
                                        {
                                            if (_cycleCount % 10 == 1)
                                                Console.WriteLine("[Line2Front] [货叉] Pos3已到位, 但双头镗Modbus未连接 → 暂不写R6108, 等待重连");
                                            break;
                                        }

                                        Console.WriteLine("[Line2Front] [货叉] Pos3已到位 工件在叉上 → R6108=1 下料完成");
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
                                        Console.WriteLine($"[Line2Front] [货叉] 下料完成已发送，R6108保持为1，R6102/R6104已清零 ✓ → 工件入天车队列 {_currentWp?.IdentityText}");
                                        _craneQueue.Enqueue(_currentWp!.Value);
                                        lock (_wpLock) { _currentWp = null; }
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
                                        // 写M914=1
                                        Console.WriteLine("[Line2Front] [货叉] ⚡ SkipBoring → M914=1 直接去Pos3天车位");
                                        await _forkSvc.GoStandbyToPos3Async(ct);
                                        forkCycleReadTask = null;
                                    }
                                    else if (fs.HasPlate) // 每次循环都尝试(ProcessCraneFrontAsync内有_craneFrontLock防重入)
                                    {
                                        if (!_skipBoringTriggered)
                                        {
                                            Console.WriteLine($"[Line2Front] [货叉] ⚡ SkipBoring Pos3已到位 → 工件入天车队列 {_currentWp?.IdentityText}");
                                            _currentWp?.ReportStage("2号线 跳过双头镗");
                                            _skipBoringTriggered = true;
                                            _forkPhase = ForkBoringPhase.WaitingForkReturnFromSkip;
                                            _craneQueue.Enqueue(_currentWp!.Value);
                                            lock (_wpLock) { _currentWp = null; }
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
                        Console.WriteLine($"[Line2Front] ⚠ 货叉状态机异常: {ex.Message}");
                    }
                }

                // ═══════════════════════════════════════════════════════════
                //  ③.5 天车独立调度 — 队列有料+天车空闲 → 循环取料执行
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
                                var crane = _craneCache.GetOrCreateService(CraneFront2No);
                                var zeroAlarm = await CraneZeroPositionGuard.CheckAsync(
                                    crane, CraneFront2No, "2号线前天车", "前天车任务出队前", ct);
                                if (zeroAlarm != null)
                                {
                                    _paused = true;
                                    OnCraneZeroPositionDetected?.Invoke(zeroAlarm);
                                    break;
                                }

                                if (!_craneQueue.TryDequeue(out var craneWp))
                                    break;

                                Console.WriteLine($"[Line2Front] [天车调度] ▶ 出队 {craneWp.IdentityText} d={craneWp.Diameter} L={craneWp.Length} 队列剩余={_craneQueue.Count}");
                                await ProcessCraneFrontAsync(craneWp, ct);
                                if (_paused) break; // 当前任务已进入人工确认状态，禁止继续消费后续天车任务。
                            }
                            Console.WriteLine("[Line2Front] [天车调度] 队列空, 天车归位");
                        }
                        finally
                        {
                            _craneFrontLock.Release();
                            Console.WriteLine("[Line2Front] [天车调度] _craneFrontLock 已释放");
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
                        ds.ForkConnected = true;
                        ds.ForkHasPlate = fs.HasPlate;
                        ds.ForkPosition = fs.CurrentPosition;
                        ds.ForkCommand = fs.CommandText;
                        // 天车接管后, 只等待货叉回待机且无板再复位Idle; 等待阶段不会再写M913/M914。
                        if (((_forkPhase == ForkBoringPhase.WaitingForkReturnFromBoring && _boringUnloadDoneSent) ||
                             (_forkPhase == ForkBoringPhase.WaitingForkReturnFromSkip && _skipBoringTriggered))
                            && fs.AtStandbyPos && !fs.HasPlate)
                        {
                            string tag = _forkPhase == ForkBoringPhase.WaitingForkReturnFromSkip ? "⚡SkipBoring" : "";
                            Console.WriteLine($"[Line2Front] [货叉] {tag} 已回待机位 无版 → 状态机复位 Idle");
                            _forkPhase = ForkBoringPhase.Idle;
                            _boringUnloadDoneSent = false; // 状态机复位: 清除R6108已发送标志
                            _m912Sent = false;
                            _skipBoringTriggered = false; // 状态机复位: 清除SkipBoring触发标志
                        }
                    }
                    catch (Exception ex)
                    {
                        ds.ForkConnected = false;
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[Line2Front] [货叉状态机] 状态刷新异常: {ex.GetType().Name} - {ex.Message}");
                    }
                }

                // ── 天车前连接状态 (通过 CraneConnectionCache 共享连接) ──
                try
                {
                    ds.CraneFrontConnected = _craneCache.GetOrCreateService(CraneFront2No).IsConnected;
                }
                catch
                {
                }

                // ── 机械手1 状态 (Y坐标 + 安全位判断) ──
                ds.Manipulator1Connected = _manipulator1?.IsConnected == true;
                if (_manipulator1?.IsConnected == true)
                {
                    try
                    {
                        var s = await _manipulator1.ReadStatusAsync(ct);
                        ds.Manipulator1Y = s?.YPos ?? 0;
                        ds.Manipulator1Safe = s != null && Math.Abs(s.YPos - _cfg.SkewBed.Manipulator1SafeY) <= 10;
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
                    }
                    catch (Exception ex)
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[Line2Front] ⚠ 双头镗状态快照失败: {ex.GetType().Name} - {ex.Message}");
                    }
                }
                // UNC探测由独立线程隔离；这里只读取新鲜快照，不能让共享目录离线拖慢主循环。
                var markerSnapshot = _markerMonitor.LatestSnapshot;
                ds.MarkerConnected = _markerMonitor.IsFreshAndConnected;
                ds.MarkerStatusText = ds.MarkerConnected
                    ? MarkerSharePath
                    : markerSnapshot.Error ?? "共享目录不可访问";

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
                Console.WriteLine($"[Line2Front] ✘ 主循环异常：{ex.GetType().Name} — {ex.Message}"); 
                await Task.Delay(2000, ct);
            }  // 未知异常 → 等2s继续
        }
        Console.WriteLine("[Line2Front] 引擎已停止");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S2: ProcessManipulatorPickupAsync — 机械手1 取料送叉
    //  完整流程:
    //    ① 读 D100 板长 (仅日志)
    //    ② ST007 取料: XY→公式Z → 充磁 → X11确认, 未吸住最多下探2次
    //      ②a. MoveAbsoluteAsync(rx, ry, ComputePickupZ(rz,d)) — 下压触发视为异常
    //      ②b. X11=0 → 退磁 → Z每次下探5mm → 再充磁确认
    //      ②c. X11=1后 Z升安全高度
    //    ③ ST712 送叉: XY→公式Z → 退磁 → Z升 → Y回安全
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
        bool magnetOn = false;          // 磁铁命令是否打开过
        bool holdingWorkpiece = false;  // X11确认吸住后才算工件真的在机械手上
        bool placedOnFork = false;      // 退磁放到货叉后, 才允许货叉状态机接管
        try
        {
            Console.WriteLine($"[Line2Front] [机械手1] ═══ 取料送叉 {wp.IdentityText} d={wp.Diameter} L={wp.Length} ═══");
            wp.ReportStage("2号线 机械手取料/送叉");

            // ①.0 安全: 取料前检查磁铁是否已有工件(断电/急停重启后X11不受PLC内存影响)
            if (_manipulator1?.IsConnected == true && await _manipulator1.ReadXBitAsync(63497, ct))
            {
                Console.WriteLine("[Line2Front] ⚠⚠⚠ 机械手1磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[Line2Front]   拒绝取料, 请人工确认机械手1状态后手动处理");
                throw new InvalidOperationException("机械手1 X11=1(磁铁已有工件), 拒绝取料防止碰撞");
            }

            // ① 读 D100 板长 — 仅日志输出，不影响流程
            int plateLen = 0;
            if (_rackSvc != null)
            {
                plateLen = await _rackSvc.ReadPlateLengthAsync(ct);
                Console.WriteLine($"[Line2Front] [机械手1] ① D100板长={plateLen}mm");
            }

            // ② ST007 取料
            if (!TryGetStationCoords("ST007", out int rx, out int ry, out int rz))
                throw new InvalidOperationException("数据库未找到 ST007 坐标");
            //z轴距离
            int formulaZ = ComputePickupZ(rz, wp.Diameter);
            //z轴安全距离
            int safeZ = _cfg.Grinding.SafeZHeight;
            Console.WriteLine($"[Line2Front] [机械手1] ② ST007({rx},{ry},{rz}) 公式Z={formulaZ} 安全Z={safeZ} (d={wp.Diameter})");
            // 设置三轴绝对速度 (X/Y/Z 各不同)
            var m1Spd = _cfg.GetManipulatorSpeed(1);
            await _manipulator1!.SetAbsSpeedAsync(m1Spd.X.Speed, m1Spd.X.Accel, m1Spd.X.Decel,
                m1Spd.Y.Speed, m1Spd.Y.Accel, m1Spd.Y.Decel, m1Spd.Z.Speed,
                m1Spd.Z.Accel, m1Spd.Z.Decel, ct);
            // ②a~②c. XY→料架公式Z, 充磁后用X11确认; X11=0最多下探2次
            int pickupZ = formulaZ;
            Console.WriteLine($"[Line2Front] [机械手1] ②a XY→ST007 Z→公式位{pickupZ}");
            for (int retry = 0; retry <= 2; retry++)
            {
                try
                {
                    if (retry == 0)
                    {
                        await _manipulator1!.MoveAbsoluteAsync(rx, ry, pickupZ, ct: ct);
                    }
                    else
                    {
                        pickupZ += 5;
                        Console.WriteLine($"[Line2Front] [机械手1]   X11=0 → 退磁后Z下探第{retry}次 → {pickupZ}");
                        await _manipulator1!.MoveAbsoluteAsync(-1, -1, pickupZ, timeoutMs: 10_000, ct: ct);
                    }
                }
                catch (PressureStopException ex)
                {
                    await RecoverFromPressureStopAsync(ct);
                    throw new InvalidOperationException($"机械手1 ST007公式取料触发下压信号, Z={pickupZ}, 已停止流程", ex);
                }

                Console.WriteLine($"[Line2Front] [机械手1] ====== ②b 充磁 START (第{retry + 1}次) ======");
                await _manipulator1!.MagnetOnAsync(ct);
                magnetOn = true;
                Console.WriteLine($"[Line2Front] [机械手1] ====== ②b 充磁 DONE ✓ {wp.IdentityText} ======");
                Console.WriteLine($"[Line2Front] [机械手1]   等X11确认吸住... {wp.IdentityText}");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct);
                holdingWorkpiece = await _manipulator1.ReadXBitAsync(63497, ct);
                Console.WriteLine($"[Line2Front] [机械手1]   X11={(holdingWorkpiece ? "1(已吸住)" : "0(未吸住)")} @Z={pickupZ} {wp.IdentityText}");
                if (holdingWorkpiece)
                    break;

                await _manipulator1.MagnetOffAsync(ct);
                magnetOn = false;
            }

            if (!holdingWorkpiece)
                throw new InvalidOperationException($"机械手1充磁后X11仍为0, 已按公式Z+2次下探尝试, 最终Z={pickupZ}, 拒绝继续送叉");

            // ②d. Z回原点0 (取料完成先回Z, 下压误触清除后重试最多3次)
            Console.WriteLine("[Line2Front] [机械手1] ②d Z回原点0");
            for (int retry = 0; retry < 3; retry++)
            {
                try { await _manipulator1!.MoveAbsoluteAsync(-1, -1, 0, ct: ct); break; }
                catch (PressureStopException) { Console.WriteLine($"[Line2Front] [机械手1]   ⚠ Z升下压误触→清除→重试{retry+1}"); await RecoverFromPressureStopAsync(ct); }
            }

            // ③ 送货叉 ST712 — 前置检查: 叉在待机位且无版, 防止叉在Pos2/Pos3时碰撞
            if (!TryGetStationCoords("ST712", out int f1x, out int f1y, out int f1z))
                throw new InvalidOperationException("数据库未找到 ST712");
            // 这里必须是硬检查: 货叉未连接/状态读不到时不能跳过, 否则机械手会盲目去ST712放料。
            if (!await ConfirmForkReadyForManipulatorAsync("送叉动作前", ct))
                throw new InvalidOperationException("货叉未连接/状态读取失败/不在待机位/已有板, 机械手拒绝送叉");
            Console.WriteLine($"[Line2Front] [机械手1]   货叉前置检查通过: 待机位 ✓ 无版 ✓");
            int forkPlaceZ = ComputePickupZ(f1z, wp.Diameter);
            Console.WriteLine($"[Line2Front] [机械手1] ③ 送叉 ST712({f1x},{f1y},{f1z}) 放料Z={forkPlaceZ}");

            // ③a. XY→叉位公式Z; 下压触发视为异常保护
            try
            {
                await _manipulator1!.MoveAbsoluteAsync(f1x, f1y, forkPlaceZ, timeoutMs: 240_000, ct: ct);
            }
            catch (PressureStopException ex)
            {
                await RecoverFromPressureStopAsync(ct);
                throw new InvalidOperationException($"机械手1 ST712公式放料触发下压信号, Z={forkPlaceZ}, 已停止流程", ex);
            }

            // ③c. 退磁放下
            await _manipulator1!.MagnetOffAsync(ct);
            magnetOn = false; // 标志置false → 工件已安全放下
            placedOnFork = true;
            holdingWorkpiece = false;
            Console.WriteLine($"[Line2Front] [机械手1]   退磁放下 ✓ {wp.IdentityText}");

            // ③d. Z回原点0 (送完货叉先回Z, 下压误触清除后重试最多3次)
            Console.WriteLine("[Line2Front] [机械手1]   Z回原点0");
            for (int retry = 0; retry < 3; retry++)
            {
                try { await _manipulator1!.MoveAbsoluteAsync(-1, -1, 0, ct: ct); break; }
                catch (PressureStopException) { Console.WriteLine($"[Line2Front] [机械手1]   ⚠ Z升下压误触→清除→重试{retry+1}"); await RecoverFromPressureStopAsync(ct); }
            }
            // ④ Y先回安全位(机械手臂离开叉区), 再通知PLC取料完成 M801=1→0
            //    先移动后发信号: 防止PLC收到M801后立即复位送料机构时机械手Y轴还在叉区
            Console.WriteLine("[Line2Front] 货叉绝对移动");
            int safeY = _cfg.SkewBed.Manipulator1SafeY;
            await _manipulator1!.MoveAbsoluteAsync(-1, safeY, -1, ct: ct);
            Console.WriteLine($"[Line2Front] [机械手1]   safeY={safeY} Z=0 ✓");
            if (_rackSvc != null)
            {
                await _rackSvc.SetPickupDoneAsync(ct);
                Console.WriteLine($"[Line2Front] [机械手1] ④ M801=1→0 取料完成 ✓ {wp.IdentityText}");
            }
            Console.WriteLine($"[Line2Front] [机械手1] ═══ 取料送叉完成 {wp.IdentityText} ═══");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line2Front] [机械手1] ✘ 异常：{ex.Message}");
            if (holdingWorkpiece && !placedOnFork)
            {
                // X11已确认吸住, 但还没有安全放到货叉; 此时最危险, 必须停住等人工确认。
                lock (_wpLock) { _currentWp = null; }
                _paused = true;
                Console.WriteLine("[Line2Front] ⚠ 机械手1已吸住工件但未放到货叉！引擎已暂停, 需人工确认机械手/工件位置！");
                OnSafetyAlarm?.Invoke($"2号线机械手1处理{wp.IdentityText}时发生异常：已吸住工件但未放到货叉。引擎已暂停，请人工确认机械手和工件位置。异常：{ex.Message}");
            }
            else if (placedOnFork)
            {
                // 工件已安全放在货叉上 → 保持 _currentWp, 让 ForkBoringPhase 状态机接管。
                Console.WriteLine("[Line2Front] [机械手1] 工件已安全放在货叉上, 保持 _currentWp 等 ForkBoringPhase 接管");
            }
            else
            {
                // 尚未确认从总上料架吸起: 不能让_currentWp继续占着在途, 否则货叉会永远等一块不存在的板。
                if (magnetOn)
                {
                    try { await _manipulator1!.MagnetOffAsync(ct); } catch { /* 停机前尽量退磁, 失败交给人工 */ }
                }
                lock (_wpLock) { _currentWp = null; }
                RequeueFrontCache(wp);
                _paused = true;
                Console.WriteLine("[Line2Front] ⚠ 机械手1未确认吸住工件, 已退回工件缓存并暂停, 请人工确认总上料架/机械手状态");
                OnSafetyAlarm?.Invoke($"2号线机械手1处理{wp.IdentityText}时发生异常：未确认吸住工件，任务已退回缓存。引擎已暂停，请确认总上料架和机械手状态。异常：{ex.Message}");
            }
        }
        finally
        {
            // 无论如何释放锁
            Console.WriteLine("[Line2Front] [DEBUG] 机械手释放 _manipulatorLock");
            _manipulatorLock.Release();
        }  // 无论如何释放锁
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S3: DispatchForkAsync — 货叉分派 (M912送料到双头镗并回待机)
    // ═══════════════════════════════════════════════════════════════════
    public async Task DispatchForkAsync(int workpieceLength, CancellationToken ct = default)
    {
        if (_forkSvc == null || !_forkSvc.IsConnected) throw new InvalidOperationException("货叉未连接");
        Console.WriteLine($"[Line2Front] [货叉] L={workpieceLength}mm → M912送料到双头镗并回待机");
        await _forkSvc.GoFeedToBoringAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S4~S6: ProcessCraneFrontAsync — 前天车: Pos3取料→打号机→中转架
    //   货叉已处理完镗孔, 天车不再参与双头镗握手
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>前天车单工件任务: Pos3取料→打号机→中转架。锁由调度层持有。</summary>
    private async Task ProcessCraneFrontAsync(WorkpieceCache wp, CancellationToken ct = default)
    {
        bool magnetOn = false;
        try
        {
            wp.ReportStage("2号线 前天车/打号机");
            var crane = _craneCache.GetOrCreateService(CraneFront2No);
            if (!crane.IsConnected)
            {
                Console.WriteLine($"[Line2Front] [前天车] 连接天车{CraneFront2No}...");
                await crane.ConnectAsync(ct);
            }
            
            int safeZ = _cfg.Grinding.SafeZHeight;
            Console.WriteLine($"[Line2Front] [前天车] ═══ 天车流程开始 {wp.IdentityText} d={wp.Diameter} L={wp.Length} ═══");

            // ① 等机械手1回安全位
            Console.WriteLine($"[Line2Front] [前天车] ① 等机械手1安全位 Y={_cfg.SkewBed.Manipulator1SafeY}mm...");
            await WaitForManipulatorSafeAsync(ct);
            Console.WriteLine("[Line2Front] [前天车] ① 机械手1已在安全位 ✓");

            // ①.5 安全: 检查天车磁铁上是否已有工件(断电重启后可能残留)
            // 用X11物理线圈(63497)而非D5029: PLC断电重启后D5029可能清零, X11不受影响
            bool hasRoller = await crane.ReadXBitAsync(63497, ct);
            if (hasRoller)
            {
                Console.WriteLine("[Line2Front] ⚠⚠⚠ 天车磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[Line2Front]   拒绝取料, 请人工确认天车状态后手动处理");
                throw new InvalidOperationException("天车X11=1(磁铁有工件), 拒绝取料防止碰撞");
            }

            // ② 货叉Pos3 Z降取料 → 充磁 → Z升
            if (!TryGetStationCoords("ST714", out int f2x, out int f2y, out int f2z))
                throw new InvalidOperationException("数据库未找到 ST714");
            int f2PickupZ = ComputePickupZ(f2z, wp.Diameter);
            int f2xOff = f2x + _craneOffsetX, f2yOff = f2y + _craneOffsetY, f2zOff = f2PickupZ + _craneOffsetZ;
            Console.WriteLine(
                $"[Line2Front] [前天车] ② XY→货叉Pos3 ST714({f2x},{f2y},{f2z}) +偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ}) Z取料={f2PickupZ}");
            var crSpd = _cfg.GetCraneSpeed(CraneFront2No); // 2号线前天车=3号
            await crane.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel, crSpd.Y.Speed, crSpd.Y.Accel,
                crSpd.Y.Decel, crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);
            await crane.MoveAbsoluteAsync(f2xOff, f2yOff, -1, ct: ct);
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, CraneFront2No, "ST714", "2号线前天车-货叉Pos3取料前", ct);
            try
            {
                await crane.MoveAbsoluteAsync(-1, -1, f2zOff, ct: ct);
            }
            catch (PressureStopException)
            {
                Console.WriteLine("[Line2Front] [前天车]   ⚡ 下压触发→恢复");
                await crane.RecoverFromPressureStopAsync(ct);
            }

            // X11检测: 充磁→等3s→查X11(63497)→没吸到就退磁→Z↓5mm→充磁→再查, 最多2次
            Console.WriteLine("[Line2Front] [前天车]   充磁→等3s→X11检测");
            int f2PickZ = f2zOff;
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("[Line2Front] [前天车]   充磁");
                    await crane.MagnetOnAsync(ct);
                    magnetOn = true;
                }
                else
                {
                    Console.WriteLine($"[Line2Front] [前天车]   退磁→Z↓到{f2PickZ}→充磁");
                    await crane.MagnetOffAsync(ct);
                    magnetOn = false;
                    try
                    {
                        await crane.MoveAbsoluteAsync(-1, -1, f2PickZ, ct: ct);
                    }
                    catch (PressureStopException)
                    {
                        await crane.RecoverFromPressureStopAsync(ct);
                    }

                    await crane.MagnetOnAsync(ct);
                    magnetOn = true;
                }

                Console.WriteLine("[Line2Front] [前天车]   等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                bool x11 = await crane.ReadXBitAsync(63497, ct);
                Console.WriteLine($"[Line2Front] [前天车]   X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    Console.WriteLine($"[Line2Front] [前天车]   ✓ X11=1 已吸到(保持在取料位Z={f2PickZ})");
                    break;
                }

                if (retry > 1) throw new Exception("货叉Pos3取料失败: 2次充磁后X11仍=0");
                f2PickZ += 5;
                Console.WriteLine($"[Line2Front] [前天车]   ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ②.5 X11已经确认吸住: 先等前天车Z升到安全高度, 再通知货叉回待机位。
            //      Z回升异常时不发M911, 仍由外层catch按"工件在天车上"处理。
            Console.WriteLine("[Line2Front] [前天车] ②.5 先升Z安全高度, 再让货叉回待机");
            Console.WriteLine("[Line2Front] [前天车]   Z升安全高度");
            await crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);

            if (_forkSvc != null) // TcpClient.Connected不可靠, 不检查IsConnected
            {
                Console.WriteLine("[Line2Front] [前天车]   通知货叉回待机位 M911=1");
                // 通知货叉回待机位置。
                await _forkSvc.GoStandbyAsync(ct);
            }

            // ③ 等中转架空位 → 获取共享区锁 → 获取中转架锁 → 打号机 → 放中转架
            //    预检: 锁外先等中转架空位, 避免抢到锁后发现全满(后天车无法取料→死锁)。
            //    安全: 共享区锁后立即拿中转架锁, 防止前天车打号期间后天车进入中转架4/5取料造成碰撞。
            Console.WriteLine("[Line2Front] [前天车] ③ 等中转架空位...");
            while (_rackSvc?.IsConnected == true)
            {
                try
                {
                    var crs = await _rackSvc.ReadAllStatusAsync(ct);
                    if (!crs.Station4HasPlate || !crs.Station5HasPlate || !crs.Station6HasPlate) break;
                    Console.WriteLine("[Line2Front] [前天车] 中转架4/5/6全满, 等后天车取走...");
                }
                catch { /* 读失败→等下轮 */ }
                await Task.Delay(500, ct);
            }
            Console.WriteLine("[Line2Front] [前天车] ③ 获取共享区锁...");
            while (!await _safety.SharedAreaLock.WaitAsync(TimeSpan.FromSeconds(1), ct))
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine("[Line2Front] [前天车] 等共享区锁...");
            }
            Console.WriteLine("[Line2Front] [前天车] 共享区锁已获取 ✓");
            bool transferRackLocked = false;
            try
            {
                Console.WriteLine("[Line2Front] [前天车] ③.5 获取中转架锁(打号前预占)...");
                while (!transferRackLocked)
                {
                    transferRackLocked = await _transferRackLock.WaitAsync(500, ct);
                    if (!transferRackLocked) Console.WriteLine("[Line2Front] [前天车] 等中转架锁(后端取料占用中)...");
                }
                Console.WriteLine("[Line2Front] [前天车] 中转架锁已获取 ✓");

                await MarkingHandshakeAsync(crane, wp, safeZ, ct);
                await PlaceOnTransferRackAsync(crane, wp, safeZ, ct, transferRackLockAlreadyHeld: true);
                _transferRackLock.Release();
                transferRackLocked = false;
                Console.WriteLine($"[Line2Front] [前天车] 中转架放料完成, 中转架锁已释放 ✓ {wp.IdentityText}");
                magnetOn = false; // 放料成功才清标志

                int homeX = _cfg.GetCraneHomeX(CraneFront2No);
                Console.WriteLine($"[Line2Front] [前天车] X→{homeX + _craneOffsetX} Z回原点 并发执行");
                var xTask = crane.MoveAbsoluteAsync(homeX + _craneOffsetX, -1, -1, ct: ct);
                var zTask = crane.HomeZAsync(ct);
                await Task.WhenAll(xTask, zTask);
                Console.WriteLine($"[Line2Front] [前天车] ═══ 天车流程完成 {wp.IdentityText} ═══");
            }
            finally
            {
                if (transferRackLocked)
                {
                    try { _transferRackLock.Release(); } catch (SemaphoreFullException) { }
                    Console.WriteLine("[Line2Front] [前天车] 异常兜底: 中转架锁已释放");
                }
                _safety.SharedAreaLock.Release();
                Console.WriteLine("[Line2Front] [前天车] 共享区锁已释放 ✓");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line2Front] [前天车] ✘ 异常：{ex.GetType().Name} — {ex.Message}");
            if (ex is TransferRackCacheException rackEx)
            {
                // 工件已经不在天车/叉上, 不能回队列; 保持暂停, 等人工确认中转架缓存。
                _forkPhase = ForkBoringPhase.Idle;
                _boringUnloadDoneSent = false;
                _m912Sent = false;
                _skipBoringTriggered = false;
                _paused = true;
                Console.WriteLine($"[Line2Front] ⚠⚠⚠ 工件已在{rackEx.RackStation}, 但后端缓存未确认；不回队列，等待人工补缓存/确认现场");
                OnSafetyAlarm?.Invoke($"2号线前天车处理{wp.IdentityText}时，工件已放到{rackEx.RackStation}，但后端缓存未确认。引擎已暂停，请人工补缓存并确认现场。");
            }
            else if (magnetOn)
            {
                // magnetOn=true → 工件在天车上 → 暂停引擎等人工确认
                _forkPhase = ForkBoringPhase.Idle;
                _boringUnloadDoneSent = false;
                _m912Sent = false;
                _skipBoringTriggered = false;
                _paused = true;
                Console.WriteLine("[Line2Front] ⚠⚠⚠ 天车已充磁但流程中断！工件在天车上！引擎已暂停,需人工处理！");
                OnSafetyAlarm?.Invoke($"2号线前天车处理{wp.IdentityText}时流程中断，天车磁铁已启动，工件位置不确定。引擎已暂停，请人工确认天车和工件位置。异常：{ex.Message}");
            }
            else
            {
                // 未充磁→工件还在叉Pos3上→放回天车队列等重试
                //   _forkPhase保持WaitingForkReturnFromBoring/Skip: 叉在Pos3带工件, 阻塞新取料
                //   R6108若已发送则不重发，天车重试只负责把Pos3工件取走
                //   天车重试时从Pos3正常取走工件, 通知货叉回待机, 状态自动恢复
                _craneQueue.Enqueue(wp);
                Console.WriteLine($"[Line2Front] [前天车] 未充磁,工件仍在叉Pos3 → 放回天车队列等待重试(队列={_craneQueue.Count})");
            }
        }
    }

    /// <summary>
    /// 打号机文件握手:
    ///   Z降(公式)→退磁放下工件→Z升安全→写 D:\job1\A (内容=刻印+直径)
    ///   →轮询等 D:\job1\B 出现(120s超时)→Z降→充磁取料→Z升→删B
    /// </summary>
    private async Task MarkingHandshakeAsync(CraneService crane, WorkpieceCache wp, int safeZ, CancellationToken ct)
    {
        if (!TryGetStationCoords("ST502", out int mx, out int my, out int mz)) throw new InvalidOperationException("数据库未找到 ST502");
        Console.WriteLine($"[Line2Front] [打号机] ④ ST502({mx},{my},{mz})");

        // ── ④a. Z降(公式)→退磁放下 ──
        int markerZ = ComputePickupZ(mz, wp.Diameter);
        int mxOff = mx + _craneOffsetX, myOff = my + _craneOffsetY, mzOff = markerZ + _craneOffsetZ;
        Console.WriteLine($"[Line2Front] [前天车] Z降={markerZ} +偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ}) 退磁放下");
        await crane.MoveAbsoluteAsync(mxOff, myOff, -1, ct: ct);
        await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, CraneFront2No, "ST502", "2号线前天车-打号机放料前", ct);
        try
        {
            await crane.MoveAbsoluteAsync(-1, -1, mzOff, ct: ct);
        }
        catch (PressureStopException)
        {
            Console.WriteLine("[Line2Front] [打号机] ⚡ 下压触发(已接触)→恢复"); 
            await crane.RecoverFromPressureStopAsync(ct);
        }
        await crane.MagnetOffAsync(ct);
        Console.WriteLine($"[Line2Front] [打号机] 退磁完成 ✓ {wp.IdentityText}");

        // ── ④b. 退磁后立即启动: Z升安全(并发) + 打号机文件握手 ──
        //    天车Z升和打号机打号同时进行, 不再串行等待
        const string markerDir = MarkerSharePath;
        // 连接网络共享
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("net", @"use \\192.168.2.74\1 /user:ggbao 123456")
            {
                CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line2Front] [打号机] 连接共享异常(可忽略): {ex.Message}");
        }

        if (!Directory.Exists(markerDir))
        {
            Console.WriteLine($"[Line2Front] [打号机] ✘ 共享目录不存在: {markerDir}"); 
            throw new DirectoryNotFoundException($"打标机共享目录不存在: {markerDir}");
        }
        Console.WriteLine($"[Line2Front] [打号机] ✓ 共享目录已连接: {markerDir}");

        var file3 = Path.Combine(markerDir, "3.txt");
        var fileA = Path.Combine(markerDir, "A.txt");
        var fileB = Path.Combine(markerDir, "B.txt");
        string content = $"{wp.MarkingContent}\r\n{wp.Diameter}";

        // 清旧B.txt(防上次残留导致打号失败)
        if (File.Exists(fileB)) { File.Delete(fileB); Console.WriteLine("[Line2Front] [打号机] ⚠ 发现旧B.txt已删除"); }

        // Z升+写A.txt 并发启动
        int markerZUp = markerZ - 300 + _craneOffsetZ; // 上升目标Z = 取料位-400+天车偏移
        var zUpTask = crane.MoveAbsoluteAsync(-1, -1, markerZUp, ct: ct);
        Console.WriteLine($"[Line2Front] [打号机] Z升+写A.txt 并发启动... Z升目标={markerZUp}");
        // 写A.txt立即启动(不等Z升) → 打号机开始打标
        Console.WriteLine($"[Line2Front] [打号机] 写A.txt: '{content.Replace("\n", " | ")}'");
        var writeATask = File.WriteAllTextAsync(fileA, content, Encoding.ASCII, ct);
        await Task.WhenAll(zUpTask, writeATask);  // 等Z升+写A都完成
        Console.WriteLine($"[Line2Front] [打号机] Z升完成 + A.txt已写入 ✓ {wp.IdentityText}");

        // ── 轮询等 B.txt (打标完成, 120s超时) ──
        Console.WriteLine("[Line2Front] [打号机] 等待B.txt (超时120s)...");
        var dl = DateTime.UtcNow.AddSeconds(120);
        int pollCount = 0;
        while (!File.Exists(fileB) && DateTime.UtcNow < dl)
        {
            await Task.Delay(500, ct); 
            pollCount++; 
            if (pollCount % 20 == 0) Console.WriteLine($"[Line2Front] [打号机]   轮询中...({pollCount * 500 / 1000}s)");
        }
        if (!File.Exists(fileB)) throw new TimeoutException("打标机超时：120s 未生成 B.txt");
        Console.WriteLine($"[Line2Front] [打号机] B.txt已生成 ✓ {wp.IdentityText}");

        // Z降充磁取料→X11检测→Z升→删B.txt
        int markerZPickup = markerZ + _craneOffsetZ; // 取料Z = 公式Z + 天车偏移
        Console.WriteLine($"[Line2Front] [前天车] Z降={markerZPickup} 充磁取料 → X11检测 → 删B.txt");
        await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, CraneFront2No, "ST502", "2号线前天车-打号机取回前", ct);
        try
        {
            await crane.MoveAbsoluteAsync(-1, -1, markerZPickup, ct: ct);
        }
        catch (PressureStopException)
        {
            Console.WriteLine("[Line2Front] [打号机] ⚡ 下压触发(取料)→恢复"); 
            await crane.RecoverFromPressureStopAsync(ct);
        }

        // X11检测: 充磁→等3s→查X11, 没吸到就退磁→Z↓5mm→充磁→再查, 最多2次
        Console.WriteLine("[Line2Front] [打号机] 充磁→等3s→X11检测");
        int mPickZ = markerZPickup;
        for (int retry = 0; retry <= 2; retry++)
        {
            if (retry == 0)
            {
                Console.WriteLine("[Line2Front] [打号机] 充磁");
                await crane.MagnetOnAsync(ct);
            }
            else
            {
                Console.WriteLine($"[Line2Front] [打号机] 退磁→Z↓到{mPickZ}→充磁");
                await crane.MagnetOffAsync(ct);
                try { await crane.MoveAbsoluteAsync(-1, -1, mPickZ, ct: ct); }
                catch (PressureStopException) { await crane.RecoverFromPressureStopAsync(ct); }
                await crane.MagnetOnAsync(ct);
            }
            Console.WriteLine("[Line2Front] [打号机] 等3s让X11稳定...");
            await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
            bool x11 = await crane.ReadXBitAsync(63497, ct);
            Console.WriteLine($"[Line2Front] [打号机] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
            if (x11) { Console.WriteLine($"[Line2Front] [打号机] ✓ X11=1 已吸到(保持在取料位Z={mPickZ}) {wp.IdentityText}"); break; }
            if (retry > 1) throw new Exception("打号机取料失败: 2次充磁后X11仍=0");
            mPickZ += 5;
            Console.WriteLine($"[Line2Front] [打号机] ⚠ X11=0 未吸到, 准备下探5mm重试");
        }
        //回安全高度
        // await crane.MoveAbsoluteAsync(-1, -1, safeZ + _craneOffsetZ, ct: ct);
        await crane.MoveAbsoluteAsync(-1, -1, markerZUp, ct: ct);
        File.Delete(fileB);
        Console.WriteLine($"[Line2Front] [打号机] ✓ 完成 {wp.IdentityText}");
    }

    /// <summary>
    /// 中转架放料:
    ///   获取或使用外层已预占的 _transferRackLock → 读M814~M816 → 选第一个空位(ST016→ST017→ST018)
    ///   → XY到中转架 → Z降(公式) → 退磁放下 → Z升安全
    /// </summary>
    private async Task PlaceOnTransferRackAsync(CraneService crane, WorkpieceCache wp, int safeZ, CancellationToken ct, bool transferRackLockAlreadyHeld = false)
    {
        bool gotTransferRack = transferRackLockAlreadyHeld;
        if (transferRackLockAlreadyHeld)
        {
            Console.WriteLine("[Line2Front] [中转架] ⑤ 使用前天车打号前已持有的_transferRackLock");
        }
        else
        {
            Console.WriteLine("[Line2Front] [中转架] ⑤ 获取_transferRackLock...");
            // ── 中转架锁: 后端取料优先, 前端放料非阻塞重试 ──
            // WaitAsync(0) 先试一次 → 拿不到说明后端在取料 → 等500ms重试
            while (!gotTransferRack)
            {
                gotTransferRack = await _transferRackLock.WaitAsync(500, ct);
                if (!gotTransferRack) Console.WriteLine("[Line2Front] [中转架] 后端取料占用中, 等待...");
            }
        }
        bool placedOnRack = false;
        bool rackCacheNotified = false;
        string? notifiedRackStation = null;
        try
        {
            if (_rackSvc == null || !_rackSvc.IsConnected) throw new InvalidOperationException("中转架服务未连接");
            // ── 读三个中转架状态 ──
            var status = await _rackSvc.ReadAllStatusAsync(ct);
            Console.WriteLine(
                $"[Line2Front] [中转架] M814={status.Station4HasPlate} M815={status.Station5HasPlate} M816={status.Station6HasPlate}");

            // ── 按优先级选空位 (M814→ST016, M815→ST017, M816→ST018) ──
            string? rackStation = null;
            if (!status.Station4HasPlate) rackStation = "ST016";
            else if (!status.Station5HasPlate) rackStation = "ST017";
            else if (!status.Station6HasPlate) rackStation = "ST018";
            else throw new InvalidOperationException("2号线3个中转架全满，无法放料！");

            if (!TryGetStationCoords(rackStation, out int rx, out int ry, out int rz))
                throw new InvalidOperationException($"未找到 {rackStation}");
            Console.WriteLine($"[Line2Front] [中转架] 选中 {rackStation}({rx},{ry},{rz})");

            // ── XY先到位 → Z降(公式+偏移) → 退磁 → Z升 ──
            int rxOff = rx + _craneOffsetX, ryOff = ry + _craneOffsetY;
            await crane.MoveAbsoluteAsync(rxOff, ryOff, -1, ct: ct); // XY到中转架上方
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, CraneFront2No, rackStation, $"2号线前天车-{rackStation}放料前", ct);
            int rackZ = ComputePickupZ(rz, wp.Diameter) + _craneOffsetZ; // Z公式 + 天车Z偏移
            Console.WriteLine($"[Line2Front] [前天车] Z降={rackZ}(公式+偏移{_craneOffsetZ}) 退磁放下");
            try
            {
                await crane.MoveAbsoluteAsync(-1, -1, rackZ, ct: ct);
            }
            catch (PressureStopException)
            {
                Console.WriteLine("[Line2Front] [中转架] ⚡ 下压触发(已接触)→恢复");
                await crane.RecoverFromPressureStopAsync(ct);
            }

            await crane.MagnetOffAsync(ct);
            // 退磁成功后, 物理工件已经离开天车落在中转架上; 后续任何异常都不能再按“仍在天车/叉上”自动重试。
            placedOnRack = true;
            await crane.MoveAbsoluteAsync(-1, -1, safeZ , ct: ct);
            Console.WriteLine($"[Line2Front] [中转架] ✓ 放料完成 {rackStation} {wp.IdentityText}");
            // 通知后端引擎: 中转架上有工件了
            notifiedRackStation = rackStation;
            // 工件已经物理放到中转架, 软件缓存写入是生命线: 未绑定或写入失败都必须进入异常处理。
            if (OnRackPlaced == null)
                throw new InvalidOperationException($"前天车已放到{rackStation}, 但中转架缓存回调未绑定");
            OnRackPlaced.Invoke(rackStation, wp);
            rackCacheNotified = true;
            wp.ReportStage($"2号线 中转架 {rackStation}");
            // 物理放料和缓存通知已经完成, 先更新本地快照, 避免PLC有版信号下一轮才刷新导致后端暂时看不到。
            if (rackStation == "ST016") DeviceStatus.TransferRack4Free = false;
            else if (rackStation == "ST017") DeviceStatus.TransferRack5Free = false;
            else if (rackStation == "ST018") DeviceStatus.TransferRack6Free = false;
        }
        catch (Exception ex)
        {
            if (placedOnRack && !rackCacheNotified)
            {
                // 物理已放下但后端_wps没有确认写入, 继续运行会形成“架上有板但无缓存”的危险状态。
                _paused = true;
                Console.WriteLine($"[Line2Front] ⚠ 工件已放到中转架{notifiedRackStation ?? "(未知)"}但后端缓存未确认, 前端引擎已暂停, 需人工补缓存/确认现场; 异常={ex.Message}");
                throw new TransferRackCacheException(notifiedRackStation ?? "(未知)", ex);
            }
            throw;
        }
        finally
        {
            // 只释放本方法确认拿到的中转架锁; 防止取消/异常路径误释放后端持有的锁。
            if (gotTransferRack && !transferRackLockAlreadyHeld)
            {
                _transferRackLock.Release();
                gotTransferRack = false;
                Console.WriteLine("[Line2Front] [中转架] _transferRackLock 已释放");
            }
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
            Console.WriteLine($"[Line2Front] [机械手1]   ⚠ 恢复异常(忽略): {ex.Message}");
        }
    }

    /// <summary>Z下降公式 — 通用取料/放料用。
    /// Z目标 = 台面Z - Round[(d/2/zFactor1)+(d/2/zFactor2)]</summary>
    private int ComputePickupZ(int rackZ, double diameter)
    {
        double d = diameter;
        int descent = (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));
        int z = rackZ - descent;
        Console.WriteLine($"[Line2Front]   Z公式: {rackZ} - [({d}/2/{_cfg.Grinding.ZFactor1})+({d}/2/{_cfg.Grinding.ZFactor2})] = {rackZ}-{descent}={z}");
        return z;
    }

    /// <summary>初始化设备连接 — 引擎启动时执行一次。
    /// 机械手1复用 ManipulatorConnectionCache(与主页面共享TCP)，货叉和上料架独立连接。</summary>
    private async Task InitServicesAsync(CancellationToken ct)
    {
        // ── 机械手1(ModbusTCP 192.168.2.85:502) — 复用缓存，不建重复连接 ──
        _manipulator1 = _manipulatorCache.GetOrCreateService(1);
        try { if (!_manipulator1.IsConnected) await _manipulator1.ConnectAsync(ct); Console.WriteLine($"[Line2Front] 机械手1 已连接 (复用ManipulatorCache) ✓"); }
        catch (Exception ex) { Console.WriteLine($"[Line2Front] ⚠ 机械手1 连接失败: {ex.Message}"); }

        // ── 货叉 ST712 (MC协议, UseBitReadForM=true, nibble编码) ──
        try
        {
            if (TryGetStationIp("ST712", out string fIp, out int fPort))
            { Console.WriteLine($"[Line2Front] 正在连接货叉 {fIp}:{fPort}..."); _forkSvc = new ForkService("二号线货叉", fIp, fPort > 0 ? fPort : 9000); await _forkSvc.ConnectAsync(ct); Console.WriteLine("[Line2Front] 货叉 已连接"); }
            else { Console.WriteLine("[Line2Front] 未找到 ST712 IP"); }
        }
        catch (Exception ex) { Console.WriteLine($"[Line2Front] 货叉连接失败: {ex.Message}"); }

        // ── 总上料架+中转架 192.168.2.63:9000 ──
        try
        {
            if (_rackSvc == null)
            { _rackSvc = new CenteringRackService("总上料架+中转架", "192.168.2.63", 9000); await _rackSvc.ConnectAsync(ct); }
            else if (!_rackSvc.IsConnected)
            { await _rackSvc.ConnectAsync(ct); }
            Console.WriteLine($"[Line2Front] 总上料架+中转架 已连接 192.168.2.63:9000 (MC) OK");
        }
        catch (Exception ex) { Console.WriteLine($"[Line2Front] 总上料架连接失败: {ex.Message}"); }

        // ── 双头镗 Modbus TCP (提前连接, 机械手取料前需要一直监视R6101状态) ──
        if (TryGetStationIp("ST402", out string bIp, out _))
        {
            try
            {
                _boringSvc = new BoringModbusService("2号线双头镗", bIp);
                await _boringSvc.ConnectAsync(ct);
                Console.WriteLine($"[Line2Front] 双头镗Modbus 已连接 {bIp}:502 ✓ (持续监视R6101)");
            }
            catch (Exception ex) { Console.WriteLine($"[Line2Front] ⚠ 双头镗 连接失败: {ex.Message} (保守拒绝机械手取料, 不跳过R6101检查)"); }
        }
        else { Console.WriteLine("[Line2Front] ⚠ 未找到 ST402 IP，双头镗无法连接"); }

        // ── 读取2号线前天车校准偏移量 (ST104的x_dis/y_dis/z_dis) ──
        if (_stationCoords.TryGetValue("ST104", out var craneRow))
        { _craneOffsetX = (int)craneRow.XOffset; _craneOffsetY = (int)craneRow.YOffset; _craneOffsetZ = (int)craneRow.ZOffset; }

        Console.WriteLine($"[Line2Front] 设备初始化完成: 机械手1✓ 货叉✓ 上料架✓ 双头镗✓ 天车偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ})");
    }

    /// <summary>等待机械手1回到安全Y位 — 轮询 ReadStatusAsync, 容差±10mm, 默认60s超时。
    /// 天车进入叉区前的安全检查，防止碰撞。</summary>
    private async Task WaitForManipulatorSafeAsync(CancellationToken ct)
    {
        int safeY = _cfg.SkewBed.Manipulator1SafeY;
        var dl = DateTime.UtcNow.AddMilliseconds(_cfg.Grinding.HandshakeTimeoutMs);
        var nextReconnectAt = DateTime.MinValue;
        var reconnectInterval = TimeSpan.FromSeconds(3);
        bool forceReconnect = false;
        Console.WriteLine($"[Line2Front] 机械手1 安全检查: 目标Y={safeY} 超时={_cfg.Grinding.HandshakeTimeoutMs / 1000}s");
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
                    Console.WriteLine("[Line2Front] 机械手1 状态读取返回null, 将限频重连后重新确认安全位");
                    forceReconnect = true;
                    continue;
                }

                // ── Y坐标在安全值±10mm以内 → 视为已到位 ──
                if (Math.Abs(s.YPos - safeY) <= 10) { Console.WriteLine($"[Line2Front] 机械手1 Y={s.YPos} 在安全位 ✓"); return; }
                Console.WriteLine($"[Line2Front] 机械手1 Y={s.YPos} 安全Y={safeY} 等待中...");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.WriteLine($"[Line2Front] 读机械手1异常：{ex.Message}"); }
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
                catch (Exception ex) { Console.WriteLine($"[Line2Front] 机械手1 重连前断开旧连接异常: {ex.Message}"); }
            }

            if (_manipulator1.IsConnected) return true;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            Console.WriteLine($"[Line2Front] 机械手1 {reason}, 尝试重连...");
            await _manipulator1.ConnectAsync(linkedCts.Token);
            Console.WriteLine("[Line2Front] 机械手1 重连成功, 继续确认安全位");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Line2Front] 机械手1 重连失败({reason}): {ex.Message}");
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
        Console.WriteLine($"[Line2Front] 慢轮告警: IO/业务耗时={elapsed.TotalMilliseconds:F0}ms 轮次={_cycleCount} 货叉阶段={_forkPhase} 缓存={CachedCount}");
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
        _forkDispatchLock.Dispose();
        _craneFrontLock.Dispose();
        if (_ownsTransferRackLock) _transferRackLock.Dispose();
        Console.WriteLine("[Line2Front] 引擎已释放");
    }
}




