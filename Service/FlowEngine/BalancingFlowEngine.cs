using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Communication.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Logging;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service.OperationalEvents;
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
// ///   M817有板且M817软件缓存存在，或ST020满足M823允许取料且M818软件缓存存在
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
    private readonly IOperationalEventReporter _exceptionReporter;
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

    /// <summary>
    /// 已从来源缓存确认转移到机械手的工件。它是业务状态，不是页面展示：
    /// 提前释放来源位置锁后，后天车可写入同名新缓存，旧动作只能继续处理自己的在途记录。
    /// </summary>
    private sealed record ManipulatorInFlightWorkpiece(
        WorkpieceCache Workpiece, string Source, string Target, int ActionVersion, string Ownership);

    private ManipulatorInFlightWorkpiece? _m2InFlightWorkpiece, _m3InFlightWorkpiece;

    // ── 页面展示上下文（严格旁路）────────────────────────────────────────
    // 这些字段只补足M2/M3从_balWps移除后“动作局部变量外不可见”的身份缺口。
    // 禁止在派发、运动、握手、锁、暂停或应急判断中读取；只能由下方Set/Clear和只读快照访问。
    private WorkpieceCache? _m2DisplayWorkpiece, _m3DisplayWorkpiece;
    private string _m2DisplayStage = string.Empty, _m3DisplayStage = string.Empty;
    private int _m2DisplayVersion, _m3DisplayVersion;

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

    /// <summary>
    /// X11=1且Z安全到位后，将来源缓存原子转为本趟机械手在途工件。
    /// 固定锁顺序为 emergency → balWps；中间禁止 await，避免旧动作提前放锁后误删新缓存。
    /// </summary>
    private void TransferBalancingCacheToInFlight(bool isM2, int actionVersion,
        string source, string target, WorkpieceCache expected)
    {
        lock (_emergencyLock)
        {
            int currentVersion = isM2 ? _m2ActionVersion : _m3ActionVersion;
            if (currentVersion != actionVersion)
                throw new InvalidOperationException($"{(isM2 ? "M2" : "M3")}动作版本已变更，禁止转移{source}来源缓存");

            lock (_balWpsLock)
            {
                if (!_balWps.TryGetValue(source, out WorkpieceCache cached))
                    throw new InvalidOperationException($"{(isM2 ? "M2" : "M3")}确认持件后未找到{source}来源缓存，禁止提前释放位置锁");
                if (!SameWorkpiece(cached, expected))
                    throw new InvalidOperationException($"{(isM2 ? "M2" : "M3")}确认持件后{source}来源缓存身份已变化，禁止转移");
                _balWps.Remove(source);
            }

            var inFlight = new ManipulatorInFlightWorkpiece(expected, source, target, actionVersion, "机械手持件");
            if (isM2) _m2InFlightWorkpiece = inFlight;
            else _m3InFlightWorkpiece = inFlight;
        }

        Console.WriteLine($"[平衡引擎#{EngineId}] [{(isM2 ? "M2" : "M3")}] 来源缓存已转在途: {source} → {target} {expected.IdentityText}");
    }

    private static bool SameWorkpiece(WorkpieceCache left, WorkpieceCache right) =>
        string.Equals(left.PlateNo, right.PlateNo, StringComparison.Ordinal) &&
        string.Equals(left.Sequence, right.Sequence, StringComparison.Ordinal) &&
        left.Diameter.Equals(right.Diameter) && left.Length.Equals(right.Length) && left.BoreType == right.BoreType;

    /// <summary>M700没有来源缓存；在同一取走确认点直接建立M3在途工件。</summary>
    private void CreateInFlightWorkpiece(bool isM2, int actionVersion,
        string source, string target, WorkpieceCache workpiece)
    {
        lock (_emergencyLock)
        {
            int currentVersion = isM2 ? _m2ActionVersion : _m3ActionVersion;
            if (currentVersion != actionVersion)
                throw new InvalidOperationException($"{(isM2 ? "M2" : "M3")}动作版本已变更，禁止建立在途工件");

            var inFlight = new ManipulatorInFlightWorkpiece(workpiece, source, target, actionVersion, "机械手持件");
            if (isM2) _m2InFlightWorkpiece = inFlight;
            else _m3InFlightWorkpiece = inFlight;
        }
    }

    private void SetInFlightOwnership(bool isM2, int actionVersion, string ownership)
    {
        lock (_emergencyLock)
        {
            var current = isM2 ? _m2InFlightWorkpiece : _m3InFlightWorkpiece;
            if (current?.ActionVersion != actionVersion) return;
            current = current with { Ownership = ownership };
            if (isM2) _m2InFlightWorkpiece = current;
            else _m3InFlightWorkpiece = current;
        }
    }

    private WorkpieceCache? GetInFlightWorkpiece(bool isM2)
    {
        lock (_emergencyLock)
            return (isM2 ? _m2InFlightWorkpiece : _m3InFlightWorkpiece)?.Workpiece;
    }

    private bool IsSourceCacheTransferredToInFlight(bool isM2, string source)
    {
        lock (_emergencyLock)
        {
            var current = isM2 ? _m2InFlightWorkpiece : _m3InFlightWorkpiece;
            return current != null && string.Equals(current.Source, source, StringComparison.Ordinal);
        }
    }

    private void ClearInFlightWorkpiece(bool isM2, int actionVersion)
    {
        lock (_emergencyLock)
        {
            if (isM2)
            {
                if (_m2InFlightWorkpiece?.ActionVersion == actionVersion) _m2InFlightWorkpiece = null;
            }
            else if (_m3InFlightWorkpiece?.ActionVersion == actionVersion)
            {
                _m3InFlightWorkpiece = null;
            }
        }
    }

    public bool IsRunning => _engineTask != null && !_engineTask.IsCompleted;
    public bool IsPaused => _paused;
    /// <summary>动平衡流程进入人工确认暂停时通知主页面。</summary>
    public Action<string>? OnSafetyAlarm;
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

    /// <summary>
    /// 动平衡缓存及M2/M3当前动作的只读工件快照。
    /// 两把锁分别短暂复制且绝不嵌套；不读取PLC、不改变缓存、不参与任何控制判断。
    /// </summary>
    public InProcessWorkpieceSnapshot[] GetInProcessWorkpieceSnapshots()
    {
        var nowUtc = DateTime.UtcNow;
        KeyValuePair<string, WorkpieceCache>[] cached;
        lock (_balWpsLock) cached = _balWps.ToArray();

        WorkpieceCache? m2Workpiece;
        WorkpieceCache? m3Workpiece;
        string m2Stage;
        string m3Stage;
        bool m2IsInFlight;
        bool m3IsInFlight;
        lock (_emergencyLock)
        {
            m2Workpiece = _m2InFlightWorkpiece?.Workpiece ?? _m2DisplayWorkpiece;
            m3Workpiece = _m3InFlightWorkpiece?.Workpiece ?? _m3DisplayWorkpiece;
            m2Stage = _m2InFlightWorkpiece?.Ownership ?? _m2DisplayStage;
            m3Stage = _m3InFlightWorkpiece?.Ownership ?? _m3DisplayStage;
            m2IsInFlight = _m2InFlightWorkpiece != null;
            m3IsInFlight = _m3InFlightWorkpiece != null;
        }

        var result = new List<InProcessWorkpieceSnapshot>(cached.Length + 2);
        foreach (var pair in cached)
        {
            result.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.BalancingCache,
                "动平衡", "动平衡", pair.Key, "等待机械手2/3处理",
                WorkpieceDisplaySnapshot.From(pair.Value), "动平衡软件缓存",
                InProcessWorkpieceStatus.Normal, "仅为软件缓存身份，不额外读取PLC", nowUtc));
        }

        if (m2Workpiece.HasValue)
        {
            result.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.BalancingInTransit,
                "动平衡", "动平衡", "机械手2", TextOrNone(m2Stage),
                WorkpieceDisplaySnapshot.From(m2Workpiece.Value), m2IsInFlight ? "M2在途业务记录" : "M2动作展示上下文",
                _paused ? InProcessWorkpieceStatus.ManualConfirmation : InProcessWorkpieceStatus.Normal,
                m2IsInFlight ? "来源缓存已转为机械手2在途工件" : "只随当前动作记录身份", nowUtc));
        }

        if (m3Workpiece.HasValue)
        {
            result.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.BalancingInTransit,
                "动平衡", "动平衡", "机械手3", TextOrNone(m3Stage),
                WorkpieceDisplaySnapshot.From(m3Workpiece.Value), m3IsInFlight ? "M3在途业务记录" : "M3动作展示上下文",
                _paused ? InProcessWorkpieceStatus.ManualConfirmation : InProcessWorkpieceStatus.Normal,
                m3IsInFlight ? "来源缓存已转为机械手3在途工件；M700可能只有直径、没有版号" : "M700来源可能只有直径、没有版号", nowUtc));
        }

        return result.ToArray();
    }

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

    /// <summary>
    /// 机械手2/3暂停动作的人工账本结案。不得调用原“清位置应急”代替本方法：
    /// 原应急会删除缓存，而这里必须按已确认的物理位置保留来源、在途或目标待交接事实。
    /// 不发送机械手/PLC命令，不释放由其它正常流程持有的锁，不自动恢复动平衡引擎。
    /// </summary>
    public string ResolveManualAction(string operationId, FlowActionManualResolution resolution)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot) ||
            (snapshot.FlowScope != "动平衡机械手2取料送ST008" && snapshot.FlowScope != "动平衡机械手3取料送ST010"))
            return $"动平衡动作账本不存在或不是待结案的机械手2/3动作: {operationId}";

        bool isM2 = snapshot.FlowScope.StartsWith("动平衡机械手2", StringComparison.Ordinal);
        string source = snapshot.Source;
        WorkpieceCache? workpiece;
        bool sourceCacheTransferred;
        int inFlightActionVersion;
        lock (_emergencyLock)
        {
            var inFlight = isM2 ? _m2InFlightWorkpiece : _m3InFlightWorkpiece;
            workpiece = inFlight?.Workpiece ?? (isM2 ? _m2DisplayWorkpiece : _m3DisplayWorkpiece);
            sourceCacheTransferred = inFlight != null && string.Equals(inFlight.Source, source, StringComparison.Ordinal);
            inFlightActionVersion = inFlight?.ActionVersion ?? 0;
        }
        if (!workpiece.HasValue && source != "M700")
            return $"动作={operationId}缺少保留的工件身份，不能凭快照重建{source}缓存；请先人工补录后再结案。";

        _paused = true;
        string result;
        switch (resolution)
        {
            case FlowActionManualResolution.StillAtSource:
                // M817/M818/M821都有完整软件缓存；M700是人工动平衡后的物理来源，
                // 没有可靠版号时只保留占用/展示，绝不能伪造新的缓存身份。
                if (source != "M700" && workpiece.HasValue)
                {
                    lock (_balWpsLock)
                    {
                        if (sourceCacheTransferred && _balWps.ContainsKey(source))
                            return $"动作={operationId}确认仍在{source}失败：该位置已存在后天车新写入的缓存，禁止用旧动作工件覆盖。";
                        _balWps[source] = workpiece.Value;
                    }
                }
                if (isM2) SetM2EmergencySource(source); else SetM3EmergencySource(source);
                result = source == "M700"
                    ? "确认仍在M700：保留M3来源占用和展示身份；M700没有可靠版号缓存，未伪造缓存"
                    : $"确认仍在{source}：来源缓存已保留/恢复；待用户恢复引擎后按一次全新任务重新二次确认";
                break;
            case FlowActionManualResolution.OnCarrier:
                if (isM2) SetM2EmergencySource(source); else SetM3EmergencySource(source);
                result = $"确认工件在机械手{(isM2 ? "2" : "3")}上：保留来源关联、展示任务牌和忙标志，禁止自动派发";
                break;
            case FlowActionManualResolution.AtTargetPendingHandoff:
                if (isM2) SetM2EmergencySource(source); else SetM3EmergencySource(source);
                result = $"确认工件已在目标{snapshot.Target}待交接：保留来源关联和展示任务牌，不伪造M711/M721或研磨缓存完成";
                break;
            case FlowActionManualResolution.RemovedManually:
                if (source != "M700" && !sourceCacheTransferred)
                {
                    lock (_balWpsLock) _balWps.Remove(source);
                }
                if (isM2)
                {
                    ClearM2Busy();
                    ClearM2EmergencyContext();
                    ClearM2Display(_m2DisplayVersion);
                    if (inFlightActionVersion != 0) ClearInFlightWorkpiece(true, inFlightActionVersion);
                }
                else
                {
                    ClearM3Busy();
                    ClearM3EmergencyContext();
                    ClearM3Display(_m3DisplayVersion);
                    if (inFlightActionVersion != 0) ClearInFlightWorkpiece(false, inFlightActionVersion);
                }
                result = sourceCacheTransferred
                    ? $"确认工件已人工移走：旧动作在途工件/忙标志/展示任务牌已清；保留{source}可能存在的新缓存，未写PLC信号"
                    : $"确认工件已人工移走：{source}来源缓存/忙标志/展示任务牌已清；未写PLC信号";
                break;
            default:
                return $"不支持的人工结论: {resolution}";
        }

        // 只有“机械手持件/目标待交接”仍代表未完成的物理搬运，必须继续占用忙标志。
        // “仍在来源位”已经把工件重新归属到来源缓存；引擎本身仍保持暂停，用户随后
        // 点击恢复时必须能按一趟全新的取料动作重新读取PLC、缓存和安全条件，不能因
        // Busy=1 永久跳过来源而形成无报警的软死锁。
        if (resolution == FlowActionManualResolution.OnCarrier ||
            resolution == FlowActionManualResolution.AtTargetPendingHandoff)
        {
            if (isM2) Interlocked.Exchange(ref _m2BusyFlag, 1);
            else Interlocked.Exchange(ref _m3BusyFlag, 1);
        }
        else if (resolution == FlowActionManualResolution.StillAtSource)
        {
            if (isM2)
            {
                ClearM2Busy();
                ClearM2EmergencyContext();
                ClearM2Display(_m2DisplayVersion);
                if (inFlightActionVersion != 0) ClearInFlightWorkpiece(true, inFlightActionVersion);
            }
            else
            {
                ClearM3Busy();
                ClearM3EmergencyContext();
                ClearM3Display(_m3DisplayVersion);
                if (inFlightActionVersion != 0) ClearInFlightWorkpiece(false, inFlightActionVersion);
            }
        }
        FlowActionManualRegistry.Remove(operationId);
        string message = $"[平衡引擎#{EngineId}] [人工账本结案] 动作={operationId}; 工件={workpiece?.IdentityText ?? snapshot.WorkpieceIdentity}; {result}; 动平衡引擎保持暂停。";
        Console.WriteLine(message);
        return message;
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
        bool m2SourceCacheTransferred = false;
        bool m3SourceCacheTransferred = false;
        int m2InFlightVersionToClear = 0;
        int m3InFlightVersionToClear = 0;
        int m2DisplayVersionToClear = 0;
        int m3DisplayVersionToClear = 0;

        lock (_emergencyLock)
        {
            // 只记住当前展示版本。应急超时会在下方提前返回并保留证据；
            // 只有软件清理真正完成后才按版本清理对应Tooltip/在制显示。
            if (position is "M817" or "M818" or "M710") m2DisplayVersionToClear = _m2DisplayVersion;
            if (position is "M700" or "M821" or "M720") m3DisplayVersionToClear = _m3DisplayVersion;
            touchesM2 =
                (_m2EmergencySource == "M817" && position is "M817" or "M818" or "M710") ||
                (_m2EmergencySource == "M818" && position is "M818" or "M710");
            touchesM3 =
                (_m3EmergencySource == "M700" && position is "M700" or "M821" or "M720") ||
                (_m3EmergencySource == "M821" && position is "M821" or "M720");

            if (touchesM2)
            {
                m2Source = _m2EmergencySource;
                var inFlight = _m2InFlightWorkpiece;
                m2SourceCacheTransferred = inFlight != null &&
                    string.Equals(inFlight.Source, m2Source, StringComparison.Ordinal);
                m2InFlightVersionToClear = inFlight?.ActionVersion ?? 0;
                m2Completion = _m2ActionCompletion?.Task;
                CancelM2ActionNoLock(logs);
            }

            if (touchesM3)
            {
                m3Source = _m3EmergencySource;
                var inFlight = _m3InFlightWorkpiece;
                m3SourceCacheTransferred = inFlight != null &&
                    string.Equals(inFlight.Source, m3Source, StringComparison.Ordinal);
                m3InFlightVersionToClear = inFlight?.ActionVersion ?? 0;
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

        // 应急默认清选中位置；只有旧动作尚未转在途时，才同时清它的实际来源。
        // 已转在途的来源键可能已由后天车写入下一块板，必须始终保留，不碰其它正常位置缓存或研磨FIFO。
        var cacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { position };
        if (touchesM2 && m2SourceCacheTransferred)
        {
            if (string.Equals(position, m2Source, StringComparison.OrdinalIgnoreCase))
                cacheKeys.Remove(position);
            logs.Add($"{m2Source}缓存=保留（旧M2动作已转在途，可能是后天车新写入）");
        }
        else if (touchesM2 && !string.IsNullOrWhiteSpace(m2Source))
        {
            cacheKeys.Add(m2Source);
        }

        if (touchesM3 && m3SourceCacheTransferred)
        {
            if (string.Equals(position, m3Source, StringComparison.OrdinalIgnoreCase))
                cacheKeys.Remove(position);
            logs.Add($"{m3Source}缓存=保留（旧M3动作已转在途，可能是后天车新写入）");
        }
        else if (touchesM3 && string.Equals(m3Source, "M821", StringComparison.OrdinalIgnoreCase))
        {
            cacheKeys.Add(m3Source);
        }
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
        if (m2InFlightVersionToClear != 0) { ClearInFlightWorkpiece(true, m2InFlightVersionToClear); logs.Add("M2在途工件=已清"); }
        if (m3InFlightVersionToClear != 0) { ClearInFlightWorkpiece(false, m3InFlightVersionToClear); logs.Add("M3在途工件=已清"); }

        // 仅清展示字段，不改设备、缓存、锁或动作版本。版本校验防止旧应急清掉新任务。
        if (m2DisplayVersionToClear != 0) { ClearM2Display(m2DisplayVersionToClear); logs.Add("M2页面显示=已清"); }
        if (m3DisplayVersionToClear != 0) { ClearM3Display(m3DisplayVersionToClear); logs.Add("M3页面显示=已清"); }

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
        IOperationalEventReporter exceptionReporter,
        SemaphoreSlim? lockM817 = null, SemaphoreSlim? lockM818 = null,
        SemaphoreSlim? lockM821 = null, SemaphoreSlim? lockM720 = null,
        St010PlacementFlags? st010PlacementFlags = null)
    {
        _manipulatorCache = mc;
        _craneCache = cc;
        _mcCache = mcc;
        _cfg = cfg;
        _stationCoords = sc;
        _exceptionReporter = exceptionReporter;
        _lockM817 = lockM817; _lockM818 = lockM818;
        _lockM821 = lockM821; _lockM720 = lockM720;
        _st010PlacementFlags = st010PlacementFlags ?? new St010PlacementFlags();
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

    /// <summary>设置M2展示身份；只允许当前动作版本写入，不改变任何业务字段。</summary>
    private void SetM2Display(int version, WorkpieceCache workpiece, string stage)
    {
        lock (_emergencyLock)
        {
            if (version != _m2ActionVersion) return;
            _m2DisplayVersion = version;
            _m2DisplayWorkpiece = workpiece;
            _m2DisplayStage = stage;
        }
    }

    /// <summary>设置M3展示身份；只允许当前动作版本写入，不改变任何业务字段。</summary>
    private void SetM3Display(int version, WorkpieceCache workpiece, string stage)
    {
        lock (_emergencyLock)
        {
            if (version != _m3ActionVersion) return;
            _m3DisplayVersion = version;
            _m3DisplayWorkpiece = workpiece;
            _m3DisplayStage = stage;
        }
    }

    /// <summary>仅清理同一M2动作版本留下的展示信息，防止旧finally清掉新动作。</summary>
    private void ClearM2Display(int version)
    {
        lock (_emergencyLock)
        {
            if (_m2DisplayVersion != version) return;
            _m2DisplayWorkpiece = null;
            _m2DisplayStage = string.Empty;
            _m2DisplayVersion = 0;
        }
    }

    /// <summary>仅清理同一M3动作版本留下的展示信息，防止旧finally清掉新动作。</summary>
    private void ClearM3Display(int version)
    {
        lock (_emergencyLock)
        {
            if (_m3DisplayVersion != version) return;
            _m3DisplayWorkpiece = null;
            _m3DisplayStage = string.Empty;
            _m3DisplayVersion = 0;
        }
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
    private readonly St010PlacementFlags _st010PlacementFlags;

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
        using var logScope = EngineLogRouter.BeginScope(EngineLogRouter.Balance);
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
            Console.WriteLine("[平衡引擎] 机械手2 ✓ (每趟M2任务取料前按配置设置速度)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[平衡引擎] 机械手2 FAIL: {ex.Message}");
        }

        try
        {
            _m3 = _manipulatorCache.GetOrCreateService(3);
            if (!_m3.IsConnected) await _m3.ConnectAsync(ct);
            Console.WriteLine("[平衡引擎] 机械手3 ✓ (每趟M3任务取料前按配置设置速度)");
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

                
                bool m817Cached = HasBalWp("M817");
                bool m818Cached = HasBalWp("M818");
                bool m821Cached = HasBalWp("M821");
                bool m2HasSource = (_m823CanPick && m818Cached) || (_m817 && m817Cached && !m818Cached);
                bool m3HasSource = (_m825CanPick && m821Cached) || (_m700 && !m821Cached);

                if (_cycleCount % 10 == 1)
                {
                    if (_m817 && !m817Cached)
                        Console.WriteLine("[平衡引擎] M2等: M817=1(1号线有板)但M817软件缓存不存在, 禁止盲取");
                    if (_m823CanPick && !m818Cached)
                        Console.WriteLine("[平衡引擎] M2等: M823=1(ST020允许取料)但M818软件缓存不存在, 禁止盲取");
                    if (m818Cached && !_m823CanPick)
                        Console.WriteLine("[平衡引擎] M2等: M818缓存存在, 等M823=1允许取ST020");
                    if (_m825CanPick && !m821Cached)
                        Console.WriteLine("[平衡引擎] M3等: M825=1(ST021允许取料)但M821软件缓存不存在, 禁止盲取");
                    if (m821Cached && !_m825CanPick)
                        Console.WriteLine("[平衡引擎] M3等: M821缓存存在, 等M825=1允许取ST021");
                }

                // ── ⑤ 机械手2触发条件: M817有板+M817缓存，或ST020(M823允许取+M818缓存存在) + 路径后天车已离开 + ST008(M710=1允许放版) ──
                if (!M2Busy && _m2?.IsConnected == true && m2HasSource)
                {
                    Console.WriteLine("[平衡引擎] 进入机械手2触发区域 ");
                    // 检查对应后天车是否已离开动平衡区域。
                    // M2优先取ST020: M817位置更远, 且去M817需要穿过ST020区域并多拿一把路径锁。
                    // 只要M818缓存存在就认为ST020有实体工件, 等M823允许后先清ST020, 不绕行取M817。
                    bool useM817 = !m818Cached && _m817 && m817Cached;
                    bool rearCranesSafe = useM817
                        ? await AreM817PathRearCranesAtSafeXAsync(ct)
                        : await IsCraneAtSafeXAsync(4, ct);
                    if (!rearCranesSafe)
                    {
                        if (_cycleCount % 10 == 1)
                            Console.WriteLine(useM817
                                ? "[平衡引擎] M2等: M817→ST008路径上的2号或4号后天车未离开动平衡区域"
                                : "[平衡引擎] M2等: ST020来源的4号后天车未离开动平衡区域");
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

    /// <summary>M817→ST008路径会经过ST020/M818区域，2号和4号后天车都必须离开。</summary>
    private async Task<bool> AreM817PathRearCranesAtSafeXAsync(CancellationToken ct)
    {
        if (!await IsCraneAtSafeXAsync(2, ct)) return false;
        return await IsCraneAtSafeXAsync(4, ct);
    }

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
    //    触发: 主循环⑤ M817有板+M817缓存，或ST020(M823允许取+M818缓存存在) + 路径后天车X≤safeX + M710(ST008)允许放版
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
        bool operationalPlacementCommitted = false; // 仅供旁路证据：退磁成功即成立，不参与业务判断
        bool m711Notified = false;
        bool keepDisplayForManualConfirmation = false; // 只控制页面证据清理，不参与任何动作判断
        WorkpieceCache? displayWorkpiece = null;
        string operationalActionId = $"BAL-M2-{actionVersion}";
        using var logAction = OperationalLog.BeginAction("动平衡", "机械手2", operationalActionId,
            source: useM817 ? "M817/ST020" : "M818/ST020", target: "ST008/M710");
        OperationalLog.Info("搬运动作开始", "机械手2已创建自动搬运任务",
            ("来源", useM817 ? "M817/ST020" : "M818/ST020"), ("目标", "ST008/M710"));
        FlowActionContext? action = null;
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(operationalActionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "动平衡", "动平衡引擎", "机械手", "M2", "来源未确定",
            "M2取料并放到ST008/M710", operationalActionId, $"{operationalActionId}:pending", null,
            "来源未确定", "ST008/M710", "机械手2", "来源未确定", "动作已创建", "M817/M818位置锁"));
        void ReleaseM2RackLocks(string reason)
        {
            // M2可能同时拿M817+M818: M817是来源架, M818是M817→M710路径保护锁。
            // 释放时必须后拿先放, 并清got标志, 防止finally重复Release导致信号量计数被放大。
            bool released = false;
            if (gotM818)
            {
                if (ConsumeM2Lock("M818")) _lockM818?.Release();
                action?.TryReleaseLock("M818");
                gotM818 = false;
                released = true;
            }
            if (gotM817)
            {
                if (ConsumeM2Lock("M817")) _lockM817?.Release();
                action?.TryReleaseLock("M817");
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
                OnSafetyAlarm?.Invoke($"动平衡机械手2检测到{pickReg}允许取料，但工件缓存缺失。引擎已暂停，禁止使用默认直径；当前缓存Keys=[{CacheKeysText}]。");
                throw new InvalidOperationException($"M2检测到{pickReg}=有板但工件缓存缺失, 当前Keys=[{CacheKeysText}], 已暂停, 禁止默认160mm取料");
            }
            var trackedWorkpiece = trackedWp.Value;
            action = new FlowActionContext(operationalActionId, "动平衡机械手2取料送ST008", "机械手2",
                trackedWorkpiece.IdentityText, pickReg, "ST008/M710", pickReg);
            if (gotM817) action.RegisterLock("M817");
            if (gotM818) action.RegisterLock("M818");
            displayWorkpiece = trackedWorkpiece;
            operationalSite = operationalSite with
            {
                Station = pickCode,
                ActionStage = $"{pickReg}取料并放到ST008/M710",
                CorrelationKey = $"{operationalActionId}:{pickReg}",
                Workpiece = trackedWorkpiece,
                PlannedSource = pickCode,
                LastConfirmedLocation = pickCode,
                LastSuccessfulCheckpoint = "来源缓存和直径已取得"
            };
            operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheMutation, pickReg);
            operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M711");
            operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "M2-ST008/M710完整交接");
            // 仅记录页面展示身份；不参与后续直径、坐标、X11或任何放行判断。
            SetM2Display(actionVersion, trackedWorkpiece, $"准备从{pickReg}取料");
            Console.WriteLine($"[平衡引擎] [M2] ① 取料 {trackedWorkpiece.IdentityText} 工件直径d={d}");
            // 取料YZ: GetArmCoord自动判断配置→数据库
            //   pickReg = "M817"或"M818", pickCode = "ST019"或"ST020"
            //   坐标优先从配置文件 balancing.armCoords 取, 没有则从数据库machine表+机械手偏移
            var (pickY, pickZ) = GetArmCoord(pickReg, pickCode, _m2OffsetY, _m2OffsetZ);
            //计算公式计算下降距离
            int pzDown = Pz(pickZ, d), safeZ = _cfg.Grinding.SafeZHeight;
            // Console.WriteLine($"[平衡引擎] ");
            // ①.0 安全: 取料前检查磁铁是否已有工件(断电重启后X11不受PLC内存影响)
            operationalTracker.BeginX11Stage("取料前残留检查");
            operationalTracker.BeginX11Attempt();
            bool unexpectedWorkpiece;
            try
            {
                unexpectedWorkpiece = await _m2!.ReadXBitAsync(63497, ct);
                operationalTracker.CompleteX11(unexpectedWorkpiece, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    operationalSite with { ActionStage = $"{pickCode}取料前X11残留检查" }, operationalTracker, ex,
                    "取料前X11读取失败，当前值无效；保留最后成功值（如有）", true);
                throw;
            }
            if (unexpectedWorkpiece)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    operationalSite with { ActionStage = $"{pickCode}取料前发现磁铁已有工件" }, operationalTracker, null,
                    "取料前X11成功读取为1，确认机械手磁铁上存在身份未知残留工件", false,
                    holdingWorkpiece: true);
                Console.WriteLine("[平衡引擎] ⚠⚠⚠ 机械手2磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[平衡引擎]   拒绝取料, 请人工确认机械手2状态后手动处理");
                throw new InvalidOperationException("机械手2 X11=1(磁铁已有工件), 拒绝取料防止碰撞");
            }
            // 每趟M2搬运只在首次物理移动前完整下发一次速度；后续Y/Z、X11重试和放料复用本次参数。
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置机械手2本任务绝对速度");
            action.MarkCommandSent();
            await ConfigureManipulatorAbsSpeedAsync(_m2!, 2, "M2", ct);
            action.Confirm();
            Console.WriteLine($"[平衡引擎] [M2] ① 取料 {pickReg}({pickCode}) {trackedWorkpiece.IdentityText} Y={pickY} Z={pzDown}");
            // 先Y移到取料位(机械手只移YZ轴)
            action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(null, pickY, 0), $"Y到{pickReg}来源位");
            action.MarkCommandSent();
            await _m2.MoveAbsoluteAsync(-1, pickY, -1, ct: ct);
            action.Confirm(new FlowActionPosition(null, pickY, 0), "来源Y到位");
            operationalTracker.BeginZDown(pzDown);
            try
            {
                //再Z下降取料
                action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(null, pickY, pzDown), "来源Z下降取料");
                action.MarkCommandSent();
                await _m2.MoveAbsoluteAsync(-1, -1, pzDown, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(null, pickY, pzDown), "来源取料Z到位");
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("Z下降触发下压保护，恢复后位置等待安全高度确认");
                await _m2.RecoverFromPressureStopAsync(ct);
                throw;
            }

            // X11检测: 充磁→等3s→查X11→没吸到就退磁→Z↓5mm→充磁→再查, 最多2次(与后天车一致)
            Console.WriteLine("[平衡引擎] [M2] 充磁→等3s→X11检测");
            operationalTracker.BeginX11Stage("充磁后持件确认");
            int curZ = pzDown;
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("[平衡引擎] [M2] 充磁");
                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(null, pickY, curZ), "来源首次充磁");
                        action.MarkCommandSent();
                        await _m2.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{pickCode}首次充磁取料" }, operationalTracker, ex,
                            "充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    mag = true;
                    action.Confirm(new FlowActionPosition(null, pickY, curZ), "首次充磁调用成功返回");
                    action.RecordFeedback(FlowActionPosition.Unknown, null, true, "首次充磁调用成功返回");
                }
                else
                {
                    curZ += 5;
                    Console.WriteLine($"[平衡引擎] [M2] 退磁→Z↓到{curZ}→充磁");
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(null, pickY, curZ), $"{pickCode}X11重试前退磁");
                        action.MarkCommandSent();
                        await _m2.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                        action.Confirm(new FlowActionPosition(null, pickY, curZ), "X11重试前退磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = $"{pickCode}X11重试前退磁" }, operationalTracker, ex,
                            "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                        throw;
                    }
                    mag = false;
                    operationalTracker.BeginZDown(curZ);
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(null, pickY, curZ), "M2 X11重试下探Z");
                        action.MarkCommandSent();
                        await _m2.MoveAbsoluteAsync(-1, -1, curZ, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(null, pickY, curZ), "M2 X11重试下探Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("M2 X11重试下探触发下压保护，恢复后实际位置未知");
                        await _m2.RecoverFromPressureStopAsync(ct);
                        throw;
                    }

                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(null, pickY, curZ), $"{pickCode}X11重试后充磁");
                        action.MarkCommandSent();
                        await _m2.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                        action.Confirm(new FlowActionPosition(null, pickY, curZ), "X11重试后充磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{pickCode}下探后再次充磁" }, operationalTracker, ex,
                            "重试充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    mag = true;
                }

                Console.WriteLine("[平衡引擎] [M2] 等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                operationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await _m2.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"{pickCode}充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后成功值（如有）", true);
                    throw;
                }
                Console.WriteLine($"[平衡引擎] [M2] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    Console.WriteLine($"[平衡引擎] [M2] ✓ X11=1 已吸到(保持在取料位Z={curZ})");
                    holdingWorkpiece = true;
                    action.BeginStep(FlowActionStep.ConfirmPickup, new FlowActionPosition(null, pickY, curZ), "X11确认机械手2持件");
                    action.RecordFeedback(new FlowActionPosition(null, pickY, curZ), true, true, "X11=1确认持件");
                    action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1确认工件由机械手2持有");
                    action.Confirm(new FlowActionPosition(null, pickY, curZ), "X11确认持件");
                    SetM2Display(actionVersion, trackedWorkpiece, $"已从{pickReg}吸住，前往ST008/M710");
                    break;
                }

                if (retry > 1)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                        operationalSite with { ActionStage = $"{pickCode}三次X11均未确认持件" }, operationalTracker, null,
                        "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在来源位", true,
                        holdingWorkpiece: false);
                    throw new Exception("M2取料失败: 2次充磁后X11仍=0");
                }
                Console.WriteLine($"[平衡引擎] [M2] ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ── ② 放料: Z↑安全→把来源缓存转为在途→XY→ST008→Z↓台面→退磁→Z↑安全 ──
            // X11已确认且Z安全后，来源缓存必须转为本动作在途记录；此后放锁不会让旧动作删掉后天车的新缓存。
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(null, pickY, safeZ), "持件后Z上升安全高度");
            action.MarkCommandSent();
            await _m2.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            action.Confirm(new FlowActionPosition(null, pickY, safeZ), "持件后Z安全到位");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "M2取料后Z升安全命令成功返回");
            operationalTracker.BeginCacheMutation(pickReg,
                EvidenceValue<string>.Confirmed(trackedWorkpiece.IdentityText, "X11=1、Z安全且来源缓存已锁内确认"),
                "将来源缓存转为M2在途工件");
            TransferBalancingCacheToInFlight(true, actionVersion, pickReg, "ST008/M710", trackedWorkpiece);
            operationalTracker.CompleteCacheMutation(pickReg,
                EvidenceValue<string>.Confirmed(trackedWorkpiece.IdentityText, "缓存转移前的来源工件"),
                EvidenceValue<string>.Confirmed("来源缓存已移除，工件已登记为M2在途", "转移成功返回"),
                "M2来源缓存转在途成功返回");
            if (!useM817)
            {
                action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(null, pickY, safeZ), "M824通知ST020已取料");
                action.MarkCommandSent();
                await WriteMc63HandshakeBitAsync(RackAddr.Bit_ST020_PickDone, "M824(ST020机械手2取料完成)", ct);
                action.Confirm(new FlowActionPosition(null, pickY, safeZ), "M824写入成功返回");
            }
            // 目的位M710在主循环里已经预检过一次; 但从预检到机械手真正放料之间,
            // 现场PLC信号/人工状态可能变化。这里在抱板去ST008前再读一次真实M710,
            // 失败或有板都不继续放料, 走现有“工件在机械手上”暂停路径, 防止叠料。
            await ConfirmM710EmptyBeforePlaceAsync(ct);
            //获得yz距离
            var (destY, destZ) = GetArmCoord("M710", "ST008", _m2OffsetY, _m2OffsetZ);
            Console.WriteLine($"[平衡引擎] [M2] ② 放料 M710(ST008) Y={destY}");
            int m2ReleaseLocksBelowY = _cfg.Balancing.M2ReleaseLocksBelowY;
            bool m2LocksReleasedDuringTransit = false;
            void ObserveM2TransitY(CraneStatus status)
            {
                if (m2LocksReleasedDuringTransit || status.YPos >= m2ReleaseLocksBelowY) return;
                m2LocksReleasedDuringTransit = true;
                ReleaseM2RackLocks($"去ST008途中实际Y={status.YPos}小于提前释放阈值={m2ReleaseLocksBelowY}");
            }
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(null, destY, safeZ), "持件Y到ST008/M710目标位");
            action.MarkCommandSent();
            await _m2.MoveAbsoluteAsync(-1, destY, -1, ct: ct, yStatusObserver: ObserveM2TransitY);
            action.Confirm(new FlowActionPosition(null, destY, safeZ), "目标Y到位");
            int placeZ = Pz(destZ, d);
            Console.WriteLine($"[平衡引擎] [M2]   放料Z公式: {destZ} - Round(...) = {placeZ}");
            operationalTracker.BeginZDown(placeZ);
            try
            {
                action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(null, destY, placeZ), "ST008/M710下降放料");
                action.MarkCommandSent();
                await _m2.MoveAbsoluteAsync(-1, -1, placeZ, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(null, destY, placeZ), "目标放料Z到位");
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("ST008/M710放料Z下降触发下压保护，恢复后实际位置需人工确认");
                await _m2.RecoverFromPressureStopAsync(ct);
                throw;
            }
            catch (Exception)
            {
                operationalTracker.MarkZUnknown("ST008/M710放料Z下降异常，当前Z位置未知");
                throw;
            }

            //退磁
            operationalTracker.BeginPlacementMagnetOff("ST008/M710");
            try
            {
                action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(null, destY, placeZ), "ST008/M710退磁放料");
                action.MarkCommandSent();
                await _m2.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff("ST008/M710");
                operationalPlacementCommitted = true;
                operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "M2-ST008/M710完整交接",
                    "目标位退磁成功返回，放料推定成立；等待Z安全回升和M711通知；来源缓存已在持件安全Z阶段转在途");
            }
            catch (Exception ex)
            {
                operationalTracker.FailMagnetOff();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                    operationalSite with { Station = "ST008", ActionStage = "ST008/M710目标位退磁放料" }, operationalTracker, ex,
                    "目标位退磁方法异常，工件是否释放未知", true,
                    holdingWorkpiece: null, placed: null, cacheNotified: null,
                    downstreamNotified: null, handoffClosed: null);
                throw;
            }
            mag = false;
            action.Confirm(new FlowActionPosition(null, destY, placeZ), "目标退磁成功返回");
            action.RecordFeedback(FlowActionPosition.Unknown, null, false, "目标退磁成功返回");
            action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "退磁成功，工件已在ST008/M710等待M711闭环");
            SetInFlightOwnership(true, actionVersion, "ST008/M710目标待交接");
            Console.WriteLine("[平衡引擎] [M2] 退磁 ✓");
            //退磁完成回安全位置
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(null, destY, safeZ), "ST008/M710放料后Z升安全高度");
            action.MarkCommandSent();
            await _m2.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            action.Confirm(new FlowActionPosition(null, destY, safeZ), "ST008/M710放料后Z安全到位");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST008/M710放料后Z升安全命令成功返回");
            placedOnM710 = true; // 工件已物理放到ST008, 后续M711失败必须暂停人工确认
            holdingWorkpiece = false;
            SetM2Display(actionVersion, trackedWorkpiece, "已放到ST008/M710，等待M711确认");

            // ── 写M711=1: 通知PLC工件已送到ST008动平衡料架1(M300→M711) ──
            if (_mc65?.IsConnected == true)
            {
                try
                {
                    action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(null, destY, safeZ), "M711通知ST008/M710放料完成");
                    action.MarkCommandSent();
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M711",
                        "M711写入调用已开始，PLC是否收到结果未知");
                    await _mc65.WriteMBitInWordAsync(700, 11, true, ct);
                    action.Confirm(new FlowActionPosition(null, destY, safeZ), "M711写入成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M711",
                        "M711写入成功返回，PLC放料通知已确认");
                    m711Notified = true;
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "M2-ST008/M710完整交接",
                        "退磁、Z安全回升及M711通知均成功返回；来源缓存已在持件安全Z阶段转在途");
                    trackedWorkpiece.ReportStage("动平衡加工中 ST008/M710");
                    SetM2Display(actionVersion, trackedWorkpiece, "M711已确认，机械手2返回安全位");
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
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, safeY, safeZ), "机械手2回配置安全Y");
            action.MarkCommandSent();
            await _m2.MoveAbsoluteAsync(-1, safeY, -1, ct: ct);
            action.Confirm(new FlowActionPosition(null, safeY, safeZ), "机械手2安全Y到位");
            action.Complete("机械手2已完成M711通知并回到安全位");
            Console.WriteLine("[平衡引擎] [M2] ═══ 完成 ═══");
        }
        catch (Exception ex)
        {
            if (ex is CraneMotionTimeoutException timeout)
            {
                // 运动超时的实际位置未知：无论是否已持件，都必须停止动平衡并把原始超时信息交给主页面弹窗。
                // 不在这里变更工件、来源、动作账本或线路启动条件；既有通用异常处理和 finally 继续负责原有记录与本趟锁清理。
                _paused = true;
                OnSafetyAlarm?.Invoke(timeout.Message);
            }
            // holdingWorkpiece=true: X11已确认工件在机械手上; 未放到目的位前必须暂停, 防止释放busy后继续派发。
            Console.WriteLine($"[平衡引擎] [M2] ✘ 异常: {ex.Message}");
            if (action is { IsFinalized: false } && action.CommandState != FlowCommandState.NotSent)
            {
                action.MarkCommandResponseUnknown(ex.Message);
                action.PauseForManualResolution(ex.Message);
            }
            if (action != null)
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                    operationalSite with { ActionStage = "机械手2动作最终异常" }, operationalTracker, ex,
                    "机械手2已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                    actionSnapshot: action.Snapshot());
            if (operationalPlacementCommitted && !m711Notified)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = "ST008", ActionStage = "ST008/M710退磁放料后M711未闭环" },
                    operationalTracker, ex,
                    "目标位退磁已成功返回并推定工件位于ST008/M710，但Z安全回升、来源缓存移除或M711通知未完整确认", true,
                    holdingWorkpiece: null, placed: null, cacheNotified: null,
                    downstreamNotified: null, handoffClosed: null);
            }
            if (!IsM2ActionCurrent(actionVersion))
            {
                Console.WriteLine("[平衡引擎] [M2] 旧动作已被应急取消, 跳过暂停/状态写回");
            }
            else if (holdingWorkpiece && !placedOnM710)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM2Display(actionVersion, displayWorkpiece.Value, "机械手2已持件，尚未放到ST008/M710，需人工确认");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M2已吸住工件但未放到ST008/M710, 引擎已暂停, 请人工确认机械手2/工件位置");
                OnSafetyAlarm?.Invoke($"动平衡机械手2已吸住工件但未放到ST008/M710。引擎已暂停，请人工确认机械手和工件位置。异常：{ex.Message}");
            }
            else if (mag && !placedOnM710)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM2Display(actionVersion, displayWorkpiece.Value, "机械手2磁铁状态异常，工件位置需人工确认");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M2磁铁处于打开/异常状态且未完成放料, 引擎已暂停, 请人工确认");
                OnSafetyAlarm?.Invoke($"动平衡机械手2磁铁处于打开或异常状态，且未完成放料。引擎已暂停，请人工确认。异常：{ex.Message}");
            }
            if (placedOnM710 && !m711Notified)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM2Display(actionVersion, displayWorkpiece.Value, "实物可能已在ST008/M710，M711未闭环，需人工确认");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M2已把工件放到ST008/M710但M711未确认, 引擎已暂停, 请人工确认PLC信号");
                OnSafetyAlarm?.Invoke($"动平衡机械手2已把工件放到ST008/M710，但M711未确认。引擎已暂停，请人工确认PLC信号。异常：{ex.Message}");
            }
        }
        finally
        {
            // 异常兜底: 未在持件去ST008途中达到提前释放阈值时，释放仍持有的来源/路径锁。
            // 已提前释放时got标志已清，这里不会重复Release。
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
            if (keepDisplayForManualConfirmation && IsM2ActionCurrent(actionVersion))
                Console.WriteLine($"[平衡引擎] [M2] 保留页面工件身份: 动作版本={actionVersion}，等待人工确认/应急成功清理");
            else
            {
                ClearM2Display(actionVersion);
                ClearInFlightWorkpiece(true, actionVersion);
            }
            FinishM2Action(actionVersion, actionCompletion);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  机械手3流程 (M3Flow): 动平衡料架2/不需动平衡位→取料→ST010研磨上料架放料→Y回安全位
    //    触发: 主循环⑥ M700有版或ST021(M825允许取+M821缓存存在) + (M821需天车4号X≤safeX) + M720(ST010)允许放版
    //    直径: useM700=true → 读D100(MC65, 动平衡料架2直径); false → 读_balWps["M821"]缓存
    //    信号: M700来源取走后写M701=1; 放料后写M721=1(MC65)通知PLC放料完成; M720在锁后和持件去目标前分别确认
    //    ⚠ M821缓存会在X11=1且Z安全后转为本动作在途记录，提前放锁后不允许旧动作删除新缓存
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
        bool operationalPlacementCommitted = false; // 仅供旁路证据：退磁成功即成立，不参与业务判断
        bool operationalSourceCacheClosed = false; // 仅供旁路证据
        bool m721Notified = false;
        bool grindingCacheNotified = false; // M720物理放料后, 研磨FIFO缓存必须写入成功
        bool keepDisplayForManualConfirmation = false; // 只控制页面证据清理，不参与任何动作判断
        WorkpieceCache? displayWorkpiece = null;
        string operationalActionId = $"BAL-M3-{actionVersion}";
        using var logAction = OperationalLog.BeginAction("动平衡", "机械手3", operationalActionId,
            source: useM700 ? "M700/ST021" : "M821/ST021", target: "ST010/M720");
        OperationalLog.Info("搬运动作开始", "机械手3已创建自动搬运任务",
            ("来源", useM700 ? "M700/ST021" : "M821/ST021"), ("目标", "ST010/M720"));
        FlowActionContext? action = null;
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(operationalActionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "动平衡", "动平衡引擎", "机械手", "M3", "来源未确定",
            "M3取料并放到ST010/M720", operationalActionId, $"{operationalActionId}:pending", null,
            "来源未确定", "ST010/M720", "机械手3", "来源未确定", "动作已创建", "M821/M720位置锁"));
        void ReleaseM3RackLocks(string reason)
        {
            bool released = false;
            if (gotM720)
            {
                if (ConsumeM3Lock("M720")) _lockM720?.Release();
                action?.TryReleaseLock("M720");
                gotM720 = false;
                released = true;
            }
            if (gotM821)
            {
                if (ConsumeM3Lock("M821")) _lockM821?.Release();
                action?.TryReleaseLock("M821");
                gotM821 = false;
                released = true;
            }
            if (released) Console.WriteLine($"[平衡引擎] [M3] {reason}: 已释放M821/M720位置锁");
        }
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

            // 主循环的M720只是锁外派发快照；等待两把位置锁可能持续数分钟。
            // 此时尚未取料，可以在目标许可已经失效时正常放弃本趟，避免占着锁去做无效取料动作。
            if (!await ConfirmM720AfterM3LocksAsync(ct))
            {
                Console.WriteLine("[平衡引擎] [M3] 锁后复查M720=0，本趟尚未取料，正常结束；finally将释放M720/M821锁、Busy和动作上下文");
                OperationalLog.Info("锁后目标许可失效", "机械手3尚未取料，正常放弃本趟并释放资源",
                    ("信号名称", "M720放料允许"), ("当前值", "0（暂不允许）"),
                    ("工件状态", "尚未取料"), ("资源清理", "finally释放M720、M821、Busy和动作上下文"));
                return;
            }

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
                        OnSafetyAlarm?.Invoke($"动平衡机械手3检测到M700有板，但D100直径读取失败。引擎已暂停。异常：{ex.Message}");
                        throw new InvalidOperationException($"M3检测到M700=有板但D100直径读取失败, 已暂停: {ex.Message}", ex);
                    }
                }
                else
                {
                    _paused = true;
                    OnSafetyAlarm?.Invoke("动平衡机械手3检测到M700有板，但MC65未连接，无法读取D100直径。引擎已暂停。");
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
                    OnSafetyAlarm?.Invoke("动平衡机械手3检测到M825允许取料，但M821工件缓存缺失。引擎已暂停，禁止使用默认直径。");
                    throw new InvalidOperationException("M3检测到M825允许取料但M821工件缓存缺失, 已暂停, 禁止默认160mm取料");
                }
                Console.WriteLine($"[平衡引擎] [M3] M821来源 {m821TrackedWp?.IdentityText ?? "版号=未知"} → 缓存读取 工件直径={d}mm");
            }

            string sourceIdentity = useM700 ? "版号=未知(M700人工动平衡后)" : (m821TrackedWp?.IdentityText ?? "版号=未知");
            // M700人工动平衡后无法可靠恢复版号，只保留已读到的直径；M821则保留完整缓存身份。
            var m3DisplayWorkpiece = m821TrackedWp ?? new WorkpieceCache
            {
                Diameter = d,
                BoreType = 1,
                Length = 0
            };
            displayWorkpiece = m3DisplayWorkpiece;
            action = new FlowActionContext(operationalActionId, "动平衡机械手3取料送ST010", "机械手3",
                sourceIdentity, pickReg, "ST010/M720", pickReg);
            if (gotM821) action.RegisterLock("M821");
            if (gotM720) action.RegisterLock("M720");
            operationalSite = operationalSite with
            {
                Station = pickCode,
                ActionStage = $"{pickReg}取料并放到ST010/M720",
                CorrelationKey = $"{operationalActionId}:{pickReg}",
                Workpiece = m3DisplayWorkpiece,
                PlannedSource = pickCode,
                LastConfirmedLocation = pickCode,
                LastSuccessfulCheckpoint = useM700 ? "D100直径已读取，工件身份未知" : "M821来源缓存已取得"
            };
            operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721");
            operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnGrindingRackPlaced");
            operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "M3-ST010/M720完整交接");
            if (!useM700)
                operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.CacheMutation, "M821");
            SetM3Display(actionVersion, m3DisplayWorkpiece, $"准备从{pickReg}取料");
            
            // 取料YZ: GetArmCoord自动判断配置→ 一般不从数据库读取 从配置文件 数据库的不适用机械手
            var (pickY, pickZ) = GetArmCoord(pickReg, pickCode, _m3OffsetY, _m3OffsetZ);
            //判断下降距离
            int pzDown = Pz(pickZ, d), safeZ = _cfg.Grinding.SafeZHeight;

            // ①.0 安全: 取料前检查磁铁是否已有工件(断电重启后X11不受PLC内存影响)
            operationalTracker.BeginX11Stage("取料前残留检查");
            operationalTracker.BeginX11Attempt();
            bool unexpectedWorkpiece;
            try
            {
                unexpectedWorkpiece = await _m3!.ReadXBitAsync(63497, ct);
                operationalTracker.CompleteX11(unexpectedWorkpiece, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    operationalSite with { ActionStage = $"{pickCode}取料前X11残留检查" }, operationalTracker, ex,
                    "取料前X11读取失败，当前值无效；保留最后成功值（如有）", true);
                throw;
            }
            if (unexpectedWorkpiece)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    operationalSite with { ActionStage = $"{pickCode}取料前发现磁铁已有工件" }, operationalTracker, null,
                    "取料前X11成功读取为1，确认机械手磁铁上存在身份未知残留工件", false,
                    holdingWorkpiece: true);
                Console.WriteLine("[平衡引擎] ⚠⚠⚠ 机械手3磁铁上已有工件(X11=1)！可能是断电/急停重启后残留！");
                Console.WriteLine("[平衡引擎]   拒绝取料, 请人工确认机械手3状态后手动处理");
                throw new InvalidOperationException("机械手3 X11=1(磁铁已有工件), 拒绝取料防止碰撞");
            }
            // 每趟M3搬运只在首次物理移动前完整下发一次速度；后续Y/Z、X11重试和放料复用本次参数。
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置机械手3本任务绝对速度");
            action.MarkCommandSent();
            await ConfigureManipulatorAbsSpeedAsync(_m3!, 3, "M3", ct);
            action.Confirm();
            Console.WriteLine($"[平衡引擎] [M3] ① 取料 {pickReg}({pickCode}) {sourceIdentity} Y={pickY} Z={pzDown}");
            // 先Y移到取料位(机械手只移YZ轴)
            action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(null, pickY, 0), $"Y到{pickReg}来源位");
            action.MarkCommandSent();
            await _m3.MoveAbsoluteAsync(-1, pickY, -1, ct: ct);
            action.Confirm(new FlowActionPosition(null, pickY, 0), "来源Y到位");
            operationalTracker.BeginZDown(pzDown);
            try
            {
                //再Z下降取料
                action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(null, pickY, pzDown), "来源Z下降取料");
                action.MarkCommandSent();
                await _m3.MoveAbsoluteAsync(-1, -1, pzDown, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(null, pickY, pzDown), "来源取料Z到位");
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("Z下降触发下压保护，恢复后位置等待安全高度确认");
                await _m3.RecoverFromPressureStopAsync(ct);
                throw;
            }

            // X11检测: 充磁→等3s→查X11→没吸到就退磁→Z↓5mm→充磁→再查, 最多2次(与后天车一致)
            Console.WriteLine("[平衡引擎] [M3] 充磁→等3s→X11检测");
            operationalTracker.BeginX11Stage("充磁后持件确认");
            int curZ = pzDown;
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine("[平衡引擎] [M3] 充磁");
                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(null, pickY, curZ), "来源首次充磁");
                        action.MarkCommandSent();
                        await _m3.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{pickCode}首次充磁取料" }, operationalTracker, ex,
                            "充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    mag = true;
                    action.Confirm(new FlowActionPosition(null, pickY, curZ), "首次充磁调用成功返回");
                    action.RecordFeedback(FlowActionPosition.Unknown, null, true, "首次充磁调用成功返回");
                }
                else
                {
                    curZ += 5;
                    Console.WriteLine($"[平衡引擎] [M3] 退磁→Z↓到{curZ}→充磁");
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(null, pickY, curZ), $"{pickCode}X11重试前退磁");
                        action.MarkCommandSent();
                        await _m3.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                        action.Confirm(new FlowActionPosition(null, pickY, curZ), "X11重试前退磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = $"{pickCode}X11重试前退磁" }, operationalTracker, ex,
                            "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                        throw;
                    }
                    mag = false;
                    operationalTracker.BeginZDown(curZ);
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(null, pickY, curZ), "M3 X11重试下探Z");
                        action.MarkCommandSent();
                        await _m3.MoveAbsoluteAsync(-1, -1, curZ, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(null, pickY, curZ), "M3 X11重试下探Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("M3 X11重试下探触发下压保护，恢复后实际位置未知");
                        await _m3.RecoverFromPressureStopAsync(ct);
                        throw;
                    }

                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(null, pickY, curZ), $"{pickCode}X11重试后充磁");
                        action.MarkCommandSent();
                        await _m3.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                        action.Confirm(new FlowActionPosition(null, pickY, curZ), "X11重试后充磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = $"{pickCode}下探后再次充磁" }, operationalTracker, ex,
                            "重试充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    mag = true;
                }

                Console.WriteLine("[平衡引擎] [M3] 等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                operationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await _m3.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"{pickCode}充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后成功值（如有）", true);
                    throw;
                }
                Console.WriteLine($"[平衡引擎] [M3] X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    Console.WriteLine($"[平衡引擎] [M3] ✓ X11=1 已吸到(保持在取料位Z={curZ})");
                    holdingWorkpiece = true;
                    action.BeginStep(FlowActionStep.ConfirmPickup, new FlowActionPosition(null, pickY, curZ), "X11确认机械手3持件");
                    action.RecordFeedback(new FlowActionPosition(null, pickY, curZ), true, true, "X11=1确认持件");
                    action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1确认工件由机械手3持有");
                    action.Confirm(new FlowActionPosition(null, pickY, curZ), "X11确认持件");
                    SetM3Display(actionVersion, m3DisplayWorkpiece, $"已从{pickReg}吸住，前往ST010/M720");
                    break;
                }

                if (retry > 1)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                        operationalSite with { ActionStage = $"{pickCode}三次X11均未确认持件" }, operationalTracker, null,
                        "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在来源位", true,
                        holdingWorkpiece: false);
                    throw new Exception("M3取料失败: 2次充磁后X11仍=0");
                }
                Console.WriteLine($"[平衡引擎] [M3] ⚠ X11=0 未吸到, 准备下探5mm重试");
            }
            //z移动安全位置
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(null, pickY, safeZ), "持件后Z上升安全高度");
            action.MarkCommandSent();
            await _m3.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            action.Confirm(new FlowActionPosition(null, pickY, safeZ), "持件后Z安全到位");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "M3取料后Z升安全命令成功返回");
            if (useM700)
                CreateInFlightWorkpiece(false, actionVersion, "M700", "ST010/M720", m3DisplayWorkpiece);
            else
            {
                operationalTracker.BeginCacheMutation("M821",
                    EvidenceValue<string>.Confirmed(m3DisplayWorkpiece.IdentityText, "X11=1、Z安全且M821来源缓存已锁内确认"),
                    "将M821来源缓存转为M3在途工件");
                TransferBalancingCacheToInFlight(false, actionVersion, "M821", "ST010/M720", m3DisplayWorkpiece);
                operationalTracker.CompleteCacheMutation("M821",
                    EvidenceValue<string>.Confirmed(m3DisplayWorkpiece.IdentityText, "缓存转移前的M821来源工件"),
                    EvidenceValue<string>.Confirmed("M821来源缓存已移除，工件已登记为M3在途", "转移成功返回"),
                    "M3来源缓存转在途成功返回");
            }
            operationalSourceCacheClosed = true;
            if (!useM700)
            {
                action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(null, pickY, safeZ), "M826通知ST021已取料");
                action.MarkCommandSent();
                await WriteMc63HandshakeBitAsync(RackAddr.Bit_ST021_PickDone, "M826(ST021机械手3取料完成)", ct);
                action.Confirm(new FlowActionPosition(null, pickY, safeZ), "M826写入成功返回");
            }

            // ── M701=1: 仅M700来源需要通知PLC“动平衡料架2已被天车取走” ──
            // 必须等X11确认吸住且Z已升到安全高度后再写, 否则PLC可能清M700而工件实际仍在ST009。
            // M821来源是2号线短板中转位, 不属于动平衡上料架2, 不能写M701。
            if (useM700)
            {
                if (_mc65?.IsConnected == true)
                {
                    try
                    {
                        action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(null, pickY, safeZ), "M701通知M700/ST009来源已取料");
                        action.MarkCommandSent();
                        await _mc65.WriteMBitInWordAsync(700, 1, true, ct);
                        action.Confirm(new FlowActionPosition(null, pickY, safeZ), "M701写入成功返回");
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
            if (_st010PlacementFlags.BeginM3Placement())
            {
                Console.WriteLine("[平衡引擎] [M3] ST010放板标志=1，研磨天车将跳过本趟M731");
                OperationalLog.Info("ST010放板标志置位", "机械手3已通过最终M720确认，开始向ST010放板",
                    ("标志", "M3St010Placing"), ("当前值", "1"), ("用途", "研磨天车M731判断"));
            }
            var (destY, destZ) = GetArmCoord("M720", "ST010", _m3OffsetY, _m3OffsetZ);
            Console.WriteLine($"[平衡引擎] [M3] ② 放料 ST010 Y={destY}");
            //移动y
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(null, destY, safeZ), "持件Y到ST010/M720目标位");
            action.MarkCommandSent();
            await _m3.MoveAbsoluteAsync(-1, destY, -1, ct: ct);
            action.Confirm(new FlowActionPosition(null, destY, safeZ), "目标Y到位");
            int placeZ = Pz(destZ, d);
            Console.WriteLine($"[平衡引擎] [M3]   放料Z公式: {destZ} - Round(...) = {placeZ}");
            operationalTracker.BeginZDown(placeZ);
            try
            {
                //移动z
                action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(null, destY, placeZ), "ST010/M720下降放料");
                action.MarkCommandSent();
                await _m3.MoveAbsoluteAsync(-1, -1, placeZ, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(null, destY, placeZ), "目标放料Z到位");
            }
            catch (PressureStopException)
            {
                operationalTracker.MarkZUnknown("ST010/M720放料Z下降触发下压保护，恢复后实际位置需人工确认");
                await _m3.RecoverFromPressureStopAsync(ct);
                throw;
            }
            catch (Exception)
            {
                operationalTracker.MarkZUnknown("ST010/M720放料Z下降异常，当前Z位置未知");
                throw;
            }
            
            operationalTracker.BeginPlacementMagnetOff("ST010/M720");
            try
            {
                action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(null, destY, placeZ), "ST010/M720退磁放料");
                action.MarkCommandSent();
                await _m3.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff("ST010/M720");
                operationalPlacementCommitted = true;
                operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "M3-ST010/M720完整交接",
                    "目标位退磁成功返回，放料推定成立；等待Z安全回升、M721和研磨缓存闭环；M821来源缓存已在持件安全Z阶段转在途");
            }
            catch (Exception ex)
            {
                operationalTracker.FailMagnetOff();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                    operationalSite with { Station = "ST010", ActionStage = "ST010/M720目标位退磁放料" }, operationalTracker, ex,
                    "目标位退磁方法异常，工件是否释放未知", true,
                    holdingWorkpiece: null, placed: null, cacheNotified: null,
                    downstreamNotified: null, handoffClosed: null);
                throw;
            }
            mag = false;
            action.Confirm(new FlowActionPosition(null, destY, placeZ), "目标退磁成功返回");
            action.RecordFeedback(FlowActionPosition.Unknown, null, false, "目标退磁成功返回");
            action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "退磁成功，工件已在ST010/M720等待M721和研磨缓存闭环");
            SetInFlightOwnership(false, actionVersion, "ST010/M720目标待交接");
            Console.WriteLine("[平衡引擎] [M3] 退磁 ✓");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(null, destY, safeZ), "ST010/M720放料后Z升安全高度");
            action.MarkCommandSent();
            await _m3.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            action.Confirm(new FlowActionPosition(null, destY, safeZ), "ST010/M720放料后Z安全到位");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST010/M720放料后Z升安全命令成功返回");
            if (_st010PlacementFlags.EndM3Placement())
            {
                Console.WriteLine("[平衡引擎] [M3] ST010放板标志=0（放料后Z安全到位）");
                OperationalLog.Info("ST010放板标志清除", "机械手3已完成ST010放料并升到安全Z，清除本趟M731判断标志",
                    ("标志", "M3St010Placing"), ("当前值", "0"), ("原因", "ST010放料后Z安全到位"));
            }
            placedOnM720 = true; // 工件已物理放到研磨上料位并且Z已离开, 后续信号失败需人工补确认
            holdingWorkpiece = false;
            SetM3Display(actionVersion, m3DisplayWorkpiece, "已放到ST010/M720，等待M721及研磨缓存确认");

            // ── 写M721=1: 通知PLC放料到研磨上料架1号位完成 → 传送带启动 ──
            if (_mc65?.IsConnected == true)
            {
                try
                {
                    action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(null, destY, safeZ), "M721通知ST010/M720放料完成");
                    action.MarkCommandSent();
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721",
                        "M721写入调用已开始，PLC是否收到结果未知");
                    await _mc65.WriteMBitInWordAsync(720, 1, true, ct); 
                    action.Confirm(new FlowActionPosition(null, destY, safeZ), "M721写入成功返回");
                    operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721",
                        "M721写入成功返回，PLC放料通知已确认");
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
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnGrindingRackPlaced",
                "研磨缓存回调调用已开始，回调副作用结果未知");
            OnGrindingRackPlaced.Invoke(grindingWp);
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.CacheNotification, "OnGrindingRackPlaced",
                "研磨缓存回调成功返回，目标缓存通知已确认");
            grindingCacheNotified = true;
            grindingWp.ReportStage("研磨上料架 ST010/M720");
            SetM3Display(actionVersion, grindingWp, "已写入研磨缓存，机械手3返回安全位");
            Console.WriteLine($"[平衡引擎] [M3] 通知研磨引擎入缓存 {grindingWp.IdentityText} d={grindingWp.Diameter} L={grindingWp.Length}");

            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "M3-ST010/M720完整交接",
                "退磁、Z安全回升、M721及研磨缓存均成功返回；适用的来源缓存已在持件安全Z阶段转在途");

            // ── ③ 回安全位: Y→配置文件manipulator3SafeY ──
            int safeY = _cfg.SkewBed.Manipulator3SafeY;
            Console.WriteLine($"[平衡引擎] [M3] Y→安全位{safeY}");
            int m3ReleaseLocksBelowY = _cfg.Balancing.M3ReleaseLocksBelowY;
            bool m3LocksReleasedDuringReturn = false;
            void ObserveM3ReturnY(CraneStatus status)
            {
                if (m3LocksReleasedDuringReturn || status.YPos >= m3ReleaseLocksBelowY) return;
                m3LocksReleasedDuringReturn = true;
                ReleaseM3RackLocks($"从ST010回安全位途中实际Y={status.YPos}小于提前释放阈值={m3ReleaseLocksBelowY}");
            }
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, safeY, safeZ), "机械手3回配置安全Y");
            action.MarkCommandSent();
            await _m3.MoveAbsoluteAsync(-1, safeY, -1, ct: ct, yStatusObserver: ObserveM3ReturnY);
            action.Confirm(new FlowActionPosition(null, safeY, safeZ), "机械手3安全Y到位");
            action.Complete("机械手3已完成M721、研磨缓存和来源账本闭环并回到安全位");
            Console.WriteLine("[平衡引擎] [M3] ═══ 完成 ═══");
        }
        catch (Exception ex)
        {
            if (ex is CraneMotionTimeoutException timeout)
            {
                // 运动超时的实际位置未知：无论是否已持件，都必须停止动平衡并把原始超时信息交给主页面弹窗。
                // 不在这里变更工件、来源、动作账本或线路启动条件；既有通用异常处理和 finally 继续负责原有记录与本趟锁清理。
                _paused = true;
                OnSafetyAlarm?.Invoke(timeout.Message);
            }
            // holdingWorkpiece=true: X11已确认工件在机械手上; 未放到目的位前必须暂停, 防止释放busy后继续派发。
            Console.WriteLine($"[平衡引擎] [M3] ✘ 异常: {ex.Message}");
            if (action is { IsFinalized: false } && action.CommandState != FlowCommandState.NotSent)
            {
                action.MarkCommandResponseUnknown(ex.Message);
                action.PauseForManualResolution(ex.Message);
            }
            if (action != null)
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                    operationalSite with { ActionStage = "机械手3动作最终异常" }, operationalTracker, ex,
                    "机械手3已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                    actionSnapshot: action.Snapshot());
            if (operationalPlacementCommitted && (!m721Notified || !grindingCacheNotified || !operationalSourceCacheClosed))
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = "ST010", ActionStage = "ST010/M720退磁放料后通知未闭环" },
                    operationalTracker, ex,
                    "工件已退磁放到ST010/M720，但M721或研磨缓存通知未完整成功", true,
                    holdingWorkpiece: null, placed: null, cacheNotified: null,
                    downstreamNotified: null, handoffClosed: null);
            }
            if (!IsM3ActionCurrent(actionVersion))
            {
                Console.WriteLine("[平衡引擎] [M3] 旧动作已被应急取消, 跳过暂停/状态写回");
            }
            else if (holdingWorkpiece && !placedOnM720)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM3Display(actionVersion, displayWorkpiece.Value, "机械手3已持件，尚未放到ST010/M720，需人工确认");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3已吸住工件但未放到M720/ST010, 引擎已暂停, 请人工确认机械手3/工件位置");
                OnSafetyAlarm?.Invoke($"动平衡机械手3已吸住工件但未放到M720/ST010。引擎已暂停，请人工确认机械手和工件位置。异常：{ex.Message}");
            }
            else if (mag && !placedOnM720)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM3Display(actionVersion, displayWorkpiece.Value, "机械手3磁铁状态异常，工件位置需人工确认");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3磁铁处于打开/异常状态且未完成放料, 引擎已暂停, 请人工确认");
                OnSafetyAlarm?.Invoke($"动平衡机械手3磁铁处于打开或异常状态，且未完成放料。引擎已暂停，请人工确认。异常：{ex.Message}");
            }
            if (placedOnM720 && !m721Notified)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM3Display(actionVersion, displayWorkpiece.Value, "实物可能已在ST010/M720，M721未闭环，需人工确认");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3已把工件放到M720/ST010但M721未确认, 引擎已暂停, 请人工确认PLC信号/研磨缓存");
                OnSafetyAlarm?.Invoke($"动平衡机械手3已把工件放到M720/ST010，但M721未确认。引擎已暂停，请确认PLC信号和研磨缓存。异常：{ex.Message}");
            }
            if (placedOnM720 && m721Notified && !grindingCacheNotified)
            {
                keepDisplayForManualConfirmation = true;
                if (displayWorkpiece.HasValue)
                    SetM3Display(actionVersion, displayWorkpiece.Value, "M721已确认但研磨缓存未写入，需人工补录");
                _paused = true;
                Console.WriteLine("[平衡引擎] ⚠ M3已把工件放到M720/ST010且M721已通知, 但研磨缓存未写入, 引擎已暂停, 请人工确认/补录缓存");
                OnSafetyAlarm?.Invoke($"动平衡机械手3已把工件放到M720/ST010且M721已通知，但研磨缓存未写入。引擎已暂停，请人工补录缓存。异常：{ex.Message}");
            }
        }
        finally
        {
            if (_st010PlacementFlags.EndM3Placement())
            {
                Console.WriteLine("[平衡引擎] [M3] ST010放板标志=0（流程退出兜底）");
                OperationalLog.Info("ST010放板标志清除", "机械手3流程退出，兜底清除本趟M731判断标志",
                    ("标志", "M3St010Placing"), ("当前值", "0"), ("原因", "流程退出兜底"));
            }
            ReleaseM3RackLocks("finally兜底");
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
            if (keepDisplayForManualConfirmation && IsM3ActionCurrent(actionVersion))
                Console.WriteLine($"[平衡引擎] [M3] 保留页面工件身份: 动作版本={actionVersion}，等待人工确认/应急成功清理");
            else
            {
                ClearM3Display(actionVersion);
                ClearInFlightWorkpiece(false, actionVersion);
            }
            FinishM3Action(actionVersion, actionCompletion);
        }
    }

    /// <summary>
    /// 每趟新的机械手搬运在首次物理移动前设置一次绝对速度。
    /// 同一趟任务后续的Y/Z移动、X11下探重试、放料和回安全位复用该次设置，避免重复写九个Modbus寄存器。
    /// </summary>
    private async Task ConfigureManipulatorAbsSpeedAsync(
        CraneService manipulator, int manipulatorNo, string taskName, CancellationToken ct)
    {
        var speed = _cfg.GetManipulatorSpeed(manipulatorNo);
        Console.WriteLine($"[平衡引擎] [{taskName}] 按当前配置设置机械手{manipulatorNo}绝对速度");
        await manipulator.SetAbsSpeedAsync(
            speed.X.Speed, speed.X.Accel, speed.X.Decel,
            speed.Y.Speed, speed.Y.Accel, speed.Y.Decel,
            speed.Z.Speed, speed.Z.Accel, speed.Z.Decel, ct);
    }

    /// <summary>Z下降公式: 台面Z - Round[(d/2/zFactor1)+(d/2/zFactor2)] — 机械手取料/放料用</summary>
    private int Pz(int z, double d) =>
        z - (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));

    /// <summary>
    /// M3取得M821/M720两把位置锁后重新读取M720。
    /// 此时尚未取料：M720=0可正常放弃本趟；通信状态未知则有限重试，最终失败暂停引擎。
    /// 已经持件后的M720等待由ConfirmM720EmptyBeforePlaceAsync负责，两种语义不得合并。
    /// </summary>
    private async Task<bool> ConfirmM720AfterM3LocksAsync(CancellationToken ct)
    {
        const int maxAttempts = 3;
        const int retryDelayMs = 500;
        const string mc65Ip = "192.168.2.65";
        const int mc65Port = 9000;
        Exception? lastError = null;

        Console.WriteLine($"[平衡引擎] [M3] 已取得M821/M720锁，开始锁后复查M720（最多{maxAttempts}次）");
        OperationalLog.Info("锁后复查目标许可", "机械手3已取得两把位置锁，重新读取M720",
            ("信号名称", "M720放料允许"), ("最大尝试次数", maxAttempts.ToString()),
            ("重试间隔", $"{retryDelayMs}ms"), ("工件状态", "尚未取料"));

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                //缓存中已有连接且 IsConnected=true：直接返回现有对象，不建立新TCP连接。
                // 只有缓存没有连接或连接已失效：才创建并连接。
                _mc65 = await _mcCache.GetOrCreateAsync(mc65Ip, mc65Port, ct);
                var r = await ReadMc65MAlignedWordAsync(720, "M3锁后M720复查", ct);
                if (r.IntValues.Length < 1)
                    throw new InvalidOperationException("M3锁后M720复查失败: MC65返回字数不足");

                bool canPlace = (r.IntValues[0] & 1) != 0; // M720 = M720字bit0，1=允许放料
                if (canPlace)
                {
                    Console.WriteLine($"[平衡引擎] [M3] 锁后复查M720=1 ✓ 尝试={attempt}/{maxAttempts} rawM720=0x{r.IntValues[0]:X4}");
                    OperationalLog.Info("锁后目标许可确认通过", "M720仍允许放料，机械手3继续取料流程",
                        ("信号名称", "M720放料允许"), ("当前值", "1（允许）"),
                        ("尝试次数", $"{attempt}/{maxAttempts}"), ("PLC原始字", $"0x{r.IntValues[0]:X4}"));
                    return true;
                }

                Console.WriteLine($"[平衡引擎] [M3] 锁后复查M720=0，目标许可已失效，尚未取料，不重试信号值");
                OperationalLog.Warn("锁后目标暂不可用", "M720已变为0，机械手3尚未取料，本趟正常放弃",
                    ("信号名称", "M720放料允许"), ("当前值", "0（暂不允许）"),
                    ("尝试次数", $"{attempt}/{maxAttempts}"), ("PLC原始字", $"0x{r.IntValues[0]:X4}"));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                _mc65SnapshotValid = false;
                Console.WriteLine($"[平衡引擎] [M3] 锁后复查M720读取失败，第{attempt}/{maxAttempts}次：{ex.Message}");
                OperationalLog.Warn("锁后目标许可读取失败", "M720状态未知，机械手3尚未取料",
                    ("信号名称", "M720放料允许"), ("尝试次数", $"{attempt}/{maxAttempts}"),
                    ("异常", ex.Message), ("后续处理", attempt < maxAttempts ? $"{retryDelayMs}ms后重连重试" : "达到上限后暂停"));

                try
                {
                    await _mcCache.InvalidateAsync(mc65Ip, mc65Port);
                }
                catch (Exception invalidateEx)
                {
                    Console.WriteLine($"[平衡引擎] [M3] MC65失效清理异常，不中断锁后复查重试：{invalidateEx.Message}");
                    OperationalLog.Warn("MC65失效清理异常", "连接清理失败，但机械手3仍按既定次数重试M720",
                        ("尝试次数", $"{attempt}/{maxAttempts}"), ("异常", invalidateEx.Message));
                }
                finally
                {
                    _mc65 = null;
                }

                if (attempt < maxAttempts)
                {
                    Console.WriteLine($"[平衡引擎] [M3] MC65连接已失效，{retryDelayMs}ms后执行第{attempt + 1}/{maxAttempts}次锁后复查");
                    await Task.Delay(retryDelayMs, ct);
                }
            }
        }

        _paused = true;
        string message = $"机械手3取得M821/M720位置锁后连续{maxAttempts}次读取M720失败，目标状态未知，动平衡引擎已暂停。最后异常：{lastError?.Message}";
        Console.WriteLine($"[平衡引擎] [M3] ⚠ {message}");
        OperationalLog.Warn("锁后目标状态未知", "M720连续读取失败，动平衡引擎已暂停并等待人工检查通信",
            ("信号名称", "M720放料允许"), ("尝试次数", maxAttempts.ToString()),
            ("工件状态", "尚未取料"), ("位置锁", "finally释放M720、M821"),
            ("最后异常", lastError?.Message ?? "未知"));
        OnSafetyAlarm?.Invoke(message);
        throw new InvalidOperationException(message, lastError);
    }

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
    /// M3已经持件、Z升至安全高度并持有M821/M720位置锁时，M720=0仅表示目标暂时不可放料：
    /// 保持当前位置和锁，轮询等待M720恢复。通信读取失败或取消仍由外层按安全异常处理。
    /// </summary>
    private async Task ConfirmM720EmptyBeforePlaceAsync(CancellationToken ct)
    {
        if (_mc65?.IsConnected != true)
            throw new InvalidOperationException("M720二次确认失败: MC65未连接");

        int waitCount = 0;
        while (true)
        {
            var r = await ReadMc65MAlignedWordAsync(720, "M720二次确认", ct);
            if (r.IntValues.Length < 1)
                throw new InvalidOperationException("M720二次确认失败: MC65返回字数不足");

            bool canPlace = (r.IntValues[0] & 1) != 0; // M720 bit0, 1=允许放版
            if (canPlace)
            {
                if (waitCount > 0)
                    OperationalLog.Info("PLC放料许可恢复", "PLC已允许放料，继续原流程",
                        ("信号名称", "M720放料允许"), ("当前值", "1（允许）"),
                        ("累计等待", $"{waitCount * _cfg.Grinding.PollIntervalMs / 1000.0:F1} 秒"));
                return;
            }

            waitCount++;
            if (waitCount == 1 || waitCount % 10 == 0)
                OperationalLog.Warn("等待PLC放料许可", "PLC暂时不允许放料，机械手保持安全等待",
                    ("信号名称", "M720放料允许"), ("当前值", "0（暂不允许）"),
                    ("工件状态", "保持持件"), ("位置锁", "M821、M720保持"),
                    ("累计等待", $"{waitCount * _cfg.Grinding.PollIntervalMs / 1000.0:F1} 秒"),
                    ("PLC原始字", $"0x{r.IntValues[0]:X4}"));
            await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
        }
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
