using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Communication.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using RackAddr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.CenteringRackAddress;

namespace AutomaticOnlineHostComputer.Service;

// /// 1号线动平衡流转引擎 — 机械手2 + 机械手3
// ///
// /// 【业务背景】
// ///   后天车把加工完的工件按版长分流:
// ///     ≥800mm → 动平衡下料架1(ST019, M817) → 需要做动平衡
// ///     <800mm → 研磨上料架1号位(ST010, M720) → 直接进研磨
// ///   本引擎负责动平衡侧的搬运。
// ///
// /// 【机械手2流程 — 动平衡料架搬运】
// ///   M817有板，或ST020满足M823允许取料且M818软件缓存存在
// ///   → Z↓取料位(充磁+X11检测→Z↑安全高度)
// ///   → XY→ST008动平衡料架1 → Z↓台面 → 退磁 → Z↑安全 → Y回安全位
// ///   工件直径从 OnBalancingRackPlaced 回调缓存读取, 取不到则暂停人工确认
// ///
// /// 【机械手3流程 — 研磨上料架搬运】
// ///   M700有板，或ST021满足M825允许取料且M821软件缓存存在
// ///   → 读D100(MC65)获取M700处工件直径
// ///   → Z↓取料位(充磁+X11检测→Z↑安全高度) → M700来源写M701=1通知PLC取料完成
// ///   → XY→ST010研磨上料架1号位(M720) → Z↓台面 → 退磁 → Z↑安全
// ///   → 写M721=1通知PLC放料完成 → Y回安全位
// ///
// /// 【主循环 500ms/轮】
// ///   ① 读 MC63(192.168.2.63:9000): M800起2字 → M817及M818~M826握手
// ///   ② 读 MC65(192.168.2.65:9000): M700所在字 → M700/M710; M720所在字 → M720
// ///   ③ 机械手空闲 + 来源可取/有缓存 + 目的地允许放料 → fire-and-forget触发搬运
// ///
// /// 【工件数据流】
// ///   后天车DoUnload → OnBalancingRackPlaced("M817", wp)
// ///     → HomeViewModel wiring → SetBalancingWp("M817", wp)
// ///     → _balWps["M817"] = wp
// ///   M2Flow取料: TryGetBalWp("M817", out wp) → wp.Diameter → Pz公式计算Z↓
// ///
// /// 【调试日志速查】
// ///   引擎启动:  [平衡引擎] 机械手2: M817/M818→ST008
// ///   连接:      [平衡引擎] MC63 ✓ (192.168.2.63:9000, 读M817/M818/M821)
// ///   信号快照:  [平衡引擎] L1 M817=0 M818放=0 M823取=0 M710=0 M700=0 M825取=0 M720=0 M2忙=False M3忙=False
// ///   工件缓存:  [平衡引擎] 工件缓存: M817 D=147mm L=800mm
// ///   M2取料:    [平衡引擎] [M2] ① 取料 ST019 Z=...
// ///   M2 X11:    [平衡引擎] [M2] X11=1(有版) (第1次)
// ///   M2完成:    [平衡引擎] [M2] ═══ 完成 ═══
// ///   M2异常:    [平衡引擎] [M2] ✘ 异常: ...
// ///   M3异常:    [平衡引擎] [M3] ✘ 异常: ...
// ///   引擎异常:   [平衡引擎] ERR: ...
///

// 机械手2 (M2Flow):
// 取料源: M817 (1号线后天车送来) / M818 (2号线后天车送来)
//     → 都是后天车送的 → 缓存里有 → TryGetBalWp 能拿到 ✅
// 目的地: M710 (ST008 动平衡料架1, MC65)
// 完成后: 写 M711=1 (MC65)
//
//     【动平衡机加工】工件顺序打乱
//
// 机械手3 (M3Flow):
// 取料源1: M700 (动平衡料架2, MC65)
//     → 动平衡做完工件顺序乱了，缓存对不上 → 必须读 D100 ✅
// 取料源2: M821 (2号线不需动平衡位, MC63)
//     → 2号线后天车直接送来的 → 应该读缓存
// 目的地:   M720 (ST010 研磨上料架1号位, MC65)
// M700取料后: 写 M701=1 (MC65)
// 完成后: 写 M721=1 (MC65)
public sealed class BalancingFlowEngine : IDisposable
{
    private static int _nextInstanceId;
    public int EngineId { get; } = Interlocked.Increment(ref _nextInstanceId);

    // ── 依赖注入 ──
    private readonly ManipulatorConnectionCache _manipulatorCache; // 机械手共享缓存(复用主页面TCP)
    private readonly CraneConnectionCache _craneCache;             // 后天车共享连接, 用于读取X坐标(安全互斥)
    private readonly MotionConfig _cfg;                            // 运动参数配置(速度/公式/Z因子等)
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords; // 工位坐标
    private readonly CancellationTokenSource _engineCts = new();   // 引擎取消令牌
    private Task? _engineTask;       // 主循环Task
    private bool _disposed;          // 是否已Dispose
    private volatile bool _paused;   // 暂停标志(主页面+引擎跨线程)
    private int _cycleCount;         // 主循环轮次计数(每500ms+1)

    // ── MC连接(共享McConnectionCache, 内部去重防重复TCP) ──
    //   MC63=192.168.2.63:9000 → 读M817(1号线有板)和2号线ST020/ST021握手位(M818~M826)
    //   MC65=192.168.2.65:9000 → 读 M710(ST008动平衡料架1) M700(动平衡料架2) M720(ST010研磨上料架) + D100直径
    private readonly McConnectionCache _mcCache;
    private MitsubishiMcClient? _mc63, _mc65;

    // ── 机械手服务(复用ManipulatorConnectionCache, CraneService封装ModbusTCP) ──
    private CraneService? _m2, _m3;        // 机械手2/3, null表示Init阶段连接失败
    // 忙标志使用int + Interlocked原子占坑:
    // 主循环先把0改成1再启动fire-and-forget任务, 防止锁等待期间每500ms重复派发同一个物理工件。
    private int _m2BusyFlag, _m3BusyFlag;

    // ── MC信号快照(每轮主循环刷新, M2Flow/M3Flow用快照参数避免读可变字段) ──
    private bool _m817, _m710, _m700, _m720;
    private bool _m818CanPlace, _m819PlaceDone, _m820CanPlace, _m821PlaceDone;
    private bool _m823CanPick, _m824PickDone, _m825CanPick, _m826PickDone;

    // ── 应急上下文: 只记录本引擎当前动作明确持有的锁, 应急释放时不猜测、不全放 ──
    private readonly object _emergencyLock = new();
    private string _m2EmergencySource = string.Empty; // M817/M818
    private string _m3EmergencySource = string.Empty; // M700/M821
    private bool _m2HoldsM817, _m2HoldsM818, _m3HoldsM821, _m3HoldsM720;
    private CancellationTokenSource? _m2ActionCts, _m3ActionCts;
    private TaskCompletionSource<bool>? _m2ActionCompletion, _m3ActionCompletion;
    private int _m2ActionVersion, _m3ActionVersion;

    // ── 工件缓存(后天车DoUnload→OnBalancingRackPlaced→SetBalancingWp写入) ──
    //   M817缓存: Line1Rear后天车放料到动平衡下料架1(长工件≥800mm)
    //   M818缓存: Line2Rear后天车放料到动平衡下料架1(长工件≥800mm)
    //   M821缓存: Line2Rear后天车放料到不需动平衡位(短工件<800mm)
    //   M700无缓存: 动平衡后工件顺序乱了, 直径从D100(MC65)读
    private readonly Dictionary<string, WorkpieceCache> _balWps = new();
    private readonly object _balWpsLock = new();
    public int CachedCount { get { lock (_balWpsLock) return _balWps.Count; } }
    public string CacheKeysText
    {
        get
        {
            lock (_balWpsLock)
                return _balWps.Count == 0 ? "(空)" : string.Join(",", _balWps.Keys.OrderBy(x => x));
        }
    }

    /// <summary>后天车放料到下料架时调用, 写入工件数据供机械手取料</summary>
    public void SetBalancingWp(string register, WorkpieceCache wp)
    {
        lock (_balWpsLock)
        {
            _balWps[register] = wp;
            Console.WriteLine($"[平衡引擎#{EngineId}] 工件缓存: {register} {wp.IdentityText} D={wp.Diameter}mm L={wp.Length}mm Keys=[{CacheKeysText}]");
        }
    }

    /// <summary>M3Flow放料至研磨上料架ST010时回调, 通知研磨引擎入缓存(完整工件信息)</summary>
    public Action<WorkpieceCache>? OnGrindingRackPlaced;

    private bool TryGetBalWp(string reg, out WorkpieceCache wp)
    {
        lock (_balWpsLock)
        {
            return _balWps.TryGetValue(reg, out wp);
        }
    }

    private bool HasBalWp(string reg)
    {
        lock (_balWpsLock)
        {
            return _balWps.ContainsKey(reg);
        }
    }

    public bool IsRunning => _engineTask != null && !_engineTask.IsCompleted;
    public bool IsPaused => _paused;
    public bool M2Busy => System.Threading.Volatile.Read(ref _m2BusyFlag) == 1;
    public bool M3Busy => System.Threading.Volatile.Read(ref _m3BusyFlag) == 1;
    public bool Mc63Connected => _mc63?.IsConnected == true;
    public bool Mc65Connected => _mc65?.IsConnected == true;
    public bool Mc63SnapshotValid => _mc63SnapshotValid;
    public bool Mc65SnapshotValid => _mc65SnapshotValid;
    public bool M817HasPlate => _m817;
    public bool M818HasPlate => _m823CanPick && HasBalWp("M818");
    public bool M821HasPlate => _m825CanPick && HasBalWp("M821");
    public bool M818CachePresent => HasBalWp("M818");
    public bool M821CachePresent => HasBalWp("M821");
    public bool M818CanPlace => _m818CanPlace;
    public bool M819PlaceDone => _m819PlaceDone;
    public bool M820CanPlace => _m820CanPlace;
    public bool M821PlaceDone => _m821PlaceDone;
    public bool M823CanPick => _m823CanPick;
    public bool M824PickDone => _m824PickDone;
    public bool M825CanPick => _m825CanPick;
    public bool M826PickDone => _m826PickDone;
    public bool M710CanPlace => _m710;
    public bool M700HasPlate => _m700;
    public bool M720CanPlace => _m720;

    // 仅供状态页面区分“连接在线”与“本轮读到完整快照”；业务派发仍使用主循环局部变量。
    private volatile bool _mc63SnapshotValid;
    private volatile bool _mc65SnapshotValid;

    public string GetBalancingEmergencyInfo(string position)
    {
        position = NormalizeBalancingPosition(position);
        string wpText;
        lock (_balWpsLock)
            wpText = _balWps.TryGetValue(position, out var wp) ? $"{wp.IdentityText} D={wp.Diameter} L={wp.Length}" : "无缓存";

        string context;
        lock (_emergencyLock)
        {
            context =
                $"M2来源={TextOrNone(_m2EmergencySource)} 持锁[M817={_m2HoldsM817},M818={_m2HoldsM818}] 忙={M2Busy}\n" +
                $"M3来源={TextOrNone(_m3EmergencySource)} 持锁[M821={_m3HoldsM821},M720={_m3HoldsM720}] 忙={M3Busy}";
        }

        return
            $"[动平衡应急诊断] 位置={position}\n" +
            $"缓存={wpText}; 全部缓存Keys=[{CacheKeysText}]\n" +
            $"PLC快照: M817={V(_m817)} M818放={V(_m818CanPlace)} M819放完={V(_m819PlaceDone)} M823取={V(_m823CanPick)} M824取完={V(_m824PickDone)} M710={V(_m710)} M700={V(_m700)} M820放={V(_m820CanPlace)} M821放完={V(_m821PlaceDone)} M825取={V(_m825CanPick)} M826取完={V(_m826PickDone)} M720={V(_m720)}\n" +
            $"{context}\n" +
            "提示: 这里只清上位机缓存和本引擎明确持有的锁; 不会写PLC信号, 不会控制磁铁/机械手动作。";
    }

    public async Task<string> EmergencyClearBalancingPositionAsync(
        string position, bool resumeAfterClear, CancellationToken ct = default)
    {
        position = NormalizeBalancingPosition(position);
        var logs = new List<string>();
        _paused = true; // 应急期间禁止主循环派发新M2/M3动作。

        bool touchesM2;
        bool touchesM3;
        Task? m2Completion = null;
        Task? m3Completion = null;
        bool releasedM2 = false;
        bool releasedM3 = false;
        string m2Source = string.Empty;
        string m3Source = string.Empty;

        lock (_emergencyLock)
        {
            touchesM2 =
                (_m2EmergencySource == "M817" && position is "M817" or "M818" or "M710") ||
                (_m2EmergencySource == "M818" && position is "M818" or "M710");
            touchesM3 =
                (_m3EmergencySource == "M700" && position is "M700" or "M821" or "M720") ||
                (_m3EmergencySource == "M821" && position is "M821" or "M720");

            if (touchesM2)
            {
                m2Source = _m2EmergencySource;
                m2Completion = _m2ActionCompletion?.Task;
                CancelM2ActionNoLock(logs);
            }

            if (touchesM3)
            {
                m3Source = _m3EmergencySource;
                m3Completion = _m3ActionCompletion?.Task;
                CancelM3ActionNoLock(logs);
            }
        }

        var pending = new[] { m2Completion, m3Completion }.Where(x => x != null).Cast<Task>().ToArray();
        if (pending.Length > 0)
        {
            var allExited = Task.WhenAll(pending);
            var timeout = Task.Delay(TimeSpan.FromSeconds(6), ct);
            if (await Task.WhenAny(allExited, timeout) != allExited)
            {
                logs.Add("旧动作6秒内未退出, 保持暂停且不释放锁、不清缓存/busy, 请确认设备通信后重试应急");
                string timeoutMessage = BuildBalancingEmergencyLog(position, logs);
                Console.WriteLine(timeoutMessage);
                return timeoutMessage;
            }
            await allExited;
            logs.Add("旧动作=已退出");
        }

        // 旧动作finally已经退出后，才释放它仍记录持有的残留位置锁。
        // 正常情况下finally会先释放并清标记；这里仅处理应急取消后残留的锁。
        lock (_emergencyLock)
        {
            if (touchesM2)
            {
                if (_m2HoldsM818) { _lockM818?.Release(); _m2HoldsM818 = false; releasedM2 = true; logs.Add("旧动作退出后释放M818残留锁"); }
                if (_m2HoldsM817) { _lockM817?.Release(); _m2HoldsM817 = false; releasedM2 = true; logs.Add("旧动作退出后释放M817残留锁"); }
            }
            if (touchesM3)
            {
                if (_m3HoldsM720) { _lockM720?.Release(); _m3HoldsM720 = false; releasedM3 = true; logs.Add("旧动作退出后释放M720残留锁"); }
                if (_m3HoldsM821) { _lockM821?.Release(); _m3HoldsM821 = false; releasedM3 = true; logs.Add("旧动作退出后释放M821残留锁"); }
            }
        }

        // 选择目的位M710/M720时，真实工件身份仍存放在动作来源M817/M818/M821。
        // 应急只清选中位置和被取消动作的实际来源，不碰其它正常位置缓存或研磨FIFO。
        var cacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { position };
        if (touchesM2 && !string.IsNullOrWhiteSpace(m2Source)) cacheKeys.Add(m2Source);
        if (touchesM3 && string.Equals(m3Source, "M821", StringComparison.OrdinalIgnoreCase)) cacheKeys.Add(m3Source);
        lock (_balWpsLock)
        {
            foreach (var key in cacheKeys)
                logs.Add(_balWps.Remove(key) ? $"缓存{key}=已删除" : $"缓存{key}=无");
        }

        lock (_emergencyLock)
        {
            if (touchesM2) { ClearM2Busy(); _m2EmergencySource = string.Empty; logs.Add("M2忙标志=已清"); }
            if (touchesM3) { ClearM3Busy(); _m3EmergencySource = string.Empty; logs.Add("M3忙标志=已清"); }
        }

        if (!releasedM2 && !releasedM3)
            logs.Add("应急残留锁=无(已由旧动作finally释放或原本未持有)");
        if (position is "M700" or "M710")
            logs.Add($"{position}=PLC物理位, 本按钮不写PLC完成信号, 需人工确认设备侧状态");
        if (position == "M720")
            logs.Add("M720研磨FIFO缓存不在动平衡应急中清理, 如需清研磨缓存请使用研磨机应急");

        if (resumeAfterClear && IsRunning) { _paused = false; logs.Add("动平衡引擎=已恢复派发"); }
        else logs.Add("动平衡引擎=保持人工暂停");

        string message = BuildBalancingEmergencyLog(position, logs);
        Console.WriteLine(message);
        return message;
    }

    private string BuildBalancingEmergencyLog(string position, List<string> logs) =>
        $"[平衡引擎#{EngineId}] [动平衡应急] {DateTime.Now:yyyy-MM-dd HH:mm:ss} 位置={position}; {string.Join("; ", logs)}; Keys=[{CacheKeysText}]";
    /// <summary>
    /// 构造器 — 保存依赖引用+4把平衡料架位置锁。
    /// lockM817/M818/M821/M720分别保护M817/M818/M821/M720位置, 防止天车和机械手同时操作。
    /// </summary>
    public BalancingFlowEngine(ManipulatorConnectionCache mc, CraneConnectionCache cc,
        McConnectionCache mcc, MotionConfig cfg, Dictionary<string, MachineManagementRowVm> sc,
        SemaphoreSlim? lockM817 = null, SemaphoreSlim? lockM818 = null,
        SemaphoreSlim? lockM821 = null, SemaphoreSlim? lockM720 = null)
    {
        _manipulatorCache = mc;
        _craneCache = cc;
        _mcCache = mcc;
        _cfg = cfg;
        _stationCoords = sc;
        _lockM817 = lockM817; _lockM818 = lockM818;
        _lockM821 = lockM821; _lockM720 = lockM720;
        Console.WriteLine($"[平衡引擎#{EngineId}] 实例已创建");
        Console.WriteLine($"  平衡锁: M817={L(_lockM817)} M818={L(_lockM818)} M821={L(_lockM821)} M720={L(_lockM720)}");

        // ── 加载机械手偏移量(从machine表x_dis/y_dis/z_dis列) ──
        if (_stationCoords.TryGetValue("ST902", out var r2)) { _m2OffsetY = (int)r2.YOffset; _m2OffsetZ = (int)r2.ZOffset; }
        if (_stationCoords.TryGetValue("ST903", out var r3)) { _m3OffsetY = (int)r3.YOffset; _m3OffsetZ = (int)r3.ZOffset; }
        Console.WriteLine($"  机械手偏移: M2 Y={_m2OffsetY} Z={_m2OffsetZ} | M3 Y={_m3OffsetY} Z={_m3OffsetZ}");
    }
    private static string L(SemaphoreSlim? l) => l != null ? "✓" : "✗(无锁!)";
    private static string TextOrNone(string value) => string.IsNullOrWhiteSpace(value) ? "无" : value;
    private static string NormalizeBalancingPosition(string position) => (position ?? string.Empty).Trim().ToUpperInvariant();

    private void SetM2EmergencySource(string source)
    {
        lock (_emergencyLock) _m2EmergencySource = source;
    }

    private void SetM3EmergencySource(string source)
    {
        lock (_emergencyLock) _m3EmergencySource = source;
    }

    private void MarkM2Lock(string lockName, bool held)
    {
        lock (_emergencyLock)
        {
            if (lockName == "M817") _m2HoldsM817 = held;
            else if (lockName == "M818") _m2HoldsM818 = held;
        }
    }

    private void MarkM3Lock(string lockName, bool held)
    {
        lock (_emergencyLock)
        {
            if (lockName == "M821") _m3HoldsM821 = held;
            else if (lockName == "M720") _m3HoldsM720 = held;
        }
    }

    private bool ConsumeM2Lock(string lockName)
    {
        lock (_emergencyLock)
        {
            if (lockName == "M817" && _m2HoldsM817) { _m2HoldsM817 = false; return true; }
            if (lockName == "M818" && _m2HoldsM818) { _m2HoldsM818 = false; return true; }
            return false;
        }
    }

    private bool ConsumeM3Lock(string lockName)
    {
        lock (_emergencyLock)
        {
            if (lockName == "M821" && _m3HoldsM821) { _m3HoldsM821 = false; return true; }
            if (lockName == "M720" && _m3HoldsM720) { _m3HoldsM720 = false; return true; }
            return false;
        }
    }

    private void ClearM2EmergencyContext()
    {
        lock (_emergencyLock)
        {
            _m2EmergencySource = string.Empty;
            _m2HoldsM817 = false;
            _m2HoldsM818 = false;
        }
    }

    private void ClearM3EmergencyContext()
    {
        lock (_emergencyLock)
        {
            _m3EmergencySource = string.Empty;
            _m3HoldsM821 = false;
            _m3HoldsM720 = false;
        }
    }

    private int BeginM2Action(CancellationTokenSource cts, TaskCompletionSource<bool> completion)
    {
        lock (_emergencyLock)
        {
            _m2ActionCts = cts;
            _m2ActionCompletion = completion;
            return ++_m2ActionVersion;
        }
    }

    private int BeginM3Action(CancellationTokenSource cts, TaskCompletionSource<bool> completion)
    {
        lock (_emergencyLock)
        {
            _m3ActionCts = cts;
            _m3ActionCompletion = completion;
            return ++_m3ActionVersion;
        }
    }

    private bool IsM2ActionCurrent(int version)
    {
        lock (_emergencyLock) return version == _m2ActionVersion;
    }

    private bool IsM3ActionCurrent(int version)
    {
        lock (_emergencyLock) return version == _m3ActionVersion;
    }

    private void FinishM2Action(int version, TaskCompletionSource<bool> completion)
    {
        lock (_emergencyLock)
        {
            if (ReferenceEquals(_m2ActionCompletion, completion)) _m2ActionCompletion = null;
            if (version == _m2ActionVersion) _m2ActionCts = null;
        }
        completion.TrySetResult(true);
    }

    private void FinishM3Action(int version, TaskCompletionSource<bool> completion)
    {
        lock (_emergencyLock)
        {
            if (ReferenceEquals(_m3ActionCompletion, completion)) _m3ActionCompletion = null;
            if (version == _m3ActionVersion) _m3ActionCts = null;
        }
        completion.TrySetResult(true);
    }

    private void CancelM2ActionNoLock(List<string> logs)
    {
        try
        {
            _m2ActionCts?.Cancel();
            if (_m2ActionCts != null) logs.Add("M2当前动作=已发送取消");
        }
        catch (ObjectDisposedException) { }
        _m2ActionCts = null;
        _m2ActionVersion++;
    }

    private void CancelM3ActionNoLock(List<string> logs)
    {
        try
        {
            _m3ActionCts?.Cancel();
            if (_m3ActionCts != null) logs.Add("M3当前动作=已发送取消");
        }
        catch (ObjectDisposedException) { }
        _m3ActionCts = null;
        _m3ActionVersion++;
    }

    // ── 平衡料架位置锁(防止天车和机械手同时操作同位置, HomeViewModel注入) ──
    private readonly SemaphoreSlim? _lockM817; // M817: Line1Rear(放) vs M2Flow(取)
    private readonly SemaphoreSlim? _lockM818; // M818: Line2Rear(放) vs M2Flow(取)
    private readonly SemaphoreSlim? _lockM821; // M821: Line2Rear(放) vs M3Flow(取)
    private readonly SemaphoreSlim? _lockM720; // M720: Line1Rear(放) vs M3Flow(放) vs Grinding(取)

    // ── 机械手偏移量(数据库machine表x_dis/y_dis/z_dis, 机械手只移YZ轴) ──
    private int _m2OffsetY, _m2OffsetZ; // 机械手2偏移量(ST902的YOffset/ZOffset)
    private int _m3OffsetY, _m3OffsetZ; // 机械手3偏移量(ST903的YOffset/ZOffset)

    /// <summary>启动引擎 — Task.Run后台运行, 幂等(重复调用忽略)</summary>
    public void Start()
    {
        if (IsRunning)
        {
            if (_paused)
            {
                Resume();
                return;
            }
            Console.WriteLine($"[平衡引擎#{EngineId}] 引擎已在运行, 跳过重复启动");
            return;
        }
        _paused = false;
        _mc63SnapshotValid = false;
        _mc65SnapshotValid = false;
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine($"  [平衡引擎#{EngineId}] 机械手2: M817/M818→ST008");
        Console.WriteLine("  机械手3: M700/M821→ST010(M720)");
        Console.WriteLine("══════════════════════════════════════════");
        _engineTask = Task.Run(() => Loop(_engineCts.Token));
    }

    public void Stop() => _engineCts.Cancel();

    public void Pause()
    {
        _paused = true;
        Console.WriteLine($"[平衡引擎#{EngineId}] ⏸ 暂停");
    }

    public void Resume()
    {
        _paused = false;
        Console.WriteLine($"[平衡引擎#{EngineId}] ▶ 恢复");
    }

    /// <summary>主循环 — 初始化连接 → 500ms轮询PLC信号 → 触发机械手搬运</summary>
    private async Task Loop(CancellationToken ct)
    {
        // ═══ 初始化: 连接MC63+MC65+机械手2+机械手3 (各加5s超时防死等) ═══
        try
        {
            using var cts63 = new CancellationTokenSource(5000);
            using var linked63 = CancellationTokenSource.CreateLinkedTokenSource(ct, cts63.Token);
            _mc63 = await _mcCache.GetOrCreateAsync("192.168.2.63", 9000, linked63.Token);
            Console.WriteLine("[平衡引擎] MC63 ✓ (192.168.2.63:9000, 读M817/M818/M821)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[平衡引擎] MC63 连接失败(5s超时): {ex.Message}");
        }
        try
        {
            using var cts65 = new CancellationTokenSource(5000);
            using var linked65 = CancellationTokenSource.CreateLinkedTokenSource(ct, cts65.Token);
            _mc65 = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, linked65.Token);
            Console.WriteLine("[平衡引擎] MC65 ✓ (192.168.2.65:9000, 读M710/M700/M720+D100)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[平衡引擎] MC65 连接失败(5s超时): {ex.Message}");
        }

        try
        {
            _m2 = _manipulatorCache.GetOrCreateService(2);
            if (!_m2.IsConnected) await _m2.ConnectAsync(ct);
            // 机械手速度配置只设一次 (避免每个Flow重复写9个寄存器)
            var spd2 = _cfg.GetManipulatorSpeed(2);
            //设置机械手2速度
            await _m2.SetAbsSpeedAsync(spd2.X.Speed, spd2.X.Accel, spd2.X.Decel,
                spd2.Y.Speed, spd2.Y.Accel, spd2.Y.Decel, spd2.Z.Speed, spd2.Z.Accel, spd2.Z.Decel, ct);
            Console.WriteLine("[平衡引擎] 机械手2 ✓ (速度已配置)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[平衡引擎] 机械手2 FAIL: {ex.Message}");
        }

        try
        {
            _m3 = _manipulatorCache.GetOrCreateService(3);
            if (!_m3.IsConnected) await _m3.ConnectAsync(ct);
            //配置文件拿速度
            var spd3 = _cfg.GetManipulatorSpeed(3);
            //设置机械手3速度
            await _m3.SetAbsSpeedAsync(spd3.X.Speed, spd3.X.Accel, spd3.X.Decel,
                spd3.Y.Speed, spd3.Y.Accel, spd3.Y.Decel, spd3.Z.Speed, spd3.Z.Accel, spd3.Z.Decel, ct);
            Console.WriteLine("[平衡引擎] 机械手3 ✓ (速度已配置)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[平衡引擎] 机械手3 FAIL: {ex.Message}");
        }

        Console.WriteLine("[平衡引擎] ═══ 主循环 ═══");

        while (!ct.IsCancellationRequested)
        {
            if (_paused)
            {
                await Task.Delay(500, ct);
                continue;
            }
            try
            {
                _cycleCount++;
                // 本轮快照必须由PLC新鲜读取得到, 不能沿用上一轮旧值派发动作。
                // 目的位(M710/M720)如果通信失败后仍沿用“空闲”的旧值, 会有叠料风险。
                bool mc63SnapshotOk = false;
                bool mc65SnapshotOk = false;
                _m817 = false;
                _m818CanPlace = _m819PlaceDone = _m820CanPlace = _m821PlaceDone = false;
                _m823CanPick = _m824PickDone = _m825CanPick = _m826PickDone = false;
                _m700 = _m710 = _m720 = false;

                // ── ① 读MC63: M800起2字→第2字=M816~M831(对齐CenteringRackService读法) ──
                // 2号线ST020/ST021已改为握手位: M818/M820允许后天车放料, M823/M825允许机械手取料。
                if (_mc63?.IsConnected == true)
                {
                    try
                    {
                        var r = await _mc63.ReadMAlignedWordAsync(800, 2, ct);
                        var w = r.IntValues[1]; // 第2字 M816~M831
                        _m817 = (w & (1 << RackAddr.Bit_ST019_HasPlate)) != 0;
                        _m818CanPlace = (w & (1 << RackAddr.Bit_ST020_CanPlace)) != 0;
                        _m819PlaceDone = (w & (1 << RackAddr.Bit_ST020_PlaceDone)) != 0;
                        _m820CanPlace = (w & (1 << RackAddr.Bit_ST021_CanPlace)) != 0;
                        _m821PlaceDone = (w & (1 << RackAddr.Bit_ST021_PlaceDone)) != 0;
                        _m823CanPick = (w & (1 << RackAddr.Bit_ST020_CanPick)) != 0;
                        _m824PickDone = (w & (1 << RackAddr.Bit_ST020_PickDone)) != 0;
                        _m825CanPick = (w & (1 << RackAddr.Bit_ST021_CanPick)) != 0;
                        _m826PickDone = (w & (1 << RackAddr.Bit_ST021_PickDone)) != 0;
                        mc63SnapshotOk = true;
                    }
                    catch (Exception ex)
                    {
                        _m817 = false;
                        _m818CanPlace = _m819PlaceDone = _m820CanPlace = _m821PlaceDone = false;
                        _m823CanPick = _m824PickDone = _m825CanPick = _m826PickDone = false;
                        Console.WriteLine($"[平衡引擎] MC63读信号失败: {ex.Message} → 本轮禁止派发");
                    }
                }

                // ── ② 读MC65: FX按字读M区必须16点对齐。M700在M688字, M710在M704字, M720在M720字 ──
                if (_mc65?.IsConnected == true)
                {
                    try
                    {
                        mc65SnapshotOk = await TryReadMc65SnapshotAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _m700 = _m710 = _m720 = false;
                        Console.WriteLine($"[平衡引擎] MC65读信号失败: {ex.Message} → 本轮禁止派发");
                    }
                }

                _mc63SnapshotValid = mc63SnapshotOk;
                _mc65SnapshotValid = mc65SnapshotOk;

                // ── ③ 断线重连(每20轮, McConnectionCache内部去重防重复连接) ──
                if (_cycleCount % 20 == 1)
                {
                    // 机械手2 (ModbusTCP)
                    if (_m2 != null && !_m2.IsConnected)
                    {
                        try { await _m2.ConnectAsync(ct); Console.WriteLine("[平衡引擎] 机械手2 重连 ✓"); }
                        catch (Exception ex) { Console.WriteLine($"[平衡引擎] 机械手2 重连失败: {ex.Message}"); }
                    }
                    // 机械手3 (ModbusTCP)
                    if (_m3 != null && !_m3.IsConnected)
                    {
                        try { await _m3.ConnectAsync(ct); Console.WriteLine("[平衡引擎] 机械手3 重连 ✓"); }
                        catch (Exception ex) { Console.WriteLine($"[平衡引擎] 机械手3 重连失败: {ex.Message}"); }
                    }
                    // MC63 (三菱MC协议, 192.168.2.63:9000, M817/M818/M821)
                    if (_mc63 == null || !_mc63.IsConnected)
                    {
                        try
                        {
                            using var cts63 = new CancellationTokenSource(5000);
                            using var linked63 = CancellationTokenSource.CreateLinkedTokenSource(ct, cts63.Token);
                            _mc63 = await _mcCache.GetOrCreateAsync("192.168.2.63", 9000, linked63.Token);
                            Console.WriteLine("[平衡引擎] MC63 重连 ✓");
                        }
                        catch (Exception ex) { Console.WriteLine($"[平衡引擎] MC63 重连失败: {ex.Message}"); }
                    }
                    // MC65 (三菱MC协议, 192.168.2.65:9000, M710/M700/M720/D100)
                    if (_mc65 == null || !_mc65.IsConnected)
                    {
                        try
                        {
                            using var cts65 = new CancellationTokenSource(5000);
                            using var linked65 = CancellationTokenSource.CreateLinkedTokenSource(ct, cts65.Token);
                            _mc65 = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, linked65.Token);
                            Console.WriteLine("[平衡引擎] MC65 重连 ✓");
                        }
                        catch (Exception ex) { Console.WriteLine($"[平衡引擎] MC65 重连失败: {ex.Message}"); }
                    }
                }

                // ── ④ 每10轮打印信号快照(调试用) ──
                if (_cycleCount % 10 == 1)
                    Console.WriteLine(
                        $"[平衡引擎] L{_cycleCount} M817={V(_m817)} M818放={V(_m818CanPlace)} M823取={V(_m823CanPick)} M710={V(_m710)} M700={V(_m700)} M820放={V(_m820CanPlace)} M821放完={V(_m821PlaceDone)} M825取={V(_m825CanPick)} M720={V(_m720)} 缓存={CacheKeysText} M2忙={M2Busy} M3忙={M3Busy}");

                if (!mc63SnapshotOk || !mc65SnapshotOk)
                {
                    if (_cycleCount % 10 == 1)
                        Console.WriteLine($"[平衡引擎] 等PLC新鲜快照: MC63={(mc63SnapshotOk ? "OK" : "NG")} MC65={(mc65SnapshotOk ? "OK" : "NG")} → 跳过本轮M2/M3派发");
                    await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
                    continue;
                }

                
                bool m818Cached = HasBalWp("M818");
                bool m821Cached = HasBalWp("M821");
                bool m2HasSource = (_m823CanPick && m818Cached) || (_m817 && !m818Cached);
                bool m3HasSource = (_m825CanPick && m821Cached) || (_m700 && !m821Cached);

                if (_cycleCount % 10 == 1)
                {
                    if (_m823CanPick && !m818Cached)
                        Console.WriteLine("[平衡引擎] M2等: M823=1(ST020允许取料)但M818软件缓存不存在, 禁止盲取");
                    if (m818Cached && !_m823CanPick)
                        Console.WriteLine("[平衡引擎] M2等: M818缓存存在, 等M823=1允许取ST020");
                    if (_m825CanPick && !m821Cached)
                        Console.WriteLine("[平衡引擎] M3等: M825=1(ST021允许取料)但M821软件缓存不存在, 禁止盲取");
                    if (m821Cached && !_m825CanPick)
                        Console.WriteLine("[平衡引擎] M3等: M821缓存存在, 等M825=1允许取ST021");
                }

                // ── ⑤ 机械手2触发条件: M817有版或ST020(M823允许取+M818缓存存在) + 后天车已离开 + ST008(M710=1允许放版) ──
                if (!M2Busy && _m2?.IsConnected == true && m2HasSource)
                {
                    Console.WriteLine("[平衡引擎] 进入机械手2触发区域 ");
                    // 检查对应后天车是否已离开动平衡区域。
                    // M2优先取ST020: M817位置更远, 且去M817需要穿过ST020区域并多拿一把路径锁。
                    // 只要M818缓存存在就认为ST020有实体工件, 等M823允许后先清ST020, 不绕行取M817。
                    bool useM817 = !m818Cached && _m817;
                    int rearCraneNo = useM817 ? 2 : 4; // M817→1号线后天车(2号), M818→2号线后天车(4号)
                    if (!await IsCraneAtSafeXAsync(rearCraneNo, ct))
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[平衡引擎] M2等: 天车{rearCraneNo}号未离开动平衡区域");
                    }
                    
                    else if (!_m710) // ST008未给允许放版信号(M710=0) → 等
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine("[平衡引擎] M2等: M710=0(ST008未允许放版)");
                    }
                    else
                    {
                        // ⚠ 先原子占坑, 再启动M2Flow:
                        //   如果等拿到锁后才置忙, 主循环会在锁等待期间重复派发多个M2Flow。
                        // 继续使用上面已经确定的来源快照, fire-and-forget延迟执行时不再读可变字段。
                        if (TryMarkM2Busy())
                            _ = M2Flow(ct, useM817);
                    }
                }

                // ── ⑥ 机械手3触发条件: ST021(M825允许取+M821缓存存在)或M700有版 + ST010(M720=1允许放版) ──
                //    安全优先: M3从M700/ST009去M720/ST010的路径会经过M821/ST021区域。
                //    只要M821缓存存在, 就先等M825允许后清M821→M720; M821清空前禁止先取M700。
                if (!M3Busy && _m3?.IsConnected == true && m3HasSource)
                {
                    bool useM700 = !m821Cached;
                    // M821优先: 先把M3路径上的2号线短板位清空, 再允许M700来源穿越该区域。
                    if (!useM700 && !await IsCraneAtSafeXAsync(4, ct))
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine("[平衡引擎] M3等: 天车4号X>safeX未离开(M821需要后天车离开)");
                    }
                    else if (!_m720)
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine($"[平衡引擎] M3等: M720=0(ST010未允许放版, {(!useM700 ? "M821优先来源" : "M700来源")})");
                    }
                    //进行取料放料操作
                    else
                    {
                        // ⚠ 先原子占坑, 防止锁等待期间重复派发同一个M3任务。
                        if (TryMarkM3Busy())
                            //进行取料放料
                            _ = M3Flow(ct, useM700);
                    }
                }

                await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[平衡引擎] ERR: {ex.Message}");
                await Task.Delay(2000, ct);
            }
        }
    }

    private static string V(bool b) => b ? "1" : "0";

    private bool TryMarkM2Busy() => System.Threading.Interlocked.CompareExchange(ref _m2BusyFlag, 1, 0) == 0;
    private bool TryMarkM3Busy() => System.Threading.Interlocked.CompareExchange(ref _m3BusyFlag, 1, 0) == 0;
    private void ClearM2Busy() => System.Threading.Volatile.Write(ref _m2BusyFlag, 0);
    private void ClearM3Busy() => System.Threading.Volatile.Write(ref _m3BusyFlag, 0);

    /// <summary>检查指定天车是否已离开动平衡/研磨区域(X <= 配置安全阈值) safex设置的-2000</summary>
    private async Task<bool> IsCraneAtSafeXAsync(int craneNo, CancellationToken ct)
    {
        try
        {
            var crane = _craneCache.GetOrCreateService(craneNo);
            if (!crane.IsConnected) return false; // 连不上保守处理: 认为不安全
            var s = await crane.ReadStatusAsync(ct);
            //从配置文件中读取安全safex
            int safeX = _cfg.Balancing.RearCraneSafeX;
            bool safe = s != null && s.XPos <= safeX;
            if (!safe && _cycleCount % 10 == 1)
                Console.WriteLine($"[平衡引擎] 天车{craneNo}号 X={s?.XPos} > {safeX}(安全阈值), 等待离开...");
            return safe;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  机械手2流程 (M2Flow): 动平衡下料架→取料→ST008放料→Y回安全位
    //    触发: 主循环⑤ M817有版或ST020(M823允许取+M818缓存存在) + 后天车X≤safeX + M710(ST008)允许放版
    //    直径: 从_balWps缓存读(后天车DoUnload→OnBalancingRackPlaced写入), 取不到则暂停人工确认
    //    信号: 放料完成后写M711=1(MC65)通知PLC工件已送到
    //    ⚠ 缓存Remove移至放料成功后, 防止放料失败时缓存丢失
    // ═══════════════════════════════════════════════════════════════════
    /// <param name="useM817">主循环快照: true=M817(1号线动平衡下料架1), false=M818(2号线)</param>
    private async Task M2Flow(CancellationToken ct, bool useM817)
    {
        using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var actionCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int actionVersion = BeginM2Action(actionCts, actionCompletion);
        ct = actionCts.Token;
        SetM2EmergencySource(useM817 ? "M817" : "M818");

        // ── 获取位置锁:
        //   M817取料 → 飞ST008路径经过M818区域 → 需两把 _lockM817 + _lockM818
        //   M818取料 → 飞ST008路径不经过M817 → 只需 _lockM818
        bool gotM817 = false;
        bool gotM818 = false;
        bool mag = false;
        bool holdingWorkpiece = false; // X11确认吸住后才算工件真的在机械手2上
        bool placedOnM710 = false;
        bool m711Notified = false;
        void ReleaseM2RackLocks(string reason)
        {
            // M2可能同时拿M817+M818: M817是来源架, M818是M817→M710路径保护锁。
            // 释放时必须后拿先放, 并清got标志, 防止finally重复Release导致信号量计数被放大。
            bool released = false;
            if (gotM818)
            {
                if (ConsumeM2Lock("M818")) _lockM818?.Release();
                gotM818 = false;
                released = true;
            }
            if (gotM817)
            {
                if (ConsumeM2Lock("M817")) _lockM817?.Release();
                gotM817 = false;
                released = true;
            }
            if (released) Console.WriteLine($"[平衡引擎] [M2] {reason}: 已释放M817/M818来源区域锁");
        }

        try
        {
            if (useM817 && _lockM817 != null)//去M817取料
            {
                Console.WriteLine("[平衡引擎] [M2] 等待 M817锁..."); 
                await _lockM817.WaitAsync(ct);
                gotM817 = true;
                MarkM2Lock("M817", true);
            }
            if (_lockM818 != null)
            {
                Console.WriteLine("[平衡引擎] [M2] 等待 M818锁...");
                //上锁M818
                await _lockM818.WaitAsync(ct);
                gotM818 = true;
                MarkM2Lock("M818", true);
            }
            // 忙标志已由主循环原子占坑, 这里只负责动作和finally释放。

            if (!useM817)
                await ConfirmMc63HandshakeBitAsync(RackAddr.Bit_ST020_CanPick, "M823(ST020允许机械手2取料)", ct);

            // 优先级: ST020(M818缓存+M823允许取料) > M817(1号线), 两个同时可取先取更近的ST020。
            // 取M817需要经过ST020区域并持有M817+M818两把锁, 因此只在M818缓存不存在时取M817。
            string pickReg = useM817 ? "M817" : "M818";//知道哪个位置有版
            string pickCode = useM817 ? "ST019" : "ST020";//获得站号
            SetM2EmergencySource(pickReg);
            // 缓存由OnBalancingRackPlaced回调写入(MagnetOff之后), 可能晚于PLC信号
            // 重试等待缓存就绪, 最多等5s(10次×500ms)
            double d = 160.0;
            WorkpieceCache? trackedWp = null; // 只用于UI大阶段上报, 不参与动作判断
            for (int wait = 0; wait < 10; wait++)
            {
                if (TryGetBalWp(pickReg, out var cachedWp))
                {
                    d = cachedWp.Diameter;
                    trackedWp = cachedWp;
                    break;
                }
                if (wait == 0)
                    Console.WriteLine($"[平衡引擎#{EngineId}] [M2] 缓存{pickReg}未就绪,等待回调写入... 当前Keys=[{CacheKeysText}]");
                if (wait < 9) await Task.Delay(500, ct);
            }
            if (trackedWp == null)
            {
                // M817/M818都是后天车放料后回调写入缓存; PLC允许取料到了但缓存没到, 说明数据链断了。
                // 直径会影响机械手Z下降深度, 不能再用默认160mm盲取。
                _paused = true;
                throw new InvalidOperationException($"M2检测到{pickReg}=有板但工件缓存缺失, 当前Keys=[{CacheKeysText}], 已暂停, 禁止默认160mm取料");
            }
            var trackedWorkpiece = trackedWp.Value;
            Console.WriteLine($"[平衡引擎] [M2] ① 取料 {trackedWorkpiece.IdentityText} 工件直径d={d}");
            // 取料YZ: GetArmCoord自动判断配置→数据库
            //   pickReg = "M817"或"M818", pickCode = "ST019"或"ST020"
            //   坐标优先从配置文件 balancing.armCoords 取, 没有则从数据库machine表+机械手偏移
            var (pickY, pickZ) = GetArmCoord(pickReg, pickCode, _m2OffsetY, _m2OffsetZ);
            //计算公式计算下降距离
            int pzDown = Pz(pickZ, d), safeZ = _cfg.Grinding.SafeZHeight;
            // Console.WriteLine($"[平衡引擎] ");
            // ①.0 安全: 取料前检查磁铁是否已有工件(断电重启后X11不受PLC内存影响)
            if (await _m2!.ReadXBitAsync(63497, ct))
            {
                Console.WriteLine("[平衡引擎] ⚠⚠⚠ 机械手2磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[平衡引擎]   拒绝取料, 请人工确认机械手2状态后手动处理");
                throw new InvalidOperationException("机械手2 X11=1(磁铁已有工件), 拒绝取料防止碰撞");
            }
            Console.WriteLine($"[平衡引擎] [M2] ① 取料 {pickReg}({pickCode}) {trackedWorkpiece.IdentityText} Y={pickY} Z={pzDown}");
            // 先Y移到取料位(机械手只移YZ轴)
            await _m2.MoveAbsoluteAsync(-1, pickY, -1, ct: ct);
            try
            {
                //再Z下降取料
                await _m2.MoveAbsoluteAsync(-1, -1, pzDown, ct: ct);
            }
            catch (PressureStopException)
            {
                await _m2.RecoverFromPressureStopAsync(ct);
            }

            // X11检测: 充磁→等3s→查X11→没吸到就退磁→Z↓5mm→充磁→再查, 最多2次(与后天车一致)
            Console.WriteLine("[平衡引擎] [M2] 充磁→等3s→X11检测");
            int curZ = pzDown;
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("[平衡引擎] [M2] 充磁");
                    await _m2.MagnetOnAsync(ct);
                    mag = true;
                }
                else
                {
                    curZ += 5;
                    Console.WriteLine($"[平衡引擎] [M2] 退磁→Z↓到{curZ}→充磁");
                    await _m2.MagnetOffAsync(ct);
                    mag = false;
                    try
                    {
                        await _m2.MoveAbsoluteAsync(-1, -1, curZ, ct: ct);
                    }
                    catch (PressureStopException)
                    {
                        await _m2.RecoverFromPressureStopAsync(ct);
                    }

                    await _m2.MagnetOnAsync(ct);
                    mag = true;
                }

                Console.WriteLine("[平衡引擎] [M2] 等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                bool x11 = await _m2.ReadXBitAsync(63497, ct);
                Console.WriteLine($"[平衡引擎] [M2] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    Console.WriteLine($"[平衡引擎] [M2] ✓ X11=1 已吸到(保持在取料位Z={curZ})");
                    holdingWorkpiece = true;
                    break;
                }

                if (retry > 1) throw new Exception("M2取料失败: 2次充磁后X11仍=0");
                Console.WriteLine($"[平衡引擎] [M2] ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ── ② 放料: Z↑安全→XY→ST008→Z↓台面→退磁→Z↑安全 ──
            // (缓存不移除: 放料失败时工件还在磁铁上, 缓存需保留供人工确认)
            await _m2.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            if (!useM817)
                await WriteMc63HandshakeBitAsync(RackAddr.Bit_ST020_PickDone, "M824(ST020机械手2取料完成)", ct);
            // 目的位M710在主循环里已经预检过一次; 但从预检到机械手真正放料之间,
            // 现场PLC信号/人工状态可能变化。这里在抱板去ST008前再读一次真实M710,
            // 失败或有板都不继续放料, 走现有“工件在机械手上”暂停路径, 防止叠料。
            await ConfirmM710EmptyBeforePlaceAsync(ct);
            //获得yz距离
            var (destY, destZ) = GetArmCoord("M710", "ST008", _m2OffsetY, _m2OffsetZ);
            Console.WriteLine($"[平衡引擎] [M2] ② 放料 M710(ST008) Y={destY}");
            await _m2.MoveAbsoluteAsync(-1, destY, -1, ct: ct);
            int placeZ = Pz(destZ, d);
            Console.WriteLine($"[平衡引擎] [M2]   放料Z公式: {destZ} - Round(...) = {placeZ}");
            try
            {
                await _m2.MoveAbsoluteAsync(-1, -1, placeZ, ct: ct);
            }
            catch (PressureStopException)
            {
                await _m2.RecoverFromPressureStopAsync(ct);
            }

            //退磁
            await _m2.MagnetOffAsync(ct);
            mag = false;
            Console.WriteLine("[平衡引擎] [M2] 退磁 ✓");
            // 工件已退磁放到M710/ST008, M2后续只需Z升安全和Y回配置安全位。
            // 此时已不再占用M817/M818来源/路径区域, 可提前释放下料架位置锁, 让后天车/后续流程不被无谓阻塞。
            ReleaseM2RackLocks("M710退磁完成");
            //退磁完成回安全位置
            await _m2.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            placedOnM710 = true; // 工件已物理放到ST008, 后续M711失败必须暂停人工确认
            holdingWorkpiece = false;
            // ── 放料成功, 清理缓存 (工件已安全放到ST008, 不怕异常) ──
            lock (_balWpsLock)
            {
                _balWps.Remove(pickReg);
            }
            Console.WriteLine($"[平衡引擎#{EngineId}] [M2] 缓存已清理 {pickReg} Keys=[{CacheKeysText}]");

            // ── 写M711=1: 通知PLC工件已送到ST008动平衡料架1(M300→M711) ──
            if (_mc65?.IsConnected == true)
            {
                try
                {
                    await _mc65.WriteMBitInWordAsync(700, 11, true, ct);
                    m711Notified = true;
                    trackedWorkpiece.ReportStage("动平衡加工中 ST008/M710");
                    Console.WriteLine($"[平衡引擎] [M2] M711=1 送工件完成 ✓ {trackedWorkpiece.IdentityText}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[平衡引擎] [M2] M711写入失败 {trackedWorkpiece.IdentityText}: {ex.Message}");
                    throw new InvalidOperationException("M2已放到ST008/M710, 但M711写入失败, 需人工确认或补写M711", ex);
                }
            }
            else
            {
                throw new InvalidOperationException("M2已放到ST008/M710, 但MC65未连接, 无法写M711通知PLC");
            }

            // ── ③ 回安全位: Y→配置文件manipulator2SafeY ──
            int safeY = _cfg.SkewBed.Manipulator2SafeY;
            Console.WriteLine($"[平衡引擎] [M2] Y→安全位{safeY}");
            //回安全y轴
            await _m2.MoveAbsoluteAsync(-1, safeY, -1, ct: ct);
            Console.WriteLine("[平衡引擎] [M2] ═══ 完成 ═══");
        }
        catch (Exception ex)
        {
            // holdingWorkpiece=true: X11已确认工件在机械手上; 未放到目的位前必须暂停, 防止释放busy后继续派发。
            Console.WriteLine($"[平衡引擎] [M2] ✘ 异常: {ex.Message}");
            if (!IsM2ActionCurrent(actionVersion))
            {
                Console.WriteLine("[平衡引擎] [M2] 旧动作已被应急取消, 跳过暂停/状态写回");
            }
            else if (holdingWorkpiece && !placedOnM710)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M2已吸住工件但未放到ST008/M710, 引擎已暂停, 请人工确认机械手2/工件位置");
            }
            else if (mag && !placedOnM710)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M2磁铁处于打开/异常状态且未完成放料, 引擎已暂停, 请人工确认");
            }
            if (placedOnM710 && !m711Notified)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M2已把工件放到ST008/M710但M711未确认, 引擎已暂停, 请人工确认PLC信号");
            }
        }
        finally
        {
            // 异常兜底: 如果流程没走到M710退磁完成, 这里释放仍持有的来源/路径锁。
            // 正常路径已在退磁后清got标志, 这里不会重复Release。
            ReleaseM2RackLocks("finally兜底");
            if (IsM2ActionCurrent(actionVersion))
            {
                ClearM2Busy(); // 释放忙标志 → 主循环可再次触发M2Flow
                ClearM2EmergencyContext();
                Console.WriteLine("[平衡引擎] [M2] 释放忙标志");
            }
            else
            {
                Console.WriteLine("[平衡引擎] [M2] 旧动作finally: 不清新动作忙标志/上下文");
            }
            FinishM2Action(actionVersion, actionCompletion);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  机械手3流程 (M3Flow): 动平衡料架2/不需动平衡位→取料→ST010研磨上料架放料→Y回安全位
    //    触发: 主循环⑥ M700有版或ST021(M825允许取+M821缓存存在) + (M821需天车4号X≤safeX) + M720(ST010)允许放版
    //    直径: useM700=true → 读D100(MC65, 动平衡料架2直径); false → 读_balWps["M821"]缓存
    //    信号: M700来源取走后写M701=1; 放料后写M721=1(MC65)通知PLC放料完成; M720二次确认在取料前做
    //    ⚠ M821缓存Remove移至放料成功后, 防止放料失败时缓存丢失
    // ═══════════════════════════════════════════════════════════════════
    /// <param name="useM700">主循环快照: true=M700(动平衡后读D100), false=M821(后天车送来读缓存)</param>
    private async Task M3Flow(CancellationToken ct, bool useM700)
    {
        using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var actionCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int actionVersion = BeginM3Action(actionCts, actionCompletion);
        ct = actionCts.Token;
        SetM3EmergencySource(useM700 ? "M700" : "M821");

        // ── 获取位置锁: 无论M700还是M821来源, 飞ST010路径都经过M821区域
        //    必须两把都拿, 先M821后M720, 统一锁顺序无死锁
        bool gotM821 = false;
        bool gotM720 = false;
        bool mag = false;
        bool holdingWorkpiece = false; // X11确认吸住后才算工件真的在机械手3上
        bool placedOnM720 = false;
        bool m721Notified = false;
        bool grindingCacheNotified = false; // M720物理放料后, 研磨FIFO缓存必须写入成功
        try
        {
            if (_lockM821 != null)
            {
                Console.WriteLine("[平衡引擎] [M3] 等待 M821锁..."); 
                await _lockM821.WaitAsync(ct);
                gotM821 = true;
                MarkM3Lock("M821", true);
            }

            if (_lockM720 != null)
            {
                Console.WriteLine("[平衡引擎] [M3] 等待 M720锁..."); 
                await _lockM720.WaitAsync(ct);
                gotM720 = true;
                MarkM3Lock("M720", true);
            }
            // 忙标志已由主循环原子占坑, 锁等待期间不会重复派发M3Flow。

            string pickReg = useM700 ? "M700" : "M821";
            string pickCode = useM700 ? "ST009" : "ST021";
            SetM3EmergencySource(pickReg);
            if (!useM700)
                await ConfirmMc63HandshakeBitAsync(RackAddr.Bit_ST021_CanPick, "M825(ST021允许机械手3取料)", ct);
            // 直径来源:
            //   M700(动平衡料架2) → 动平衡后工件顺序乱了 → 读 D100(MC65) 取直径
            //   M821(2号线不需动平衡位) → 2号线后天车直接送来 → 读 _balWps 缓存
            double d;
            WorkpieceCache? m821TrackedWp = null;
            if (useM700)//T 表示 M700有板
            {
                d = 0;
                //连接上ip 2.65   动平衡料架2 工件有板
                if (_mc65?.IsConnected == true)
                {
                    try
                    {
                        //读取D100 工件直径
                        var d100 = await _mc65.ReadAsync(MitsubishiMcClient.DeviceD, 100, 1, ct);
                        if (d100.IntValues.Length == 0 || d100.IntValues[0] <= 0)
                            throw new InvalidOperationException("D100无有效直径");
                        d = d100.IntValues[0];
                        Console.WriteLine($"[平衡引擎] [M3] M700来源 版号=未知(M700人工动平衡后) → D100读取 工件直径={d}mm");
                    }
                    catch (Exception ex)
                    {
                        // M700是人工动平衡后的乱序工件, 只能相信D100; 读不到直径时不能默认160mm盲取。
                        _paused = true;
                        throw new InvalidOperationException($"M3检测到M700=有板但D100直径读取失败, 已暂停: {ex.Message}", ex);
                    }
                }
                else
                {
                    _paused = true;
                    throw new InvalidOperationException("M3检测到M700=有板但MC65未连接, D100不可读, 已暂停");
                }
            }
            // M821来源: 2号线后天车直接送来 → 从缓存读取(重试5s等回调写入,和M2Flow一致)
            else
            {
                d = 0;
                for (int wait = 0; wait < 10; wait++)
                {
                    if (TryGetBalWp("M821", out var cachedWp))
                    {
                        //获得直径 这个是从缓存拿的
                        d = cachedWp.Diameter; 
                        m821TrackedWp = cachedWp;
                        break;
                    }
                    if (wait == 0) Console.WriteLine("[平衡引擎] [M3] 缓存M821未就绪,等待回调写入...");
                    if (wait < 9) await Task.Delay(500, ct);
                }
                if (m821TrackedWp == null)
                {
                    // M821由2号线后天车放料回调写缓存; 缓存缺失时身份和直径都不可靠, 不能默认160mm。
                    _paused = true;
                    throw new InvalidOperationException("M3检测到M825允许取料但M821工件缓存缺失, 已暂停, 禁止默认160mm取料");
                }
                Console.WriteLine($"[平衡引擎] [M3] M821来源 {m821TrackedWp?.IdentityText ?? "版号=未知"} → 缓存读取 工件直径={d}mm");
            }

            string sourceIdentity = useM700 ? "版号=未知(M700人工动平衡后)" : (m821TrackedWp?.IdentityText ?? "版号=未知");
            
            // 取料YZ: GetArmCoord自动判断配置→ 一般不从数据库读取 从配置文件 数据库的不适用机械手
            var (pickY, pickZ) = GetArmCoord(pickReg, pickCode, _m3OffsetY, _m3OffsetZ);
            //判断下降距离
            int pzDown = Pz(pickZ, d), safeZ = _cfg.Grinding.SafeZHeight;

            // ①.0 安全: 取料前检查磁铁是否已有工件(断电重启后X11不受PLC内存影响)
            if (await _m3!.ReadXBitAsync(63497, ct))
            {
                Console.WriteLine("[平衡引擎] ⚠⚠⚠ 机械手3磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[平衡引擎]   拒绝取料, 请人工确认机械手3状态后手动处理");
                throw new InvalidOperationException("机械手3 X11=1(磁铁已有工件), 拒绝取料防止碰撞");
            }
            Console.WriteLine($"[平衡引擎] [M3] ① 取料 {pickReg}({pickCode}) {sourceIdentity} Y={pickY} Z={pzDown}");
            // 先Y移到取料位(机械手只移YZ轴)
            await _m3.MoveAbsoluteAsync(-1, pickY, -1, ct: ct);
            try
            {
                //再Z下降取料
                await _m3.MoveAbsoluteAsync(-1, -1, pzDown, ct: ct);
            }
            catch (PressureStopException)
            {
                await _m3.RecoverFromPressureStopAsync(ct);
            }

            // X11检测: 充磁→等3s→查X11→没吸到就退磁→Z↓5mm→充磁→再查, 最多2次(与后天车一致)
            Console.WriteLine("[平衡引擎] [M3] 充磁→等3s→X11检测");
            int curZ = pzDown;
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("[平衡引擎] [M3] 充磁");
                    await _m3.MagnetOnAsync(ct);
                    mag = true;
                }
                else
                {
                    curZ += 5;
                    Console.WriteLine($"[平衡引擎] [M3] 退磁→Z↓到{curZ}→充磁");
                    await _m3.MagnetOffAsync(ct);
                    mag = false;
                    try
                    {
                        await _m3.MoveAbsoluteAsync(-1, -1, curZ, ct: ct);
                    }
                    catch (PressureStopException)
                    {
                        await _m3.RecoverFromPressureStopAsync(ct);
                    }

                    await _m3.MagnetOnAsync(ct);
                    mag = true;
                }

                Console.WriteLine("[平衡引擎] [M3] 等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                bool x11 = await _m3.ReadXBitAsync(63497, ct);
                Console.WriteLine($"[平衡引擎] [M3] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    Console.WriteLine($"[平衡引擎] [M3] ✓ X11=1 已吸到(保持在取料位Z={curZ})");
                    holdingWorkpiece = true;
                    break;
                }

                if (retry > 1) throw new Exception("M3取料失败: 2次充磁后X11仍=0");
                Console.WriteLine($"[平衡引擎] [M3] ⚠ X11=0 未吸到, 准备下探5mm重试");
            }
            //z移动安全位置
            await _m3.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            if (!useM700)
                await WriteMc63HandshakeBitAsync(RackAddr.Bit_ST021_PickDone, "M826(ST021机械手3取料完成)", ct);

            // ── M701=1: 仅M700来源需要通知PLC“动平衡料架2已被天车取走” ──
            // 必须等X11确认吸住且Z已升到安全高度后再写, 否则PLC可能清M700而工件实际仍在ST009。
            // M821来源是2号线短板中转位, 不属于动平衡上料架2, 不能写M701。
            if (useM700)
            {
                if (_mc65?.IsConnected == true)
                {
                    try
                    {
                        await _mc65.WriteMBitInWordAsync(700, 1, true, ct);
                        Console.WriteLine($"[平衡引擎] [M3] M701=1 通知PLC M700取料完成 {sourceIdentity}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[平衡引擎] [M3] M701写入失败 {sourceIdentity}: {ex.Message}");
                        throw new InvalidOperationException("M3已从M700/ST009吸起工件并Z升安全, 但M701写入失败, 需人工确认或补写M701", ex);
                    }
                }
                else
                {
                    throw new InvalidOperationException("M3已从M700/ST009吸起工件并Z升安全, 但MC65未连接, 无法写M701通知PLC");
                }
            }

            // ── ② 放料: XY→ST010→Z↓台面→退磁→Z↑安全(持锁中) ──
            // M720在主循环里已经预检为空, 且M3已持有M720位置锁; 这里仍然动作前二次读PLC。
            // 锁只能防软件内部互斥, 不能防现场信号变化或人工干预, 所以M720有板/读失败都禁止继续放料。
            await ConfirmM720EmptyBeforePlaceAsync(ct);
            var (destY, destZ) = GetArmCoord("M720", "ST010", _m3OffsetY, _m3OffsetZ);
            Console.WriteLine($"[平衡引擎] [M3] ② 放料 ST010 Y={destY}");
            //移动y
            await _m3.MoveAbsoluteAsync(-1, destY, -1, ct: ct);
            int placeZ = Pz(destZ, d);
            Console.WriteLine($"[平衡引擎] [M3]   放料Z公式: {destZ} - Round(...) = {placeZ}");
            try
            {
                //移动z
                await _m3.MoveAbsoluteAsync(-1, -1, placeZ, ct: ct);
            }
            catch (PressureStopException)
            {
                await _m3.RecoverFromPressureStopAsync(ct);
            }
            
            await _m3.MagnetOffAsync(ct);
            mag = false;
            Console.WriteLine("[平衡引擎] [M3] 退磁 ✓");
            await _m3.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            placedOnM720 = true; // 工件已物理放到研磨上料位并且Z已离开, 后续信号失败需人工补确认
            holdingWorkpiece = false;

            // ── 写M721=1: 通知PLC放料到研磨上料架1号位完成 → 传送带启动 ──
            if (_mc65?.IsConnected == true)
            {
                try
                {
                    await _mc65.WriteMBitInWordAsync(720, 1, true, ct); 
                    m721Notified = true;
                    Console.WriteLine($"[平衡引擎] [M3] M721=1 通知PLC放料完成 {sourceIdentity}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[平衡引擎] [M3] M721写入失败 {sourceIdentity}: {ex.Message}");
                    throw new InvalidOperationException("M3已放到M720/ST010, 但M721写入失败, 需人工确认或补写M721", ex);
                }
            }
            else
            {
                throw new InvalidOperationException("M3已放到M720/ST010, 但MC65未连接, 无法写M721通知PLC");
            }

            // ── 通知研磨引擎入FIFO缓存 (M700来源: 只有直径; M821来源: 缓存里保留版号/序号/长度) ──
            WorkpieceCache grindingWp;
            if (useM700)
            {
                grindingWp = new WorkpieceCache { Diameter = d, BoreType = 1, Length = 0 };
            }
            else if (m821TrackedWp.HasValue)
            {
                grindingWp = m821TrackedWp.Value;
            }
            else if (!TryGetBalWp("M821", out grindingWp))
            {
                grindingWp = new WorkpieceCache { Diameter = d, BoreType = 1, Length = 0 };
            }
            grindingWp.Diameter = d; // d是直径, M700从D100读, M821从缓存读
            grindingWp.BoreType = 1; // bore固定1, 研磨机不需要版孔区分
            // 工件已物理放到M720且M721已通知PLC, 研磨FIFO缓存是后续取料生命线, 不能静默跳过。
            if (OnGrindingRackPlaced == null)
                throw new InvalidOperationException("M3已放到M720/ST010, 但研磨缓存回调未绑定");
            OnGrindingRackPlaced.Invoke(grindingWp);
            grindingCacheNotified = true;
            grindingWp.ReportStage("研磨上料架 ST010/M720");
            Console.WriteLine($"[平衡引擎] [M3] 通知研磨引擎入缓存 {grindingWp.IdentityText} d={grindingWp.Diameter} L={grindingWp.Length}");

            // ── 放料成功, 清理M821来源的缓存 (工件已安全放到ST010) ──
            if (!useM700) // M821来源走缓存, M820来源走D200无需清
            {
                lock (_balWpsLock)
                {
                    _balWps.Remove("M821");
                }
                Console.WriteLine($"[平衡引擎#{EngineId}] [M3] 缓存已清理 M821 Keys=[{CacheKeysText}]");
            }

            // ── ③ 回安全位: Y→配置文件manipulator3SafeY ──
            int safeY = _cfg.SkewBed.Manipulator3SafeY;
            Console.WriteLine($"[平衡引擎] [M3] Y→安全位{safeY}");
            await _m3.MoveAbsoluteAsync(-1, safeY, -1, ct: ct);
            Console.WriteLine("[平衡引擎] [M3] ═══ 完成 ═══");
        }
        catch (Exception ex)
        {
            // holdingWorkpiece=true: X11已确认工件在机械手上; 未放到目的位前必须暂停, 防止释放busy后继续派发。
            Console.WriteLine($"[平衡引擎] [M3] ✘ 异常: {ex.Message}");
            if (!IsM3ActionCurrent(actionVersion))
            {
                Console.WriteLine("[平衡引擎] [M3] 旧动作已被应急取消, 跳过暂停/状态写回");
            }
            else if (holdingWorkpiece && !placedOnM720)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3已吸住工件但未放到M720/ST010, 引擎已暂停, 请人工确认机械手3/工件位置");
            }
            else if (mag && !placedOnM720)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3磁铁处于打开/异常状态且未完成放料, 引擎已暂停, 请人工确认");
            }
            if (placedOnM720 && !m721Notified)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3已把工件放到M720/ST010但M721未确认, 引擎已暂停, 请人工确认PLC信号/研磨缓存");
            }
            if (placedOnM720 && m721Notified && !grindingCacheNotified)
            {
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3已把工件放到M720/ST010且M721已通知, 但研磨缓存未写入, 引擎已暂停, 请人工确认/补录缓存");
            }
        }
        finally
        {
            //释放两把锁
            // 注意: 实际释放按后拿先放, 先释放M720/ST010锁, 再释放M821锁。
            if (gotM720 && ConsumeM3Lock("M720")) _lockM720?.Release();  // 先释放M720/ST010锁(后拿的先放)
            if (gotM821 && ConsumeM3Lock("M821")) _lockM821?.Release();  // 再释放M821锁(先拿的后放)
            if (IsM3ActionCurrent(actionVersion))
            {
                ClearM3Busy();
                ClearM3EmergencyContext();
                Console.WriteLine($"[平衡引擎] [M3] 释放忙标志 + 位置锁");
            }
            else
            {
                Console.WriteLine("[平衡引擎] [M3] 旧动作finally: 不清新动作忙标志/上下文");
            }
            FinishM3Action(actionVersion, actionCompletion);
        }
    }

    /// <summary>Z下降公式: 台面Z - Round[(d/2/zFactor1)+(d/2/zFactor2)] — 机械手取料/放料用</summary>
    private int Pz(int z, double d) =>
        z - (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));

    /// <summary>
    /// M2放ST008前二次确认M710允许放版。
    /// 主循环快照只用于派发预检; 真正放料前必须重新读MC65, 防止快照后现场状态变化导致叠料。
    /// </summary>
    private async Task ConfirmM710EmptyBeforePlaceAsync(CancellationToken ct)
    {
        if (_mc65?.IsConnected != true)
            throw new InvalidOperationException("M710二次确认失败: MC65未连接");

        var r = await ReadMc65MAlignedWordAsync(704, "M710二次确认", ct);
        if (r.IntValues.Length == 0)
            throw new InvalidOperationException("M710二次确认失败: MC65返回字数不足");

        bool canPlace = (r.IntValues[0] & (1 << 6)) != 0; // M710 = M704 bit6, 1=允许放版
        Console.WriteLine($"[平衡引擎] [M2] 二次确认M710={(canPlace ? "可放料" : "不可放料")} rawM704=0x{r.IntValues[0]:X4}");
        if (!canPlace)
            throw new InvalidOperationException("M710二次确认=不可放料, 禁止M2放料");
    }

    /// <summary>
    /// M3放ST010/M720前二次确认M720允许放版。
    /// 与下料预检一致直接读M720所在字; 读失败或未允许放版都按危险处理, 由外层暂停/人工确认。
    /// </summary>
    private async Task ConfirmM720EmptyBeforePlaceAsync(CancellationToken ct)
    {
        if (_mc65?.IsConnected != true)
            throw new InvalidOperationException("M720二次确认失败: MC65未连接");

        var r = await ReadMc65MAlignedWordAsync(720, "M720二次确认", ct);
        if (r.IntValues.Length < 1)
            throw new InvalidOperationException("M720二次确认失败: MC65返回字数不足");

        bool canPlace = (r.IntValues[0] & 1) != 0; // M720 bit0, 1=允许放版
        Console.WriteLine($"[平衡引擎] [M3] 二次确认M720={(canPlace ? "可放料" : "不可放料")} rawM720=0x{r.IntValues[0]:X4}");
        if (!canPlace)
            throw new InvalidOperationException("M720二次确认=不可放料, 禁止M3放料");
    }

    private async Task ConfirmMc63HandshakeBitAsync(int bitOffset, string signalName, CancellationToken ct)
    {
        if (_mc63?.IsConnected != true)
            throw new InvalidOperationException($"{signalName}二次确认失败: MC63未连接");

        var r = await ReadMc63M816WordAsync($"{signalName}二次确认", ct);
        if (r.IntValues.Length < 2)
            throw new InvalidOperationException($"{signalName}二次确认失败: MC63返回字数不足");

        int m816 = r.IntValues[1];
        bool allowed = (m816 & (1 << bitOffset)) != 0;
        Console.WriteLine($"[平衡引擎] 二次确认{signalName}={(allowed ? "1" : "0")} rawM816=0x{m816:X4}");
        if (!allowed)
            throw new InvalidOperationException($"{signalName}二次确认=0, 禁止取料");
    }

    private async Task WriteMc63HandshakeBitAsync(int bitOffset, string signalName, CancellationToken ct)
    {
        if (_mc63?.IsConnected != true)
            throw new InvalidOperationException($"{signalName}写入失败: MC63未连接");

        await _mc63.WriteMBitInWordAsync(RackAddr.M_RearHandshakeWordStart, bitOffset, true, ct);
        Console.WriteLine($"[平衡引擎] {signalName}=1 ✓");
    }

    private async Task<ReadResult> ReadMc63M816WordAsync(string label, CancellationToken ct)
    {
        try
        {
            return await _mc63!.ReadMAlignedWordAsync(800, 2, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"MC63字读{label}(M816~M831)失败: {ex.Message}", ex);
        }
    }

    private async Task<ReadResult> ReadMc65MAlignedWordAsync(int alignedAddress, string label, CancellationToken ct, int count = 1)
    {
        try
        {
            return await _mc65!.ReadMAlignedWordAsync(alignedAddress, count, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"MC65字读{label}(M{alignedAddress}~M{alignedAddress + count * 16 - 1})失败: {ex.Message}", ex);
        }
    }

    private async Task<bool> TryReadMc65SnapshotAsync(CancellationToken ct)
    {
        try
        {
            var r = await ReadMc65MAlignedWordAsync(688, "M700/M710/M720批量快照", ct, count: 3);
            if (r.IntValues.Length < 3)
                throw new InvalidOperationException($"MC65批量快照返回字数不足: {r.IntValues.Length}/3");

            ApplyMc65Snapshot(r.IntValues[0], r.IntValues[1], r.IntValues[2]);
            return true;
        }
        catch (Exception batchEx)
        {
            Console.WriteLine($"[平衡引擎] MC65批量读失败, 尝试分段读取: {batchEx.Message}");

            var r688 = await ReadMc65MAlignedWordAsync(688, "M700", ct);
            var r704 = await ReadMc65MAlignedWordAsync(704, "M710", ct);
            var r720 = await ReadMc65MAlignedWordAsync(720, "M720", ct);
            if (r688.IntValues.Length < 1 || r704.IntValues.Length < 1 || r720.IntValues.Length < 1)
                throw new InvalidOperationException("MC65分段快照返回字数不足");

            ApplyMc65Snapshot(r688.IntValues[0], r704.IntValues[0], r720.IntValues[0]);
            Console.WriteLine("[平衡引擎] MC65分段读取恢复 ✓");
            return true;
        }
    }

    private void ApplyMc65Snapshot(int word688, int word704, int word720)
    {
        _m700 = (word688 & (1 << 12)) != 0; // M700 = M688 bit12
        _m710 = (word704 & (1 << 6)) != 0;  // M710 = M704 bit6
        _m720 = (word720 & 1) != 0;         // M720 = M720 bit0
    }

    /// <summary>从数据库取工位XYZ坐标(含数据库偏移量)</summary>
    private bool TryCoords(string code, out int x, out int y, out int z)
    {
        x = y = z = 0;
        if (!_stationCoords.TryGetValue(code, out var r)) return false;
        x = (int)(r.X + r.XOffset);
        y = (int)(r.Y + r.YOffset);
        z = (int)(r.Z + r.ZOffset);
        return true;
    }

    /// <summary>取机械手YZ坐标: 配置文件非0→用配置; 否则→数据库坐标+机械手偏移</summary>
    /// <param name="signalCode">M817/M818/M710/M700/M821/M720</param>
    /// <param name="dbStation">数据库站号(ST019/ST020/ST008/ST009/ST021/ST010)</param>
    /// <param name="armOffsetY">机械手Y偏移(_m2OffsetY或_m3OffsetY)</param>
    /// <param name="armOffsetZ">机械手Z偏移(_m2OffsetZ或_m3OffsetZ)</param>
    private (int y, int z) GetArmCoord(string signalCode, string dbStation, int armOffsetY, int armOffsetZ)
    {
        if (_cfg.Balancing.ArmCoords.TryGetValue(signalCode, out var cfg) && cfg.Y != 0)
            return (cfg.Y, cfg.Z); // 配置文件有值→直接用(不叠加数据库和机械手偏移)

        // 配置文件Y=0→从数据库取+叠加机械手偏移
        if (TryCoords(dbStation, out _, out int dbY, out int dbZ))
            return (dbY + armOffsetY, dbZ + armOffsetZ);

        throw new Exception($"缺少{signalCode}坐标(数据库{dbStation}未找到且配置文件未设置)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engineCts.Cancel();
        _engineCts.Dispose();
        Console.WriteLine("[平衡引擎] 已释放");
    }
}
