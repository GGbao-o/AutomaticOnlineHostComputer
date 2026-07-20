using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 主线流程引擎 —— 整个自动化产线的"大脑"。
/// <para>
/// 运行在后台 Task 中，不断从任务队列取工件，依次推动经过：
///   Idle → Loading → Boring → Marking → SkewBed → BalanceCheck → Grinding → Done
/// </para>
/// <para>
/// 天车调度通过「绝对位移」模式：从数据库 machine 表查站号坐标 →
/// 设绝对速度 → 写目标坐标（DINT）→ 触发 D4520~D4522 → 轮询到位 → 复位。
/// </para>
/// </summary>
public sealed class ProductionFlowEngine : IDisposable
{
    private readonly ConcurrentQueue<WorkpieceContext> _taskQueue = new();
    private readonly CancellationTokenSource _engineCts = new();
    private readonly CraneConnectionCache _craneCache;
    private readonly ManipulatorConnectionCache _manipulatorCache;
    private readonly IOperationalEventReporter _exceptionReporter;
    private Task? _engineTask;
    private bool _disposed;

    /// <summary>工位坐标缓存（stationCode → machineRow），从数据库加载。</summary>
    private Dictionary<string, MachineManagementRowVm> _stationCoords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>运动参数配置（从 Config/motion_settings.json 加载）</summary>
    private readonly MotionConfig _cfg;

    /// <summary>默认绝对速度 X/Y/Z（mm/s），从配置文件加载。</summary>
    public int DefaultAbsSpeedX { get; set; } = 300;
    public int DefaultAbsAccelX { get; set; } = 150;
    public int DefaultAbsDecelX { get; set; } = 150;
    public int DefaultAbsSpeedY { get; set; } = 300;
    public int DefaultAbsAccelY { get; set; } = 150;
    public int DefaultAbsDecelY { get; set; } = 150;
    public int DefaultAbsSpeedZ { get; set; } = 150;
    public int DefaultAbsAccelZ { get; set; } = 80;
    public int DefaultAbsDecelZ { get; set; } = 80;

    // ═══════════════════════════════════════════════════════════════
    //  预约系统 — 防多天车/机械手同时操作同一工位
    // ═══════════════════════════════════════════════════════════════

    /// <summary>lock 对象，保护 _reservations 字典的并发访问。</summary>
    private readonly object _reservationLock = new();

    /// <summary>
    /// 已预约的工位字典：站号 → (预约者名称, 预约时间)。
    /// 天车/机械手去某工位前先预约，操作完成后释放。
    /// </summary>
    private readonly Dictionary<string, (string Owner, DateTime Time)> _reservations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 预约一个工位。成功返回 true；已被他人预约返回 false。
    /// 同一预约者重复预约同一工位直接返回 true（幂等）。
    /// </summary>
    /// <param name="stationCode">工位站号（如 ST031）</param>
    /// <param name="ownerName">预约者名称（如"1号线天车前"）</param>
    public bool ReserveStation(string stationCode, string ownerName)
    {
        lock (_reservationLock)
        {
            if (_reservations.TryGetValue(stationCode, out var existing))
            {
                if (existing.Owner == ownerName)
                {
                    Console.WriteLine($"[FlowEngine] 🔒 预约 {stationCode} → 已是 {ownerName} 持有（幂等）");
                    return true;
                }

                Console.WriteLine($"[FlowEngine] 🔒 预约失败 {stationCode} → 已被 {existing.Owner} 占用（{existing.Time:HH:mm:ss}）");
                return false;
            }

            _reservations[stationCode] = (ownerName, DateTime.Now);
            Console.WriteLine($"[FlowEngine] 🔒 预约成功 {stationCode} → {ownerName}  [{DateTime.Now:HH:mm:ss}]");
            return true;
        }
    }

    /// <summary>释放工位预约（操作完成后调用）。</summary>
    /// <param name="stationCode">工位站号</param>
    /// <param name="ownerName">预约者名称（校验用，防止误释放）</param>
    public void ReleaseStation(string stationCode, string ownerName)
    {
        lock (_reservationLock)
        {
            if (_reservations.TryGetValue(stationCode, out var existing) && existing.Owner == ownerName)
            {
                _reservations.Remove(stationCode);
                Console.WriteLine($"[FlowEngine] 🔓 释放预约 {stationCode} → {ownerName}  已空闲");
            }
        }
    }

    /// <summary>查询工位是否已被预约。</summary>
    public bool IsStationReserved(string stationCode)
    {
        lock (_reservationLock)
            return _reservations.ContainsKey(stationCode);
    }

    /// <summary>
    /// 等待工位释放（轮询 500ms 间隔，超时返回 false）。
    /// </summary>
    /// <param name="stationCode">工位站号</param>
    /// <param name="ownerName">等待者名称（日志用）</param>
    /// <param name="timeoutMs">超时毫秒，默认 30s</param>
    /// <returns>工位释放后返回 true，超时返回 false</returns>
    public async Task<bool> WaitForStationAsync(string stationCode, string ownerName, int timeoutMs = 30_000, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Console.WriteLine($"[FlowEngine] ⏳ {ownerName} 等待工位 {stationCode} 释放（最长{timeoutMs / 1000}s）...");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsStationReserved(stationCode))
            {
                Console.WriteLine($"[FlowEngine] ⏳ {ownerName} 等待结束：工位 {stationCode} 已空闲");
                return true;
            }

            await Task.Delay(500, ct);
        }

        Console.WriteLine($"[FlowEngine] ⏳ {ownerName} 等待超时：工位 {stationCode} 仍在使用中");
        return false;
    }

    /// <summary>查询工位的 IP 地址，供外部（如 GrinderPoll）使用。</summary>
    public bool TryGetStationIp(string stationCode, out string ip)
    {
        ip = string.Empty;
        if (!_stationCoords.TryGetValue(stationCode, out var row)) return false;
        if (string.IsNullOrWhiteSpace(row.Ip)) return false;
        ip = row.Ip.Trim();
        return true;
    }

    /// <summary>打诊断日志：当前所有预约</summary>
    public void LogReservations()
    {
        lock (_reservationLock)
        {
            if (_reservations.Count == 0)
            {
                Console.WriteLine("[FlowEngine] 📋 当前预约：无");
                return;
            }

            Console.WriteLine($"[FlowEngine] 📋 当前预约（{_reservations.Count}）：");
            foreach (var kv in _reservations)
                Console.WriteLine($"[FlowEngine]    {kv.Key} → {kv.Value.Owner}  ({kv.Value.Time:HH:mm:ss})");
        }
    }

    /// <summary>引擎是否正在运行</summary>
    public bool IsRunning => _engineTask != null && !_engineTask.IsCompleted;

    private readonly PositionUpdateService _posSvc;

    public ProductionFlowEngine(CraneConnectionCache craneCache, ManipulatorConnectionCache manipulatorCache,
        PositionUpdateService positionService, MotionConfig cfg, IOperationalEventReporter exceptionReporter)
    {
        _craneCache = craneCache;
        _manipulatorCache = manipulatorCache;
        _posSvc = positionService;
        _cfg = cfg; // 使用共享配置实例, UI 修改后即时生效
        _exceptionReporter = exceptionReporter;

        Console.WriteLine($"[FlowEngine] 引擎实例已创建（速度走GetCraneSpeed/GetManipulatorSpeed, 未配置设备用AbsMove默认值）");
    }

    /// <summary>
    /// 从数据库 machine 表加载所有工位坐标到内部缓存。
    /// 引擎每次启动前由 HomeViewModel 调用一次。
    /// </summary>
    public void LoadStationCoords(List<MachineManagementRowVm> machineRows)
    {
        _stationCoords = machineRows
            .Where(r => !string.IsNullOrWhiteSpace(r.StationCode))
            .ToDictionary(
                r => r.StationCode.Trim().ToUpperInvariant(),
                r => r,
                StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[FlowEngine] 已加载 {_stationCoords.Count} 个工位坐标");
        foreach (var kv in _stationCoords.Take(10))
            Console.WriteLine($"[FlowEngine]   {kv.Key}: X={kv.Value.X} Y={kv.Value.Y} Z={kv.Value.Z}  ({kv.Value.Name})");
        if (_stationCoords.Count > 10)
            Console.WriteLine($"[FlowEngine]   ... 还有 {_stationCoords.Count - 10} 个工位");
    }

    /// <summary>
    /// 天车自动调度：指挥指定天车走到目标工位的绝对坐标。
    /// <para>流程：查数据库坐标 → 设绝对速度 → 写目标值 → 触发D4520~D4522 → 轮询到位 → 复位 → 完成</para>
    /// </summary>
    /// <param name="craneNo">天车编号 1~5</param>
    /// <param name="stationCode">目标工位站号（如 ST601）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>安全到达返回 true</returns>
    public async Task<bool> MoveCraneToStationAsync(int craneNo, string stationCode, CancellationToken ct = default)
    {
        var craneInfo = _craneCache.GetCraneInfo(craneNo);
        var craneName = craneInfo.Name;

        if (!_stationCoords.TryGetValue(stationCode, out var station))
        {
            Console.WriteLine($"[FlowEngine] [{craneName}] ✘ 站号 {stationCode} 不在坐标缓存中");
            TryReportMoveFinalFailure(
                "天车", craneNo, craneName, stationCode, "坐标解析",
                $"站号 {stationCode} 不在坐标缓存中，无法取得目标XYZ。",
                "绝对移动命令未发送；本调用未确认设备实际位置。",
                null, null, null, reservationReleasePending: false, motionMayHaveStarted: false);
            return false;
        }

        int xTarget = (int)Math.Round(station.X);
        int yTarget = (int)Math.Round(station.Y);
        int zTarget = (int)Math.Round(station.Z);

        Console.WriteLine($"══════════════════════════════════════════");
        Console.WriteLine($"[FlowEngine] [{craneName}] ▶ 自动调度到工位");
        Console.WriteLine($"[FlowEngine]   目标站号={stationCode} 站名={station.Name}");
        Console.WriteLine($"[FlowEngine]   目标坐标 X={xTarget} Y={yTarget} Z={zTarget}");
        Console.WriteLine($"[FlowEngine]   绝对速度 X={DefaultAbsSpeedX}/{DefaultAbsAccelX}/{DefaultAbsDecelX} Y={DefaultAbsSpeedY}/{DefaultAbsAccelY}/{DefaultAbsDecelY} Z={DefaultAbsSpeedZ}/{DefaultAbsAccelZ}/{DefaultAbsDecelZ}");
        Console.WriteLine($"══════════════════════════════════════════");

        // ── 预约机制：尝试预约目标工位，被占用则等待释放 ──────────
        if (!ReserveStation(stationCode, craneName))
        {
            LogReservations();
            if (!await WaitForStationAsync(stationCode, craneName, ct: ct))
            {
                Console.WriteLine($"[FlowEngine] [{craneName}] ✘ 工位 {stationCode} 超时未释放，放弃调度");
                TryReportMoveFinalFailure(
                    "天车", craneNo, craneName, stationCode, "等待工位预约",
                    $"等待工位 {stationCode} 释放超时，未取得本次预约。",
                    "绝对移动命令未发送；本次调度返回false。",
                    xTarget, yTarget, zTarget, reservationReleasePending: false, motionMayHaveStarted: false);
                return false;
            }
            // 等到释放，立刻抢预约
            if (!ReserveStation(stationCode, craneName))
            {
                Console.WriteLine($"[FlowEngine] [{craneName}] ✘ 工位 {stationCode} 被其他等待者抢走");
                TryReportMoveFinalFailure(
                    "天车", craneNo, craneName, stationCode, "重新预约工位",
                    $"工位 {stationCode} 释放后重新预约失败，可能已被其他等待者取得。",
                    "绝对移动命令未发送；本次调度返回false。",
                    xTarget, yTarget, zTarget, reservationReleasePending: false, motionMayHaveStarted: false);
                return false;
            }
        }

        string monitoringStage = "获取天车服务";
        bool monitoringMotionMayHaveStarted = false;
        try
        {
            var craneSvc = _craneCache.GetOrCreateService(craneNo);

            // 1. 安全检查
            monitoringStage = "移动前安全检查";
            var safety = await craneSvc.CheckSafetyAsync(ct);
            if (!safety.AllPassed)
            {
                Console.WriteLine($"[FlowEngine] [{craneName}] ✘ 安全检查未通过：{safety.FailReason}");
                TryReportMoveFinalFailure(
                    "天车", craneNo, craneName, stationCode, "移动前安全检查",
                    $"安全检查未通过：{safety.FailReason}",
                    "绝对移动命令未发送；本次调度返回false。",
                    xTarget, yTarget, zTarget, reservationReleasePending: true, motionMayHaveStarted: false);
                return false;
            }

            // 2. 设置绝对速度
            monitoringStage = "设置绝对速度";
            var crSpd = _cfg.GetCraneSpeed(craneNo);
            await craneSvc.SetAbsSpeedAsync(crSpd.X.Speed, crSpd.X.Accel, crSpd.X.Decel, crSpd.Y.Speed, crSpd.Y.Accel, crSpd.Y.Decel, crSpd.Z.Speed, crSpd.Z.Accel, crSpd.Z.Decel, ct);

            // 3. 读取天车当前坐标（日志用）
            monitoringStage = "读取移动前坐标";
            var before = await craneSvc.ReadStatusAsync(ct);
            if (before != null)
                Console.WriteLine($"[FlowEngine] [{craneName}] 当前坐标 X={before.XPos} Y={before.YPos} Z={before.ZPos}");

            // 4. 执行绝对移动（自动等待到位）
            monitoringStage = "执行绝对移动";
            monitoringMotionMayHaveStarted = true;
            await craneSvc.MoveAbsoluteAsync(xTarget, yTarget, zTarget, ct: ct);

            // 5. 读取到位后坐标（确认）
            monitoringStage = "读取移动后坐标";
            var after = await craneSvc.ReadStatusAsync(ct);
            if (after != null)
            {
                Console.WriteLine($"[FlowEngine] [{craneName}] 到位坐标 X={after.XPos} Y={after.YPos} Z={after.ZPos}");
                Console.WriteLine($"[FlowEngine] [{craneName}] 偏差 ΔX={after.XPos - xTarget} ΔY={after.YPos - yTarget} ΔZ={after.ZPos - zTarget}");
            }

            Console.WriteLine($"[FlowEngine] [{craneName}] ✔ 自动调度完成，已到达 {station.Name}");

            // 到位坐标以PLC实时值为准，不再持久化到crane表。

            return true;
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"[FlowEngine] [{craneName}] ✘ 移动超时：{ex.Message}");
            TryReportMoveFinalFailure(
                "天车", craneNo, craneName, stationCode, monitoringStage,
                $"移动流程在{monitoringStage}阶段发生超时：{ex.Message}",
                "设备运动结果未知；本次调度返回false。",
                xTarget, yTarget, zTarget, reservationReleasePending: true,
                motionMayHaveStarted: monitoringMotionMayHaveStarted,
                exception: ex);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FlowEngine] [{craneName}] ✘ 移动异常：{ex.Message}");
            TryReportMoveFinalFailure(
                "天车", craneNo, craneName, stationCode, monitoringStage,
                $"移动流程在{monitoringStage}阶段发生异常：{ex.Message}",
                "设备运动结果未知；本次调度返回false。",
                xTarget, yTarget, zTarget, reservationReleasePending: true,
                motionMayHaveStarted: monitoringMotionMayHaveStarted,
                exception: ex,
                severity: ex is OperationCanceledException
                    ? OperationalEventSeverity.Information
                    : OperationalEventSeverity.Error);
            return false;
        }
        finally
        {
            // 无论如何都要释放预约，防止工位永久锁定
            ReleaseStation(stationCode, craneName);
        }
    }

    /// <summary>
    /// 机械手自动调度（特殊处理：无 X 轴，XTarget 传 -1 跳过）。
    /// </summary>
    public async Task<bool> MoveManipulatorToStationAsync(int manipulatorNo, string stationCode, CancellationToken ct = default)
    {
        var info = _manipulatorCache.GetManipulatorInfo(manipulatorNo);

        if (!_stationCoords.TryGetValue(stationCode, out var station))
        {
            Console.WriteLine($"[FlowEngine] [{info.Name}] ✘ 站号 {stationCode} 不在坐标缓存中");
            TryReportMoveFinalFailure(
                "机械手", manipulatorNo, info.Name, stationCode, "坐标解析",
                $"站号 {stationCode} 不在坐标缓存中，无法取得目标YZ。",
                "绝对移动命令未发送；本调用未确认设备实际位置。",
                null, null, null, reservationReleasePending: false, motionMayHaveStarted: false);
            return false;
        }

        int yTarget = (int)Math.Round(station.Y);
        int zTarget = (int)Math.Round(station.Z);

        Console.WriteLine($"══════════════════════════════════════════");
        Console.WriteLine($"[FlowEngine] [{info.Name}] ▶ 机械手自动调度到工位");
        Console.WriteLine($"[FlowEngine]   目标站号={stationCode} 站名={station.Name}");
        Console.WriteLine($"[FlowEngine]   目标坐标 Y={yTarget} Z={zTarget}（机械手无X轴，跳过）");
        Console.WriteLine($"══════════════════════════════════════════");

        // ── 预约机制 ──────────────────────────────────────────────
        if (!ReserveStation(stationCode, info.Name))
        {
            LogReservations();
            if (!await WaitForStationAsync(stationCode, info.Name, ct: ct))
            {
                Console.WriteLine($"[FlowEngine] [{info.Name}] ✘ 工位 {stationCode} 超时未释放");
                TryReportMoveFinalFailure(
                    "机械手", manipulatorNo, info.Name, stationCode, "等待工位预约",
                    $"等待工位 {stationCode} 释放超时，未取得本次预约。",
                    "绝对移动命令未发送；本次调度返回false。",
                    null, yTarget, zTarget, reservationReleasePending: false, motionMayHaveStarted: false);
                return false;
            }
            if (!ReserveStation(stationCode, info.Name))
            {
                Console.WriteLine($"[FlowEngine] [{info.Name}] ✘ 工位被抢");
                TryReportMoveFinalFailure(
                    "机械手", manipulatorNo, info.Name, stationCode, "重新预约工位",
                    $"工位 {stationCode} 释放后重新预约失败，可能已被其他等待者取得。",
                    "绝对移动命令未发送；本次调度返回false。",
                    null, yTarget, zTarget, reservationReleasePending: false, motionMayHaveStarted: false);
                return false;
            }
        }

        string monitoringStage = "获取机械手服务";
        bool monitoringMotionMayHaveStarted = false;
        try
        {
            var svc = _manipulatorCache.GetOrCreateService(manipulatorNo);
            monitoringStage = "移动前安全检查";
            var safety = await svc.CheckSafetyAsync(ct);
            if (!safety.AllPassed)
            {
                Console.WriteLine($"[FlowEngine] [{info.Name}] ✘ 安全检查未通过：{safety.FailReason}");
                TryReportMoveFinalFailure(
                    "机械手", manipulatorNo, info.Name, stationCode, "移动前安全检查",
                    $"安全检查未通过：{safety.FailReason}",
                    "绝对移动命令未发送；本次调度返回false。",
                    null, yTarget, zTarget, reservationReleasePending: true, motionMayHaveStarted: false);
                return false;
            }

            monitoringStage = "设置绝对速度";
            var mnSpd = _cfg.GetManipulatorSpeed(manipulatorNo);
            await svc.SetAbsSpeedAsync(mnSpd.X.Speed, mnSpd.X.Accel, mnSpd.X.Decel, mnSpd.Y.Speed, mnSpd.Y.Accel, mnSpd.Y.Decel, mnSpd.Z.Speed, mnSpd.Z.Accel, mnSpd.Z.Decel, ct);
            // 机械手 X 传 -1 跳过 X 轴
            monitoringStage = "执行绝对移动";
            monitoringMotionMayHaveStarted = true;
            await svc.MoveAbsoluteAsync(-1, yTarget, zTarget, ct: ct);

            Console.WriteLine($"[FlowEngine] [{info.Name}] ✔ 机械手自动调度完成，已到达 {station.Name}");
            return true;
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"[FlowEngine] [{info.Name}] ✘ 移动超时：{ex.Message}");
            TryReportMoveFinalFailure(
                "机械手", manipulatorNo, info.Name, stationCode, monitoringStage,
                $"移动流程在{monitoringStage}阶段发生超时：{ex.Message}",
                "设备运动结果未知；本次调度返回false。",
                null, yTarget, zTarget, reservationReleasePending: true,
                motionMayHaveStarted: monitoringMotionMayHaveStarted,
                exception: ex);
            return false;
        }
        finally
        {
            ReleaseStation(stationCode, info.Name);
        }
    }

    /// <summary>
    /// 旁路记录公共移动入口的最终失败。只复制调用点已有数据；任何监控异常均被隔离。
    /// </summary>
    private void TryReportMoveFinalFailure(
        string deviceType,
        int deviceNo,
        string deviceName,
        string stationCode,
        string actionStage,
        string detailMessage,
        string result,
        int? targetX,
        int? targetY,
        int? targetZ,
        bool reservationReleasePending,
        bool motionMayHaveStarted,
        Exception? exception = null,
        OperationalEventSeverity severity = OperationalEventSeverity.Error)
    {
        try
        {
            const string contextReason = "公共移动入口未接收工件上下文";
            OperationalEvidence unavailable = OperationalEvidence.Unavailable(contextReason);
            EvidenceValue<int> targetXEvidence = targetX.HasValue
                ? EvidenceValue<int>.Confirmed(targetX.Value, "来自已加载工位坐标")
                : deviceType == "机械手"
                    ? EvidenceValue<int>.NotApplicable("机械手绝对移动不使用X轴")
                    : EvidenceValue<int>.Unavailable("目标工位坐标未取得");
            EvidenceValue<int> targetYEvidence = targetY.HasValue
                ? EvidenceValue<int>.Confirmed(targetY.Value, "来自已加载工位坐标")
                : EvidenceValue<int>.Unavailable("目标工位坐标未取得");
            string reservationReason = reservationReleasePending
                ? "上报时工位预约当前可能仍持有；原finally将尝试释放；结果尚未确认"
                : "失败发生在取得本次工位预约之前，本次没有预约待释放";
            var reservation = new LockItemEvidence(
                $"工位预约:{stationCode}",
                reservationReleasePending
                    ? EvidenceValue<bool>.Unknown(reservationReason)
                    : EvidenceValue<bool>.Confirmed(false, reservationReason),
                reservationReleasePending
                    ? EvidenceValue<bool>.Unknown(reservationReason)
                    : EvidenceValue<bool>.NotApplicable(reservationReason),
                reservationReleasePending
                    ? "原finally执行结果未知，人工处置前需确认预约和设备实际位置。"
                    : "本次调用未持有目标工位预约。");
            ExceptionEvidence exceptionEvidence = exception == null
                ? unavailable.Exception
                : new ExceptionEvidence(
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    exception.InnerException?.ToString() ?? string.Empty,
                    $"{deviceName}调度至{stationCode}，阶段={actionStage}",
                    exception.StackTrace ?? string.Empty);
            var physicalConclusion = motionMayHaveStarted
                ? new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.CommandResultUnknown,
                    EvidenceAvailability.Unknown,
                    "设备运动物理结果未知",
                    "异常可能发生在运动命令发送前后，调用点没有最终位置确认")
                : new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.CommandNotSent,
                    EvidenceAvailability.Confirmed,
                    "绝对移动命令未发送",
                    "失败出口位于执行绝对移动步骤之前");

            _exceptionReporter.Report(new OperationalEventContext
            {
                EventCode = "ENGINE_FINAL_FAILURE",
                Severity = severity,
                Category = OperationalEventCategory.FinalFailure,
                Scope = "未提供线路",
                Engine = nameof(ProductionFlowEngine),
                DeviceType = deviceType,
                DeviceNo = deviceNo.ToString(),
                Station = stationCode,
                ActionStage = actionStage,
                Title = $"{deviceName}公共移动最终失败",
                Source = deviceType == "天车"
                    ? "ProductionFlowEngine.MoveCraneToStationAsync"
                    : "ProductionFlowEngine.MoveManipulatorToStationAsync",
                CorrelationKey = $"ProductionFlowEngine.Move:{deviceType}:{deviceNo}:{stationCode}:{actionStage}",
                DetailMessage = $"{detailMessage} 目标坐标：X={targetX?.ToString() ?? "未知"}，Y={targetY?.ToString() ?? "未知"}，Z={targetZ?.ToString() ?? "未知"}。{reservationReason}",
                Result = result,
                CapturedException = exception,
                PhysicalConclusion = physicalConclusion,
                BusinessPaused = EvidenceValue<bool>.Unknown("公共移动入口不知道调用方或全局引擎是否已暂停"),
                Evidence = unavailable with
                {
                    Position = unavailable.Position with
                    {
                        TargetX = targetXEvidence,
                        TargetY = targetYEvidence,
                        FailureStage = EvidenceValue<string>.Confirmed(actionStage, "调用点失败分支")
                    },
                    Locks = new LockEvidence(
                        reservationReleasePending ? EvidenceAvailability.Unknown : EvidenceAvailability.Confirmed,
                        reservationReason,
                        new[] { reservation }),
                    Recovery = new RecoveryEvidence(
                        reservationReleasePending ? EvidenceAvailability.Unknown : EvidenceAvailability.NotApplicable,
                        reservationReason,
                        reservationReleasePending
                            ? EvidenceValue<bool>.Unknown(reservationReason)
                            : EvidenceValue<bool>.NotApplicable(reservationReason),
                        reservationReleasePending
                            ? EvidenceValue<bool>.Unknown(reservationReason)
                            : EvidenceValue<bool>.NotApplicable(reservationReason),
                        reservationReleasePending
                            ? new[] { new RecoveryStepEvidence("原finally释放工位预约", RecoveryStepState.Unknown, reservationReason) }
                            : Array.Empty<RecoveryStepEvidence>(),
                        Array.Empty<string>(),
                        reservationReason),
                    Guidance = new OperatorGuidance(
                        new[]
                        {
                            $"确认{deviceName}当前实际XYZ和目标工位{stationCode}是否安全。",
                            reservationReleasePending ? "确认原finally是否已释放目标工位预约。" : "确认目标工位当前占用者。"
                        },
                        new[] { "禁止在设备实际位置未确认时直接重发绝对移动命令。" },
                        "设备位置、工位占用和安全条件全部人工确认后方可重新调度。"),
                    Exception = exceptionEvidence
                }
            });
        }
        catch { }
    }

    /// <summary>旁路记录旧生产主循环的最终失败，不参与状态机判断。</summary>
    private void TryReportEngineFinalFailure(
        WorkpieceContext ctx,
        FlowStage failedStage,
        OperationalEventSeverity severity,
        string title,
        string detailMessage,
        Exception exception)
    {
        try
        {
            const string unavailableReason = "旧生产主循环调用点没有该项证据";
            OperationalEvidence unavailable = OperationalEvidence.Unavailable(unavailableReason);
            string line = ctx.AssignedLine.HasValue ? $"{ctx.AssignedLine.Value}号线" : "未分配线路";
            string stage = failedStage.ToString();
            var exceptionEvidence = new ExceptionEvidence(
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                exception.InnerException?.ToString() ?? string.Empty,
                $"工件={ctx.PlateNo}/{ctx.Sequence}，失败阶段={stage}",
                exception.StackTrace ?? string.Empty);

            _exceptionReporter.Report(new OperationalEventContext
            {
                EventCode = "ENGINE_FINAL_FAILURE",
                Severity = severity,
                Category = OperationalEventCategory.FinalFailure,
                Scope = line,
                Engine = nameof(ProductionFlowEngine),
                DeviceType = "流程引擎",
                DeviceNo = line,
                Station = stage,
                ActionStage = stage,
                Title = title,
                Source = "ProductionFlowEngine.EngineLoopAsync",
                CorrelationKey = $"ProductionFlowEngine:{ctx.PlateNo}:{ctx.Sequence}:{stage}",
                DetailMessage = detailMessage,
                Result = "上报后原catch按既有逻辑把CurrentStage复位为Idle；工件不会自动重新入队。",
                CapturedException = exception,
                PhysicalConclusion = new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.Unknown,
                    EvidenceAvailability.Unknown,
                    "工件和设备物理状态未知",
                    "旧主循环上下文没有设备动作结果证据"),
                BusinessPaused = EvidenceValue<bool>.Confirmed(false, "该catch没有暂停引擎"),
                Evidence = unavailable with
                {
                    Workpiece = new WorkpieceEvidence(
                        string.IsNullOrWhiteSpace(ctx.PlateNo)
                            ? EvidenceValue<string>.Unknown("WorkpieceContext.PlateNo为空")
                            : EvidenceValue<string>.Confirmed(ctx.PlateNo, "来自WorkpieceContext"),
                        string.IsNullOrWhiteSpace(ctx.Sequence)
                            ? EvidenceValue<string>.Unknown("WorkpieceContext.Sequence为空")
                            : EvidenceValue<string>.Confirmed(ctx.Sequence, "来自WorkpieceContext"),
                        ctx.Diameter > 0
                            ? EvidenceValue<double>.Confirmed(ctx.Diameter, "来自WorkpieceContext")
                            : EvidenceValue<double>.Unknown("WorkpieceContext.Diameter未提供有效正值"),
                        ctx.Length > 0
                            ? EvidenceValue<double>.Confirmed(ctx.Length, "来自WorkpieceContext")
                            : EvidenceValue<double>.Unknown("WorkpieceContext.Length未提供有效正值"),
                        string.IsNullOrWhiteSpace(ctx.LoadMethod)
                            ? EvidenceValue<string>.Unknown("尚未分配或调用点没有来源")
                            : EvidenceValue<string>.Confirmed(ctx.LoadMethod, "来自WorkpieceContext.LoadMethod"),
                        EvidenceValue<string>.Confirmed(ctx.CurrentStepText, "来自失败前CurrentStage"),
                        EvidenceValue<string>.Inferred(nameof(ProductionFlowEngine), "工件由旧生产主循环出队处理"),
                        EvidenceValue<string>.Unknown("调用点没有最后确认物理位置"),
                        "EngineLoopAsync捕获异常前的WorkpieceContext"),
                    BusinessState = new BusinessStateEvidence(
                        EvidenceAvailability.Confirmed,
                        "已保存失败前状态；Idle复位在上报之后执行",
                        EvidenceValue<string>.Unknown("调用点没有独立保存最后成功检查点"),
                        EvidenceValue<string>.Confirmed(stage, "异常捕获后、复位前保存"),
                        EvidenceValue<string>.Unknown("原catch将在上报后尝试复位Idle，当前尚未执行"),
                        EvidenceValue<string>.Unavailable("旧主循环没有缓存快照"),
                        EvidenceValue<string>.Unavailable("旧主循环没有缓存快照"),
                        EvidenceValue<string>.Inferred(nameof(ProductionFlowEngine), "工件由旧生产主循环出队处理"),
                        EvidenceValue<string>.Unknown("失败后软件所有权没有显式转移"),
                        EvidenceValue<bool>.Unknown("调用点没有持件传感器证据"),
                        EvidenceValue<bool>.Unknown("调用点没有放料确认"),
                        EvidenceValue<bool>.Unavailable("旧主循环没有缓存通知证据"),
                        Array.Empty<PhysicalCommitmentEvidence>()),
                    Recovery = new RecoveryEvidence(
                        EvidenceValue<bool>.Confirmed(false, "catch内没有自动恢复动作"),
                        EvidenceValue<bool>.Confirmed(false, "catch内没有自动恢复动作"),
                        Array.Empty<RecoveryStepEvidence>(),
                        Array.Empty<string>(),
                        "原catch将在上报后复位Idle；该工件不会自动重新入队。"),
                    Guidance = new OperatorGuidance(
                        new[]
                        {
                            "核对异常阶段涉及设备的实际位置、持件和工件落点。",
                            "确认工件身份与现场一致后，再决定是否人工重新入队。"
                        },
                        new[]
                        {
                            "禁止在物理状态未确认时直接重新入队。",
                            "禁止仅根据软件Idle状态判断设备和工件已经安全。"
                        },
                        "物理位置、工件身份和设备安全条件全部确认后方可继续。"),
                    Exception = exceptionEvidence
                }
            });
        }
        catch { }
    }

    /// <summary>引擎启动后持续运行，从队列取任务处理。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            Console.WriteLine("[FlowEngine] 引擎已在运行，跳过重复启动。");
            return;
        }

        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  [FlowEngine] 主线流程引擎启动");
        Console.WriteLine("  状态机: Idle → Loading → Boring → Marking → SkewBed → BalanceCheck → Grinding → Done");
        Console.WriteLine("══════════════════════════════════════════");

        _engineTask = Task.Run(() => EngineLoopAsync(_engineCts.Token));
    }

    /// <summary>停止引擎，等待当前工件处理完毕。</summary>
    public void Stop()
    {
        Console.WriteLine("[FlowEngine] 收到停止信号，等待当前任务完成...");
        _engineCts.Cancel();
    }

    /// <summary>
    /// 将一个工件加入处理队列。引擎空闲时立即开始处理，
    /// 正在处理其他工件时排队等待。
    /// </summary>
    public void EnqueueWorkpiece(WorkpieceContext ctx)
    {
        _taskQueue.Enqueue(ctx);
        Console.WriteLine($"[FlowEngine] 📥 工件入队：{ctx}");
        Console.WriteLine($"[FlowEngine]    当前队列长度：{_taskQueue.Count}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  引擎主循环
    // ═══════════════════════════════════════════════════════════════

    private async Task EngineLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_taskQueue.TryDequeue(out var ctx))
            {
                Console.WriteLine();
                Console.WriteLine($"══════════════ [FlowEngine] 开始处理工件 ══════════════");
                Console.WriteLine($"  {ctx}");
                Console.WriteLine($"══════════════════════════════════════════════════════");

                try
                {
                    await ProcessWorkpieceAsync(ctx, ct);
                }
                catch (OperationCanceledException ex)
                {
                    Console.WriteLine($"[FlowEngine] ⚠ 工件处理被取消：{ctx.PlateNo}");
                    FlowStage failedStage = ctx.CurrentStage;
                    TryReportEngineFinalFailure(
                        ctx, failedStage, OperationalEventSeverity.Information,
                        "工件流程被取消",
                        $"工件 {ctx.PlateNo} 在 {failedStage} 阶段收到取消。",
                        ex);
                    ctx.CurrentStage = FlowStage.Idle;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FlowEngine] ✘ 工件处理异常：{ctx.PlateNo} — {ex.Message}");
                    Console.WriteLine($"[FlowEngine]    {ex.GetType().Name}: {ex.Message}");
                    FlowStage failedStage = ctx.CurrentStage;
                    TryReportEngineFinalFailure(
                        ctx, failedStage, OperationalEventSeverity.Error,
                        "工件流程最终失败",
                        $"工件 {ctx.PlateNo} 在 {failedStage} 阶段发生未处理异常：{ex.Message}",
                        ex);
                    ctx.CurrentStage = FlowStage.Idle; // 异常后回到空闲，可重新入队
                }

                Console.WriteLine($"══════════════ [FlowEngine] 工件处理结束 ══════════════");
                Console.WriteLine();
            }
            else
            {
                // 队列空，等待 1 秒再检查
                await Task.Delay(1000, ct);
            }
        }

        Console.WriteLine("[FlowEngine] 引擎已停止。");
    }

    // ═══════════════════════════════════════════════════════════════
    //  引擎 ↔ UI 同步
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 推进工件到指定阶段，同步更新 UI 行 + 写数据库跟踪记录。
    /// </summary>
    private void AdvanceStage(WorkpieceContext ctx, FlowStage stage)
    {
        ctx.CurrentStage = stage;
        ctx.StageStartTime = DateTime.Now;

        if (ctx.UiRow != null)
        {
            ctx.UiRow.Step  = ctx.CurrentStepText;
            ctx.UiRow.State = stage == FlowStage.Done ? "已完成" : "运行中";
        }

        Console.WriteLine($"[FlowEngine] → {ctx.CurrentStepText}  [{ctx.PlateNo}]");

        // 异步记录工件阶段到数据库（fire-and-forget，不影响流程）
        _ = _posSvc.InsertWorkpieceTrackAsync(
            ctx.PlateNo, ctx.Sequence, ctx.CurrentStepText, ctx.ProcessType,
            ctx.Length, ctx.Diameter, ctx.PlugHole, ctx.MarkingContent,
            ctx.AssignedLine, ctx.LoadMethod, ctx.NeedsBalance);
    }

    // ═══════════════════════════════════════════════════════════════
    //  工件流水线
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 按顺序推动工件经过全部阶段。
    /// 跳过双头镗工艺时自动跳过 Boring 阶段。
    /// </summary>
    private async Task ProcessWorkpieceAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        // ── Stage 1: 上料分派 ──────────────────────────────────────
        AdvanceStage(ctx, FlowStage.Loading);
        await DoLoadingAsync(ctx, ct);
        Console.WriteLine($"[FlowEngine] ✔ Stage 1/6 完成 — {ctx.CurrentStepText} | 线体={ctx.AssignedLine} 上料方式={ctx.LoadMethod}");

        // ── Stage 2: 双头镗加工（或跳过） ──────────────────────────
        if (ctx.SkipBoring)
        {
            Console.WriteLine($"[FlowEngine] ⏭ 跳过 Stage 2/6 — {ctx.ProcessType}");
        }
        else
        {
            AdvanceStage(ctx, FlowStage.Boring);
            await DoBoringAsync(ctx, ct);
            Console.WriteLine($"[FlowEngine] ✔ Stage 2/6 完成 — 双头镗加工");
        }

        // ── Stage 3: 打标 ─────────────────────────────────────────
        AdvanceStage(ctx, FlowStage.Marking);
        await DoMarkingAsync(ctx, ct);
        Console.WriteLine($"[FlowEngine] ✔ Stage 3/6 完成 — 打标");

        // ── Stage 4: 斜床加工 ─────────────────────────────────────
        AdvanceStage(ctx, FlowStage.SkewBed);
        await DoSkewBedAsync(ctx, ct);
        Console.WriteLine($"[FlowEngine] ✔ Stage 4/6 完成 — 斜床加工");

        // ── Stage 5: 动平衡判断 ───────────────────────────────────
        AdvanceStage(ctx, FlowStage.BalanceCheck);
        await DoBalanceCheckAsync(ctx, ct);
        Console.WriteLine($"[FlowEngine] ✔ Stage 5/6 完成 — 动平衡判断: {(ctx.NeedsBalance ? "需要动平衡" : "跳过动平衡")}");

        // ── Stage 6: 研磨 ─────────────────────────────────────────
        AdvanceStage(ctx, FlowStage.Grinding);
        await DoGrindingAsync(ctx, ct);
        Console.WriteLine($"[FlowEngine] ✔ Stage 6/6 完成 — 研磨");

        // ── 完成 ──────────────────────────────────────────────────
        AdvanceStage(ctx, FlowStage.Done);
        ctx.CompletedAt = DateTime.Now;
        var totalTime = ctx.CompletedAt.Value - ctx.CreatedAt;
        Console.WriteLine($"[FlowEngine] 🎉 工件全部工序完成！版号={ctx.PlateNo} 总耗时={totalTime.TotalSeconds:F1}s");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stage 1: 上料分派
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 上料分派逻辑：
    ///   1. 人工放料到上料架 → 旋转夹紧（人工操作，上位机等待）
    ///   2. 小机械手 1 抓取工件
    ///   3. 判断工件长度 → 分派线体
    ///      - ≤1.25m → 线体1或线体2（优先空闲线体）
    ///      - 1.25m < L ≤ 1.5m → 只能线体2
    ///   4. 判断上料方式
    ///      - 长工件 → 货叉送入双头镗
    ///      - 短工件 → 前天车直接送入双头镗
    /// </summary>
    private async Task DoLoadingAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[FlowEngine] ▶ Stage 1: 上料分派 — 版号={ctx.PlateNo} 长度={ctx.Length}mm");

        // ── 1. 等待上料架就绪（人工放料 + 旋转夹紧） ──────────────
        Console.WriteLine($"[FlowEngine]   等待上料架就绪（人工放料+旋转夹紧）...");
        // TODO: 等 PLC 信号（上料架夹紧完成）—— 当前用模拟延迟
        await Task.Delay(500, ct);
        Console.WriteLine($"[FlowEngine]   上料架已就绪（模拟）");

        // ── 2. 小机械手 1 去上料架取料 ────────────────────────────
        Console.WriteLine($"[FlowEngine]   机械手1 → 上料架(ST001)");
        bool moved = await MoveManipulatorToStationAsync(1, "ST001", ct);
        if (moved)
            Console.WriteLine($"[FlowEngine]   机械手1 已到达上料架");
        else
            Console.WriteLine($"[FlowEngine]   ⚠ 机械手1 无法移动到上料架（可能无IP），继续模拟");

        // TODO: 有IP后充磁取料 → 提起Z轴
        await Task.Delay(300, ct);

        // ── 3. 判断长度 → 分派线体 ────────────────────────────────
        if (ctx.Length <= 1250)
        {
            ctx.AssignedLine = new Random().Next(1, 3);
            Console.WriteLine($"[FlowEngine]   长度={ctx.Length}mm ≤ 1.25m，线体1或2，选中线体{ctx.AssignedLine}");
        }
        else if (ctx.Length <= 1500)
        {
            ctx.AssignedLine = 2;
            Console.WriteLine($"[FlowEngine]   长度={ctx.Length}mm 在 1.25m~1.5m，只能线体2");
        }
        else
        {
            throw new InvalidOperationException($"工件长度 {ctx.Length}mm 超出最大加工范围（1.5m）！");
        }

        // ── 4. 判断上料方式 → 调度天车 ────────────────────────────
        if (ctx.Length > 1000)
        {
            ctx.LoadMethod = "货叉";
            Console.WriteLine($"[FlowEngine]   长度={ctx.Length}mm > 1.0m → 长工件 → 货叉送入（等地址表）");
        }
        else
        {
            ctx.LoadMethod = "前天车";
            Console.WriteLine($"[FlowEngine]   长度={ctx.Length}mm ≤ 1.0m → 短工件 → 前天车送入");

            // 调度前天车去对应线体的双头镗上料位
            string boringSta = ctx.AssignedLine == 2 ? "ST402" : "ST401";
            int craneNo = ctx.AssignedLine == 2 ? 3 : 1;
            Console.WriteLine($"[FlowEngine]   天车{craneNo} → {boringSta}");
            await MoveCraneToStationAsync(craneNo, boringSta, ct);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stage 2: 双头镗加工
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 双头镗加工握手流程（Syntec CNC）。
    /// 完整流程见 Docs/03-设备交互流程/主线流程_状态机设计.md
    /// </summary>
    private async Task DoBoringAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[FlowEngine] ▶ Stage 2: 双头镗加工 — 线体{ctx.AssignedLine} 上料方式={ctx.LoadMethod}");

        // ═══════════════════════════════════════════════════════════
        // TODO: 等设备 IP 到位后替换为真实逻辑
        // BoringModbusService 已封装好新双头镗方法：
        //
        //   var boringSvc = new BoringModbusService("双头镗", ip);
        //   await boringSvc.ConnectAsync(ct);
        //
        //   // 1. 等 CNC 请求数据（R6101=1）
        //   while (!await boringSvc.IsRequestDataAsync(ct))
        //       await Task.Delay(500, ct);
        //   Console.WriteLine("[FlowEngine]   双头镗请求数据 R6101=1");
        //
        //   // 2. 下发加工参数（版长@740、外径@705、堵厚@703等）
        //   await boringSvc.SendMachiningParamsAsync(
        //       ctx.Length, ctx.Diameter, ctx.PlugHole, 0, 0, ct);
        //   await boringSvc.SetDataSentDoneAsync(ct);  // R6102=1
        //   Console.WriteLine("[FlowEngine]   加工参数已下发，R6102=1");
        //
        //   // 3. 等 CNC 请求上料（R6103=1）
        //   while (!await boringSvc.IsRequestLoadAsync(ct))
        //       await Task.Delay(500, ct);
        //
        //   // 4. 通知货叉/天车上料
        //   // if (ctx.LoadMethod == "货叉") { 通知货叉送入 } else { 前天车送入 }
        //   // 上料到位 → 写 R6104=1
        //
        //   // 5. 货叉送料自动回待机且无板 → 写 R6104=1，双头镗开始加工
        //
        //   // 6. 等加工完成请求下料（R6107=1）
        //   while (!await boringSvc.IsRequestUnloadAsync(ct))
        //       await Task.Delay(500, ct);
        //
        //   // 7. 通知货叉取料到Pos3 → 写 R6108=1
        // ═══════════════════════════════════════════════════════════

        Console.WriteLine($"[FlowEngine]   [占位] 双头镗加工模拟（等 R6101/R6103/R6107 真实信号）...");
        await Task.Delay(2000, ct); // 模拟加工时间
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stage 3: 打标
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 打标机打号流程（文件握手或 Modbus TCP）。
    /// 详见 Docs/03-设备交互流程/打标机_文件交互流程.md
    /// </summary>
    private async Task DoMarkingAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[FlowEngine] ▶ Stage 3: 打标 — 内容={ctx.MarkingContent}");

        // ═══════════════════════════════════════════════════════════
        // TODO: LaserMarkerService 已封装两种模式（ModbusTCP / FileHandshake）
        //
        //   var markerSvc = new LaserMarkerService(ip);  // 或 sharedFolderPath
        //   await markerSvc.ConnectAsync(ct);
        //   await markerSvc.SendMarkCommandAsync(ctx.PlateNo, ctx.MarkingContent, ct);
        // ═══════════════════════════════════════════════════════════

        Console.WriteLine($"[FlowEngine]   [占位] 打标模拟...");

        // 流程简述（参考项目已验证）：
        // 1. 前天车从双头镗取料 → 送至打标机
        // 2. 上位机生成打标文件（或写 Modbus 寄存器）
        // 3. 打标机执行打标
        // 4. 打标完成 → 通知前天车取料 → 放中转架

        await Task.Delay(1500, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stage 4: 斜床加工
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 斜床加工分派（10 台：6 台沈阳 FANUC + 4 台锦州 Modbus）。
    /// 后天车从中转架取料 → 分派到空闲斜床 → 写加工参数 → 等加工完成 → 取回放工位架。
    /// </summary>
    private async Task DoSkewBedAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[FlowEngine] ▶ Stage 4: 斜床加工 — 共10台斜床");

        // ═══════════════════════════════════════════════════════════
        // TODO: FanucSkewBedService（沈阳6台）和 ModbusSkewBedService（锦州4台）已封装
        //
        //   // 1. 后天车从中转架取料
        //   //    通过 CraneService 控制后天车（craneNo=2或4）移动
        //
        //   // 2. 扫描10台斜床状态 → 找空闲的
        //   var idleBeds = new List<int>();
        //   foreach (var bed in allSkewBeds) {
        //       if (await bed.IsMachineReadyAsync(ct)) idleBeds.Add(bed.Id);
        //   }
        //   int target = idleBeds.First(); // 选第一台空闲的
        //
        //   // 3. 下发加工参数到选中的斜床
        //   //    FANUC: FanucSkewBedService.SendMachiningParamsAsync(...)
        //   //    Modbus: ModbusSkewBedService.SetMachiningModeAsync(...)
        //
        //   // 4. 等加工完成 → 天车取回 → 放工位架
        // ═══════════════════════════════════════════════════════════

        Console.WriteLine($"[FlowEngine]   [占位] 斜床分派模拟...");
        await Task.Delay(2000, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stage 5: 动平衡判断分流
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 动平衡判断（纯逻辑，不依赖硬件）：
    ///   长度 > 800mm → 需要动平衡 → 机械手2放动平衡上料架 → 人工 → 机械手3取 → 研磨上料架
    ///   长度 ≤ 800mm → 跳过动平衡 → 直接放研磨上料架
    /// </summary>
    private async Task DoBalanceCheckAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[FlowEngine] ▶ Stage 5: 动平衡判断 — 长度={ctx.Length}mm 阈值=800mm");

        if (ctx.NeedsBalance)
        {
            Console.WriteLine($"[FlowEngine]   长度={ctx.Length}mm > 800mm → 需要动平衡");
            Console.WriteLine($"[FlowEngine]   机械手2 → 07动平衡上料架 → (人工取放) → 08动平衡下料架 → 机械手3 → 研磨上料架");

            // TODO: 控制机械手2 将工件放到动平衡上料架
            // TODO: 等待人工取走 + 放回（可能需要操作员确认按钮）
            // TODO: 控制机械手3 从动平衡下料架取料 → 放研磨上料架

            await Task.Delay(1000, ct); // 模拟动平衡耗时
            Console.WriteLine($"[FlowEngine]   动平衡完成（模拟）");
        }
        else
        {
            Console.WriteLine($"[FlowEngine]   长度={ctx.Length}mm ≤ 800mm → 跳过动平衡，直接上研磨上料架");

            // TODO: 控制机械手2 直接将工件放研磨上料架
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stage 6: 研磨加工
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 研磨加工分派（4 台：两款不同系统各 2 台）。
    /// 研磨天车从研磨上料架（旋转记忆架）取料 → 分派空闲研磨机 → 加工 → 下料架。
    /// </summary>
    private async Task DoGrindingAsync(WorkpieceContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[FlowEngine] ▶ Stage 6: 研磨加工 — 共4台研磨机（1/2=新代 3/4=西门子）");

        // ── 尝试连接研磨机并读取状态（IP从数据库machine表获取） ────
        // 研磨机1/2: 新代数控 TypeB（ST701/ST702）
        // 研磨机3/4: 西门子 TypeA（ST703/ST704）
        try
        {
            var grinderStations = new[] { ("ST701", "研磨机1(新代)", PlcGrinderService.GrinderType.TypeB),
                                          ("ST702", "研磨机2(新代)", PlcGrinderService.GrinderType.TypeB),
                                          ("ST703", "研磨机3(西门子)", PlcGrinderService.GrinderType.TypeA),
                                          ("ST704", "研磨机4(西门子)", PlcGrinderService.GrinderType.TypeA) };

            foreach (var (code, name, gtype) in grinderStations)
            {
                if (!_stationCoords.TryGetValue(code, out var row) || string.IsNullOrWhiteSpace(row.Ip))
                {
                    Console.WriteLine($"[FlowEngine]   {name} ({code}) IP未配置，跳过");
                    continue;
                }

                var port = row.Port > 0 ? row.Port : 502;
                var grinder = new PlcGrinderService(name, row.Ip.Trim(), gtype, port);
                await grinder.ConnectAsync(ct);
                await grinder.ReadAllStatusAsync(ct);
                await grinder.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FlowEngine]   ⚠ 研磨机状态读取异常：{ex.Message}");
        }

        // 研磨流程由 GrindingFlowEngine 独立控制（主页面启动/暂停按钮）
        // HomeViewModel 创建 GrindingFlowEngine 实例，注入 station_coords + craneCache
        // 引擎后台循环：扫描研磨机 → 分配工件 → 天车取料送料 → 握手 → 下料
        Console.WriteLine($"[FlowEngine]   研磨流程由 GrindingFlowEngine 独立控制（主页面启动/暂停按钮）");
        await Task.Delay(500, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  释放
    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engineCts.Cancel();
        _engineCts.Dispose();
        Console.WriteLine("[FlowEngine] 引擎已释放。");
    }
}
