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
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

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
        Dictionary<string, MachineManagementRowVm> stationCoords, McConnectionCache mcc)
    {
        _craneCache = craneCache;
        _cfg = cfg;
        _stationCoords = stationCoords;
        _mcCache = mcc;

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
            $"信号: 请求数据={g.LastRequestData} 请求下料={(g.LastR7304 != 0 || ((g.LastDi >> 12) & 1) == 1)} 加工中={g.LastMachining} 门开={g.LastDoorOpen} DI=0x{g.LastDi:X4} R7304={g.LastR7304}\n" +
            $"研磨天车锁={CraneLockText()}\n" +
            "提示: 应急处理会先清研磨机PLC输出/参数, 成功后清Pending和状态; 失败时需要二次确认是否仅清软件。";
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
            catch (Exception ex)
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
    ///   ② 卡死检测（Loading/Unloading/WaitingForUnload 超过安全观察时间 → 暂停并保留状态）
    ///   ③ 加工监视（Machining + 请求下料=1 → WaitingForUnload）
    ///   ④ 【优先】下料（WaitingForUnload + 天车空闲 → 取料放到 ST710）
    ///   ⑤ 【次之】上料（Idle+请求数据=1 + 天车空闲 → ST709 取料送研磨机）
    /// </para>
    /// <para>
    /// 设计要点：
    ///   - 下料优先于上料：研磨机被成品占着必须优先卸出，否则永远无法接新单
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
                    if (g.LastDoorOpen && (g.State == GrinderState.Loading || g.State == GrinderState.Machining || g.State == GrinderState.WaitingForUnload))
                        extra += ",门开";
                    string conn = g.Svc?.IsConnected == true ? "✓" : "✗";
                    return $"{g.StationCode}:{g.State}{extra}({conn})";
                }));
                if (_cycleCount % 10 == 1) Console.WriteLine($"[GrindingEngine] 扫描 缓存={CachedCount} | {summary}");

                // ═══════════════════════════════════════════════════════════
                //  ② 卡死检测(专用配置GrindingStuckTimeoutMs)
                //    安全第一: 现场运动可能很慢, 这里只做“暂停+保留状态”, 绝不强制Idle/清缓存。
                //    研磨上下料存在工件在天车/机台/下料架三种物理位置, 超时不能推断工件已经安全。
                // ═══════════════════════════════════════════════════════════
                var stuckTimeoutMs = Math.Max(_cfg.Grinding.GrindingStuckTimeoutMs, 10 * 60 * 1000);
                var stuckTimeout = TimeSpan.FromMilliseconds(stuckTimeoutMs);
                bool pausedByTimeout = false;
                foreach (var g in _grinders)
                {
                    if ((g.State == GrinderState.Loading || g.State == GrinderState.Unloading || g.State == GrinderState.WaitingForUnload)
                        && DateTime.UtcNow - g.StateChangedAt > stuckTimeout)
                    {
                        Console.WriteLine($"[GrindingEngine] ⚠ [{g.Name}] 状态={g.State} 超过安全观察时间 {stuckTimeout.TotalSeconds}s(配置{_cfg.Grinding.GrindingStuckTimeoutMs}ms, 实际按更长保护值)，暂停等待人工确认");
                        Console.WriteLine($"[GrindingEngine] ⚠ [{g.Name}] 保留 PendingWorkpiece={g.PendingWorkpiece?.IdentityText ?? "null"}，不回Idle、不清缓存，避免慢动作时数据/现场断开");
                        g.StateChangedAt = DateTime.UtcNow;
                        _paused = true;
                        OnSafetyAlarm?.Invoke($"{g.Name}状态={g.State}超过安全观察时间{stuckTimeout.TotalSeconds:F0}秒。研磨引擎已暂停，PendingWorkpiece={g.PendingWorkpiece?.IdentityText ?? "null"}，请人工确认现场。");
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
                //  ④ 优先：下料（WaitingForUnload + 天车空闲 → 取料放ST710研磨机下料架
                //  下料架MC64: M720=1同时表示无板且允许天车放版
                //  成品工件占着研磨机，必须优先卸出才能接下一单
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

                // ═══════════════════════════════════════════════════════════
                //  ⑤ 上料: Idle+请求数据=1 + M730=1 + 缓存有数据 + 天车空闲 → 取料送研磨机
                //    检查顺序: ①研磨机就绪→②M730有板→③缓存有数据→④抢天车锁(条件满足后再抢,减少无效竞争)
                // ═══════════════════════════════════════════════════════════
                // ── 先检查条件(锁外), 条件满足再抢锁 ──
                //找到可用的研磨机 空闲 请求数据
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
                    // 等待分支也必须节流。否则这里会跳过循环尾部延时，持续高速扫描4台PLC，
                    // 反而挤占正常握手、页面状态读取和其它共享通信任务。
                    await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
                    continue;
                }

                // ── 检查研磨上料架3号位M730是否有板(MC65读, 传送带末端可取位) ──
                // 主循环这里只做派发前预检; 真正去ST709取料前会再读一次M730,
                // 防止预检后链条/现场信号变化导致天车空取。
                bool m730HasPlate = await ConfirmGrindingFeedReadyAsync("主循环预检", ct);
                if (!m730HasPlate)
                {
                    await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);
                    continue; // M730无板→等下轮
                }

                // ── 条件全部满足, 抢天车锁 ──
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
                            Console.WriteLine($"[GrindingEngine] [{ready.Name}] 应急/暂停已生效, 放弃本次上料派发并释放天车锁");
                        }
                        else if (!await EnsureGrindingCranePositionReadyAsync("研磨上料任务出队前", ct))
                        {
                            // 必须在FIFO出队和研磨机状态变化前校验，报警时任务仍留在原缓存。
                            _craneLock.Release();
                        }
                        // ── 缓存有数据 → 出队上料 ──
                        else if (TryDequeueCache(out var wp))
                        {
                            var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            int actionVersion = BeginGrindingAction(actionCts, completion, "上料", ready.StationCode);
                            Console.WriteLine($"[GrindingEngine] 🚀 分配 {wp.IdentityText} d={wp.Diameter} → {ready.Name}({ready.StationCode}) 请求数据=1 缓存剩余={CachedCount}");
                            ready.State = GrinderState.Loading;
                            ready.StateChangedAt = DateTime.UtcNow;
                            ready.PendingWorkpiece = wp;
                            // 上料动作和完成信号必须在派发门闩内一起登记，避免应急漏等旧动作。
                            _ = ProcessWorkpieceAsync(ready, wp, actionVersion, actionCts, completion);
                        }
                        else
                        {
                            // 缓存空→释放天车锁
                            _craneLock.Release();
                            if (_cycleCount % 10 == 1)
                                Console.WriteLine($"[GrindingEngine] ⏳ 有研磨机{ready.Name}就绪但缓存空, 等待工件入队...");
                        }
                    }
                    finally
                    {
                        if (dispatchGateHeld) _grindingDispatchGate.Release();
                        else _craneLock.Release(); // 等待派发门闩时取消，不能遗留已取得的天车锁。
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
                TryCommitGrinderSignals(g, scanVersion, s.RawDI, s.ReqData, s.ReqUnload ? 1 : 0, s.Busy, s.Door);
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

    private static bool TryCommitGrinderSignals(GrinderContext g, long scanVersion, int rawDi,
        bool requestData, int requestUnload, bool machining, bool doorOpen)
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
        return true;
    }

    /// <summary>
    /// 按优先级找可用的研磨机(Idle+PLC联机就绪+无PendingWorkpiece)。
    /// 条件: State=Idle, LastRequestData=1(PLC就绪), Svc已连接, 无在途工件
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
            && g.PendingWorkpiece == null);        // 无在途工件(后台流程跑完会清)
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
    /// <para>14 个步骤：</para>
    /// <para>  ① 等待研磨机请求数据（避免 PLC 未就绪就写参数）</para>
    /// <para>  ② 检查磨石厚度报警（仅 TypeA，有报警则暂停引擎）</para>
    /// <para>  ③ 写工件参数 + 数据传输完成(3s长信号) — 通知 PLC 参数已下发</para>
    /// <para>  ④ 天车 XY+Z 到上料架 ST709（坐标从数据库读）</para>
    /// <para>  ⑤ 充磁取料（轮询 X6=1 确认充磁到位）</para>
    /// <para>  ⑥ Z 升到安全高度(绝对Z=400)</para>
    /// <para>  ⑦ XY 移到研磨机位置</para>
    /// <para>  ⑧ 等待研磨机请求上料 + 门打开（装载需门开）</para>
    /// <para>  ⑨ Z 下降到装料位置(研磨机Z + d/2)——Z变大=向下，此步 200ms 轮询 X2 下压限位</para>
    /// <para>  ⑩ 上料到达锁紧位置(3s长信号) — 通知 PLC 尾座可夹紧</para>
    /// <para>  ⑪ 等待尾座锁紧完成</para>
    /// <para>  ⑫ 退磁释放工件（轮询 X7=1 + D5029=0 确认退磁到位）</para>
    /// <para>  ⑬ Z 升到安全高度(绝对Z=400)</para>
    /// <para>  ⑭ 上料完成(3s长信号) → 标记研磨机=加工中，PLC 开始加工</para>
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
        using var _ = actionCts;
        var ct = actionCts.Token;
        grinder.PendingWorkpiece = wp; // 标记在途, FindReadyGrinder会跳过此研磨机
        wp.ReportStage($"{grinder.Name} 上料中");
        var craneName = $"天车#{_cfg.Grinding.CraneNo}";
        CraneService? crane = null;
        bool magnetOn = false;  // 充磁标志: true=工件在天车上(异常时无法自动回缓存), false=工件还在ST709架上
        bool holdingWorkpiece = false;  // X11确认吸住后才认为工件真实在天车上, 不能只看“发过充磁命令”
        bool placedInGrinder = false;   // 退磁放入研磨机后, 工件已离开ST709缓存队列, 异常时绝不能回缓存
        bool loadDoneNotified = false;  // SetLoadDone成功后, 研磨机PLC已收到上料完成, 状态可进入Machining

        
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

            // ── ② 检查磨石厚度报警(仅西门子TypeA) ──
            //    有报警→暂停引擎+提示更换磨石, 工件放回缓存(还没取料)
            //    有报警则暂停引擎并提示更换磨石，不继续写入
            if (grinder.GrinderType == PlcGrinderService.GrinderType.TypeA)
            {
                bool stoneAlarm = await grinder.Svc!.HasAnyGrindStoneAlarmAsync(ct);
                if (stoneAlarm)
                {
                    Console.WriteLine($"══════════════════════════════════════════════");
                    Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠⚠⚠ 磨石厚度报警！暂停流程，请更换磨石 ⚠⚠⚠");
                    Console.WriteLine($"══════════════════════════════════════════════");
                    Console.WriteLine($"[GrindingEngine] ⚠ 磨石报警！暂停引擎");
                    _paused = true;
                    OnSafetyAlarm?.Invoke($"{grinder.Name}磨石厚度报警。研磨引擎已暂停，工件{wp.IdentityText}已放回缓存，请更换磨石后恢复。");
                    grinder.State = GrinderState.Idle;
                    grinder.StateChangedAt = DateTime.UtcNow;
                    grinder.PendingWorkpiece = null; // 清PendingWorkpiece, 否则FindReadyGrinder永久排除此研磨机
                    // 工件还在 ST709 架上，放回缓存（finally 会释放锁）
                    RequeueWorkpiece(wp);
                    Console.WriteLine($"[GrindingEngine] ↩ 工件 d={wp.Diameter} 放回缓存（磨石报警，未取料）");
                    return;  // 中止上料流程
                }
            }

            // ── ③ 补充长度后写工件参数 ──
            //    M3Flow(M700来源)Length=0: 先从D200(位置②测长)读取长度
            //    再一次性写直径/版孔/长度到研磨机PLC(40012/R7316等)
            if (wp.Diameter <= 0)
                throw new InvalidOperationException($"工件直径无效 d={wp.Diameter}, 不能计算取放料Z");
            if (wp.Length == 0)
            {
                if (_mc65?.IsConnected != true)
                    throw new InvalidOperationException("工件长度为0且MC65未连接, 不能从D200补长");

                // M3放板时允许Length=0, 但研磨参数下发前必须用2号位D200补到有效长度。
                var d200 = await _mc65.ReadAsync(MitsubishiMcClient.DeviceD, 200, 1, ct);
                double len = d200.IntValues.Length > 0 ? d200.IntValues[0] : 0;
                if (len <= 0)
                    throw new InvalidOperationException($"D200测长无效 len={len}, 暂停避免向研磨机发送长度0");

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
                Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ③ D200测长读数={len}mm → wp.Length={wp.Length}mm");
            }
            // 写研磨参数前再确认ST709/M730仍有板。
            // 主循环预检只说明“派发当时”有板; 补长度/等待请求数据期间链条可能滚动或信号变化。
            // 若此处无板/读取失败, 工件还没被天车吸走, 外层会把缓存放回队头, 不让研磨机收到一套没有对应物理板的数据。
            if (!await ConfirmGrindingFeedReadyAsync("写研磨参数前", ct))
                throw new InvalidOperationException("ST709/M730写参数前确认无版或读取失败, 工件未取走, 已回缓存等待下轮");

           

            // ── ④ 天车去研磨上料架3号位ST709取料(传送带末端=可拾取位) ──
            //    安全: 先读X11确认磁铁无残留(断电重启保护)
            if (await crane.ReadXBitAsync(63497, ct))
                throw new InvalidOperationException($"天车#{_cfg.Grinding.CraneNo} X11=1(磁铁已有工件), 拒绝取料防止碰撞");

            // 主循环预检到真正取料之间会先写研磨参数、可能还会补长度。
            // 这段时间链条/现场信号可能变化, 所以天车动作前必须重新确认ST709/M730有版。
            if (!await ConfirmGrindingFeedReadyAsync("上料动作前", ct))
                throw new InvalidOperationException("ST709/M730动作前确认无版或读取失败, 工件未取走, 已回缓存等待下轮");

            if (!TryGetStationCoords("ST709", out int rackX, out int rackY, out int rackZ))
                throw new InvalidOperationException("数据库未找到 ST709 上料架坐标");
            Console.WriteLine($"[GrindingEngine] [{craneName}] ④ 去上料架3号位ST709({rackX},{rackY}) 取料 Z基准={rackZ}...");
            var grSpd = _cfg.GetCraneSpeed(_cfg.Grinding.CraneNo);
            await crane.SetAbsSpeedAsync(
                grSpd.X.Speed, grSpd.X.Accel, grSpd.X.Decel,
                grSpd.Y.Speed, grSpd.Y.Accel, grSpd.Y.Decel,
                grSpd.Z.Speed, grSpd.Z.Accel, grSpd.Z.Decel, ct);

            // XY先到上料架位置, 绝对编码器稳定确认后再下降取料。
            int pickupZ = ComputePickupZ(rackZ, wp.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ④ 目标 Z={pickupZ} (基准{rackZ}) 偏移({_craneOffsetX},{_craneOffsetY},{_craneOffsetZ})");
            // 新上料动作首次跨工位横移前确认Z零位，防止上次异常遗留低Z直接横移。
            Console.WriteLine($"[GrindingEngine] [{craneName}] 去ST709取料前确认Z=0±5mm");
            await crane.EnsureZAtZeroAsync(5, ct);
            await crane.MoveAbsoluteAsync(ApplyOffsetX(rackX), ApplyOffsetY(rackY), -1, ct: ct);
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, _cfg.Grinding.CraneNo, "ST709", "研磨天车-ST709上料架取料前", ct);
            await crane.MoveAbsoluteAsync(-1, -1, ApplyOffsetZ(pickupZ), ct: ct);

            // ── ⑤ 充磁取料 ───────────────────────────────────────
            //    充磁成功=工件已吸附到天车上，之后失败无法自动回缓存
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑤ 充磁取料...");
            await crane.MagnetOnAsync(ct);
            magnetOn = true;
            Console.WriteLine($"[GrindingEngine] [{craneName}]   充磁完成");

            // ── ⑤b X11检测: 充磁→等3s→查X11(63497)→没吸到退磁Z↓5mm重试, 最多2次 ──
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑤b 充磁→等3s→X11检测");
            int pickupCheckZ = pickupZ;
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
                    await crane.MagnetOffAsync(ct); magnetOn = false;
                    try { await crane.MoveAbsoluteAsync(-1, -1, ApplyOffsetZ(pickupCheckZ), ct: ct); }
                    catch (PressureStopException) { await crane.RecoverFromPressureStopAsync(ct); }
                    await crane.MagnetOnAsync(ct); magnetOn = true;
                }
                Console.WriteLine($"[GrindingEngine] [{craneName}]   等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                bool x11 = await crane.ReadXBitAsync(63497, ct);
                if (x11)
                {
                    holdingWorkpiece = true;
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   ✓ X11=1 已吸到");
                    break;
                }
                if (retry > 1) throw new Exception($"上料取料失败: 2次充磁后X11仍=0");
                Console.WriteLine($"[GrindingEngine] [{craneName}]   ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ── ⑤c Z 升到安全高度 ──
            //    M3Flow同款模式: 必须先Z升离传送带, 再写M731释放传送带。
            //    否则下一块板可能立即滑入位置3, 天车还在取料高度存在碰撞风险。
            int safeZ = _cfg.Grinding.SafeZHeight;
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑤c Z升到安全高度 {safeZ}（绝对坐标，不加偏移）");
            await crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);

            // ── ⑥ 通知PLC已取走: M731=1 → PLC将M730清零+释放传送带 ──
            if (_mc65?.IsConnected == true)
            {
                try { await _mc65.WriteMBitInWordAsync(720, 11, true, ct); Console.WriteLine($"[GrindingEngine] M731=1 通知PLC取料完成 ✓ {wp.IdentityText}"); }
                catch (Exception ex) { throw new InvalidOperationException($"M731写入失败, 物理工件已取离ST709, 必须暂停人工确认/补写: {ex.Message}", ex); }
            }
            else
            {
                throw new InvalidOperationException("MC65未连接, 工件已吸起但无法写M731通知取料完成");
            }

            // ── ⑦ XY 移到研磨机位置（加偏移）─────────────────────
            if (!TryGetStationCoords(grinder.StationCode, out int gx, out int gy, out int gz))
                throw new InvalidOperationException($"数据库未找到 {grinder.StationCode} 坐标");
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑦ XY移到研磨机位置({gx}+{_craneOffsetX},{gy}+{_craneOffsetY})");
            await crane.MoveAbsoluteAsync(ApplyOffsetX(gx), ApplyOffsetY(gy), -1, ct: ct);
            //写研磨机加工参数
            //方法体内会把doule类型转为int
            await grinder.Svc!.SendRollerParamsAsync(wp.Diameter, wp.BoreType, wp.Length, ct);
            await grinder.Svc.SetDataSentDoneAsync(ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ③ 工件参数已下发(d={wp.Diameter} L={wp.Length})");

            // ── ⑧ 等研磨机请求上料 + 门打开 ──────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑧ 等待请求上料+门打开...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsRequestLoadAsync(ct),
                "请求上料", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   请求上料=1 ✓，等待门打开...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsDoorOpenAsync(ct),
                "门打开", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   门已打开 ✓，准备下降送料");

            // ── ⑨ Z 下降到研磨机装料位置（加 Z 偏移）─────────────
            int loadZ = ComputeGrinderLoadZ(gz, wp.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑨ Z下降到装料位置 {loadZ}+{_craneOffsetZ} (研磨机Z={gz} - d/2={wp.Diameter / 2})");
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, _cfg.Grinding.CraneNo, grinder.StationCode, $"研磨天车-{grinder.StationCode}上料放入前", ct);
            await crane.MoveAbsoluteAsync(-1, -1, ApplyOffsetZ(loadZ), ct: ct);

            // ── ⑩ 上料到达锁紧位置 ───────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑩ 上料到达锁紧位置(3s长信号)...");
            await grinder.Svc.SetLoadInPlaceAsync(ct);

            // ── ⑪ 等锁紧完成 ─────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑪ 等待尾座锁紧完成...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsClampDoneLoadOutAsync(ct),
                "锁紧完成", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   锁紧完成，开始退磁");

            // ── ⑫ 退磁 ──────────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑫ 退磁释放工件...");
            await crane.MagnetOffAsync(ct);
            magnetOn = false;
            placedInGrinder = true; // MagnetOff成功返回后, 按物理现场处理为工件已放入研磨机。

            // ── ⑬ Z 升到安全高度（不加偏移）───────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑬ Z升到安全高度 {safeZ}（绝对坐标，不加偏移）");
            await crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);

            // ── ⑭ 上料完成 ──────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑭ 上料完成(3s长信号) {wp.IdentityText}");
            await grinder.Svc.SetLoadDoneAsync(ct);
            loadDoneNotified = true;

            // ── 完成 → 标记加工中 ─────────────────────────────────
            grinder.State = GrinderState.Machining;     // ← 研磨机进入加工状态
            grinder.StateChangedAt = DateTime.UtcNow;
            wp.ReportStage($"{grinder.Name} 加工中");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 上料完成，研磨机开始加工 {wp.IdentityText} ═══");
        }
        catch (PressureStopException pEx)
        {
            // ═══ 下压急停: 天车Z↓时磁铁碰到工件/障碍物, PLC触发D4523 ═══
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
        using var _ = actionCts;
        var ct = actionCts.Token;
        var wp = grinder.PendingWorkpiece;
        var craneName = $"天车#{_cfg.Grinding.CraneNo}";
        bool magnetOn = false;  // 充磁标志: true=成品已吸在天车上, 异常时无法恢复
        bool holdingWorkpiece = false;     // X11确认吸住后, 成品才算真实在天车上
        bool placedOnUnloadRack = false;   // ST710退磁成功后, 成品已在下料架, 不能再当作仍在研磨机
        bool unloadRackNotified = false;   // M721写入成功后, 下料架PLC才算收到放版完成通知
        bool unloadDoneNotified = false;   // SetUnloadDone成功后, 研磨机PLC才算完成闭环

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
            if (workpiece.Diameter <= 0)
                throw new InvalidOperationException($"成品直径无效 d={workpiece.Diameter}, 不能计算研磨下料Z");
            workpiece.ReportStage($"{grinder.Name} 下料中");

            // ═══ 连接天车(共享连接, 不建重复TCP) ═══
            var crane = _craneCache.GetOrCreateService(_cfg.Grinding.CraneNo);
            if (!crane.IsConnected) await crane.ConnectAsync(ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 开始下料流程 {workpiece.IdentityText} 直径={workpiece.Diameter} ═══");

            // ── ①.0 安全: 断电重启后磁铁可能残留工件, 用X11物理线圈检查 ──
            if (await crane.ReadXBitAsync(63497, ct))
                throw new InvalidOperationException($"天车#{_cfg.Grinding.CraneNo} X11=1(磁铁已有工件), 拒绝下料取料防止碰撞");

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
            await crane.SetAbsSpeedAsync(
                grSpd.X.Speed, grSpd.X.Accel, grSpd.X.Decel,
                grSpd.Y.Speed, grSpd.Y.Accel, grSpd.Y.Decel,
                grSpd.Z.Speed, grSpd.Z.Accel, grSpd.Z.Decel, ct);
            // 新下料动作首次去研磨机取板前确认Z零位；失败时禁止继续XY靠近设备。
            Console.WriteLine($"[GrindingEngine] [{craneName}] 去{grinder.StationCode}下料取板前确认Z=0±5mm");
            await crane.EnsureZAtZeroAsync(5, ct);
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(ApplyOffsetX(gx), ApplyOffsetY(gy), -1, ct: c), "XY去研磨机取料位", ct);
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, _cfg.Grinding.CraneNo, grinder.StationCode, $"研磨天车-{grinder.StationCode}下料取料前", ct);
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, ApplyOffsetZ(pickupZ), ct: c), "Z降研磨机取料位", ct);

            // ── ③ 充磁取工件 ─────────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ③ 充磁取工件");
            await CraneOpAsync(crane, c => crane.MagnetOnAsync(c), "充磁", ct);
            magnetOn = true;

            // ── ③b X11检测: 充磁→等3s→查X11→没吸到退磁Z↓5mm重试, 最多2次 ──
            Console.WriteLine($"[GrindingEngine] [{craneName}] ③b 充磁→等3s→X11检测");
            int unlPickupCheckZ = pickupZ;
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
                    await CraneOpAsync(crane, c => crane.MagnetOffAsync(c), "退磁(X11重试)", ct);
                    magnetOn = false;
                    try { await crane.MoveAbsoluteAsync(-1, -1, ApplyOffsetZ(unlPickupCheckZ), ct: ct); }
                    catch (PressureStopException) { await crane.RecoverFromPressureStopAsync(ct); }
                    await CraneOpAsync(crane, c => crane.MagnetOnAsync(c), "充磁(X11重试)", ct);
                    magnetOn = true;
                }
                Console.WriteLine($"[GrindingEngine] [{craneName}]   等3s让X11稳定...");
                await Task.Delay(_cfg.Grinding.X11StableDelayMs, ct); // X11稳定延时(配置文件)
                bool x11 = await crane.ReadXBitAsync(63497, ct);
                Console.WriteLine($"[GrindingEngine] [{craneName}]   X11={(x11 ? "1(有版)" : "0(无版)")} (第{retry + 1}次)");
                if (x11)
                {
                    holdingWorkpiece = true;
                    Console.WriteLine($"[GrindingEngine] [{craneName}]   ✓ X11=1 已吸到");
                    break;
                }
                if (retry > 1) throw new Exception("下料取料失败: 2次充磁后X11仍=0");
                Console.WriteLine($"[GrindingEngine] [{craneName}]   ⚠ X11=0 未吸到, 准备下探5mm重试");
            }

            // ── ④ 下料到达位置（通知研磨机 PLC：天车已到，请松开尾座）──
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ④ 下料到达位置(3s长信号)...");
            await grinder.Svc!.SetUnloadInPlaceAsync(ct);

            // ── ⑤ 等待尾座松开完成 ─────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑤ 等待尾座松开完成...");
            await WaitForGrinderSignalAsync(grinder,
                async () => await grinder.Svc!.IsUnclampDoneAsync(ct),
                "松开完成", _cfg.Grinding.HandshakeTimeoutMs, ct);
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}]   尾座已松开");

            // ── ⑥ Z 升到安全高度（不加偏移）──────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑥ Z升到安全高度 {_cfg.Grinding.SafeZHeight}（绝对坐标，不加偏移）");
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, _cfg.Grinding.SafeZHeight, ct: c), "Z升安全高度", ct);

            // ── ⑦ XY 移到下料架 ST710（加偏移）───────────────
            // 主循环预检到实际放料之间要等待门开、松尾座、Z升安全。
            // ST710可能在这段时间被人工/设备改变状态, 因此动作前必须重新确认。
            if (!await ConfirmGrindingUnloadRackReadyAsync("下料动作前", ct))
                throw new InvalidOperationException("ST710/M720动作前确认失败, 成品仍由天车保持, 暂停等待人工确认");

            if (!TryGetStationCoords("ST710", out int unloadX, out int unloadY, out int unloadRackZ))
                throw new InvalidOperationException("数据库未找到 ST710 下料架坐标");
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑦ XY移到下料架 ST710({unloadX}+{_craneOffsetX},{unloadY}+{_craneOffsetY}) Z基准={unloadRackZ}");
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(ApplyOffsetX(unloadX), ApplyOffsetY(unloadY), -1, ct: c), "XY去下料架", ct);
            await XAbsFineTuneHelper.VerifyAndFineTuneAsync(crane, _cfg, _cfg.Grinding.CraneNo, "ST710", "研磨天车-ST710下料架放料前", ct);

            // ── ⑧ Z 下降到放料位置（加 Z 偏移）──────────────
            int unloadZ = ComputeUnloadZ(unloadRackZ, workpiece.Diameter);
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑧ Z下降到下料位置 {unloadZ}+{_craneOffsetZ} (基准{unloadRackZ} - 磁铁下降={unloadRackZ - unloadZ})");
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, ApplyOffsetZ(unloadZ), ct: c), "Z降放料", ct);

            // ── ⑨ 退磁放下工件 ───────────────────────────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑨ 退磁放下工件");
            await CraneOpAsync(crane, c => crane.MagnetOffAsync(c), "退磁", ct);
            magnetOn = false;
            placedOnUnloadRack = true; // MagnetOff成功返回后, 按物理现场处理为成品已放到ST710。

            // ── ⑩ Z 升到安全高度（不加偏移）──────────────
            Console.WriteLine($"[GrindingEngine] [{craneName}] ⑩ Z升到安全高度 {_cfg.Grinding.SafeZHeight}（绝对坐标，不加偏移）");
            await CraneOpAsync(crane, c => crane.MoveAbsoluteAsync(-1, -1, _cfg.Grinding.SafeZHeight, ct: c), "Z升安全高度", ct);

            // ── ⑩.5 通知下料架PLC(MC64): Z升安全后写M721=1放版完成 ──
            if (_mc64?.IsConnected != true)
                throw new InvalidOperationException("MC64未连接, 成品已放到ST710但无法写M721");

            // 成品已经物理放到下料架；M721失败时不能继续释放研磨机，避免PLC不知道已有板。
            await _mc64.WriteMBitInWordAsync(720, 1, true, ct);
            Console.WriteLine($"[GrindingEngine] [{craneName}]   MC64 M721=1 通知放版完成 ✓ {workpiece.IdentityText}");
            unloadRackNotified = true;

            // ── ⑪ 下料完成（通知研磨机 PLC：工件已放下，可开始下一循环）──
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑪ 下料完成(3s长信号)... {workpiece.IdentityText}");
            await grinder.Svc.SetUnloadDoneAsync(ct);
            unloadDoneNotified = true;

            grinder.State = GrinderState.Idle;
            grinder.StateChangedAt = DateTime.UtcNow;
            grinder.PendingWorkpiece = null;
            workpiece.ReportStage("已完成", "已完成");
            Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ═══ 下料完成，研磨机空闲 {workpiece.IdentityText} ═══");
        }
        catch (PressureStopException pEx)
        {
            // 下压急停：PLC已检测到磁铁接触工件/障碍物
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
    /// <summary>最近一次加工中信号: TypeA=40001-14 / TypeB=R7306。PLC正在执行加工程序=1</summary>
    public bool LastMachining { get; set; }
    /// <summary>最近一次门开信号: TypeA=40001-15 / TypeB=R7307。研磨机防护门已开=1</summary>
    public bool LastDoorOpen { get; set; }
    /// <summary>状态扫描版本。超时/失败会递增版本, 防止旧扫描任务晚返回后覆盖已清空的信号。</summary>
    public long SignalScanVersion;
    /// <summary>进入当前状态的时间戳(UTC), 用于卡死检测:超过HandshakeTimeoutMs强制回Idle</summary>
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
