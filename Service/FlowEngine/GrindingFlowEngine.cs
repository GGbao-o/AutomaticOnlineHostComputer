using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Logging;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 研磨自动流程引擎。
/// <para>
/// 启动后自动循环：扫描 4 台研磨机 → 从缓存取工件 → 天车 #5 去上料架取料
/// → 送料+握手 → 研磨机加工 → 下料。
/// </para>
/// <para>
/// 天车一次只能处理一个工件（充磁限制），工件按 FIFO 顺序串行处理，
/// 分配给优先级最高的空闲研磨机（ST701 > ST702 > ST703 > ST704）。
/// </para>
/// </summary>
public sealed class GrindingFlowEngine : IDisposable
{
    // ── 依赖注入 ──
    private readonly CraneConnectionCache _craneCache;    // 天车#5共享连接(复用主页面TCP, 避免双连接冲突)
    private readonly MotionConfig _cfg;                   // 运动参数配置(速度/Z公式系数/超时/轮询间隔/X11延时等)
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords; // 工位坐标(数据库machine表, 含XYZ+偏移量)
    private readonly IOperationalEventReporter _exceptionReporter;
    private readonly GrindingWorkpieceDisplayService _workpieceDisplay = new(); // ST709取料参数旁路显示，不参与业务准入
    // ── MC连接(共享McConnectionCache, 与平衡/后端引擎共用, 防重复TCP) ──
    //   MC65=192.168.2.65: 上料架(研磨上料架) M730(末位有板/允许取板) M731(取板完成) D200(2号位测长,读)
    //   MC64=192.168.2.64: 下料架(研磨下料架) M720(无板且允许放版) M721(放版完成,写)
    private readonly McConnectionCache _mcCache;
    private MitsubishiMcClient? _mc65;   // 上料架PLC
    private MitsubishiMcClient? _mc64;   // 下料架PLC
    private readonly CancellationTokenSource _engineCts = new(); // 引擎取消令牌
    private Task? _engineTask;       // 主循环Task
    private bool _disposed;          // 是否已Dispose
    private volatile bool _paused;   // 暂停标志(volatile: 主页面+引擎线程跨线程可见)
    private volatile bool _fastNextCycle; // UnloadFromGrinderAsync完成后设true, 缩短主循环延迟快速响应
    private int _cycleCount;         // 主循环轮次计数
    private readonly Dictionary<string, DateTime> _waitingLogAtUtc = new(StringComparer.Ordinal);
    private static readonly TimeSpan WaitingLogHeartbeat = TimeSpan.FromSeconds(10);

    /// <summary>天车信号量：保证一次只有一个研磨动作占用天车#5(充磁限制)。</summary>
    private readonly SemaphoreSlim _craneLock = new(1, 1);
    // 应急清除和主循环派发共用此门闩：拿到天车锁后必须在门闩内再次检查暂停状态，
    // 防止主循环已经越过轮询顶部的暂停判断时，又恰好启动一个新动作。
    private readonly SemaphoreSlim _grindingDispatchGate = new(1, 1);
    private readonly object _emergencyLock = new();
    private bool _craneLockHeldByFlow;
    private string _craneLockAction = string.Empty;
    private string _craneLockStation = string.Empty;
    private CancellationTokenSource? _grindingActionCts;
    private TaskCompletionSource<bool>? _grindingActionCompletion;
    private int _grindingActionVersion;

    // ═══════════════════════════════════════════════════════════════
    //  工件缓存 — FIFO队列 + 列表(UI显示数量)
    //   入队: 后天车DoUnload→短工件→EnqueueWorkpiece
    //   出队: 主循环步骤⑤→TryDequeueCache→ProcessWorkpieceAsync
    //   回退: 分配失败/磨石报警→RequeueWorkpiece放回队列头部
    // ═══════════════════════════════════════════════════════════════
    private readonly LinkedList<WorkpieceCache> _cacheQueue = new();
    private readonly List<WorkpieceCache> _cachedList = new();  // 用于UI显示CachedCount
    private readonly object _cacheLock = new();

    /// <summary>已缓存工件数量（UI 绑定）</summary>
    public int CachedCount
    {
        get { lock (_cacheLock) return _cachedList.Count; }
    }

    /// <summary>供状态页显示的只读缓存快照；不读取或修改FIFO队列。</summary>
    public WorkpieceCache[] GetCachedWorkpiecesSnapshot()
    {
        lock (_cacheLock) return _cachedList.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════
    //  4台研磨机上下文 — 分别跟踪状态/信号/PendingWorkpiece
    //   连接: 通过GrinderPoll注入共享PlcGrinderService(不自建, 避免西门子双连接)
    //   状态机: Idle→Loading→Machining→WaitingForUnload→Unloading→Idle
    // ═══════════════════════════════════════════════════════════════
    private readonly List<GrinderContext> _grinders;

    public IReadOnlyList<GrinderContext> Grinders => _grinders;

    /// <summary>GrinderPoll 注入共享的 PlcGrinderService（避免引擎另建连接导致双连接冲突）。</summary>
    public void SetGrinderService(string stationCode, PlcGrinderService svc)
    {
        TrySetGrinderService(stationCode, svc, out _);
    }

    /// <summary>
    /// 判断GrinderPoll是否允许替换研磨机共享连接。
    /// Loading/Unloading/WaitingForUnload都可能正在等待握手或准备下料, 外部线程不能替换/释放Svc。
    /// </summary>
    public bool CanReplaceGrinderService(string stationCode, out string reason)
    {
        reason = string.Empty;
        var g = _grinders.FirstOrDefault(x => x.StationCode == stationCode);
        if (g == null)
        {
            reason = $"未找到研磨机{stationCode}";
            return false;
        }

        if (g.State == GrinderState.Loading || g.State == GrinderState.Unloading || g.State == GrinderState.WaitingForUnload)
        {
            reason = $"{g.Name} 状态={g.State}, 可能正在握手/等待下料, 禁止外部替换共享连接";
            return false;
        }

        return true;
    }

    /// <summary>安全注入研磨机共享服务。关键状态下拒绝替换, 避免打断上/下料握手。</summary>
    public bool TrySetGrinderService(string stationCode, PlcGrinderService svc, out string reason)
    {
        if (!CanReplaceGrinderService(stationCode, out reason))
        {
            Console.WriteLine($"[GrindingEngine] ⚠ 拒绝替换共享服务: {reason}");
            return false;
        }

        var g = _grinders.First(x => x.StationCode == stationCode);
        g.Svc = svc;
        Console.WriteLine($"[GrindingEngine] [{g.Name}] 共享服务已注入");
        return true;
    }

    /// <summary>引擎是否正在运行</summary>
    public bool IsRunning => _engineTask != null && !_engineTask.IsCompleted;
    public bool IsPaused => _paused;

    /// <summary>研磨天车当前动作的只读展示快照；仅复制内存状态，不读取设备。</summary>
    public CraneTaskSnapshot GetCraneTaskSnapshot()
    {
        lock (_emergencyLock)
        {
            var target = _grinders.FirstOrDefault(g => string.Equals(g.StationCode, _craneLockStation, StringComparison.OrdinalIgnoreCase));
            var workpiece = target == null ? null : WorkpieceDisplaySnapshot.From(target.PendingWorkpiece);
            bool active = _craneLockHeldByFlow;
            return new CraneTaskSnapshot(active, _cfg.Grinding.CraneNo, $"研磨天车#{_cfg.Grinding.CraneNo}", workpiece,
                active ? (_craneLockAction + "中") : "空闲",
                active && _craneLockAction == "上料" ? "ST709" : _craneLockStation,
                active && _craneLockAction == "下料" ? "研磨下料架" : _craneLockStation,
                active && _paused, active && _paused ? "研磨流程已暂停，请人工确认现场" : string.Empty, DateTime.UtcNow);
        }
    }

    /// <summary>四台研磨机的只读展示快照；不读取PLC。</summary>
    public ProcessStationSnapshot[] GetGrinderStationSnapshots()
        => _grinders.Select(g => new ProcessStationSnapshot(g.StationCode, GrinderStateText(g.State),
            WorkpieceDisplaySnapshot.From(g.PendingWorkpiece), g.StateChangedAt, g.WpRecoveryNeeded)).ToArray();

    private static string GrinderStateText(GrinderState state) => state switch
    {
        GrinderState.Idle => "空闲",
        GrinderState.Loading => "上料中",
        GrinderState.Machining => "加工中",
        GrinderState.WaitingForUnload => "等待下料",
        GrinderState.Unloading => "下料中",
        _ => state.ToString()
    };
    /// <summary>研磨天车连续两次读到XYZ全零时通知主页面更新状态并弹窗。</summary>
    public Action<CraneZeroPositionAlarm>? OnCraneZeroPositionDetected;
    /// <summary>研磨流程进入人工确认暂停时通知主页面。</summary>
    public Action<string>? OnSafetyAlarm;

    private int BeginGrindingAction(CancellationTokenSource cts, TaskCompletionSource<bool> completion,
        string action, string stationCode)
    {
        lock (_emergencyLock)
        {
            _craneLockHeldByFlow = true;
            _craneLockAction = action;
            _craneLockStation = stationCode;
            _grindingActionCts = cts;
            _grindingActionCompletion = completion;
            return ++_grindingActionVersion;
        }
    }

    private bool ReleaseRecordedCraneLock(string reason, int? actionVersion = null)
    {
        lock (_emergencyLock)
        {
            if (actionVersion.HasValue && actionVersion.Value != _grindingActionVersion)
            {
                Console.WriteLine($"[GrindingEngine] {reason}: 旧动作版本已失效, 不释放当前天车锁");
                return false;
            }
            if (!_craneLockHeldByFlow) return false;
            _craneLockHeldByFlow = false;
            _craneLockAction = string.Empty;
            _craneLockStation = string.Empty;
        }

        _craneLock.Release();
        Console.WriteLine($"[GrindingEngine] {reason}: _craneLock已释放");
        return true;
    }

    private bool IsGrindingActionCurrent(int actionVersion)
    {
        lock (_emergencyLock) return actionVersion == _grindingActionVersion;
    }

    private void FinishGrindingAction(int actionVersion, TaskCompletionSource<bool> completion)
    {
        lock (_emergencyLock)
        {
            if (ReferenceEquals(_grindingActionCompletion, completion)) _grindingActionCompletion = null;
            if (actionVersion == _grindingActionVersion) _grindingActionCts = null;
        }
        // 清完动作引用后再通知应急流程，确保它继续清状态时旧finally已经没有后续写入。
        completion.TrySetResult(true);
    }

    private Task? CancelGrindingActionForEmergency(string stationCode, List<string> logs)
    {
        lock (_emergencyLock)
        {
            if (!_craneLockHeldByFlow || !string.Equals(_craneLockStation, stationCode, StringComparison.OrdinalIgnoreCase))
                return null;

            var completion = _grindingActionCompletion?.Task;
            try
            {
                if (_grindingActionCts != null)
                {
                    _grindingActionCts.Cancel();
                    logs.Add($"研磨天车当前动作={_craneLockAction} 已发送取消");
                }
                else
                {
                    logs.Add($"研磨天车当前动作={_craneLockAction} 取消令牌已结束, 等待应急释放记录锁");
                }
            }
            catch (ObjectDisposedException) { }
            _grindingActionCts = null;
            _grindingActionVersion++;
            return completion ?? Task.CompletedTask;
        }
    }

    private string CraneLockText()
    {
        lock (_emergencyLock)
        {
            return _craneLockHeldByFlow
                ? $"已占用 动作={_craneLockAction} 研磨机={_craneLockStation}"
                : $"未占用 CurrentCount={_craneLock.CurrentCount}";
        }
    }

    // 天车编号 → 站号映射 (3号天车为2号线前天车, 坐标/偏移站号是ST104)
    private static readonly Dictionary<int, string> CraneNoToStation = new()
    {
        [1] = "ST901", [2] = "ST902", [3] = "ST104", [4] = "ST904", [5] = "ST905",
    };

    /// <summary>天车 X/Y/Z 偏移量（补偿机械误差，默认 0）</summary>
    private double _craneOffsetX, _craneOffsetY, _craneOffsetZ;

    /// <param name="mcc">MC共享连接缓存(与平衡/后端引擎共用, 防同一PLC重复TCP连接)</param>
    public GrindingFlowEngine(CraneConnectionCache craneCache, MotionConfig cfg,
        Dictionary<string, MachineManagementRowVm> stationCoords, McConnectionCache mcc,
        IOperationalEventReporter exceptionReporter)
    {
        _craneCache = craneCache;
        _cfg = cfg;
        _stationCoords = stationCoords;
        _mcCache = mcc;
        _exceptionReporter = exceptionReporter;

        _grinders = new List<GrinderContext>
        {
            new("ST701", "研磨机1(新代)",   PlcGrinderService.GrinderType.TypeB),
            new("ST702", "研磨机2(新代)",   PlcGrinderService.GrinderType.TypeB),
            new("ST703", "研磨机3(西门子)", PlcGrinderService.GrinderType.TypeA),
            new("ST704", "研磨机4(西门子)", PlcGrinderService.GrinderType.TypeA),
        };

        // 读取天车的 X/Y/Z 偏移量（从 machine 表的 x_dis/y_dis/z_dis）
        if (CraneNoToStation.TryGetValue(_cfg.Grinding.CraneNo, out var craneStation)
            && _stationCoords.TryGetValue(craneStation, out var craneRow))
        {
            _craneOffsetX = craneRow.XOffset;
            _craneOffsetY = craneRow.YOffset;
            _craneOffsetZ = craneRow.ZOffset;
            Console.WriteLine($"[GrindingEngine] 天车偏移量 ({craneStation}): X={_craneOffsetX} Y={_craneOffsetY} Z={_craneOffsetZ}");
        }
        else
        {
            Console.WriteLine($"[GrindingEngine] ⚠ 未找到天车#{_cfg.Grinding.CraneNo}的偏移量，使用默认 0");
        }

        Console.WriteLine("[GrindingEngine] 引擎实例已创建（4台研磨机上下文）");
    }

    /// <summary>对目标坐标应用天车偏移（-1=不动该轴，不加偏移）</summary>
    private int ApplyOffsetX(int target) => target == -1 ? -1 : target + (int)Math.Round(_craneOffsetX);
    private int ApplyOffsetY(int target) => target == -1 ? -1 : target + (int)Math.Round(_craneOffsetY);
    private int ApplyOffsetZ(int target) => target == -1 ? -1 : target + (int)Math.Round(_craneOffsetZ);

    // ═══════════════════════════════════════════════════════════════
    //  公开控制
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启动研磨自动流程</summary>
    public void Start()
    {
        if (IsRunning)
        {
            if (_paused)
            {
                Resume();
                return;
            }
            Console.WriteLine("[GrindingEngine] 引擎已在运行，跳过重复启动");
            return;
        }
        using var logScope = EngineLogRouter.BeginScope(EngineLogRouter.Grinding);
        _paused = false;
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  [GrindingEngine] 研磨自动流程引擎启动");
        Console.WriteLine($"  天车=#{_cfg.Grinding.CraneNo}  安全高度={_cfg.Grinding.SafeZHeight}mm");
        Console.WriteLine($"  Z公式系数: zFactor1={_cfg.Grinding.ZFactor1} zFactor2={_cfg.Grinding.ZFactor2}");
        Console.WriteLine($"  研磨机优先级: ST701 > ST702 > ST703 > ST704");
        Console.WriteLine("══════════════════════════════════════════");
        _engineTask = Task.Run(() => EngineLoopAsync(_engineCts.Token));
    }

    /// <summary>停止引擎</summary>
    public void Stop()
    {
        Console.WriteLine("[GrindingEngine] ▶ 停止引擎...");
        _engineCts.Cancel();
    }

    /// <summary>立即暂停（急停天车 D4518）</summary>
    /// <remarks>
    /// TODO: 当前实现发送真实急停信号, 暂停后需人工复位天车。
    /// 后续可考虑改为软暂停(仅设 _paused=true, 不发送急停),
    /// 或增加"软暂停"和"急停"两个独立按钮。
    /// </remarks>
    public async Task PauseAsync()
    {
        _paused = true;
        Console.WriteLine("[GrindingEngine] ⏸ 暂停！发送天车急停...");
        try
        {
            var crane = _craneCache.GetOrCreateService(_cfg.Grinding.CraneNo);
            if (!crane.IsConnected) await crane.ConnectAsync();
            await crane.EmergencyStopAsync();
            Console.WriteLine("[GrindingEngine] ⏸ 天车急停已发送，引擎暂停");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GrindingEngine] ⚠ 急停发送异常：{ex.Message}（引擎已标记暂停）");
        }
    }

    /// <summary>
    /// 坐标全零保护使用软暂停：只阻止新任务派发，不发送D4518急停，
    /// 因为校验发生在天车新动作开始之前，不应额外改变现场设备状态。
    /// </summary>
    public void PauseForCraneZeroPosition()
    {
        _paused = true;
        Console.WriteLine("[GrindingEngine] ⏸ 研磨天车XYZ连续全零，已软暂停新任务派发");
    }

    /// <summary>恢复运行</summary>
    public void Resume()
    {
        _paused = false;
        Console.WriteLine("[GrindingEngine] ▶ 引擎恢复运行");
    }

    // ═══════════════════════════════════════════════════════════════
    //  工件缓存
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 将工件写入FIFO缓存(OnGrindingRackPlaced回调→Line1Rear/M3Flow放料后调用)。
    /// 入队顺序=传送带顺序: 先放的先到3号位, 先被研磨天车取走。
    /// </summary>
    public void EnqueueWorkpiece(double diameter, int boreType, double length)
    {
        var wp = new WorkpieceCache { Diameter = diameter, BoreType = boreType, Length = length };
        EnqueueWorkpiece(wp);
    }

    /// <summary>
    /// 将完整工件信息写入FIFO缓存。保留版号/序号, 异常恢复时能和主页面任务对应。
    /// 这个就是写入研磨上料架内存数据的
    /// </summary>
    public void EnqueueWorkpiece(WorkpieceCache wp)
    {
        lock (_cacheLock)
        {
            _cacheQueue.AddLast(wp); // FIFO入队: 后放的工件排在队尾, 对应传送带后到
            _cachedList.Add(wp);     // 同步UI显示数量
        }
        Console.WriteLine($"[GrindingEngine] 📥 工件入缓存 {wp.IdentityText} 直径={wp.Diameter} 长度={wp.Length}  队列长度={CachedCount}");
    }

    /// <summary>将工件放回缓存队头（分配失败回退）。工件仍在ST709/M730末端, 必须保持下一次仍先取它。</summary>
    private void RequeueWorkpiece(WorkpieceCache wp)
    {
        lock (_cacheLock)
        {
            _cacheQueue.AddFirst(wp);      // 回到队头, 避免物理3号位工件与缓存顺序错位
            _cachedList.Insert(0, wp);     // UI列表顺序与真实FIFO保持一致
        }
        Console.WriteLine($"[GrindingEngine] ↩ 工件放回缓存 {wp.IdentityText} d={wp.Diameter}  队列长度={CachedCount}");
    }

    /// <summary>清空缓存</summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cacheQueue.Clear();
            _cachedList.Clear();
        }
        Console.WriteLine($"[GrindingEngine] 🗑 缓存已清空  队列长度=0");
    }

    public string GetGrindingEmergencyInfo(string target)
    {
        target = NormalizeGrindingTarget(target);
        string cacheText;
        lock (_cacheLock)
        {
            cacheText = _cacheQueue.First?.Value.IdentityText ?? "无";
        }

        var g = _grinders.FirstOrDefault(x => x.StationCode == target);
        if (g == null)
        {
            return
                $"[研磨应急诊断] 目标={target}\n" +
                $"ST709上料缓存队头={cacheText}; 队列长度={CachedCount}\n" +
                $"研磨天车锁={CraneLockText()}\n" +
                "提示: ST709只清上位机FIFO队头, 不写PLC信号。";
        }

        return
            $"[研磨应急诊断] 目标={g.StationCode} {g.Name}\n" +
            $"状态={g.State}; Pending={g.PendingWorkpiece?.IdentityText ?? "无"}; WpRecoveryNeeded={g.WpRecoveryNeeded}\n" +
            $"信号: 请求数据={g.LastRequestData} 联机={(g.GrinderType == PlcGrinderService.GrinderType.TypeA ? g.LastOnlineMode.ToString() : "不适用")} 请求下料={(g.LastR7304 != 0 || ((g.LastDi >> 12) & 1) == 1)} 加工中={g.LastMachining} 门开={g.LastDoorOpen} DI=0x{g.LastDi:X4} R7304={g.LastR7304}\n" +
            $"研磨天车锁={CraneLockText()}\n" +
            "提示: 应急处理会先清研磨机PLC输出/参数, 成功后清Pending和状态; 失败时需要二次确认是否仅清软件。";
    }

    /// <summary>
    /// 仅复位目标研磨机阻塞新上料候选的内存字段。不会写PLC，也不会清信号、恢复标记、动作账本或锁。
    /// 仅适用于PLC已正常请求数据且研磨天车尚未派发的人工恢复场景。
    /// </summary>
    public string ResetGrindingMemoryState(string stationCode)
    {
        var grinder = _grinders.FirstOrDefault(g =>
            string.Equals(g.StationCode, stationCode, StringComparison.OrdinalIgnoreCase));
        if (grinder == null) return $"未找到研磨机 {stationCode}";

        string oldState = GrinderStateText(grinder.State);
        string oldWorkpiece = grinder.PendingWorkpiece?.IdentityText ?? "无";
        grinder.State = GrinderState.Idle;
        grinder.PendingWorkpiece = null;

        string result = $"{grinder.StationCode} 内存状态已复位：状态 {oldState}→空闲，Pending工件 {oldWorkpiece}→无。未写PLC，未清信号、恢复标记、快照、账本或锁。";
        Console.WriteLine($"[GrindingEngine] {result}");
        return result;
    }

    /// <summary>
    /// 已暂停研磨天车动作的人工账本结案。只在现场已确认工件真实位置后调用；
    /// 不发送天车、磁铁、PLC/CNC命令，也不自动恢复研磨引擎。四种结论只改变能够
    /// 被现场事实证明的软件账本，避免把“已在目标”错误清成Idle或把“仍在来源”丢失。
    /// </summary>
    public string ResolveManualAction(string operationId, FlowActionManualResolution resolution)
    {
        if (!FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot) ||
            !snapshot.FlowScope.StartsWith("研磨天车", StringComparison.Ordinal))
            return $"研磨动作账本不存在或不是待结案的研磨天车动作: {operationId}";

        bool isLoad = snapshot.FlowScope == "研磨天车上料";
        string grinderCode = isLoad ? snapshot.Target : snapshot.Source;
        var grinder = _grinders.FirstOrDefault(x => x.StationCode == grinderCode);
        if (grinder == null)
            return $"研磨动作={operationId}的研磨机站号无效: {grinderCode}";
        WorkpieceCache? wp = grinder.PendingWorkpiece;
        if (!wp.HasValue)
            return $"研磨动作={operationId}没有PendingWorkpiece；请先补录工件后再结案，不能凭快照直接清除。";

        _paused = true;
        string result;
        switch (resolution)
        {
            case FlowActionManualResolution.StillAtSource when isLoad:
                // 只有人工确认工件确实仍在ST709时才能回FIFO；Pending和研磨机Loading一并撤销。
                RequeueWorkpiece(wp.Value);
                grinder.PendingWorkpiece = null;
                grinder.State = GrinderState.Idle;
                grinder.WpRecoveryNeeded = false;
                result = "确认仍在ST709：工件已回研磨FIFO队头，研磨机Pending已清空并回Idle";
                break;
            case FlowActionManualResolution.StillAtSource:
                // 下料来源是研磨机，保留Pending让后续专用下料应急/人工PLC交接有身份依据。
                grinder.State = GrinderState.WaitingForUnload;
                result = $"确认仍在{grinder.StationCode}：保留Pending，状态回WaitingForUnload，未写下料完成信号";
                break;
            case FlowActionManualResolution.OnCarrier:
                grinder.State = isLoad ? GrinderState.Loading : GrinderState.Unloading;
                result = "确认工件在研磨天车：保留Pending和在途状态，禁止自动派发/自动重试";
                break;
            case FlowActionManualResolution.AtTargetPendingHandoff:
                // 上料目标仍须人工确认PLC已接收LoadDone；下料目标仍须人工确认M721/UnloadDone。
                grinder.State = isLoad ? GrinderState.Loading : GrinderState.Unloading;
                result = $"确认工件已在目标{snapshot.Target}待交接：保留Pending和{grinder.State}，不得伪造PLC/CNC完成位";
                break;
            case FlowActionManualResolution.RemovedManually:
                grinder.PendingWorkpiece = null;
                grinder.State = GrinderState.Idle;
                grinder.WpRecoveryNeeded = false;
                result = "确认工件已人工移走：Pending已清空、研磨机回Idle；现场仍须自行完成设备侧复位";
                break;
            default:
                return $"不支持的人工结论: {resolution}";
        }

        grinder.StateChangedAt = DateTime.UtcNow;
        FlowActionManualRegistry.Remove(operationId);
        string message = $"[GrindingEngine] [人工账本结案] 动作={operationId}; 工件={wp.Value.IdentityText}; {result}; 研磨引擎保持暂停。";
        Console.WriteLine(message);
        return message;
    }

    public async Task<string> EmergencyClearGrindingAsync(string target, bool skipDeviceClear,
        bool resumeAfterClear, CancellationToken ct = default)
    {
        target = NormalizeGrindingTarget(target);
        if (target == "ST709")
        {
            _paused = true; // 只做应急软暂停，不发送天车急停，不改变正常引擎条件。
            WorkpieceCache? removed = null;
            await _grindingDispatchGate.WaitAsync(ct);
            try
            {
                // 如果已有研磨天车动作，队头可能已经被该动作取走；此时继续删除会误删下一块板。
                lock (_emergencyLock)
                {
                    if (_craneLockHeldByFlow)
                    {
                        string failure = $"研磨应急失败: ST709存在在途天车动作={_craneLockAction}, 目标研磨机={_craneLockStation}; 未清FIFO，研磨引擎保持暂停";
                        Console.WriteLine($"[GrindingEngine] [研磨应急] {failure}");
                        return failure;
                    }
                }

                lock (_cacheLock)
                {
                    if (_cacheQueue.First != null)
                    {
                        removed = _cacheQueue.First.Value;
                        _cacheQueue.RemoveFirst();
                        if (_cachedList.Count > 0) _cachedList.RemoveAt(0);
                    }
                }
            }
            finally
            {
                _grindingDispatchGate.Release();
            }

            if (resumeAfterClear && IsRunning) _paused = false;

            string msg = $"[GrindingEngine] [研磨应急] {DateTime.Now:yyyy-MM-dd HH:mm:ss} ST709上料FIFO队头={(removed?.IdentityText ?? "无")} 已清; 队列长度={CachedCount}; 未写PLC信号; 研磨引擎={(_paused ? "保持暂停" : "已恢复派发")}";
            Console.WriteLine(msg);
            return msg;
        }

        var g = _grinders.FirstOrDefault(x => x.StationCode == target);
        if (g == null) return $"未知研磨应急目标: {target}";

        var emergencyLogs = new List<string>();
        _paused = true; // 应急期间先停新派发；这里只是软暂停，不发送天车急停指令。
        Console.WriteLine($"[GrindingEngine] [研磨应急] 开始处理 {g.StationCode}/{g.Name}, 新派发已软暂停, resumeAfterClear={resumeAfterClear}");

        Task? actionCompletion;
        await _grindingDispatchGate.WaitAsync(ct);
        try
        {
            // 与拿到天车锁后的二次检查串行，保证不会漏掉“刚要启动”的研磨动作。
            _paused = true;
            actionCompletion = CancelGrindingActionForEmergency(g.StationCode, emergencyLogs);
        }
        finally
        {
            _grindingDispatchGate.Release();
        }

        if (actionCompletion != null)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(6), ct);
            if (await Task.WhenAny(actionCompletion, timeout) != actionCompletion)
            {
                ct.ThrowIfCancellationRequested();
                emergencyLogs.Add("旧动作6秒内未退出, 保持暂停且不清设备/软件状态、不释放天车锁");
                string timeoutMessage = $"[GrindingEngine] [研磨应急] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {g.StationCode}/{g.Name}; {string.Join("; ", emergencyLogs)}; 请确认设备通信后重试应急";
                Console.WriteLine(timeoutMessage);
                return timeoutMessage;
            }

            await actionCompletion;
            emergencyLogs.Add("旧动作=已确认退出");
        }
        else
        {
            emergencyLogs.Add("当前动作=无匹配在途动作");
        }

        string deviceLog;
        if (skipDeviceClear)
        {
            deviceLog = "设备寄存器=用户确认跳过清零, 仅清上位机软件状态";
        }
        else
        {
            if (g.Svc == null)
            {
                string failure = $"设备侧清零失败: {g.Name} 通信服务未注入或未连接\n{string.Join("; ", emergencyLogs)}；未清上位机软件状态、未释放记录锁，研磨引擎保持暂停。请确认是否仅清上位机软件状态。";
                Console.WriteLine($"[GrindingEngine] [研磨应急] {g.StationCode}/{g.Name} {failure.Replace(Environment.NewLine, " ")}");
                return failure;
            }
            try
            {
                await g.Svc.ClearEmergencyRegistersAsync(ct);
                deviceLog = g.GrinderType == PlcGrinderService.GrinderType.TypeA
                    ? "设备寄存器=西门子40011~40014已清零"
                    : "设备寄存器=新代R7311~R7318已清零";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string failure = $"设备侧清零失败: {ex.Message}\n{string.Join("; ", emergencyLogs)}；未清上位机软件状态、未释放记录锁，研磨引擎保持暂停。请确认是否仅清上位机软件状态。";
                Console.WriteLine($"[GrindingEngine] [研磨应急] {g.StationCode}/{g.Name} {failure.Replace(Environment.NewLine, " ")}");
                return failure;
            }
        }

        var oldState = g.State;
        var oldWp = g.PendingWorkpiece;
        // 作废应急开始前已经发出的扫描；否则旧扫描晚返回会把刚清掉的信号快照重新写回。
        Interlocked.Increment(ref g.SignalScanVersion);
        g.State = GrinderState.Idle;
        g.PendingWorkpiece = null;
        g.WpRecoveryNeeded = false;
        g.LastRequestData = false;
        g.LastMachining = false;
        g.LastDoorOpen = false;
        g.LastOnlineMode = false;
        g.LastDi = 0;
        g.LastR7304 = 0;
        g.StateChangedAt = DateTime.UtcNow;

        bool releasedCrane = false;
        lock (_emergencyLock)
        {
            if (_craneLockHeldByFlow && _craneLockStation == g.StationCode)
                releasedCrane = true;
        }
        if (releasedCrane)
            ReleaseRecordedCraneLock($"{g.Name}研磨应急");

        if (resumeAfterClear && IsRunning)
        {
            _paused = false;
            emergencyLogs.Add("研磨引擎=已恢复派发");
        }
        else
        {
            emergencyLogs.Add("研磨引擎=保持人工暂停");
        }

        string actionLog = string.Join("; ", emergencyLogs);
        string message = $"[GrindingEngine] [研磨应急] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {g.StationCode}/{g.Name} 原状态={oldState} 工件={oldWp?.IdentityText ?? "无"}; {actionLog}; {deviceLog}; 软件状态=Idle/Pending清空; 天车锁={(releasedCrane ? "已释放" : "未释放(非当前研磨动作持有)")}";
        Console.WriteLine(message);
        return message;
    }

    private static string NormalizeGrindingTarget(string target) => (target ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>尝试从缓存取一个工件（非阻塞）</summary>
    private bool TryDequeueCache(out WorkpieceCache wp)
    {
        lock (_cacheLock)
        {
            if (_cacheQueue.First != null)
            {
                wp = _cacheQueue.First.Value;
                _cacheQueue.RemoveFirst();
                if (_cachedList.Count > 0) _cachedList.RemoveAt(0);
                return true;
            }
        }
        wp = default;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  主循环
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 引擎主循环（后台 Task 运行）。
    /// <para>
    /// 调度策略（每 500ms 一轮）：
    ///   ① 扫描 4 台研磨机状态
    ///   ② 卡死检测（Loading/WaitingForUnload/Unloading 按各自观察时间超时 → 暂停并保留状态）
    ///   ③ 加工监视（Machining + 请求下料=1 → WaitingForUnload）
    ///   ④ 【优先】上料（Idle+请求数据=1 + 天车空闲 → ST709 取料送研磨机）
    ///   ⑤ 【回退】下料（上料不可派发时，WaitingForUnload + 天车空闲 → 取料放到 ST710）
    /// </para>
    /// <para>
    /// 设计要点：
    ///   - 上料优先于下料：上料不可派发时才尝试下料，保持研磨机的供料节拍
    ///   - 天车一次只做一件事（_craneLock SemaphoreSlim），但多台研磨机可同时加工
    ///   - 缓存为空或研磨机吞不下时，工件自动放回队列 FIFO 等待
    /// </para>
    /// </summary>
    private async Task EngineLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ── 暂停检查 ──────────────────────────────────────────
            if (_paused)
            {
                await Task.Delay(500, ct);
                continue;
            }

            try
            {
                // ═══════════════════════════════════════════════════════════
                //  ⓪ MC首次连接 + 定期重连(每20轮, 与平衡引擎一致)
                //     McConnectionCache内部按key(IP:port)去重, 共享实例不会重复建TCP
                // ═══════════════════════════════════════════════════════════
                if (_cycleCount == 1 || _cycleCount % 20 == 1)
                {
                    // MC65 上料架 (192.168.2.65:9000): 读M730+读D200+写M731
                    if (_mc65 == null || !_mc65.IsConnected)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(5000);
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                            _mc65 = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, linked.Token);
                            Console.WriteLine("[GrindingEngine] MC65 ✓ (192.168.2.65:9000, 上料架:读M730/D200 + 写M731)");
                        }
                        catch (Exception ex) { Console.WriteLine($"[GrindingEngine] MC65连接失败: {ex.Message}, 下轮重试"); }
                    }
                    // MC64 下料架 (192.168.2.64:9000): 读M720允许放版 + 写M721放版完成
                    if (_mc64 == null || !_mc64.IsConnected)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(5000);
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                            _mc64 = await _mcCache.GetOrCreateAsync("192.168.2.64", 9000, linked.Token);
                            Console.WriteLine("[GrindingEngine] MC64 ✓ (192.168.2.64:9000, 下料架:读M720 + 写M721)");
                        }
                        catch (Exception ex) { Console.WriteLine($"[GrindingEngine] MC64连接失败: {ex.Message}, 下轮重试"); }
                    }
                }

                // ═══════════════════════════════════════════════════════════
                //  ① 扫描 4 台研磨机
                // ═══════════════════════════════════════════════════════════
                await ScanAllGrindersAsync(ct);

                var summary = string.Join(" | ", _grinders.Select(g =>
                {
                    string extra = "";
                    if (g.State == GrinderState.Machining)
                    {
                        extra = g.LastMachining ? ",加工中" : ",加工完";
                        if (g.LastR7304 != 0) extra += "+请求下料";
                    }
                    if (g.HasGrindStoneAlarm)
                        extra += $",磨石{(g.LastGrindStone1Alarm ? "1" : "2")}报警/停止自动分配";
                    if (g.LastDoorOpen && (g.State == GrinderState.Loading || g.State == GrinderState.Machining || g.State == GrinderState.WaitingForUnload))
                        extra += ",门开";
                    string conn = g.Svc?.IsConnected == true ? "✓" : "✗";
                    return $"{g.StationCode}:{g.State}{extra}({conn})";
                }));
                if (_cycleCount % 10 == 1) Console.WriteLine($"[GrindingEngine] 扫描 缓存={CachedCount} | {summary}");

                // ═══════════════════════════════════════════════════════════
                //  ② 卡死检测(按上料中/等待下料/下料中的独立分钟配置)
                //    安全第一: 现场运动可能很慢, 这里只做“暂停+保留状态”, 绝不强制Idle/清缓存。
                //    研磨上下料存在工件在天车/机台/下料架三种物理位置, 超时不能推断工件已经安全。
                // ═══════════════════════════════════════════════════════════
                bool pausedByTimeout = false;
                foreach (var g in _grinders)
                {
                    TimeSpan stateTimeout = GetStateTimeout(g.State);
                    if (stateTimeout > TimeSpan.Zero && DateTime.UtcNow - g.StateChangedAt > stateTimeout)
                    {
                        Console.WriteLine($"[GrindingEngine] ⚠ [{g.Name}] 状态={g.State} 超过安全观察时间 {stateTimeout.TotalMinutes:F0}分钟，暂停等待人工确认");
                        Console.WriteLine($"[GrindingEngine] ⚠ [{g.Name}] 保留 PendingWorkpiece={g.PendingWorkpiece?.IdentityText ?? "null"}，不回Idle、不清缓存，避免慢动作时数据/现场断开");
                        g.StateChangedAt = DateTime.UtcNow;
                        _paused = true;
                        OnSafetyAlarm?.Invoke($"{g.Name}状态={g.State}超过安全观察时间{stateTimeout.TotalMinutes:F0}分钟。研磨引擎已暂停，PendingWorkpiece={g.PendingWorkpiece?.IdentityText ?? "null"}，请人工确认现场。");
                        pausedByTimeout = true;
                    }
                }
                if (pausedByTimeout) continue; // 本轮不再分配新上/下料任务, 等人工确认现场后Resume。

                // ═══════════════════════════════════════════════════════════
                //  ③ 加工监视：Machining + PLC请求下料=1 → WaitingForUnload
                // ═══════════════════════════════════════════════════════════
                _cycleCount++;
                foreach (var g in _grinders)
                {
                    if (g.State == GrinderState.Machining)
                    {
                        bool reqUnload = g.LastR7304 != 0;
                        if (reqUnload || _cycleCount % 10 == 0)
                        {
                            Console.WriteLine($"[GrindingEngine] [{g.Name}] ③ 检测请求下料 bit12={(reqUnload ? 1 : 0)} 加工中={(g.LastMachining ? 1 : 0)}" +
                                (reqUnload ? " → 加工完成！" : $" → 等待加工完成...(第{_cycleCount}轮)"));
                        }
                        if (reqUnload)
                        {
                            Console.WriteLine($"[GrindingEngine] [{g.Name}] 🚚 请求下料！加工完成，标记等待下料");
                            g.State = GrinderState.WaitingForUnload;
                            g.StateChangedAt = DateTime.UtcNow;
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════
                //  ④ 优先：上料；只有未能启动上料时才进入下料回退。
                // ═══════════════════════════════════════════════════════════
                bool loadDispatched = await TryDispatchLoadAsync(ct);
                if (!loadDispatched)
                {
                    // ═══════════════════════════════════════════════════════════
                    //  ⑤ 回退：下料（WaitingForUnload + 天车空闲 → 取料放ST710研磨机下料架）
                    //  下料架MC64: M720=1同时表示无板且允许天车放版
                    //  没有上料可派发时，优先卸出成品以腾出下一轮产能。
                    // ═══════════════════════════════════════════════════════════
                foreach (var g in _grinders)
                {
                    if (g.State == GrinderState.WaitingForUnload)
                    {
                        // ── 工件数据丢失(引擎重启后) → 标记WpRecoveryNeeded等人工确认, 不强制Idle ──
                        if (g.PendingWorkpiece == null)
                        {
                            if (!g.WpRecoveryNeeded)
                            {
                                g.WpRecoveryNeeded = true;
                                Console.WriteLine($"[GrindingEngine] [{g.Name}] ⚠⚠⚠ WaitingForUnload但PendingWorkpiece=null！(引擎重启丢失数据),需人工确认工件参数方可下料");
                            }
                            continue; // 不强制Idle, 保留WaitingForUnload等人工确认
                        }
                        else if (g.WpRecoveryNeeded)
                        {
                            // PendingWorkpiece已恢复(人工确认后注入) → 清除恢复标记
                            g.WpRecoveryNeeded = false;
                            Console.WriteLine($"[GrindingEngine] [{g.Name}] PendingWorkpiece已恢复, WpRecoveryNeeded清除");
                        }
                        // ── 检查下料架MC64: M720=1表示无板且允许放版(不满足则等下轮, 不破坏研磨机状态) ──
                        bool unloadRackOk = false; // MC64未连接/读失败时保守等待, 不允许盲放到ST710。
                        if (_mc64?.IsConnected == true)
                        {
                            try
                            {
                                var r = await _mc64.ReadMAlignedWordAsync(720, 1, ct);
                                unloadRackOk = r.IntValues.Length > 0 && (r.IntValues[0] & 1) != 0;
                                if (!unloadRackOk && _cycleCount % 10 == 1)
                                    Console.WriteLine($"[GrindingEngine] [{g.Name}] ⏳ 下料架不可用(MC64 M720=0), 等待...");
                            }
                            catch (Exception ex)
                            {
                                unloadRackOk = false;
                                Console.WriteLine($"[GrindingEngine] [{g.Name}] 读取下料架MC64 M720失败, 已失效连接等待重连: {ex.Message}");
                                await _mcCache.InvalidateAsync("192.168.2.64", 9000);
                                _mc64 = null;
                            }
                        }
                        else if (_cycleCount % 10 == 1)
                        {
                            Console.WriteLine($"[GrindingEngine] [{g.Name}] ⏳ 下料架MC64未连接, 等待连接恢复后再下料...");
                        }
                        if (!unloadRackOk) continue; // 下料架不可用→等下轮

                        if (await _craneLock.WaitAsync(0, ct))
                        {
                            bool dispatchGateHeld = false;
                            try
                            {
                                await _grindingDispatchGate.WaitAsync(ct);
                                dispatchGateHeld = true;
                                // 应急可能在本轮顶部暂停检查之后发生，拿到天车锁后必须再次确认。
                                if (_paused)
                                {
                                    _craneLock.Release();
                                    Console.WriteLine($"[GrindingEngine] [{g.Name}] 应急/暂停已生效, 放弃本次下料派发并释放天车锁");
                                }
                                else if (!await EnsureGrindingCranePositionReadyAsync("研磨下料任务派发前", ct))
                                {
                                    // 尚未改变研磨机状态或登记动作；保留原工件身份等待人工处理。
                                    _craneLock.Release();
                                }
                                else
                                {
                                    var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                    int actionVersion = BeginGrindingAction(actionCts, completion, "下料", g.StationCode);
                                    Console.WriteLine($"[GrindingEngine] [{g.Name}] 🚚 开始下料！天车锁已获取 {g.PendingWorkpiece?.IdentityText ?? "版号=未知"}");
                                    g.State = GrinderState.Unloading;
                                    g.StateChangedAt = DateTime.UtcNow;
                                    // 下料动作和完成信号必须在派发门闩内一起登记，避免应急漏等旧动作。
                                    _ = UnloadFromGrinderAsync(g, actionVersion, actionCts, completion);
                                }
                            }
                            finally
                            {
                                if (dispatchGateHeld) _grindingDispatchGate.Release();
                                else _craneLock.Release(); // 等待派发门闩时取消，不能遗留已取得的天车锁。
                            }
                        }
                        else
                        {
                            if (ShouldLogWaiting($"crane-busy:{g.StationCode}"))
                                Console.WriteLine($"[GrindingEngine] [{g.Name}] ⏳ 等待下料但天车锁忙，排队中...");
                        }
                    }
                }
                }

                // ── 下料刚完成→缩短延迟快速响应上料; 否则正常500ms ──
                int delay = _fastNextCycle ? 50 : _cfg.Grinding.PollIntervalMs;
                _fastNextCycle = false;
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[GrindingEngine] ✘ 主循环异常：{ex.GetType().Name} — {ex.Message}");
                await Task.Delay(2000, ct);
            }
        }
        Console.WriteLine("[GrindingEngine] 引擎已停止");
    }

    /// <summary>按研磨机当前中间状态读取独立的观察时间；非中间状态不参与超时暂停。</summary>
    private TimeSpan GetStateTimeout(GrinderState state) => state switch
    {
        GrinderState.Loading => ToSafeStateTimeout(_cfg.Grinding.LoadingTimeoutMinutes, 10),
        GrinderState.WaitingForUnload => ToSafeStateTimeout(_cfg.Grinding.WaitingForUnloadTimeoutMinutes, 20),
        GrinderState.Unloading => ToSafeStateTimeout(_cfg.Grinding.UnloadingTimeoutMinutes, 10),
        _ => TimeSpan.Zero
    };

    /// <summary>配置页或手工 JSON 写入 0/负数时回退默认值，禁止立即触发安全暂停。</summary>
    private static TimeSpan ToSafeStateTimeout(int configuredMinutes, int defaultMinutes) =>
        TimeSpan.FromMinutes(configuredMinutes > 0 ? configuredMinutes : defaultMinutes);

    /// <summary>
    /// 尝试派发一笔研磨上料。只有任务已登记、天车锁已交给后台上料流程时才返回 true；
    /// 任何上料前置条件不满足时返回 false，以便同一轮回退检查下料。
    /// </summary>
    private async Task<bool> TryDispatchLoadAsync(CancellationToken ct)
    {
        var ready = FindReadyGrinder();
        if (ready == null)
        {
            if (_cycleCount % 10 == 1)
            {
                var idleNoReq = _grinders.Where(g => g.State == GrinderState.Idle && !g.LastRequestData).ToList();
                if (idleNoReq.Count > 0)
                    Console.WriteLine($"[GrindingEngine] ⏳ 有空闲但无请求数据：{string.Join(", ", idleNoReq.Select(g => g.StationCode))}");
                else
                    Console.WriteLine("[GrindingEngine] ⏳ 无就绪研磨机(全部忙或未联机)");
            }
            return false;
        }

        if (CachedCount == 0)
        {
            if (_cycleCount % 10 == 1)
                Console.WriteLine($"[GrindingEngine] ⏳ 有研磨机{ready.Name}就绪但缓存空, 等待工件入队...");
            return false;
        }

        // 主循环预检；真正去 ST709 取料前仍会再次读取 M730，防止现场信号变化导致空取。
        if (!await ConfirmGrindingFeedReadyAsync("上料优先预检", ct))
            return false;
        
        //非阻塞式拿锁 拿不到就return false
        if (!await _craneLock.WaitAsync(0, ct))
        {
            if (ShouldLogWaiting("crane-busy:grinding-load"))
                Console.WriteLine($"[GrindingEngine] [{ready.Name}] ⏳ 上料条件满足但天车锁忙，等待下一轮...");
            return false;
        }

        bool dispatchGateHeld = false;
        bool craneLockTransferred = false;
        try
        {
            //拿锁 阻塞式拿锁   这个锁式防止主循环已经越过轮询顶部的暂停判断时，又恰好启动一个新动作。
            await _grindingDispatchGate.WaitAsync(ct);
            dispatchGateHeld = true;

            // 应急可能在本轮顶部暂停检查之后发生，拿到天车锁后必须再次确认。
            if (_paused)
            {
                Console.WriteLine($"[GrindingEngine] [{ready.Name}] 应急/暂停已生效, 放弃本次上料派发并释放天车锁");
                return false;
            }

            // 此时已取得天车锁和派发门闩，且尚未出队或启动天车动作；超限只做软暂停，保留所有FIFO身份。
            int maxFifoCount = Math.Max(1, _cfg.Grinding.MaxFifoCount);
            int cachedCount = CachedCount;
            //判断缓存数量是否>配置文件设置的数量最大值 现在目前是2 如果大于2说明有问题
            if (cachedCount > maxFifoCount)
            {
                _paused = true;
                string overflowMessage = $"研磨上料FIFO数量={cachedCount}，超过配置上限{maxFifoCount}。研磨引擎已暂停；本轮尚未派发天车动作、未移动天车、未写PLC信号、未删除FIFO数据。" +
                    "请先现场确认ST709及传送带工件状态；确认物理队头已处理后，请点击主页面应急按钮执行ST709研磨应急。每次只清除一个FIFO队头数据。";
                Console.WriteLine($"[GrindingEngine] ⚠ {overflowMessage}");
                OnSafetyAlarm?.Invoke(overflowMessage);
                return false;
            }

            // 必须在 FIFO 出队和研磨机状态变化前校验，报警时任务仍留在原缓存。
            if (!await EnsureGrindingCranePositionReadyAsync("研磨上料任务出队前", ct))
                return false;   
            
            //拿缓存数据 
            if (!TryDequeueCache(out var wp))
            {
                if (_cycleCount % 10 == 1)
                    Console.WriteLine($"[GrindingEngine] [{ready.Name}] 上料派发时缓存已空，改为检查下料");
                return false;
            }

            var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int actionVersion = BeginGrindingAction(actionCts, completion, "上料", ready.StationCode);
            Console.WriteLine($"[GrindingEngine] 🚀 分配 {wp.IdentityText} d={wp.Diameter} → {ready.Name}({ready.StationCode}) 请求数据=1 缓存剩余={CachedCount}");
            ready.State = GrinderState.Loading;
            ready.StateChangedAt = DateTime.UtcNow;
            ready.PendingWorkpiece = wp;
            // 上料动作和完成信号必须在派发门闩内一起登记，避免应急漏等旧动作。
            _ = ProcessWorkpieceAsync(ready, wp, actionVersion, actionCts, completion);
            craneLockTransferred = true;
            return true;
        }
        finally
        {
            //这个锁：防止主循环已经越过轮询顶部的暂停判断时，又恰好启动一个新动作。
            if (dispatchGateHeld) _grindingDispatchGate.Release();
            //天车锁
            if (!craneLockTransferred) _craneLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  扫描研磨机状态
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 清空研磨机旧扫描信号。
    /// 注意: PLC/串口读失败时不能沿用上轮 LastRequestData/LastR7304=true,
    /// 否则会在现场状态未知时误触发上料/下料。这里只清触发快照, 不改正在执行的业务状态。
    /// </summary>
    private void ClearCachedGrinderSignals(GrinderContext g, string reason)
    {
        Interlocked.Increment(ref g.SignalScanVersion);
        g.LastRequestData = false;
        g.LastR7304 = 0;
        g.LastMachining = false;
        g.LastDoorOpen = false;
        g.LastOnlineMode = false;

        if (_cycleCount % 10 == 0)
            Console.WriteLine($"[GrindingEngine] [{g.Name}] {reason}, 已清空旧触发信号, 等待读取恢复");
    }

    /// <summary>
    /// 确认研磨机上料架3号位(ST709/M730)当前确实有版。
    /// 主循环预检和天车动作前都调用它, 避免使用旧快照去空取。
    /// </summary>
    private async Task<bool> ConfirmGrindingFeedReadyAsync(string context, CancellationToken ct)
    {
        if (_mc65?.IsConnected != true)
        {
            Console.WriteLine($"[GrindingEngine] {context}: MC65未连接, ST709/M730状态未知");
            return false;
        }

        try
        {
            var r = await _mc65.ReadMAlignedWordAsync(720, 1, ct);
            int raw = r.IntValues.Length > 0 ? r.IntValues[0] : 0;
            bool hasPlate = (raw & (1 << 10)) != 0; // M730 = M720 bit10
            if (!hasPlate && _cycleCount % 10 == 1)
                Console.WriteLine($"[GrindingEngine] {context}: 研磨上料架3号位无板(M730=0), 等待传送带送来");
            return hasPlate;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GrindingEngine] {context}: 读取ST709/M730失败, 保守等待: {ex.Message}");
            await _mcCache.InvalidateAsync("192.168.2.65", 9000);
            _mc65 = null;
            return false;
        }
    }

    /// <summary>
    /// 确认研磨机下料架(ST710)当前可放料。
    /// M720=1同时表示下料架无板且允许天车放版。
    /// 下料从研磨机取起后再确认一次, 宁可暂停人工确认, 不能盲目去占用下料架。
    /// </summary>
    private async Task<bool> ConfirmGrindingUnloadRackReadyAsync(string context, CancellationToken ct)
    {
        if (_mc64?.IsConnected != true)
        {
            Console.WriteLine($"[GrindingEngine] {context}: MC64未连接, ST710/M720状态未知");
            return false;
        }

        try
        {
            var r = await _mc64.ReadMAlignedWordAsync(720, 1, ct);
            int raw = r.IntValues.Length > 0 ? r.IntValues[0] : 0;
            bool m720CanPlace = (raw & 1) != 0;
            if (!m720CanPlace || _cycleCount % 10 == 1)
                Console.WriteLine($"[GrindingEngine] {context}: ST710/M720={(m720CanPlace ? "无板且允许放料" : "不可放料")} raw=0x{raw:X4}");
            return m720CanPlace;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GrindingEngine] {context}: 读取ST710/M720失败, 保守等待/暂停: {ex.Message}");
            await _mcCache.InvalidateAsync("192.168.2.64", 9000);
            _mc64 = null;
            return false;
        }
    }

    /// <summary>
    /// 扫描4台研磨机状态(请求数据/请求下料/加工中/门开)。
    /// <para>重连失败→跳过本轮读取不阻塞; TypeB用临时变量原子赋值避免部分更新。</para>
    /// </summary>
    private async Task ScanAllGrindersAsync(CancellationToken ct)
    {
        var tasks = new Task[_grinders.Count];
        var ctsList = new CancellationTokenSource[_grinders.Count];
        for (int i = 0; i < _grinders.Count; i++)
        {
            var perGrinderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perGrinderCts.CancelAfter(3000);
            ctsList[i] = perGrinderCts;
            long scanVersion = Interlocked.Increment(ref _grinders[i].SignalScanVersion);
            tasks[i] = ScanOneGrinderAsync(_grinders[i], scanVersion, perGrinderCts.Token)
                .ContinueWith(_ => perGrinderCts.Dispose());
        }

        var all = Task.WhenAll(tasks);
        if (await Task.WhenAny(all, Task.Delay(3000, ct)) != all)
        {
            for (int i = 0; i < tasks.Length; i++)
            {
                if (tasks[i].IsCompleted) continue;

                try { ctsList[i].Cancel(); } catch { }
                ClearCachedGrinderSignals(_grinders[i], "扫描超时");
            }

            if (_cycleCount % 10 == 1)
                Console.WriteLine("[GrindingEngine] 研磨机状态扫描超过3s, 已取消未完成读并清空旧触发信号");
        }
    }

    private async Task ScanOneGrinderAsync(GrinderContext g, long scanVersion, CancellationToken ct)
    {
        try
        {
            // ── Loading/Unloading状态降频扫描: 天车正在操作, 信号不变, 每5轮扫一次省IO ──
            if ((g.State == GrinderState.Loading || g.State == GrinderState.Unloading) && _cycleCount % 5 != 0)
            {
                return; // 跳过本轮, 保留上次信号值(信号在天车操作期间不会变化)
            }

            // ── 无共享服务 → 跳过(GrinderPoll还没注入)。每20轮输出一次避免刷屏 ──
            if (g.Svc == null)
            {
                ClearCachedGrinderSignals(g, "Svc为空");
                if (_cycleCount % 20 == 1)
                    Console.WriteLine($"[GrindingEngine] [{g.Name}] Svc=null, 等待GrinderPoll注入...(第{_cycleCount}轮)");
                return;
            }

            // ── 重连检查 ──
            if (!g.Svc.IsConnected)
            {
                Console.WriteLine($"[GrindingEngine] [{g.Name}] 连接已断开, 尝试重连(3s超时)...");
                try
                {
                    await g.Svc.ConnectAsync(ct);
                    Console.WriteLine($"[GrindingEngine] [{g.Name}] ✔ 重连成功");
                }
                catch (Exception ex)
                {
                    // 重连失败时必须清掉旧触发信号。
                    // 保留旧true会让主循环在现场未知时继续派发上料/下料, 生产环境风险更高。
                    ClearCachedGrinderSignals(g, "重连失败");
                    Console.WriteLine($"[GrindingEngine] [{g.Name}] 重连失败(跳过本轮): {ex.Message}");
                    return;
                }
            }

            // ── TypeA(西门子): 一次批量读DI寄存器, 原子解析 ──
            if (g.GrinderType == PlcGrinderService.GrinderType.TypeA)
            {
                var s = await g.Svc.ReadTypeAStatusSnapshotAsync(ct);
                CommitTypeAGrinderSignals(g, scanVersion, s, "状态扫描");
            }
            // ── TypeB(新代): 一次批量读R7301~R7308快照, 避免多次读导致新旧信号混用 ──
            else
            {
                var s = await g.Svc!.ReadTypeBStatusSnapshotAsync(ct);
                TryCommitGrinderSignals(g, scanVersion, g.LastDi, s.ReqData, s.ReqUnload ? 1 : 0, s.Busy, s.Door);
            }
        }
        catch (Exception ex)
        {
            // 单台研磨机读信号异常不影响其他研磨机扫描
            Console.WriteLine($"[GrindingEngine] [{g.Name}] 扫描异常：{ex.Message}");
            if (g.State != GrinderState.Loading && g.State != GrinderState.Unloading)
            {
                try { if (g.Svc != null) await g.Svc.DisconnectAsync(); } catch { }
            }
            // 扫描异常时不沿用旧信号。下一轮读到PLC后会重新刷新这些快照。
            ClearCachedGrinderSignals(g, "扫描异常");
        }
    }

    /// <summary>
    /// 提交西门子 TypeA 的完整快照，并把磨石报警作为“仅本机禁止新任务分配”的状态。
    /// 锁覆盖旧扫描和取料前二次确认，防止较早的正常扫描覆盖刚读取到的报警。
    /// </summary>
    private void CommitTypeAGrinderSignals(GrinderContext g, long scanVersion,
        PlcGrinderService.TypeAStatusSnapshot snapshot, string source)
    {
        lock (g.GrindStoneAlarmGate)
        {
            bool wasAlarm = g.HasGrindStoneAlarm;
            if (!TryCommitGrinderSignals(g, scanVersion, snapshot.RawDI, snapshot.ReqData,
                    snapshot.ReqUnload ? 1 : 0, snapshot.Busy, snapshot.Door,
                    snapshot.GrindStone1Alarm, snapshot.GrindStone2Alarm, snapshot.OnlineMode))
                return;

            LogGrindStoneAlarmState(g, wasAlarm, source);
        }
    }

    /// <summary>
    /// 磨石厚度是单机维护信号：只禁止该机接收新的自动上料，绝不暂停整个研磨引擎。
    /// 报警进入/恢复只输出一次；持续报警每分钟提示一次，避免轮询刷屏。
    /// </summary>
    private static void LogGrindStoneAlarmState(GrinderContext g, bool wasAlarm, string source)
    {
        bool hasAlarm = g.HasGrindStoneAlarm;
        string stones = string.Join("、", new[]
        {
            g.LastGrindStone1Alarm ? "磨石1" : null,
            g.LastGrindStone2Alarm ? "磨石2" : null,
        }.Where(x => x != null));
        var now = DateTime.UtcNow;

        if (hasAlarm != wasAlarm)
        {
            g.LastGrindStoneAlarmHeartbeatAtUtc = now;
            if (hasAlarm)
                Console.WriteLine($"[GrindingEngine] [{g.Name}] ⚠ {stones}厚度报警({source})，仅停止本机自动分配；其他研磨机继续运行");
            else
                Console.WriteLine($"[GrindingEngine] [{g.Name}] ✓ 磨石厚度报警已恢复({source})，本机恢复自动分配资格");
            return;
        }

        if (hasAlarm && now - g.LastGrindStoneAlarmHeartbeatAtUtc >= TimeSpan.FromMinutes(1))
        {
            g.LastGrindStoneAlarmHeartbeatAtUtc = now;
            Console.WriteLine($"[GrindingEngine] [{g.Name}] ⏳ {stones}厚度仍报警，持续停止本机自动分配；其他研磨机继续运行");
        }
    }

    private static bool TryCommitGrinderSignals(GrinderContext g, long scanVersion, int rawDi,
        bool requestData, int requestUnload, bool machining, bool doorOpen,
        bool grindStone1Alarm = false, bool grindStone2Alarm = false, bool onlineMode = false)
    {
        if (Interlocked.Read(ref g.SignalScanVersion) != scanVersion)
        {
            Console.WriteLine($"[GrindingEngine] [{g.Name}] 丢弃过期扫描结果 scan={scanVersion}, current={Interlocked.Read(ref g.SignalScanVersion)}");
            return false;
        }

        g.LastDi = rawDi;
        g.LastRequestData = requestData;
        g.LastR7304 = requestUnload;
        g.LastMachining = machining;
        g.LastDoorOpen = doorOpen;
        g.LastGrindStone1Alarm = grindStone1Alarm;
        g.LastGrindStone2Alarm = grindStone2Alarm;
        g.LastOnlineMode = onlineMode;
        return true;
    }

    /// <summary>
    /// 按优先级找可用的研磨机(Idle+PLC请求数据+无PendingWorkpiece+无磨石报警)。
    /// 西门子 TypeA 还必须为40001-3联机模式；单机和磨石报警都只排除本机新上料。
    /// </summary>
    private bool ShouldLogWaiting(string key)
    {
        var now = DateTime.UtcNow;
        if (_waitingLogAtUtc.TryGetValue(key, out var last) && now - last < WaitingLogHeartbeat)
            return false;
        _waitingLogAtUtc[key] = now;
        return true;
    }

    private GrinderContext? FindReadyGrinder()
    {
        return _grinders.FirstOrDefault(g =>
            g.State == GrinderState.Idle          // 空闲
            && g.LastRequestData                   // PLC已联机(请求数据=1)
            && g.Svc != null                      // 共享服务已注入(GrinderPoll)
            && g.Svc.IsConnected                  // Modbus已连接
            && g.PendingWorkpiece == null          // 无在途工件(后台流程跑完会清)
            && !g.HasGrindStoneAlarm               // 磨石厚度报警：仅本机停止自动分配
            && (g.GrinderType != PlcGrinderService.GrinderType.TypeA || g.LastOnlineMode)); // TypeA单机时禁止新上料
    }

    // ═══════════════════════════════════════════════════════════════
    //  Z 计算公式
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 上料架取料绝对 Z = rackZ - [(d/2 / zFactor1) + (d/2 / zFactor2)]。
    /// <para>走绝对位移，写 D3106~D3107 的值就是计算结果。</para>
    /// <para>例：rackZ=1506, d=205 → 1506 - 226 = 1283</para>
    /// </summary>
    private int ComputePickupZ(int rackZ, double diameter)
    {
        double d = diameter;
        int descent = (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));
        int z = rackZ - descent;
        Console.WriteLine($"[GrindingEngine]   取料绝对Z: {rackZ} - [({d}/2/{_cfg.Grinding.ZFactor1}) + ({d}/2/{_cfg.Grinding.ZFactor2})] = {rackZ} - {descent} = {z}");
        return z;
    }

    /// <summary>研磨机装料绝对 Z = grinderZ - d/2</summary>
    private int ComputeGrinderLoadZ(int grinderZ, double diameter)
    {
        return grinderZ - (int)Math.Round(diameter / 2.0);
    }

    /// <summary>下料架放料绝对 Z = rackZ - [(d/2/0.9537)+(d/2/0.866)]，同取料公式</summary>
    private int ComputeUnloadZ(int rackZ, double diameter)
    {
        double d = diameter;
        int descent = (int)Math.Round((d / 2.0 / _cfg.Grinding.ZFactor1) + (d / 2.0 / _cfg.Grinding.ZFactor2));
        int z = rackZ - descent;
        Console.WriteLine($"[GrindingEngine]   下料绝对Z: {rackZ} - [({d}/2/{_cfg.Grinding.ZFactor1}) + ({d}/2/{_cfg.Grinding.ZFactor2})] = {rackZ} - {descent} = {z}");
        return z;
    }

    /// <summary>尽力写入研磨工件显示屏；失败只记日志，不改变研磨业务状态。</summary>
    private async Task WriteWorkpieceDisplayBestEffortAsync(WorkpieceCache wp, CancellationToken ct)
    {
        var result = await _workpieceDisplay.TryWriteAsync(
            _cfg.Grinding.WorkpieceDisplayIp, wp.Diameter, wp.Length, ct);
        if (result.Success)
        {
            Console.WriteLine(
                $"[GrindingEngine] [工件显示屏] 写入成功 IP={_cfg.Grinding.WorkpieceDisplayIp}:502 " +
                $"d={(int)Math.Round(wp.Diameter)}→100 L={(int)Math.Round(wp.Length)}→101");
            return;
        }

        Console.WriteLine(
            $"[GrindingEngine] [工件显示屏] ⚠ 写入失败但继续取料 IP={_cfg.Grinding.WorkpieceDisplayIp}:502 " +
            $"阶段={result.Stage} d={wp.Diameter} L={wp.Length}: {result.Error?.Message}");
    }

    /// <summary>
    /// 目标研磨机上料准备阶段（持件XY、加工参数/数据完成、目标微调）失败后的安全清理。
    /// 工件已经确认由研磨天车持有，故只清目标机参数/上位机输出；不得清Pending或回研磨FIFO。
    /// </summary>
    private async Task ClearTargetGrinderPreparationAsync(
        GrinderContext grinder, WorkpieceCache wp, Exception originalException)
    {
        const string phase = "自动清理研磨机目标位准备参数";
        Console.WriteLine(
            $"[GrindingEngine] [{grinder.Name}] ⚠ {phase}开始: 工件={wp.IdentityText}; 原始异常={originalException.Message}");
        OperationalLog.Warn(phase, "XY/参数链/微调异常，清目标机参数和上位机输出后暂停；Pending保留",
            ("研磨机", grinder.StationCode), ("工件", wp.IdentityText), ("原始异常", originalException.Message),
            ("后续处理", "ClearEmergencyRegistersAsync；Pending保留；由外层异常暂停"));

        try
        {
            await grinder.Svc!.ClearEmergencyRegistersAsync(CancellationToken.None);
            Console.WriteLine(
                $"[GrindingEngine] [{grinder.Name}] ✓ {phase}成功: 参数及上位机输出已清零; Pending保留");
            OperationalLog.Info(phase, "参数及上位机输出已清零，Pending保留，等待外层异常暂停",
                ("研磨机", grinder.StationCode), ("工件", wp.IdentityText));
        }
        catch (Exception clearEx)
        {
            Console.WriteLine(
                $"[GrindingEngine] [{grinder.Name}] ⚠ {phase}失败: 原始异常={originalException.Message}; " +
                $"清理异常={clearEx.Message}; Pending保留");
            OperationalLog.Warn(phase, "自动清理失败，保留原始异常语义并暂停",
                ("研磨机", grinder.StationCode), ("工件", wp.IdentityText),
                ("原始异常", originalException.Message), ("清理异常", clearEx.Message),
                ("后续处理", "Pending保留；由外层异常暂停"));
        }
    }

    /// <summary>从 _stationCoords 读指定站号的 XYZ 坐标，未找到返回 false。</summary>
    private bool TryGetStationCoords(string stationCode, out int x, out int y, out int z)
    {
        x = y = z = 0;
        if (!_stationCoords.TryGetValue(stationCode, out var row)) return false;
        x = (int)Math.Round(row.X);
        y = (int)Math.Round(row.Y);
        z = (int)Math.Round(row.Z);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  核心流程：取料 → 送料 → 握手
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 天车取料 + 送料到研磨机 + 握手全流程（由主循环 fire-and-forget 调用）。
    /// <para>15 个步骤：</para>
    /// <para>  ① 等待研磨机请求数据（避免 PLC 未就绪就写参数）</para>
    /// <para>  ② 检查磨石厚度报警（仅 TypeA；报警机停止新任务分配，其他机继续）</para>
    /// <para>  ③ 复核工件、补D200长度并写旁路显示屏</para>
    /// <para>  ④ 天车 XY+Z 到上料架 ST709（坐标从数据库读）</para>
    /// <para>  ⑤ 充磁取料（轮询 X6=1 确认充磁到位）</para>
    /// <para>  ⑥ Z 升到安全高度(绝对Z=400)</para>
    /// <para>  ⑦ XY移向研磨机与最终参数、数据传输完成(3s长信号)并行执行</para>
    /// <para>  ⑧ 目标X绝对编码器微调</para>
    /// <para>  ⑨ 等待研磨机请求上料 + 门打开（装载需门开）</para>
    /// <para>  ⑩ Z 下降到装料位置(研磨机Z - d/2)——Z变大=向下，此步 200ms 轮询 X2 下压限位</para>
    /// <para>  ⑪ 上料到达锁紧位置(3s长信号) — 通知 PLC 尾座可夹紧</para>
    /// <para>  ⑫ 等待尾座锁紧完成</para>
    /// <para>  ⑬ 退磁释放工件（轮询 X7=1 + D5029=0 确认退磁到位）</para>
    /// <para>  ⑭ Z 升到安全高度(绝对Z=400)</para>
    /// <para>  ⑮ 上料完成(3s长信号) → 标记研磨机=加工中，PLC 开始加工</para>
    /// <para>⚠ 异常时：下压急停(PressureStopException)暂停引擎。</para>
    /// <para>⚠ 充磁前失败 → 工件还在 ST709 架上 → 自动放回缓存队列。</para>
    /// <para>⚠ 充磁后失败 → 工件已在天车上 → 无法自动恢复，需人工处理。</para>
    /// <para>⚠ finally 块始终释放天车信号量 _craneLock。</para>
    /// </summary>
    /// <summary>
    /// 上料全流程: 天车#5→ST709取料→送研磨机→握手→退磁→标记加工中。
    /// 主循环已设State=Loading+StateChangedAt, 本方法设PendingWorkpiece；异常时按物理位置决定是否回缓存。
    /// <para>⚠ magnetOn=true后(充磁后)工件在天车上, 异常无法自动恢复, 需人工处理。</para>
    /// <para>⚠ finally释放_craneLock, 保证天车锁不泄漏。</para>
    /// </summary>
    private async Task ProcessWorkpieceAsync(GrinderContext grinder, WorkpieceCache wp, int actionVersion,
        CancellationTokenSource actionCts, TaskCompletionSource<bool> completion)
    {
        string actionId = OperationalEventContextFactory.NewActionId("GRIND-LOAD");
        var action = new FlowActionContext(actionId, "研磨天车上料", $"研磨天车#{_cfg.Grinding.CraneNo}",
            wp.IdentityText, "ST709", grinder.StationCode, "研磨FIFO");
        using var logAction = OperationalLog.BeginAction("研磨", $"{_cfg.Grinding.CraneNo}号天车", actionId,
            wp.IdentityText, "ST709", grinder.StationCode);
        OperationalLog.Info("搬运动作开始", "已创建研磨上料任务", ("板号与序号", wp.IdentityText));
        action.RegisterLock("GrindingCrane");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        using var _ = actionCts;
        var ct = actionCts.Token;
        grinder.PendingWorkpiece = wp; // 标记在途, FindReadyGrinder会跳过此研磨机
        wp.ReportStage($"{grinder.Name} 上料中");
        var craneName = $"天车#{_cfg.Grinding.CraneNo}";
        CraneService? crane = null;
        bool magnetOn = false;  // 充磁标志: true=工件在天车上(异常时无法自动回缓存), false=工件还在ST709架上
        bool holdingWorkpiece = false;  // X11确认吸住后才认为工件真实在天车上, 不能只看“发过充磁命令”
        bool placedInGrinder = false;   // 退磁放入研磨机后, 工件已离开ST709缓存队列, 异常时绝不能回缓存
        bool operationalPlacementCommitted = false; // 仅供旁路证据，不参与业务判断
        bool loadDoneNotified = false;  // SetLoadDone成功后, 研磨机PLC已收到上料完成, 状态可进入Machining
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(actionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "研磨", "研磨引擎", "研磨天车", _cfg.Grinding.CraneNo.ToString(), "ST709",
            $"ST709取料并放入{grinder.StationCode}", actionId, $"{actionId}:grinding-load", wp,
            "ST709", grinder.StationCode, "研磨天车", "ST709", "研磨上料动作已创建", "研磨天车锁"));
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "SetLoadDone");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{grinder.StationCode}上料完整交接");

        
        try
        {
            // ═══ 连接天车(CraneConnectionCache共享, 不建重复TCP) ═══
            crane = _craneCache.GetOrCreateService(_cfg.Grinding.CraneNo);
            if (!crane.IsConnected) await crane.ConnectAsync(ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 开始取料送料流程 {wp.IdentityText} 直径={wp.Diameter} ═══");

            // ── ① 等待研磨机请求数据(PLC联机就绪信号) ──
            //    协议: 机台切联机后PLC先置请求数据=1, 上位机才能写参数
            //    超时: HandshakeTimeoutMs(默认120s), 超时抛TimeoutException
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ① 等待研磨机请求数据(PLC联机就绪)...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsRequestDataAsync(ct),
                "请求数据", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   ① 请求数据=1 ✓ PLC已就绪");

            // ── ② 检查磨石厚度报警及联机状态(仅西门子TypeA) ──
            //    正式取料前重新读完整快照，覆盖扫描与动作之间才出现的报警或切单机。
            //    两者都只是本机新上料准入：工件仍在ST709，退回缓存；不暂停研磨引擎。
            if (grinder.GrinderType == PlcGrinderService.GrinderType.TypeA)
            {
                var snapshot = await grinder.Svc!.ReadTypeAStatusSnapshotAsync(ct);
                CommitTypeAGrinderSignals(grinder, Interlocked.Increment(ref grinder.SignalScanVersion), snapshot,
                    "取料前二次确认");
                if (!snapshot.OnlineMode || grinder.HasGrindStoneAlarm)
                {
                    Console.WriteLine($"══════════════════════════════════════════════");
                    string blockedReason = !snapshot.OnlineMode
                        ? "单机模式(40001-3=0)，停止本机自动分配"
                        : "磨石厚度报警，停止本机自动分配，请更换磨石";
                    Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠⚠⚠ {blockedReason} ⚠⚠⚠");
                    Console.WriteLine($"══════════════════════════════════════════════");
                    grinder.State = GrinderState.Idle;
                    grinder.StateChangedAt = DateTime.UtcNow;
                    grinder.PendingWorkpiece = null; // 清PendingWorkpiece, 否则FindReadyGrinder永久排除此研磨机
                    // 工件还在 ST709 架上，放回缓存（finally 会释放锁）
                    RequeueWorkpiece(wp);
                    Console.WriteLine($"[GrindingEngine] ↩ 工件 d={wp.Diameter} 放回缓存（{blockedReason}，未取料；其他研磨机继续分配）");
                    return;  // 中止上料流程
                }
            }

            // ── ③ 补充长度后写工件参数 ──
            //    M3Flow(M700来源)Length=0: 先从D200(位置②测长)读取长度
            //    再一次性写直径/版孔/长度到研磨机PLC(40012/R7316等)
            if (wp.Diameter <= 0)
                throw new InvalidOperationException($"工件直径无效 d={wp.Diameter}, 不能计算取放料Z");
            //工件长度=0时候 这个工件是从动平衡过来的
            if (wp.Length == 0)
            {
                if (_mc65?.IsConnected != true)
                    throw new InvalidOperationException("工件长度为0且MC65未连接, 不能从D200补长");

                // M3放板时允许Length=0, 但研磨参数下发前必须用2号位D200补到有效长度。
                var d200 = await _mc65.ReadAsync(MitsubishiMcClient.DeviceD, 200, 1, ct);
                double len = d200.IntValues.Length > 0 ? d200.IntValues[0] : 0;
                if (len <= 0)
                    throw new InvalidOperationException($"D200测长无效 len={len}, 暂停避免向研磨机发送长度0");
                //重新赋值工件信息
                wp = new WorkpieceCache
                {
                    PlateNo = wp.PlateNo,
                    Sequence = wp.Sequence,
                    Diameter = wp.Diameter,
                    BoreType = wp.BoreType,
                    Length = len,
                    MarkingContent = wp.MarkingContent,
                    LeftPlugThickness = wp.LeftPlugThickness,
                    RightPlugThickness = wp.RightPlugThickness,
                    InnerTaper = wp.InnerTaper,
                    CornerSize = wp.CornerSize,
                    BoringProcess = wp.BoringProcess,
                    SkewBedProcess = wp.SkewBedProcess,
                    SkipBoring = wp.SkipBoring,
                    ForceBalancing = wp.ForceBalancing,
                };
                grinder.PendingWorkpiece = wp; // 补长后的数据同步到Pending, 后续异常恢复能看到真实长度。
                operationalSite = operationalSite with { Workpiece = wp, LastSuccessfulCheckpoint = "D200长度补充成功" };
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ③ D200测长读数={len}mm → wp.Length={wp.Length}mm");
            }
            // 写研磨参数前再确认ST709/M730仍有板。
            // 主循环预检只说明“派发当时”有板; 补长度/等待请求数据期间链条可能滚动或信号变化。
            // 若此处无板/读取失败, 工件还没被天车吸走, 外层会把缓存放回队头, 不让研磨机收到一套没有对应物理板的数据。
            if (!await ConfirmGrindingFeedReadyAsync("写研磨参数前", ct))
                throw new InvalidOperationException("ST709/M730写参数前确认无版或读取失败, 工件未取走, 已回缓存等待下轮");


            // ── ④ 天车去研磨上料架3号位ST709取料(传送带末端=可拾取位) ──
            //    安全: 先读X11确认磁铁无残留(断电重启保护)
            operationalTracker.BeginX11Stage("取料前残留检查");
            operationalTracker.BeginX11Attempt();
            bool hasExistingWorkpiece;
            try
            {
                hasExistingWorkpiece = await crane.ReadXBitAsync(63497, ct);
                operationalTracker.CompleteX11(hasExistingWorkpiece, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    operationalSite with { ActionStage = "ST709取料前X11残留检查" }, operationalTracker, ex,
                    "取料前X11读取失败，当前值无效；保留最后成功值（如有）", true);
                throw;
            }
            physicalTracker?.TryObserveX11(hasExistingWorkpiece);
            if (hasExistingWorkpiece)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    operationalSite with { ActionStage = "ST709取料前发现磁铁已有工件" }, operationalTracker, null,
                    "取料前X11成功读取为1，确认研磨天车磁铁上存在身份未知残留工件", false,
                    holdingWorkpiece: true);
                throw new InvalidOperationException($"天车#{_cfg.Grinding.CraneNo} X11=1(磁铁已有工件), 拒绝取料防止碰撞");
            }

            // 主循环预检到真正取料之间会先写研磨参数、可能还会补长度。
            // 这段时间链条/现场信号可能变化, 所以天车动作前必须重新确认ST709/M730有版。
            if (!await ConfirmGrindingFeedReadyAsync("上料动作前", ct))
                throw new InvalidOperationException("ST709/M730动作前确认无版或读取失败, 工件未取走, 已回缓存等待下轮");

            // 显示屏只展示本次准备取走工件的最终直径/长度；通信失败只记日志，不阻断取料。
            await WriteWorkpieceDisplayBestEffortAsync(wp, ct);

            if (!TryGetStationCoords("ST709", out int rackX, out int rackY, out int rackZ))
                throw new InvalidOperationException("数据库未找到 ST709 上料架坐标");
            Console.WriteLine($"[GrindingEngine] [{craneName}] ④ 去上料架3号位ST709({rackX},{rackY}) 取料 Z基准={rackZ}...");
            var grSpd = _cfg.GetCraneSpeed(_cfg.Grinding.CraneNo);
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置研磨天车本任务绝对速度");
            action.MarkCommandSent();
            //设置5号天车速度
            await crane.SetAbsSpeedAsync(
                grSpd.X.Speed, grSpd.X.Accel, grSpd.X.Decel,
                grSpd.Y.Speed, grSpd.Y.Accel, grSpd.Y.Decel,
                grSpd.Z.Speed, grSpd.Z.Accel, grSpd.Z.Decel, ct);
            action.Confirm();

            // XY先到上料架位置, 绝对编码器稳定确认后再下降取料。
            int pickupZ = ComputePickupZ(rackZ, wp.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ④ 目标 Z={pickupZ} (基准{rackZ}) 偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ})");
            // 新上料动作首次跨工位横移前确认Z零位，防止上次异常遗留低Z直接横移。
            Console.WriteLine($"[GrindingEngine] [{craneName}] 去ST709取料前确认Z=0±5mm");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), "去ST709前确认Z回零");
            action.MarkCommandSent();
            await crane.EnsureZAtZeroAsync(5, ct);
            action.Confirm(new FlowActionPosition(null, null, 0), "取料前Z=0已确认");
            physicalTracker?.TryConfirmSafeZ(0, 5, "ST709取料前EnsureZAtZeroAsync成功返回");
            action.BeginStep(FlowActionStep.MoveXYToSource, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), 0), "XY到ST709来源位");
            action.MarkCommandSent();
            await crane.MoveAbsoluteAsync(ApplyOffsetX(rackX), ApplyOffsetY(rackY), -1, ct: ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), 0), "ST709上方XY到位");
            action.BeginStep(FlowActionStep.FineTuneSource, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), 0), "ST709取料前X绝对编码器微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                crane, _cfg, _cfg.Grinding.CraneNo, "ST709", "研磨天车-ST709上料架取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "研磨", engine: "研磨引擎", deviceNo: _cfg.Grinding.CraneNo.ToString(), station: "ST709",
                    actionStage: "ST709上料架取料前XY微调", workpiece: wp,
                    source: OperationalEventContextFactory.ConfirmedLocation("ST709", "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation(grinder.StationCode, "已选研磨机目标"),
                    owner: "研磨天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后调用ApplyOffsetZ计算ST709取料目标"),
                    physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, true, "Z零位检查已返回", "ST709")),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), 0), "ST709取料前微调成功返回");
            int pickupTargetZ = ApplyOffsetZ(pickupZ);
            operationalTracker.BeginZDown(pickupTargetZ);
            action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), pickupTargetZ), "ST709下降取料");
            action.MarkCommandSent();
            await crane.MoveAbsoluteAsync(-1, -1, pickupTargetZ, ct: ct);
            operationalTracker.CompleteZDown();
            action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), pickupTargetZ), "ST709取料Z到位");

            // ── ⑤ 充磁取料 ───────────────────────────────────────
            //    充磁成功=工件已吸附到天车上，之后失败无法自动回缓存
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑤ 充磁取料...");
            operationalTracker.BeginMagnetOn();
            try
            {
                action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), pickupTargetZ), "ST709首次充磁");
                action.MarkCommandSent();
                await crane.MagnetOnAsync(ct);
                operationalTracker.CompleteMagnetOn();
            }
            catch (Exception ex)
            {
                operationalTracker.FailMagnetOn();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                    operationalSite with { ActionStage = "ST709首次充磁取料" }, operationalTracker, ex,
                    "充磁方法异常，实际命令与磁铁状态未知", true);
                throw;
            }
            magnetOn = true;
            action.Confirm();
            action.RecordFeedback(FlowActionPosition.Unknown, null, true, "首次充磁调用成功返回");
            Console.WriteLine($"[GrindingEngine] [{craneName}]   充磁完成");

            // ── ⑤b X11检测: 充磁→等3s→查X11(63497)→没吸到退磁Z↓5mm重试, 最多2次 ──
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑤b 充磁→等3s→X11检测");
            int pickupCheckZ = pickupZ;
            operationalTracker.BeginX11Stage("充磁后持件确认");
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   充磁(X11第1次)");
                }
                else
                {
                    pickupCheckZ += 5;
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   退磁→Z↓到{pickupCheckZ}→充磁");
                    operationalTracker.BeginMagnetOff();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), ApplyOffsetZ(pickupCheckZ)), "ST709 X11重试前退磁");
                        action.MarkCommandSent();
                        await crane.MagnetOffAsync(ct);
                        operationalTracker.CompleteMagnetOff();
                        action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), ApplyOffsetZ(pickupCheckZ)), "ST709 X11重试前退磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOff();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                            operationalSite with { ActionStage = "ST709 X11重试前退磁" }, operationalTracker, ex,
                            "退磁方法异常，实际退磁结果未知", true, holdingWorkpiece: holdingWorkpiece);
                        throw;
                    }
                    magnetOn = false;
                    int retryTargetZ = ApplyOffsetZ(pickupCheckZ);
                    operationalTracker.BeginZDown(retryTargetZ);
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPick, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), retryTargetZ), "ST709 X11重试下探Z");
                        action.MarkCommandSent();
                        await crane.MoveAbsoluteAsync(-1, -1, retryTargetZ, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), retryTargetZ), "ST709 X11重试下探Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown("ST709 X11重试下探触发下压保护，恢复后实际位置未知");
                        await crane.RecoverFromPressureStopAsync(ct);
                        throw;
                    }
                    operationalTracker.BeginMagnetOn();
                    try
                    {
                        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), retryTargetZ), "ST709 X11重试后充磁");
                        action.MarkCommandSent();
                        await crane.MagnetOnAsync(ct);
                        operationalTracker.CompleteMagnetOn();
                        action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), retryTargetZ), "ST709 X11重试后充磁成功返回");
                    }
                    catch (Exception ex)
                    {
                        operationalTracker.FailMagnetOn();
                        OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                            operationalSite with { ActionStage = "ST709下探后再次充磁" }, operationalTracker, ex,
                            "重试充磁方法异常，实际命令与磁铁状态未知", true);
                        throw;
                    }
                    magnetOn = true;
                }
                Console.WriteLine($"[GrindingEngine] [{craneName}]   等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                operationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await crane.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"ST709充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后成功值（如有）", true);
                    throw;
                }
                physicalTracker?.TryObserveX11(x11);
                if (x11)
                {
                    holdingWorkpiece = true;
                    action.BeginStep(FlowActionStep.ConfirmPickup, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), ApplyOffsetZ(pickupCheckZ)), "X11确认研磨天车持件");
                    action.RecordFeedback(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), ApplyOffsetZ(pickupCheckZ)), true, true, "X11=1确认持件");
                    action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1确认工件由研磨天车持有");
                    action.Confirm();
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   ✓ X11=1 已吸到");
                    break;
                }
                if (retry > 1)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                        operationalSite with { ActionStage = "ST709三次X11均未确认持件" }, operationalTracker, null,
                        "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在ST709", true,
                        holdingWorkpiece: false);
                    throw new Exception($"上料取料失败: 2次充磁后X11仍=0");
                }
                Console.WriteLine($"[GrindingEngine] [{craneName}]   ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ── ⑤c Z 升到安全高度，并行通知PLC已取走 ──
            //    现场确认：启动Z回升后经过配置延时即可写M731释放传送带。
            //    两个任务均成功返回前不得离开ST709继续后续XY动作。
            int safeZ = _cfg.Grinding.SafeZHeight;
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑤c Z升到安全高度 {safeZ}（绝对坐标，不加偏移）");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(null, null, safeZ), "持件后Z上升安全高度");
            action.MarkCommandSent();
            Task zSafeTask = crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            Console.WriteLine($"[GrindingEngine] [{craneName}] Z回升已启动，{_cfg.Grinding.M731NotifyDelayMs}ms后写M731并行通知PLC");
            await Task.Delay(_cfg.Grinding.M731NotifyDelayMs, ct);

            // ── ⑥ 通知PLC已取走: M731=1 → PLC将M730清零+释放传送带 ──
            if (_mc65?.IsConnected != true)
            {
                await zSafeTask;
                action.Confirm(new FlowActionPosition(null, null, safeZ), "持件后Z安全到位，MC65未连接无法写M731");
                physicalTracker?.TryConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST709取料后Z升安全命令成功返回");
                operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST709取料后Z升安全命令成功返回");
                throw new InvalidOperationException("MC65未连接, 工件已吸起但无法写M731通知取料完成");
            }

            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), safeZ), "M731通知ST709已取料");
            action.MarkCommandSent();
            Task m731Task = _mc65.WriteMBitInWordAsync(720, 11, true, ct);
            try
            {
                await Task.WhenAll(zSafeTask, m731Task);
            }
            catch (Exception ex)
            {
                if (zSafeTask.IsCompletedSuccessfully)
                {
                    action.RecordFeedback(new FlowActionPosition(null, null, safeZ), true, true, "ST709取料后Z已安全到位，等待M731异常处理");
                    physicalTracker?.TryConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST709取料后Z升安全命令成功返回");
                    operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST709取料后Z升安全命令成功返回");
                }
                if (!m731Task.IsCompletedSuccessfully)
                    throw new InvalidOperationException($"M731写入失败, 物理工件已取离ST709, 必须暂停人工确认/补写: {ex.Message}", ex);
                throw;
            }
            action.Confirm(new FlowActionPosition(ApplyOffsetX(rackX), ApplyOffsetY(rackY), safeZ), "Z安全到位且M731写入成功返回");
            physicalTracker?.TryConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST709取料后Z升安全命令成功返回");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "ST709取料后Z升安全命令成功返回");
            Console.WriteLine($"[GrindingEngine] Z安全到位 + M731=1 通知PLC取料完成 ✓ {wp.IdentityText}");

            // ── ⑦ 目标XY与最终加工参数并行准备（加偏移）───────────
            if (!TryGetStationCoords(grinder.StationCode, out int gx, out int gy, out int gz))
                throw new InvalidOperationException($"数据库未找到 {grinder.StationCode} 坐标");

            async Task SendTargetGrinderParametersAsync()
            {
                await grinder.Svc!.SendRollerParamsAsync(wp.Diameter, wp.BoreType, wp.Length, ct);
                await grinder.Svc.SetDataSentDoneAsync(ct);
            }

            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑦ 并行准备目标研磨机：XY→({gx}+{_craneOffsetX},{gy}+{_craneOffsetY})，同时下发最终加工参数");
            action.BeginStep(FlowActionStep.MoveXYToTarget, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), $"持件XY到{grinder.StationCode}");
            action.MarkCommandSent();

            OperationalLog.Info("研磨目标并行准备开始", "天车持件XY与最终加工参数链同时启动",
                ("研磨机", grinder.StationCode), ("工件", wp.IdentityText),
                ("XY目标", $"{ApplyOffsetX(gx)}/{ApplyOffsetY(gy)}"),
                ("参数", $"直径={wp.Diameter}; 孔型={wp.BoreType}; 长度={wp.Length}"));
            Task xyMoveTask = crane.MoveAbsoluteAsync(ApplyOffsetX(gx), ApplyOffsetY(gy), -1, ct: ct);
            Task parameterTransferTask = SendTargetGrinderParametersAsync();

            try
            {
                await Task.WhenAll(xyMoveTask, parameterTransferTask);
            }
            catch (Exception ex)
            {
                await ClearTargetGrinderPreparationAsync(grinder, wp, ex);
                throw;
            }

            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), "研磨机目标上方XY到位");
            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), $"{grinder.StationCode}研磨参数与数据完成通知");
            action.MarkCommandSent();
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), "研磨参数与数据完成通知成功返回");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ✓ 目标XY、最终参数与数据传输完成信号均完成，开始目标微调");
            OperationalLog.Info("研磨目标并行准备完成", "XY和最终加工参数链均成功返回，继续目标位微调",
                ("研磨机", grinder.StationCode), ("工件", wp.IdentityText));

            // ── ⑧ 目标位绝对编码器微调 ───────────────────────────
            action.BeginStep(FlowActionStep.FineTuneTarget, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), $"{grinder.StationCode}上料前X绝对编码器微调");
            action.MarkCommandSent();
            try
            {
                await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                    crane, _cfg, _cfg.Grinding.CraneNo, grinder.StationCode, $"研磨天车-{grinder.StationCode}上料放入前",
                    reporter: _exceptionReporter,
                    failureContext: OperationalEventContextFactory.FineTuneFailure(
                        scope: "研磨", engine: "研磨引擎", deviceNo: _cfg.Grinding.CraneNo.ToString(), station: grinder.StationCode,
                        actionStage: $"{grinder.StationCode}上料放入前XY微调", workpiece: wp,
                        source: OperationalEventContextFactory.ConfirmedLocation("ST709", "本物理周期来源"),
                        target: OperationalEventContextFactory.ConfirmedLocation(grinder.StationCode, "已选研磨机目标"),
                        owner: "研磨天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后调用ApplyOffsetZ计算研磨机装料目标"),
                        physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "ST709取料X11=1且研磨机尚未进入上料请求等待", "天车/研磨机上方")),
                    actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            }
            catch (Exception ex)
            {
                await ClearTargetGrinderPreparationAsync(grinder, wp, ex);
                throw;
            }
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), "研磨机上料前微调成功返回");

            // ── ⑨ 等研磨机请求上料 + 门打开 ──────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑨ 目标微调完成，等待请求上料+门打开...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsRequestLoadAsync(ct),
                "请求上料", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   请求上料=1 ✓，等待门打开...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsDoorOpenAsync(ct),
                "门打开", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   门已打开 ✓，准备下降送料");

            // ── ⑩ Z 下降到研磨机装料位置（加 Z 偏移）─────────────
            int loadZ = ComputeGrinderLoadZ(gz, wp.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑩ Z下降到装料位置 {loadZ}+{_craneOffsetZ} (研磨机Z={gz} - d/2={wp.Diameter / 2})");
            int loadTargetZ = ApplyOffsetZ(loadZ);
            operationalTracker.BeginZDown(loadTargetZ);
            try
            {
                action.BeginStep(FlowActionStep.MoveZDownToPlace, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), loadTargetZ), $"{grinder.StationCode}下降放料");
                action.MarkCommandSent();
                await crane.MoveAbsoluteAsync(-1, -1, loadTargetZ, ct: ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), loadTargetZ), "研磨机目标放料Z到位");
            }
            catch (Exception)
            {
                operationalTracker.MarkZUnknown($"{grinder.StationCode}放料Z下降异常，当前Z位置未知");
                throw;
            }

            // ── ⑪ 上料到达锁紧位置 ───────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑪ 上料到达锁紧位置(3s长信号)...");
            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), loadTargetZ), $"{grinder.StationCode} SetLoadInPlace上料到位通知");
            action.MarkCommandSent();
            await grinder.Svc.SetLoadInPlaceAsync(ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), loadTargetZ), "SetLoadInPlace成功返回");

            // ── ⑫ 等锁紧完成 ─────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑫ 等待尾座锁紧完成...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsClampDoneLoadOutAsync(ct),
                "锁紧完成", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   锁紧完成，开始退磁");

            // ── ⑬ 退磁 ──────────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑬ 退磁释放工件...");
            operationalTracker.BeginPlacementMagnetOff(grinder.StationCode);
            try
            {
                action.BeginStep(FlowActionStep.MagnetOffSent, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), loadTargetZ), $"{grinder.StationCode}夹紧后退磁放料");
                action.MarkCommandSent();
                await crane.MagnetOffAsync(ct);
                operationalTracker.CompletePlacementMagnetOff(grinder.StationCode);
                operationalPlacementCommitted = true;
                operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{grinder.StationCode}上料完整交接",
                    "目标位退磁成功返回，放料推定成立；等待Z安全回升和SetLoadDone通知");
            }
            catch (Exception ex)
            {
                operationalTracker.FailMagnetOff();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                    operationalSite with { Station = grinder.StationCode, ActionStage = $"{grinder.StationCode}夹紧后退磁放料" },
                    operationalTracker, ex, "研磨机夹紧后退磁方法异常，工件是否释放未知", true,
                    holdingWorkpiece: null, placed: null, downstreamNotified: null, handoffClosed: null);
                throw;
            }
            magnetOn = false;
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), loadTargetZ), "研磨机目标退磁成功返回");
            placedInGrinder = true; // MagnetOff成功返回后, 按物理现场处理为工件已放入研磨机。
            action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "目标退磁成功，工件已在研磨机等待SetLoadDone闭环");

            // ── ⑭ Z 升到安全高度（不加偏移）───────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑭ Z升到安全高度 {safeZ}（绝对坐标，不加偏移）");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), "研磨机放料后Z升安全高度");
            action.MarkCommandSent();
            await crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), "研磨机放料后Z安全到位");
            operationalTracker.ConfirmSafeZ(safeZ, _cfg.AbsMove.Tolerance, "研磨机放料后Z升安全命令成功返回");

            // ── ⑮ 上料完成 ──────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑮ 上料完成(3s长信号) {wp.IdentityText}");
            action.BeginStep(FlowActionStep.NotifyDownstream, new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), $"{grinder.StationCode} SetLoadDone上料完成通知");
            action.MarkCommandSent();
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "SetLoadDone",
                "SetLoadDone调用已开始，研磨机PLC是否收到结果未知");
            await grinder.Svc.SetLoadDoneAsync(ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), safeZ), "SetLoadDone成功返回");
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "SetLoadDone",
                "SetLoadDone成功返回，研磨机上料完成通知已确认");
            loadDoneNotified = true;
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, $"{grinder.StationCode}上料完整交接",
                "退磁、Z安全回升及SetLoadDone通知均成功返回");

            // ── 完成 → 标记加工中 ─────────────────────────────────
            grinder.State = GrinderState.Machining;     // ← 研磨机进入加工状态
            grinder.StateChangedAt = DateTime.UtcNow;
            wp.ReportStage($"{grinder.Name} 加工中");
            action.Complete("研磨机上料、SetLoadDone和加工状态已完整闭环");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 上料完成，研磨机开始加工 {wp.IdentityText} ═══");
        }
        catch (CraneMotionTimeoutException timeout)
        {
            if (!action.IsFinalized) { action.MarkCommandResponseUnknown(timeout.Message); action.PauseForManualResolution(timeout.Message); }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "研磨天车上料运动超时" }, operationalTracker, timeout,
                "研磨引擎已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                actionSnapshot: action.Snapshot());
            // 位置未知不能按“尚未充磁”回写FIFO；保留工件身份并暂停，等待人工确认。
            if (!IsGrindingActionCurrent(actionVersion)) return;
            _paused = true;
            grinder.State = placedInGrinder ? (loadDoneNotified ? GrinderState.Machining : GrinderState.Loading) : GrinderState.Loading;
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = wp;
            OnSafetyAlarm?.Invoke($"研磨天车给{grinder.Name}上料时{timeout.Stage}运动超时。目标=({timeout.XTarget},{timeout.YTarget},{timeout.ZTarget})，" +
                $"最后坐标={timeout.LastKnownStatus?.XPos}/{timeout.LastKnownStatus?.YPos}/{timeout.LastKnownStatus?.ZPos}，未到位轴={string.Join("/", timeout.UnreachedAxes)}。" +
                "D4518停止指令已尝试发送；研磨引擎已暂停，工件未回FIFO，禁止自动重试。请人工确认天车和工件位置。");
        }
        catch (PressureStopException pEx)
        {
            if (!action.IsFinalized) { action.MarkCommandResponseUnknown(pEx.Message); action.PauseForManualResolution(pEx.Message); }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PRESSURE_STOP_DETECTED",
                operationalSite with { ActionStage = "研磨天车上料下压保护" }, operationalTracker, pEx,
                "研磨引擎已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                actionSnapshot: action.Snapshot());
            // ═══ 下压急停: 天车Z↓时磁铁碰到工件/障碍物, PLC触发D4523 ═══
            if (operationalPlacementCommitted && !loadDoneNotified)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = grinder.StationCode, ActionStage = $"{grinder.StationCode}退磁放料后上料完成未闭环" },
                    operationalTracker, pEx,
                    "退磁成功返回并推定工件已放入研磨机，但后续上料完成闭环未确认", true,
                    holdingWorkpiece: null, placed: null, downstreamNotified: null, handoffClosed: null);
            }
            Console.WriteLine($"══════════════════════════════════════════════");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠⚠⚠ 下压急停(D4523)触发！");
            Console.WriteLine($"[GrindingEngine]   异常: {pEx.Message}");
            Console.WriteLine($"[GrindingEngine]   充磁状态={(magnetOn ? "已充磁(工件在天车上)" : "未充磁(工件在ST709架上)")}");
            Console.WriteLine($"[GrindingEngine]   恢复: ①写D4523=0 ②清除D4514=2→0 ③重新启动引擎");
            if (!IsGrindingActionCurrent(actionVersion))
            {
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] 旧上料动作已被应急取消, 跳过急停状态/缓存写回");
                return;
            }
            if (placedInGrinder)
            {
                Console.WriteLine($"[GrindingEngine] ⚠ 工件 {wp.IdentityText} 已放入研磨机, 保留PendingWorkpiece, 不回缓存");
            }
            else if (holdingWorkpiece || magnetOn)
            {
                Console.WriteLine($"[GrindingEngine] ⚠⚠⚠ 工件 d={wp.Diameter} 已吸在天车上,需人工处理！");
            }
            else
            {
                RequeueWorkpiece(wp); // 未充磁→工件还在架上→放回缓存
                Console.WriteLine($"[GrindingEngine] ↩ 工件 d={wp.Diameter} 放回缓存(未充磁)");
            }
            Console.WriteLine($"══════════════════════════════════════════════");
            _paused = true;                             // 暂停引擎, 等人工清除急停
            grinder.State = placedInGrinder ? (loadDoneNotified ? GrinderState.Machining : GrinderState.Loading)
                : (holdingWorkpiece || magnetOn ? GrinderState.Loading : GrinderState.Idle);
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = (placedInGrinder || holdingWorkpiece || magnetOn) ? wp : null;
            OnSafetyAlarm?.Invoke($"{grinder.Name}上料时触发下压急停。研磨引擎已暂停，工件={grinder.PendingWorkpiece?.IdentityText ?? wp.IdentityText}，请人工处理并复位设备。异常：{pEx.Message}");
        }
        catch (Exception ex)
        {
            // ═══ 通用异常: 网络断/PLC超时/Modbus异常等 ═══
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ✘ 上料流程异常: {ex.Message}");
            if (!action.IsFinalized && action.CommandState != FlowCommandState.NotSent)
            {
                action.MarkCommandResponseUnknown(ex.Message);
                action.PauseForManualResolution(ex.Message);
            }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "研磨天车上料最终异常" }, operationalTracker, ex,
                "研磨引擎已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                actionSnapshot: action.Snapshot());
            if (operationalPlacementCommitted && !loadDoneNotified)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = grinder.StationCode, ActionStage = $"{grinder.StationCode}退磁放料后上料完成未闭环" },
                    operationalTracker, ex,
                    "退磁成功返回并推定工件已放入研磨机，但SetLoadDone未确认", true,
                    holdingWorkpiece: null, placed: null, downstreamNotified: null, handoffClosed: null);
            }
            Console.WriteLine($"[GrindingEngine]   充磁状态={(magnetOn ? "已充磁(工件在天车上)" : "未充磁(工件在ST709架上)")}");
            if (!IsGrindingActionCurrent(actionVersion))
            {
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] 旧上料动作已被应急取消, 跳过状态/缓存写回");
                return;
            }
            if (placedInGrinder)
            {
                // 已放进研磨机后, 即使磁铁已退磁也不能回缓存, 否则同一块板会被再次分配。
                _paused = true;
                grinder.State = loadDoneNotified ? GrinderState.Machining : GrinderState.Loading;
                grinder.PendingWorkpiece = wp;
                Console.WriteLine($"[GrindingEngine] ⚠⚠⚠ 工件 {wp.IdentityText} 已放入研磨机但握手未完全闭环, 暂停等待人工确认");
                OnSafetyAlarm?.Invoke($"{grinder.Name}上料异常：工件{wp.IdentityText}已放入研磨机，但握手未完全闭环。研磨引擎已暂停，请人工确认。异常：{ex.Message}");
            }
            else if (holdingWorkpiece || magnetOn)
            {
                // X11确认吸住后, 异常必须保留在途数据, 由人工确认天车上工件去向。
                _paused = true; // 暂停引擎防止天车带着工件继续动
                grinder.State = GrinderState.Loading;
                grinder.PendingWorkpiece = wp;
                Console.WriteLine($"[GrindingEngine] ⚠⚠⚠ 工件 d={wp.Diameter} 已吸在天车上,引擎暂停,需人工处理！");
                OnSafetyAlarm?.Invoke($"{grinder.Name}上料异常：工件{wp.IdentityText}已在研磨天车上。研磨引擎已暂停，请人工确认天车和工件位置。异常：{ex.Message}");
            }
            else
            {
                // 还没充磁→工件还在ST709架上→自动放回缓存, 等下次分配
                RequeueWorkpiece(wp);
                grinder.State = GrinderState.Idle;
                grinder.PendingWorkpiece = null;
                Console.WriteLine($"[GrindingEngine] ↩ 工件 d={wp.Diameter} 放回缓存(未充磁,自动恢复)");
            }
            grinder.StateChangedAt = DateTime.UtcNow;
        }
        finally
        {
            ReleaseRecordedCraneLock($"{grinder.Name}上料finally", actionVersion); // ← 无论如何释放天车锁, 防止死锁
            FinishGrindingAction(actionVersion, completion);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  下料流程
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// <summary>
    /// 下料流程：天车去研磨机取已加工工件 → 研磨机握手 → 送到下料架 ST710 放下。
    /// <para>11 个步骤：</para>
    /// <para>  ① 等待研磨机门打开</para>
    /// <para>  ② XY+Z 到研磨机取料位置(研磨机Z - d/2)</para>
    /// <para>  ③ 充磁取工件（先吸住，防止松开尾座后工件掉落）</para>
    /// <para>  ④ 下料到达位置(3s长信号) — 通知研磨机 PLC 天车已吸住工件，可松开尾座</para>
    /// <para>  ⑤ 等待尾座松开完成</para>
    /// <para>  ⑥ Z 升到安全高度(绝对Z=400)</para>
    /// <para>  ⑦ XY 移到下料架 ST710（坐标从数据库读）</para>
    /// <para>  ⑧ Z 下降到下料位置</para>
    /// <para>  ⑨ 退磁放下工件</para>
    /// <para>  ⑩ Z 升到安全高度</para>
    /// <para>  ⑪ 下料完成(3s长信号) — 通知研磨机 PLC 工件已放到下料架，可开始下一循环</para>
    /// </summary>
    /// <summary>
    /// 下料全流程: 天车#5→研磨机取成品→等门开→等尾座松开→Z↑→下料架ST710放下→通知PLC完成。
    /// 主循环已设State=Unloading+StateChangedAt, 本方法完成后设State=Idle+清PendingWorkpiece；异常时保留现场数据。
    /// <para>⚠ finally释放_craneLock, 保证天车锁不泄漏。</para>
    /// <para>⚠ 取料失败时(magnetOn=true)无法自动回缓存, 需人工处理。</para>
    /// </summary>
    private async Task UnloadFromGrinderAsync(GrinderContext grinder, int actionVersion,
        CancellationTokenSource actionCts, TaskCompletionSource<bool> completion)
    {
        string actionId = OperationalEventContextFactory.NewActionId("GRIND-UNLOAD");
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker = OperationalEventContextFactory.TryCreatePhysicalTracker();
        using var _ = actionCts;
        var ct = actionCts.Token;
        var wp = grinder.PendingWorkpiece;
        var action = new FlowActionContext(actionId, "研磨天车下料", $"研磨天车#{_cfg.Grinding.CraneNo}",
            wp?.IdentityText ?? "待人工补录", grinder.StationCode, "ST710", "研磨机PendingWorkpiece");
        using var logAction = OperationalLog.BeginAction("研磨", $"{_cfg.Grinding.CraneNo}号天车", actionId,
            wp?.IdentityText ?? "待人工补录", grinder.StationCode, "ST710");
        OperationalLog.Info("搬运动作开始", "已创建研磨下料任务", ("板号与序号", wp?.IdentityText ?? "待人工补录"));
        action.RegisterLock("GrindingCrane");
        var craneName = $"天车#{_cfg.Grinding.CraneNo}";
        bool magnetOn = false;  // 充磁标志: true=成品已吸在天车上, 异常时无法恢复
        bool holdingWorkpiece = false;     // X11确认吸住后, 成品才算真实在天车上
        bool placedOnUnloadRack = false;   // ST710退磁成功后, 成品已在下料架, 不能再当作仍在研磨机
        bool operationalPlacementCommitted = false; // 仅供旁路证据，不参与业务判断
        bool unloadRackNotified = false;   // M721写入成功后, 下料架PLC才算收到放版完成通知
        bool unloadDoneNotified = false;   // SetUnloadDone成功后, 研磨机PLC才算完成闭环
        var operationalTracker = OperationalEventContextFactory.CreatePhysicalCycleTrackerOrDisabled(actionId);
        var operationalSite = OperationalEventContextFactory.CreatePhysicalSiteOrEmpty(() => new OperationalEventContextFactory.OperationalPhysicalEventSite(
            "研磨", "研磨引擎", "研磨天车", _cfg.Grinding.CraneNo.ToString(), grinder.StationCode,
            $"{grinder.StationCode}取料并放到ST710", actionId, $"{actionId}:grinding-unload", wp,
            grinder.StationCode, "ST710", "研磨天车", grinder.StationCode,
            "研磨下料动作已创建", "研磨天车锁"));
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "SetUnloadDone");
        operationalTracker.RegisterMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST710下料完整交接");

        try
        {
            if (!wp.HasValue)
            {
                // 理论上主循环已挡住PendingWorkpiece=null, 这里仍保护锁释放和现场状态。
                _paused = true;
                grinder.State = GrinderState.WaitingForUnload;
                grinder.StateChangedAt = DateTime.UtcNow;
                grinder.WpRecoveryNeeded = true;
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠ PendingWorkpiece=null，无法知道成品直径，暂停等待人工确认后再下料");
                OnSafetyAlarm?.Invoke($"{grinder.Name}请求下料，但PendingWorkpiece为空，无法确认成品身份和直径。研磨引擎已暂停，请人工补录工件信息。");
                return;
            }
            var workpiece = wp.Value;
            operationalSite = operationalSite with { Workpiece = workpiece };
            if (workpiece.Diameter <= 0)
                throw new InvalidOperationException($"成品直径无效 d={workpiece.Diameter}, 不能计算研磨下料Z");
            workpiece.ReportStage($"{grinder.Name} 下料中");

            // ═══ 连接天车(共享连接, 不建重复TCP) ═══
            var crane = _craneCache.GetOrCreateService(_cfg.Grinding.CraneNo);
            if (!crane.IsConnected) await crane.ConnectAsync(ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 开始下料流程 {workpiece.IdentityText} 直径={workpiece.Diameter} ═══");

            // ── ①.0 安全: 断电重启后磁铁可能残留工件, 用X11物理线圈检查 ──
            operationalTracker.BeginX11Stage("取料前残留检查");
            operationalTracker.BeginX11Attempt();
            bool hasExistingWorkpiece;
            try
            {
                hasExistingWorkpiece = await crane.ReadXBitAsync(63497, ct);
                operationalTracker.CompleteX11(hasExistingWorkpiece, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operationalTracker.FailX11();
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                    operationalSite with { ActionStage = $"{grinder.StationCode}取料前X11残留检查" }, operationalTracker, ex,
                    "取料前X11读取失败，当前值无效；保留最后成功值（如有）", true);
                throw;
            }
            physicalTracker?.TryObserveX11(hasExistingWorkpiece);
            if (hasExistingWorkpiece)
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_UNEXPECTED_WORKPIECE",
                    operationalSite with { ActionStage = $"{grinder.StationCode}取料前发现磁铁已有工件" }, operationalTracker, null,
                    "取料前X11成功读取为1，确认研磨天车磁铁上存在身份未知残留工件", false,
                    holdingWorkpiece: true);
                throw new InvalidOperationException($"天车#{_cfg.Grinding.CraneNo} X11=1(磁铁已有工件), 拒绝下料取料防止碰撞");
            }

            if (!TryGetStationCoords(grinder.StationCode, out int gx, out int gy, out int gz))
                throw new InvalidOperationException($"数据库未找到 {grinder.StationCode} 坐标");

            // ── ① 等待研磨机门打开 ──────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ① 等待研磨机门打开...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsDoorOpenAsync(ct),
                "门开", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   门已打开 ✓");

            // ── ② XY到研磨机取料位置, 绝对编码器稳定确认后再Z下降（加偏移）───────────────
            int pickupZ = ComputeGrinderLoadZ(gz, workpiece.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ② 去研磨机取料 XY=({gx}+{_craneOffsetX},{gy}+{_craneOffsetY}) Z={pickupZ}+{_craneOffsetZ}");
            var grSpd = _cfg.GetCraneSpeed(_cfg.Grinding.CraneNo);
            action.BeginStep(FlowActionStep.PreCheck, FlowActionPosition.Unknown, "设置研磨天车下料任务绝对速度");
            action.MarkCommandSent();
            await crane.SetAbsSpeedAsync(
                grSpd.X.Speed, grSpd.X.Accel, grSpd.X.Decel,
                grSpd.Y.Speed, grSpd.Y.Accel, grSpd.Y.Decel,
                grSpd.Z.Speed, grSpd.Z.Accel, grSpd.Z.Decel, ct);
            action.Confirm();
            // 新下料动作首次去研磨机取板前确认Z零位；失败时禁止继续XY靠近设备。
            Console.WriteLine($"[GrindingEngine] [{craneName}] 去{grinder.StationCode}下料取板前确认Z=0±5mm");
            action.BeginStep(FlowActionStep.ReturnSafe, new FlowActionPosition(null, null, 0), $"去{grinder.StationCode}前确认Z回零");
            action.MarkCommandSent();
            await crane.EnsureZAtZeroAsync(5, ct);
            action.Confirm(new FlowActionPosition(null, null, 0), "取料前Z=0已确认");
            physicalTracker?.TryConfirmSafeZ(0, 5, $"{grinder.StationCode}取料前EnsureZAtZeroAsync成功返回");
            action.BeginStep(FlowActionStep.MoveXYToSource,
                new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), 0), $"XY到{grinder.StationCode}下料来源位");
            action.MarkCommandSent();
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(ApplyOffsetX(gx), ApplyOffsetY(gy), -1, ct: c), "XY去研磨机取料位", ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), 0), "研磨机来源位XY到位");
            action.BeginStep(FlowActionStep.FineTuneSource,
                new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), 0), $"{grinder.StationCode}取料前XY微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                crane, _cfg, _cfg.Grinding.CraneNo, grinder.StationCode, $"研磨天车-{grinder.StationCode}下料取料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "研磨", engine: "研磨引擎", deviceNo: _cfg.Grinding.CraneNo.ToString(), station: grinder.StationCode,
                    actionStage: $"{grinder.StationCode}下料取料前XY微调", workpiece: workpiece,
                    source: OperationalEventContextFactory.ConfirmedLocation(grinder.StationCode, "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation("ST710", "本物理周期目标"),
                    owner: "研磨天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后调用ApplyOffsetZ计算研磨机取料目标"),
                    physicalPhase: OperationalEventContextFactory.PickupBeforeZDown(physicalTracker, true, "Z零位检查且研磨机门开已返回", grinder.StationCode)),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), 0), "研磨机来源位XY微调完成");
            int grinderPickupTargetZ = ApplyOffsetZ(pickupZ);
            operationalTracker.BeginZDown(grinderPickupTargetZ);
            action.BeginStep(FlowActionStep.MoveZDownToPick,
                new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), grinderPickupTargetZ), $"{grinder.StationCode}下降取料");
            action.MarkCommandSent();
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, grinderPickupTargetZ, ct: c), "Z降研磨机取料位", ct);
            operationalTracker.CompleteZDown();
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), grinderPickupTargetZ), "研磨机取料Z到位");

            // ── ③ 充磁取工件 ─────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ③ 充磁取工件");
            action.BeginStep(FlowActionStep.MagnetOnSent,
                new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), grinderPickupTargetZ), $"{grinder.StationCode}首次充磁");
            action.MarkCommandSent();
            await CraneOpAsync(crane, async c =>
            {
                operationalTracker.BeginMagnetOn();
                try
                {
                    await crane.MagnetOnAsync(c);
                    operationalTracker.CompleteMagnetOn();
                }
                catch (Exception ex)
                {
                    operationalTracker.FailMagnetOn();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                        operationalSite with { ActionStage = $"{grinder.StationCode}首次充磁取料" }, operationalTracker, ex,
                        "CraneOp实际充磁尝试失败；即使包装器随后重连成功也保留本次真实失败", true);
                    throw;
                }
            }, "充磁", ct);
            magnetOn = true;
            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), grinderPickupTargetZ), "研磨机首次充磁调用成功返回");

            // ── ③b X11检测: 充磁→等3s→查X11→没吸到退磁Z↓5mm重试, 最多2次 ──
            Console.WriteLine($"[GrindingEngine] [{craneName}] ③b 充磁→等3s→X11检测");
            int unlPickupCheckZ = pickupZ;
            operationalTracker.BeginX11Stage("充磁后持件确认");
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry == 0)
                {
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   充磁(X11第1次)");
                }
                else
                {
                    unlPickupCheckZ += 5;
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   退磁→Z↓到{unlPickupCheckZ}→充磁");
                    await CraneOpAsync(crane, async c =>
                    {
                        operationalTracker.BeginMagnetOff();
                        try
                        {
                            action.BeginStep(FlowActionStep.MagnetOffSent, FlowActionPosition.Unknown, $"{grinder.StationCode}X11重试前退磁");
                            action.MarkCommandSent();
                            await crane.MagnetOffAsync(c);
                            operationalTracker.CompleteMagnetOff();
                            action.Confirm();
                        }
                        catch (Exception ex)
                        {
                            operationalTracker.FailMagnetOff();
                            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                                operationalSite with { ActionStage = $"{grinder.StationCode}X11重试前退磁" }, operationalTracker, ex,
                                "CraneOp实际退磁尝试失败；实际退磁结果未知", true,
                                holdingWorkpiece: holdingWorkpiece);
                            throw;
                        }
                    }, "退磁(X11重试)", ct);
                    magnetOn = false;
                    int retryTargetZ = ApplyOffsetZ(unlPickupCheckZ);
                    operationalTracker.BeginZDown(retryTargetZ);
                    try
                    {
                        action.BeginStep(FlowActionStep.MoveZDownToPick,
                            new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), retryTargetZ), $"{grinder.StationCode}X11重试下探");
                        action.MarkCommandSent();
                        await crane.MoveAbsoluteAsync(-1, -1, retryTargetZ, ct: ct);
                        operationalTracker.CompleteZDown();
                        action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), retryTargetZ), "X11重试下探Z到位");
                    }
                    catch (PressureStopException)
                    {
                        operationalTracker.MarkZUnknown($"{grinder.StationCode} X11重试下探触发下压保护，恢复后实际位置未知");
                        await crane.RecoverFromPressureStopAsync(ct);
                        throw;
                    }
                    await CraneOpAsync(crane, async c =>
                    {
                        operationalTracker.BeginMagnetOn();
                        try
                        {
                            action.BeginStep(FlowActionStep.MagnetOnSent,
                                new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), retryTargetZ), $"{grinder.StationCode}X11重试后充磁");
                            action.MarkCommandSent();
                            await crane.MagnetOnAsync(c);
                            operationalTracker.CompleteMagnetOn();
                            action.Confirm(new FlowActionPosition(ApplyOffsetX(gx), ApplyOffsetY(gy), retryTargetZ), "X11重试后充磁调用成功返回");
                        }
                        catch (Exception ex)
                        {
                            operationalTracker.FailMagnetOn();
                            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
                                operationalSite with { ActionStage = $"{grinder.StationCode}下探后再次充磁" }, operationalTracker, ex,
                                "CraneOp实际重试充磁失败；实际命令与磁铁状态未知", true);
                            throw;
                        }
                    }, "充磁(X11重试)", ct);
                    magnetOn = true;
                }
                Console.WriteLine($"[GrindingEngine] [{craneName}]   等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                operationalTracker.BeginX11Attempt();
                bool x11;
                try
                {
                    x11 = await crane.ReadXBitAsync(63497, ct);
                    operationalTracker.CompleteX11(x11, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operationalTracker.FailX11();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_READ_FAILED",
                        operationalSite with { ActionStage = $"{grinder.StationCode}充磁后X11第{retry + 1}次读取" }, operationalTracker, ex,
                        "充磁后X11读取失败；本次值无效并保留最后成功值（如有）", true);
                    throw;
                }
                physicalTracker?.TryObserveX11(x11);
                Console.WriteLine($"[GrindingEngine] [{craneName}]   X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    holdingWorkpiece = true;
                    action.BeginStep(FlowActionStep.ConfirmPickup, FlowActionPosition.Unknown, "X11确认研磨天车已从研磨机持件");
                    action.RecordFeedback(FlowActionPosition.Unknown, true, true, "X11=1确认持件");
                    action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "X11=1确认工件由研磨天车持有");
                    action.Confirm();
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   ✓ X11=1 已吸到");
                    break;
                }
                if (retry > 1)
                {
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_X11_NOT_CONFIRMED",
                        operationalSite with { ActionStage = $"{grinder.StationCode}三次X11均未确认持件" }, operationalTracker, null,
                        "三次业务X11读取均有效返回0；只能确认未确认持件，不能推断工件仍在研磨机", true,
                        holdingWorkpiece: false);
                    throw new Exception("下料取料失败: 2次充磁后X11仍=0");
                }
                Console.WriteLine($"[GrindingEngine] [{craneName}]   ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ── ④ 下料到达位置（通知研磨机 PLC：天车已到，请松开尾座）──
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ④ 下料到达位置(3s长信号)...");
            action.BeginStep(FlowActionStep.NotifyDownstream, FlowActionPosition.Unknown, $"通知{grinder.StationCode}天车下料到位");
            action.MarkCommandSent();
            await grinder.Svc!.SetUnloadInPlaceAsync(ct);
            action.Confirm();

            // ── ⑤ 等待尾座松开完成 ─────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑤ 等待尾座松开完成...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsUnclampDoneAsync(ct),
                "松开完成", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   尾座已松开");

            // ── ⑥ Z 升到安全高度（不加偏移）──────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑥ Z升到安全高度 {_cfg.Grinding.SafeZHeight}（绝对坐标，不加偏移）");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new FlowActionPosition(null, null, _cfg.Grinding.SafeZHeight), "研磨机取料后Z升安全高度");
            action.MarkCommandSent();
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, _cfg.Grinding.SafeZHeight, ct: c), "Z升安全高度", ct);
            action.Confirm(new FlowActionPosition(null, null, _cfg.Grinding.SafeZHeight), "研磨机取料后Z安全到位");
            physicalTracker?.TryConfirmSafeZ(_cfg.Grinding.SafeZHeight, _cfg.AbsMove.Tolerance, "研磨机取料后Z升安全命令成功返回");
            operationalTracker.ConfirmSafeZ(_cfg.Grinding.SafeZHeight, _cfg.AbsMove.Tolerance, "研磨机取料后Z升安全命令成功返回");

            // ── ⑦ XY 移到下料架 ST710（加偏移）───────────────
            // 主循环预检到实际放料之间要等待门开、松尾座、Z升安全。
            // ST710可能在这段时间被人工/设备改变状态, 因此动作前必须重新确认。
            if (!await ConfirmGrindingUnloadRackReadyAsync("下料动作前", ct))
                throw new InvalidOperationException("ST710/M720动作前确认失败, 成品仍由天车保持, 暂停等待人工确认");

            if (!TryGetStationCoords("ST710", out int unloadX, out int unloadY, out int unloadRackZ))
                throw new InvalidOperationException("数据库未找到 ST710 下料架坐标");
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑦ XY移到下料架 ST710({unloadX}+{_craneOffsetX},{unloadY}+{_craneOffsetY}) Z基准={unloadRackZ}");
            action.BeginStep(FlowActionStep.MoveXYToTarget,
                new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "持件XY到ST710下料架");
            action.MarkCommandSent();
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), -1, ct: c), "XY去下料架", ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "ST710目标位XY到位");
            action.BeginStep(FlowActionStep.FineTuneTarget,
                new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "ST710放料前XY微调");
            action.MarkCommandSent();
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
                crane, _cfg, _cfg.Grinding.CraneNo, "ST710", "研磨天车-ST710下料架放料前",
                reporter: _exceptionReporter,
                failureContext: OperationalEventContextFactory.FineTuneFailure(
                    scope: "研磨", engine: "研磨引擎", deviceNo: _cfg.Grinding.CraneNo.ToString(), station: "ST710",
                    actionStage: "ST710下料架放料前XY微调", workpiece: workpiece,
                    source: OperationalEventContextFactory.ConfirmedLocation(grinder.StationCode, "本物理周期来源"),
                    target: OperationalEventContextFactory.ConfirmedLocation("ST710", "已确认下料架目标"),
                    owner: "研磨天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后计算unloadZ并调用ApplyOffsetZ"),
                    physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "研磨机取料X11=1且Z已升安全", "天车/ST710上方")),
                actionId: actionId, physicalTracker: physicalTracker, ct: ct);
            action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "ST710放料前XY微调完成");

            // ── ⑧ Z 下降到放料位置（加 Z 偏移）──────────────
            int unloadZ = ComputeUnloadZ(unloadRackZ, workpiece.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑧ Z下降到下料位置 {unloadZ}+{_craneOffsetZ} (基准{unloadRackZ} - 磁铁下降={unloadRackZ - unloadZ})");
            int unloadTargetZ = ApplyOffsetZ(unloadZ);
            operationalTracker.BeginZDown(unloadTargetZ);
            try
            {
                action.BeginStep(FlowActionStep.MoveZDownToPlace,
                    new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), unloadTargetZ), "ST710下降放料");
                action.MarkCommandSent();
                await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, unloadTargetZ, ct: c), "Z降放料", ct);
                operationalTracker.CompleteZDown();
                action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), unloadTargetZ), "ST710放料Z到位");
            }
            catch (Exception)
            {
                operationalTracker.MarkZUnknown("ST710放料Z下降异常，当前Z位置未知");
                throw;
            }

            // ── ⑨ 退磁放下工件 ───────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑨ 退磁放下工件");
            action.BeginStep(FlowActionStep.MagnetOffSent,
                new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), unloadTargetZ), "ST710退磁放料");
            action.MarkCommandSent();
            await CraneOpAsync(crane, async c =>
            {
                operationalTracker.BeginPlacementMagnetOff("ST710");
                try
                {
                    await crane.MagnetOffAsync(c);
                    operationalTracker.CompletePlacementMagnetOff("ST710");
                    operationalPlacementCommitted = true;
                    operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST710下料完整交接",
                        "目标位退磁成功返回，放料推定成立；等待Z安全回升、M721和SetUnloadDone通知");
                }
                catch (Exception ex)
                {
                    operationalTracker.FailMagnetOff();
                    OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "CRANE_MAGNET_OFF_FAILED",
                        operationalSite with { Station = "ST710", ActionStage = "ST710目标位退磁放料" }, operationalTracker, ex,
                        "CraneOp实际退磁尝试失败；工件是否释放未知", true,
                        holdingWorkpiece: null, placed: null,
                        downstreamNotified: null, handoffClosed: null);
                    throw;
                }
            }, "退磁", ct);
            magnetOn = false;
            placedOnUnloadRack = true; // MagnetOff成功返回后, 按物理现场处理为成品已放到ST710。
            action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff, "ST710退磁成功，等待M721和SetUnloadDone闭环");
            action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), unloadTargetZ), "ST710退磁调用成功返回");

            // ── ⑩ Z 升到安全高度（不加偏移）──────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑩ Z升到安全高度 {_cfg.Grinding.SafeZHeight}（绝对坐标，不加偏移）");
            action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece,
                new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "ST710放料后Z升安全高度");
            action.MarkCommandSent();
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, _cfg.Grinding.SafeZHeight, ct: c), "Z升安全高度", ct);
            operationalTracker.ConfirmSafeZ(_cfg.Grinding.SafeZHeight, _cfg.AbsMove.Tolerance, "ST710放料后Z升安全命令成功返回");
            action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "ST710放料后Z安全到位");

            // ── ⑩.5 通知下料架PLC(MC64): Z升安全后写M721=1放版完成 ──
            if (_mc64?.IsConnected != true)
                throw new InvalidOperationException("MC64未连接, 成品已放到ST710但无法写M721");

            // 成品已经物理放到下料架；M721失败时不能继续释放研磨机，避免PLC不知道已有板。
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721",
                "M721写入调用已开始，下料架PLC是否收到结果未知");
            action.BeginStep(FlowActionStep.NotifyDownstream,
                new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "写M721通知ST710放料完成");
            action.MarkCommandSent();
            await _mc64.WriteMBitInWordAsync(720, 1, true, ct);
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "M721",
                "M721写入成功返回，下料架放版通知已确认");
            action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "M721放料完成通知成功返回");
            Console.WriteLine($"[GrindingEngine] [{craneName}]   MC64 M721=1 通知放版完成 ✓ {workpiece.IdentityText}");
            unloadRackNotified = true;

            // ── ⑪ 下料完成（通知研磨机 PLC：工件已放下，可开始下一循环）──
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑪ 下料完成(3s长信号)... {workpiece.IdentityText}");
            operationalTracker.BeginMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "SetUnloadDone",
                "SetUnloadDone调用已开始，研磨机PLC是否收到结果未知");
            action.BeginStep(FlowActionStep.NotifyDownstream,
                new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), $"通知{grinder.StationCode}下料完成");
            action.MarkCommandSent();
            await grinder.Svc.SetUnloadDoneAsync(ct);
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.DownstreamNotification, "SetUnloadDone",
                "SetUnloadDone成功返回，研磨机下料完成通知已确认");
            action.Confirm(new FlowActionPosition(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), _cfg.Grinding.SafeZHeight), "研磨机下料完成通知成功返回");
            unloadDoneNotified = true;
            operationalTracker.CompleteMonitorStep(OperationalMonitorStepKind.PhysicalHandoff, "ST710下料完整交接",
                "退磁、Z安全回升、M721和SetUnloadDone通知均成功返回");

            grinder.State = GrinderState.Idle;
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = null;
            workpiece.ReportStage("已完成", "已完成");
            action.Complete("研磨下料、M721和SetUnloadDone已完整闭环");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 下料完成，研磨机空闲 {workpiece.IdentityText} ═══");
        }
        catch (CraneMotionTimeoutException timeout)
        {
            if (!action.IsFinalized) { action.MarkCommandResponseUnknown(timeout.Message); action.PauseForManualResolution(timeout.Message); }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "研磨天车下料运动超时" }, operationalTracker, timeout,
                "研磨引擎已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                actionSnapshot: action.Snapshot());
            if (!IsGrindingActionCurrent(actionVersion)) return;
            _paused = true;
            grinder.State = placedOnUnloadRack && unloadDoneNotified ? GrinderState.Idle : GrinderState.Unloading;
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = placedOnUnloadRack && unloadRackNotified && unloadDoneNotified ? null : wp;
            OnSafetyAlarm?.Invoke($"研磨天车从{grinder.Name}下料时{timeout.Stage}运动超时。目标=({timeout.XTarget},{timeout.YTarget},{timeout.ZTarget})，" +
                $"最后坐标={timeout.LastKnownStatus?.XPos}/{timeout.LastKnownStatus?.YPos}/{timeout.LastKnownStatus?.ZPos}，未到位轴={string.Join("/", timeout.UnreachedAxes)}。" +
                "D4518停止指令已尝试发送；研磨引擎已暂停，工件状态未自动清除，禁止自动重试。请人工确认天车和工件位置。");
        }
        catch (PressureStopException pEx)
        {
            if (!action.IsFinalized) { action.MarkCommandResponseUnknown(pEx.Message); action.PauseForManualResolution(pEx.Message); }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PRESSURE_STOP_DETECTED",
                operationalSite with { ActionStage = "研磨天车下料下压保护" }, operationalTracker, pEx,
                "研磨引擎已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                actionSnapshot: action.Snapshot());
            // 下压急停：PLC已检测到磁铁接触工件/障碍物
            if (operationalPlacementCommitted && (!unloadRackNotified || !unloadDoneNotified))
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = "ST710", ActionStage = "ST710退磁放料后通知未闭环" },
                    operationalTracker, pEx,
                    "退磁成功返回并推定成品已放到ST710，但下料架M721或研磨机SetUnloadDone未完整确认", true,
                    holdingWorkpiece: null, placed: null,
                    downstreamNotified: null,
                    handoffClosed: null);
            }
            Console.WriteLine($"══════════════════════════════════════════════");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠⚠⚠ 下料时下压急停触发！");
            Console.WriteLine($"[GrindingEngine]   异常详情：{pEx.Message}");
            Console.WriteLine($"[GrindingEngine]   恢复步骤：①写D4523=0 ②清除报警D4514=2→0 ③重新启动");
            Console.WriteLine($"══════════════════════════════════════════════");
            if (!IsGrindingActionCurrent(actionVersion))
            {
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] 旧下料动作已被应急取消, 跳过急停状态/Pending写回");
                return;
            }
            _paused = true;
            // 安全恢复: 不清PendingWorkpiece。根据物理位置保留等待人工确认, 避免成品在天车/下料架时软件放行新任务。
            grinder.State = placedOnUnloadRack && unloadDoneNotified ? GrinderState.Idle
                : (holdingWorkpiece || magnetOn || placedOnUnloadRack ? GrinderState.Unloading : GrinderState.WaitingForUnload);
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = placedOnUnloadRack && unloadRackNotified && unloadDoneNotified ? null : wp;
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠ 下料急停后保留状态={grinder.State}, PendingWorkpiece={grinder.PendingWorkpiece?.IdentityText ?? "null"}");
            OnSafetyAlarm?.Invoke($"{grinder.Name}下料时触发下压急停。研磨引擎已暂停，状态={grinder.State}，工件={grinder.PendingWorkpiece?.IdentityText ?? "未知"}，请人工处理并复位设备。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ✘ 下料流程异常：{ex.Message}");
            if (!action.IsFinalized && action.CommandState != FlowCommandState.NotSent)
            {
                action.MarkCommandResponseUnknown(ex.Message);
                action.PauseForManualResolution(ex.Message);
            }
            OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "ENGINE_FINAL_FAILURE",
                operationalSite with { ActionStage = "研磨天车下料最终异常" }, operationalTracker, ex,
                "研磨引擎已暂停，异常页面应以动作账本确认工件归属、步骤和锁。", true,
                actionSnapshot: action.Snapshot());
            if (operationalPlacementCommitted && (!unloadRackNotified || !unloadDoneNotified))
            {
                OperationalEventContextFactory.TryReportPhysical(_exceptionReporter, "PHYSICAL_HANDOFF_NOT_CLOSED",
                    operationalSite with { Station = "ST710", ActionStage = "ST710退磁放料后通知未闭环" },
                    operationalTracker, ex,
                    "退磁成功返回并推定成品已放到ST710，但下料架M721或研磨机SetUnloadDone未完整确认", true,
                    holdingWorkpiece: null, placed: null,
                    downstreamNotified: null,
                    handoffClosed: null);
            }
            if (!IsGrindingActionCurrent(actionVersion))
            {
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] 旧下料动作已被应急取消, 跳过状态/Pending写回");
                return;
            }
            _paused = true;
            // 未吸住: 成品大概率还在研磨机内, 保持WaitingForUnload。
            // 已吸住未放: 成品在天车上, 保持Unloading并暂停。
            // 已放ST710但未闭环: 成品在下料架, 保持Unloading并要求人工补写/确认M721或SetUnloadDone。
            grinder.State = placedOnUnloadRack && unloadDoneNotified ? GrinderState.Idle
                : (holdingWorkpiece || magnetOn || placedOnUnloadRack ? GrinderState.Unloading : GrinderState.WaitingForUnload);
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = placedOnUnloadRack && unloadRackNotified && unloadDoneNotified ? null : wp;
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠ 下料异常后已暂停, 状态={grinder.State}, placedOnUnloadRack={placedOnUnloadRack}, unloadRackNotified={unloadRackNotified}, unloadDoneNotified={unloadDoneNotified}");
            OnSafetyAlarm?.Invoke($"{grinder.Name}下料流程异常。研磨引擎已暂停，状态={grinder.State}，工件={grinder.PendingWorkpiece?.IdentityText ?? "未知"}。异常：{ex.Message}");
        }
        finally
        {
            ReleaseRecordedCraneLock($"{grinder.Name}下料finally", actionVersion);
            if (IsGrindingActionCurrent(actionVersion))
                _fastNextCycle = true; // 下料完成, 通知主循环快速检查上料任务
            FinishGrindingAction(actionVersion, completion);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════════════════════════════

    /// <summary>轮询等待某个信号为 true，超时抛出 TimeoutException。紧轮询(SignalPollIntervalMs默认50ms)。</summary>
    private async Task WaitForSignalAsync(
        Func<Task<bool>> check, string signalName, int timeoutMs, CancellationToken ct)
    {
        int pollMs = _cfg.Grinding.SignalPollIntervalMs; // 紧轮询, 不等主循环500ms
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await check()) return;
            await Task.Delay(pollMs, ct);
        }
        throw new TimeoutException($"等待信号超时({timeoutMs / 1000}s)：{signalName}");
    }

    /// <summary>
    /// 带诊断日志的轮询等待。
    /// TypeA 每次轮询读取一次状态快照，既判断信号也按变化/周期输出诊断日志。
    /// </summary>
    /// <summary>带诊断日志的紧轮询等待。TypeA使用同一次快照判断和显示，避免重复读DI。</summary>
    private async Task WaitForGrinderSignalAsync(
        GrinderContext grinder,
        Func<Task<bool>> check, string signalName, int timeoutMs, CancellationToken ct)
    {
        int pollMs = _cfg.Grinding.SignalPollIntervalMs; // 紧轮询(默认50ms)
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int pollCount = 0;
        int logEvery = Math.Max(1, 5000 / Math.Max(1, pollMs));
        string? lastLogKey = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            pollCount++;

            // TypeA：所有状态都在40001一个字内, 同一次快照既用于判断也用于诊断。
            if (grinder.GrinderType == PlcGrinderService.GrinderType.TypeA && grinder.Svc != null)
            {
                try
                {
                    var s = await grinder.Svc.ReadTypeAStatusSnapshotAsync(ct);
                    string logKey = s.ToSignalText();
                    if (!string.Equals(lastLogKey, logKey, StringComparison.Ordinal) || pollCount == 1 || pollCount % logEvery == 1)
                    {
                        lastLogKey = logKey;
                        Console.WriteLine($"[GrindingEngine] [{grinder.Name}] #{pollCount} TypeA快照 {s.ToSignalText()} 等待:{signalName}");
                    }

                    if (TryGetTypeASignalValue(s, signalName, out bool ready))
                    {
                        if (ready) return;
                        await Task.Delay(pollMs, ct);
                        continue;
                    }
                }
                catch { /* 快照读取失败不阻塞, 继续走原check并由外层超时保护 */ }
            }

            if (await check()) return;
            await Task.Delay(pollMs, ct);
        }
        throw new TimeoutException($"等待信号超时({timeoutMs / 1000}s)：{signalName}");
    }

    private static bool TryGetTypeASignalValue(PlcGrinderService.TypeAStatusSnapshot s, string signalName, out bool ready)
    {
        if (signalName.Contains("请求数据", StringComparison.Ordinal))
            ready = s.ReqData;
        else if (signalName.Contains("请求上料", StringComparison.Ordinal))
            ready = s.ReqLoad;
        else if (signalName.Contains("锁紧", StringComparison.Ordinal))
            ready = s.Clamped;
        else if (signalName.Contains("请求下料", StringComparison.Ordinal))
            ready = s.ReqUnload;
        else if (signalName.Contains("松开", StringComparison.Ordinal))
            ready = s.Unclamp;
        else if (signalName.Contains("门", StringComparison.Ordinal))
            ready = s.Door;
        else if (signalName.Contains("加工", StringComparison.Ordinal))
            ready = s.Busy;
        else
        {
            ready = false;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 天车操作防御包装: 自动重连+重试一次。
    /// CraneConnectionCache共享连接不创建重复TCP, ConnectAsync内部检查IsConnected跳过已连。
    /// 仅捕获网络相关异常(IO/超时/Socket断开), 其他异常(如PressureStop)直接向上抛。
    /// </summary>
    private async Task CraneOpAsync(CraneService crane, Func<CancellationToken, Task> action, string desc, CancellationToken ct)
    {
        try
        {
            // 连接断了才重连, 复用CraneConnectionCache共享实例不建重复TCP
            if (!crane.IsConnected)
            {
                Console.WriteLine($"[GrindingEngine] 天车「{desc}」前检测未连接, 正在重连...");
                await crane.ConnectAsync(ct);
            }
            await action(ct);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException || ex is SocketException)
        {
            // 网络瞬断: 重连后重试一次, 失败则向上抛让上层catch兜底
            Console.WriteLine($"[GrindingEngine] ⚠ 天车操作「{desc}」网络异常: {ex.Message}, 重连后重试一次...");
            await crane.ConnectAsync(ct);
            await action(ct);
            Console.WriteLine($"[GrindingEngine] 天车重连后「{desc}」成功 ✓");
        }
    }

    private async Task<bool> EnsureGrindingCranePositionReadyAsync(string context, CancellationToken ct)
    {
        int craneNo = _cfg.Grinding.CraneNo;
        var crane = _craneCache.GetOrCreateService(craneNo);
        var alarm = await CraneZeroPositionGuard.CheckAsync(crane, craneNo, "研磨天车", context, ct);
        if (alarm == null) return true;

        PauseForCraneZeroPosition();
        OnCraneZeroPositionDetected?.Invoke(alarm);
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engineCts.Cancel();
        _workpieceDisplay.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engineCts.Dispose();
        _craneLock.Dispose();
        _grindingDispatchGate.Dispose();
        Console.WriteLine("[GrindingEngine] 引擎已释放");
    }
}

// ═══════════════════════════════════════════════════════════════════
//  辅助类型
// ═══════════════════════════════════════════════════════════════════

/// <summary>单台研磨机上下文。主循环线程写状态, fire-and-forget后台线程读</summary>
public sealed class GrinderContext
{
    public string StationCode { get; }                    // 站号: ST701~ST704
    public string Name { get; }                           // 显示名称: "研磨机X(新代/西门子)"
    public PlcGrinderService.GrinderType GrinderType { get; } // TypeA=西门子(批量DI读) TypeB=新代(4次独立读)
    /// <summary>当前状态(Idle→Loading→Machining→WaitingForUnload→Unloading→Idle)。主循环写, 后台流程读</summary>
    public GrinderState State { get; set; } = GrinderState.Idle;
    /// <summary>GrinderPoll注入的共享Modbus服务(不自建连接, 避免西门子单连接冲突)</summary>
    public PlcGrinderService? Svc { get; set; }
    /// <summary>正在处理的工件(Loading/Unloading中有效)。null时FindReadyGrinder才能分配新任务</summary>
    public WorkpieceCache? PendingWorkpiece { get; set; }
    /// <summary>引擎重启后CNC仍在加工/等待下料但PendingWorkpiece=null→需人工确认工件参数后手动下料</summary>
    public bool WpRecoveryNeeded { get; set; }

    /// <summary>TypeA: 最近一次DI寄存器原始值(b9请求数据 b12请求下料 b14加工中 b15门开)</summary>
    public int LastDi { get; set; }
    /// <summary>TypeB: R7304最近值(请求下料), 非0=已加工完请求下料</summary>
    public int LastR7304 { get; set; }
    /// <summary>最近一次请求数据信号: TypeA=40001-9 / TypeB=R7301。PLC联机就绪=1</summary>
    public bool LastRequestData { get; set; }
    /// <summary>TypeA西门子最近一次机台模式: 40001-3，0=单机、1=联机。TypeB不使用此条件。</summary>
    public bool LastOnlineMode { get; set; }
    /// <summary>最近一次加工中信号: TypeA=40001-14 / TypeB=R7306。PLC正在执行加工程序=1</summary>
    public bool LastMachining { get; set; }
    /// <summary>最近一次门开信号: TypeA=40001-15 / TypeB=R7307。研磨机防护门已开=1</summary>
    public bool LastDoorOpen { get; set; }
    /// <summary>TypeA最近一次磨石1厚度报警: 40001-1。仅禁止本机接收新的自动上料。</summary>
    public bool LastGrindStone1Alarm { get; set; }
    /// <summary>TypeA最近一次磨石2厚度报警: 40001-2。仅禁止本机接收新的自动上料。</summary>
    public bool LastGrindStone2Alarm { get; set; }
    /// <summary>任一磨石报警时为true；不影响本机已加工工件的下料。</summary>
    public bool HasGrindStoneAlarm => LastGrindStone1Alarm || LastGrindStone2Alarm;
    /// <summary>保护磨石报警状态与取料前二次确认，避免旧扫描覆盖新报警。</summary>
    internal object GrindStoneAlarmGate { get; } = new();
    /// <summary>持续磨石报警的最近一次心跳日志UTC时间。</summary>
    public DateTime LastGrindStoneAlarmHeartbeatAtUtc { get; set; } = DateTime.MinValue;
    /// <summary>状态扫描版本。超时/失败会递增版本, 防止旧扫描任务晚返回后覆盖已清空的信号。</summary>
    public long SignalScanVersion;
    /// <summary>进入当前状态的时间戳(UTC)，用于按状态观察时间检测；超时后暂停并保留状态。</summary>
    public DateTime StateChangedAt { get; set; } = DateTime.UtcNow;

    public GrinderContext(string code, string name, PlcGrinderService.GrinderType type)
    {
        StationCode = code; Name = name; GrinderType = type;
    }
}

/// <summary>研磨机状态</summary>
public enum GrinderState
{
    Idle,             // 空闲
    Loading,          // 天车正在送料
    Machining,        // 加工中
    WaitingForUnload, // 加工完等待下料
    Unloading,        // 天车正在下料
}

/// <summary>研磨工件缓存数据</summary>
public struct WorkpieceCache
{
    public string PlateNo;       // 版号, 用于异常恢复时和主页面任务对应
    public string Sequence;      // 序号, 同一版号多件时用于区分; 允许ERP携带英文/符号
    public double Diameter;      // 直径 mm (保留小数精度直传CNC)
    public int BoreType;         // 1=大孔(100) 2=小孔(70)
    public double Length;        // 长度 mm (保留小数精度直传CNC)
    public string MarkingContent;// 刻印内容
    public double LeftPlugThickness;   //左堵厚 mm
    public double RightPlugThickness;  //右堵厚 mm
    public double InnerTaper;          //内孔锥度, 正常双头镗任务按*100写R2046
    public double CornerSize;          //圆角大小, 正常双头镗任务按*100写R2048
    public string BoringProcess; // 镗孔工艺
    public string SkewBedProcess; // 斜床工艺
    public bool SkipBoring;      // 跳过双头镗: 叉→Pos2→天车取→打号→中转架
    public bool ForceBalancing;  // 人工指定必须做动平衡: 不改变线路/斜床加工, 只让后端下料强制走M817/M818
    public Action<string, string>? ReportProgress; // UI进度旁路回调: 只显示状态, 不参与任何业务判断
    public string IdentityText => string.IsNullOrWhiteSpace(PlateNo) ? "版号=未知" : $"版号={PlateNo} 序号={Sequence}";

    /// <summary>
    /// 上报工件大阶段。该方法只用于主页面显示, 失败只能写日志, 不能影响天车/PLC/缓存业务。
    /// </summary>
    public void ReportStage(string step, string state = "运行中")
    {
        try
        {
            ReportProgress?.Invoke(step, state);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorkpieceProgress] {IdentityText} 上报失败: {ex.Message}");
        }
    }
}
