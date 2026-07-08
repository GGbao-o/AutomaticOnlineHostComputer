using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.CraneAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>下压信号触发导致的运动停止异常。</summary>
    public sealed class PressureStopException : Exception
    {
        public string CraneName { get; }
        public PressureStopException(string craneName)
            : base($"下压信号触发 D4523，{craneName} 当前运动已紧急停止。手动恢复：①写D4523=0 ②清除报警D4514=2→0")
        {
            CraneName = craneName;
        }
    }

    /// <summary>
    /// 天车通信服务（汇川 PLC，Modbus TCP）。
    /// <para>
    /// 每台天车实例化一个 CraneService，传入对应 IP 地址。<br/>
    /// 公共 API 均通过 IDeviceClient 的 ReadAsync / WriteAsync 访问，
    /// 不直接调用 ModbusTcpClient 内部私有方法。
    /// </para>
    /// <para>5台天车 IP：
    ///   1号线天车前 192.168.2.81；1号线天车后 192.168.2.82；
    ///   2号线天车前 192.168.2.83；2号线天车后 192.168.2.84；
    ///   研磨机天车  192.168.2.80
    /// </para>
    /// </summary>
    public class CraneService
    {
        private const int MaxIoAttempts = 3;

        // ── 字段 ─────────────────────────────────────────────────────
        private readonly ModbusTcpClient _client;
        private readonly string _name; // 天车名称，调试输出用
        private readonly SemaphoreSlim _connectionLock = new(1, 1); // 连接/断开串行化，防止UI和引擎同时重连同一设备
        private readonly object _statusLogLock = new(); // UI与流程会共享服务，保护状态日志节流字段
        private bool _manualModeSet;   // 是否已确认 PLC 处于手动模式，避免重复写 D4500
        private string? _lastStatusLogKey;
        private DateTime _lastStatusLogAtUtc;
        private static readonly TimeSpan StatusLogHeartbeat = TimeSpan.FromSeconds(10);

        // ── 构造 ─────────────────────────────────────────────────────
        /// <param name="name">天车名称（如"1号线天车前"），仅用于日志输出</param>
        /// <param name="ip">PLC IP 地址</param>
        /// <param name="port">Modbus TCP 端口，默认 502</param>
        public CraneService(string name, string ip, int port = 502)
        {
            _name   = name;
            _client = new ModbusTcpClient(ip, port, unitId: 1, timeoutMs: 3000);
            Console.WriteLine($"[CraneService] [{_name}] 创建实例，IP={ip}:{port}");
        }

        // ── 连接管理 ──────────────────────────────────────────────────

        /// <summary>建立 Modbus TCP 连接。重置手动模式缓存（重连后 PLC 状态未知）。</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _connectionLock.WaitAsync(ct);
            try
            {
                if (_client.IsConnected)
                {
                    return;
                }

                Console.WriteLine($"[CraneService] [{_name}] 正在连接...");
                await _client.ConnectAsync(ct);
                _manualModeSet = false; // 重连后 PLC 模式未知，重置缓存
                Console.WriteLine($"[CraneService] [{_name}] 连接成功。IsConnected={_client.IsConnected}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CraneService] [{_name}] 连接失败：{ex.Message}");
                throw;
            }
            finally { _connectionLock.Release(); }
        }

        /// <summary>断开连接。</summary>
        public async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                await _client.DisconnectAsync();
                _manualModeSet = false;
                Console.WriteLine($"[CraneService] [{_name}] 已断开连接。");
            }
            finally { _connectionLock.Release(); }
        }

        /// <summary>当前是否已连接。</summary>
        public bool IsConnected => _client.IsConnected;

        // ═══════════════════════════════════════════════════════════════
        // 状态读取
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 一次性读取天车关键状态快照（D5000~D5029，共 30 个连续寄存器）。
        /// 读取失败时返回 null，并输出错误日志。
        /// </summary>
        public async Task<CraneStatus?> ReadStatusAsync(CancellationToken ct = default)
        {
            try
            {
                // 使用公共 ReadAsync：读 D5000 起 30 个寄存器，返回 int[]
                var result = await SafeIoAsync(token => _client.ReadAsync(Addr.D_RequestData, 30, token), "读取状态 D5000~D5029", ct);
                var r = result.IntValues; // int[] 长度 30

                var status = new CraneStatus
                {
                    RequestData         = (short)r[0],   // D5000
                    ArrivedMagnet       = (short)r[1],   // D5001
                    PickDone            = (short)r[2],   // D5002
                    ArrivedTarget       = (short)r[3],   // D5003
                    LoadDone            = (short)r[4],   // D5004
                    CurrentTaskNo       = (short)r[5],   // D5005
                    Busy                = (short)r[6],   // D5006
                    StateMachineStep    = (short)r[7],   // D5007
                    Mode                = (short)r[8],   // D5008
                    Fault               = (short)r[9],   // D5009
                    RunConditionMissing = (short)r[10],  // D5010
                    ServoAlarm          = (short)r[11],  // D5011
                    PlcServoAlarm       = (short)r[12],  // D5012
                    PlcAlarm            = (short)r[13],  // D5013
                    // DINT=32位有符号，小端（低字在前）：D5014=低字 D5015=高字
                    // r[N] 被 IntValues 强转 short，必须先 &0xFFFF 清符号位再组合
                    XEncoderAbs  = ((r[15] & 0xFFFF) << 16) | (r[14] & 0xFFFF),  // D5014(低)+D5015(高)
                    XEncoderZero = ((r[17] & 0xFFFF) << 16) | (r[16] & 0xFFFF),  // D5016(低)+D5017(高)
                    XPos         = ((r[19] & 0xFFFF) << 16) | (r[18] & 0xFFFF),  // D5018(低)+D5019(高)
                    YEncoderAbs  = (ushort)r[20],  // D5020
                    YEncoderZero = (ushort)r[21],  // D5021
                    YPos         = (ushort)r[22],  // D5022（无符号，0~65535）
                    ZEncoderAbs  = (ushort)r[23],  // D5023
                    ZEncoderZero = (ushort)r[24],  // D5024
                    ZPos         = (ushort)r[25],  // D5025（无符号，0~65535）
                    PlcSeqNo         = (short)r[26],  // D5026
                    TaskSourceDevice = (short)r[27],  // D5027
                    TaskTargetDevice = (short)r[28],  // D5028
                    HasRoller        = (short)r[29],  // D5029
                };

                // 到位轮询最快200ms一次。只在关键状态变化或心跳周期到达时打印，
                // 避免正常运动期间大量同步刷盘；异常和动作日志仍始终保留。
                if (ShouldLogStatus(status))
                {
                    Console.WriteLine($"[CraneService] [{_name}] 状态快照 | " +
                        $"Mode={status.Mode} Busy={status.Busy} Fault={status.Fault} " +
                        $"ServoAlarm={status.ServoAlarm} PlcAlarm={status.PlcAlarm} " +
                        $"HasPlate={status.HasRoller} X={status.XPos} Y={status.YPos} Z={status.ZPos}");
                }
                return status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CraneService] [{_name}] 读取状态异常：{ex.Message}");
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 手动指令写入（D4500~D4518）
        // 所有手动操作前需先写 D4500=2 使能手动操作，急停 D4518 除外。
        // 注意：协议中 D4001 是自动/手动模式切换，D4500 是手动操作使能，
        //       二者配合使用。本实现先写 D4500=2；
        //       若 PLC 当前为自动模式(D4001=1)，可能还需先写 D4001=2。
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 确保 PLC 处于手动模式：
        ///   1. D4001=2（自动/手动模式切换 → 手动）
        ///   2. D4500=2（手动操作使能）
        /// 首次调用必写两个寄存器，后续调用若已知处于手动模式则跳过。
        /// </summary>
        private async Task EnsureManualModeAsync(CancellationToken ct)
        {
            if (_manualModeSet)
                return; // 已确认处于手动模式，跳过

            // 协议要求：先切模式（D4001=2），再使能手动操作（D4500=2）
            await WriteRegAsync(Addr.D_ModeCmd, 2, "切换手动模式 D4001", ct);
            await WriteRegAsync(Addr.D_ManualMode, 2, "手动操作使能 D4500", ct);
            _manualModeSet = true;
        }

        // ─── 急停 / 下压急停 ─────────────────────────────────────────

        /// <summary>
        /// 急停伺服运动（D4518=2）。
        /// ⚠️ 不需要先切手动模式，紧急情况直接写入。
        /// 写完 2 立刻回 0，避免 PLC 一直处于急停触发态。
        /// </summary>
        public async Task EmergencyStopAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 急停 D4518");
            await WriteRegAsync(Addr.D_ManualEStop, 2, "急停(触发)", ct);
            await WriteRegAsync(Addr.D_ManualEStop, 0, "急停(复位)", ct);
        }

        /// <summary>
        /// 读取下压急停状态（D4523）。≠0 表示 PLC 检测到 Z 轴下压，已触发急停。
        /// Z 轴下降过程应轮询此值，一旦触发立即停止。
        /// </summary>
        public async Task<bool> IsPressureEStopAsync(CancellationToken ct = default)
        {
            try
            {
                int val = await _client.ReadIntAsync(Addr.D_PressureEStop, ct);
                bool triggered = val != 0;
                if (triggered)
                    Console.WriteLine($"[CraneService] [{_name}] ⚠ 下压急停触发 D4523={val}");
                return triggered;
            }
            catch
            {
                return false; // 读不到就认为没触发，不阻塞流程
            }
        }

        /// <summary>
        /// 清除下压急停状态（写 D4523=0）。
        /// 手动恢复流程：① 写 D4523=0 → ② 清除报警 D4514=2→0 → ③ 恢复正常。
        /// </summary>
        public async Task ClearPressureEStopAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 清除下压急停 D4523=0");
            await WriteRegAsync(Addr.D_PressureEStop, 0, "清除下压急停 D4523", ct);
            _manualModeSet = false;  // 急停后PLC可能退出手动模式，重置缓存强制下次重写D4001/D4500
        }

        /// <summary>
        /// 减速停止伺服运动（D4517=2 → 0 脉冲）。
        /// </summary>
        public async Task SlowStopAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 减速停止 D4517");
            await WriteRegAsync(Addr.D_ManualSlowStop, 2, "减速停止(触发)", ct);
            await WriteRegAsync(Addr.D_ManualSlowStop, 0, "减速停止(复位)", ct);
        }

        // ─── 点动 / 回原点 ─────────────────────────────────────────────

        /// <summary>设置手动相对运动参数（D4001=2, D4501=距离, D4502=方向）。</summary>
        private async Task SetManualMoveParamsAsync(int distance, int direction, CancellationToken ct)
        {
            if (distance <= 0) throw new ArgumentOutOfRangeException(nameof(distance), "distance 必须大于 0");
            if (direction is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(direction), "direction 仅支持 1(正向) / 2(负向)");

            Console.WriteLine($"[CraneService] [{_name}] ▶ 设置点动参数 distance={distance}, direction={direction}");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualDistance, distance, "设置手动距离", ct);
            await WriteRegAsync(Addr.D_ManualDirection, direction, "设置手动方向", ct);
        }

        public async Task MoveXAsync(int distance, bool positive, CancellationToken ct = default)
        {
            var dir = positive ? 1 : 2;
            Console.WriteLine($"[CraneService] [{_name}] ▶ X{(positive ? "+" : "-")} 点动 distance={distance}");
            await SetManualMoveParamsAsync(distance, dir, ct);
            await WriteRegAsync(Addr.D_ManualMoveX, 2, "X轴相对运动", ct);
        }

        public async Task MoveYAsync(int distance, bool positive, CancellationToken ct = default)
        {
            var dir = positive ? 1 : 2;
            Console.WriteLine($"[CraneService] [{_name}] ▶ Y{(positive ? "+" : "-")} 点动 distance={distance}");
            await SetManualMoveParamsAsync(distance, dir, ct);
            await WriteRegAsync(Addr.D_ManualMoveY, 2, "Y轴相对运动", ct);
        }

        public async Task MoveZAsync(int distance, bool positive, CancellationToken ct = default)
        {
            var dir = positive ? 1 : 2;
            Console.WriteLine($"[CraneService] [{_name}] ▶ Z{(positive ? "+" : "-")} 点动 distance={distance}");
            await SetManualMoveParamsAsync(distance, dir, ct);
            await WriteRegAsync(Addr.D_ManualMoveZ, 2, "Z轴相对运动", ct);
        }

        /// <summary>X轴回原点：D4508=2 触发 → 等回零完成 → D4508=0 复位（必须写0）。</summary>
        public async Task HomeXAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ X轴回原点 D4508=2");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualHomeX, 2, "X轴回原点(触发)", ct);
            // 等待 X 轴回零完成（PLC 定位完成信号 X23=63511）
            await Task.Delay(200, ct);
            await WriteRegAsync(Addr.D_ManualHomeX, 0, "X轴回原点(复位)", ct);
        }

        /// <summary>Y轴回原点：D4507=2 触发 → D4507=0 复位（必须写0）。</summary>
        public async Task HomeYAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ Y轴回原点 D4507=2");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualHomeY, 2, "Y轴回原点(触发)", ct);
            await Task.Delay(200, ct);
            await WriteRegAsync(Addr.D_ManualHomeY, 0, "Y轴回原点(复位)", ct);
        }

        /// <summary>Z轴回原点：D4506=2 触发 → D4506=0 复位（必须写0）。</summary>
        public async Task HomeZAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ Z轴回原点 D4506=2");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualHomeZ, 2, "Z轴回原点(触发)", ct);
            await Task.Delay(200, ct);
            await WriteRegAsync(Addr.D_ManualHomeZ, 0, "Z轴回原点(复位)", ct);
        }

        // ─── 伺服通/断电 ──────────────────────────────────────────────

        /// <summary>
        /// 手动伺服通电：写 D4500=2（首次）→ D4516=2 触发 → D4516=0 复位。
        /// 2 代表通电指令，写完立刻回 0 避免 PLC 一直处于通电触发态。
        /// </summary>
        public async Task ServoPowerOnAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 伺服通电");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualServoPowerOn, 2, "伺服通电(触发)", ct);
            await WriteRegAsync(Addr.D_ManualServoPowerOn, 0, "伺服通电(复位)", ct);
        }

        /// <summary>
        /// 手动伺服断电：写 D4500=2（首次）→ D4515=2 触发 → D4515=0 复位。
        /// </summary>
        public async Task ServoPowerOffAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 伺服断电");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualServoPowerOff, 2, "伺服断电(触发)", ct);
            await WriteRegAsync(Addr.D_ManualServoPowerOff, 0, "伺服断电(复位)", ct);
        }

        // ─── 清除报警 ────────────────────────────────────────────────

        /// <summary>
        /// 清除伺服/PLC 报警：写 D4500=2（首次）→ D4514=2 触发清除 → D4514=0 复位。
        /// 2 是"解除报警"指令，写完立刻回 0，否则 PLC 一直处于解除报警态。
        /// </summary>
        public async Task ClearAlarmAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 清除报警");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualClearAlarm, 2, "清除报警(触发)", ct);
            await Task.Delay(50, ct); // 50ms 间隔确保 PLC 捕获上升沿
            await WriteRegAsync(Addr.D_ManualClearAlarm, 0, "清除报警(复位)", ct);
        }

        /// <summary>
        /// 下压急停后完整恢复：清下压→清报警→伺服通电→强制重设手动模式→复位三轴绝对触发位。
        /// 调用后可直接执行绝对移动或充退磁。
        /// </summary>
        public async Task RecoverFromPressureStopAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ═══ 下压后完整恢复 ═══");
            // ① 清下压 D4523=0
            await WriteRegAsync(Addr.D_PressureEStop, 0, "清下压 D4523", ct);
            // ② 强制重设手动模式 (不依赖缓存)
            await WriteRegAsync(Addr.D_ModeCmd, 2, "切换手动模式 D4001", ct);
            await WriteRegAsync(Addr.D_ManualMode, 2, "手动操作使能 D4500", ct);
            _manualModeSet = true;
            // ③ 清报警 D4514=2→0
            await WriteRegAsync(Addr.D_ManualClearAlarm, 2, "清除报警(触发)", ct);
            await Task.Delay(50, ct);
            await WriteRegAsync(Addr.D_ManualClearAlarm, 0, "清除报警(复位)", ct);
            // ④ 伺服通电 D4516=2→0
            await WriteRegAsync(Addr.D_ManualServoPowerOn, 2, "伺服通电(触发)", ct);
            await WriteRegAsync(Addr.D_ManualServoPowerOn, 0, "伺服通电(复位)", ct);
            // ⑤ 复位三轴绝对移动触发位 D4520/D4521/D4522=0
            await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "复位Z触发 D4520", ct);
            await WriteRegAsync(Addr.D_ManualYAbsMove, 0, "复位Y触发 D4521", ct);
            await WriteRegAsync(Addr.D_ManualXAbsMove, 0, "复位X触发 D4522", ct);
            // ⑥ 验证 D4523 已清零（最多重试3次, 防硬件故障导致无限循环恢复）
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(500, ct);
                int d4523 = await _client.ReadIntAsync(Addr.D_PressureEStop, ct);
                if (d4523 == 0)
                {
                    Console.WriteLine($"[CraneService] [{_name}] ✓ D4523=0 已确认清零");
                    break;
                }
                if (i == 2)
                    throw new InvalidOperationException(
                        $"[{_name}] D4523 无法清零(当前={d4523})，疑似下压传感器物理故障或卡死！需人工检修");
                Console.WriteLine($"[CraneService] [{_name}] ⚠ D4523={d4523} 未清零, 重试{i + 1}/3...");
                await WriteRegAsync(Addr.D_PressureEStop, 0, "清下压 D4523(重试)", ct);
            }
            Console.WriteLine($"[CraneService] [{_name}] ═══ 恢复完成，可继续运动 ═══");
        }

        // ─── 充磁 / 退磁 ─────────────────────────────────────────────

        /// <summary>
        /// 读 X 区输入信号（D63488起始，每个16bit寄存器=16个X点）。
        /// X0~X15在D63488，X16~X31在D63489...
        /// </summary>
        /// <summary>读 X 输入信号（FC01 Read Coils，地址=63488+X编号，如X6=63494）。</summary>
        public async Task<bool> ReadXBitAsync(int address, CancellationToken ct = default)
        {
            try
            {
                return await SafeIoAsync(token => _client.ReadCoilAsync(address, token), $"X区读取 D{address}", ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CraneService] [{_name}] X区读取最终失败 D{address}：{ex.Message}");
                // X区信号通常用于X11有板、X2下压、X6/X7磁铁反馈等安全判断。
                // 读取失败不能等价为false；false会被上层理解成“无板/未下压/未反馈”，
                // 生产上会把“通信未知”误判成“安全”，所以必须抛出让动作流程进入异常/暂停分支。
                throw new InvalidOperationException($"X区读取失败 D{address}, 状态未知, 禁止按0处理", ex);
            }
        }

        /// <summary>
        /// 充磁：D4510=2触发 → 轮询X6(FC01)或D5029=1 → D4510=0停止。
        /// </summary>
        public async Task MagnetOnAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 充磁 D4510=2");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualMagnetOn, 2, "充磁(触发)", ct);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            bool ok = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
                bool x6 = await ReadXBitAsync(Addr.D_X6_MagnetizeOk, ct);
                if (x6) { Console.WriteLine($"[CraneService] [{_name}] ✔ X6充磁反馈=1"); ok = true; break; }
            }
            if (!ok) Console.WriteLine($"[CraneService] [{_name}] ⚠ 充磁反馈超时3s，X6未置1");
            await WriteRegAsync(Addr.D_ManualMagnetOn, 0, "充磁(停止)", ct);
        }

        /// <summary>
        /// 退磁释放工件：D4511=2 → 轮询X7(FC01)=1且X6=0 → D4511=0。
        /// D5029不作为退磁成功条件；X7不到位则重新触发退磁，最多10次。
        /// </summary>
        public async Task MagnetOffAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 退磁释放工件");
            await EnsureManualModeAsync(ct);

            const int maxRetries = 10;

            for (int retry = 1; retry <= maxRetries; retry++)
            {
                bool x7ok = false;
                bool x6on = true;

                try
                {
                    await WriteRegAsync(Addr.D_ManualMagnetOff, 2, "退磁(触发)", ct);

                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(100, ct);
                        x6on = await ReadXBitAsync(Addr.D_X6_MagnetizeOk, ct);
                        x7ok = await ReadXBitAsync(Addr.D_X7_DemagnetizeOk, ct);

                        if (x7ok && !x6on)
                        {
                            Console.WriteLine($"[CraneService] [{_name}] ✔ 退磁成功 X6=0 X7=1（第{retry}次）");
                            return;
                        }
                    }
                }
                finally
                {
                    await WriteRegAsync(Addr.D_ManualMagnetOff, 0, "退磁(停止)", ct);
                }

                Console.WriteLine($"[CraneService] [{_name}] ⚠ 退磁未到位 第{retry}/{maxRetries}次 X6={(x6on ? 1 : 0)} X7={(x7ok ? 1 : 0)}");
            }

            throw new TimeoutException($"退磁失败：X7退磁反馈未到位或X6仍为充磁状态，已重试 {maxRetries} 次，请检查电磁铁");
        }

        /// <summary>
        /// 抖动松料：取料后 X 轴往复微动，松动可能卡住的工件。
        /// <para>往复 count 次（默认 5），每次移动 amplitude（默认 5mm），奇数次正向、偶数次负向。</para>
        /// <para>参考项目已验证效果，需天车已充磁吸住工件后调用。</para>
        /// </summary>
        /// <param name="count">往复次数（默认 5）</param>
        /// <param name="amplitude">抖动振幅 mm（默认 5，小幅度防止甩飞工件）</param>
        public async Task ShakeReleaseAsync(int count = 5, int amplitude = 5, CancellationToken ct = default)
        {
            if (amplitude <= 0) throw new ArgumentOutOfRangeException(nameof(amplitude), "振幅必须大于0");
            if (count <= 0) return;

            Console.WriteLine($"[CraneService] [{_name}] ▶ 抖动松料：{count}次 振幅={amplitude}mm");
            await EnsureManualModeAsync(ct);

            for (int i = 1; i <= count; i++)
            {
                ct.ThrowIfCancellationRequested();

                int direction = (i % 2 == 1) ? 1 : 2;  // 奇次正向，偶次负向
                int distance = amplitude;               // 每次固定振幅

                Console.WriteLine($"[CraneService] [{_name}]   抖动 {i}/{count}：方向={(direction == 1 ? "+" : "-")}{distance}mm");

                // 设置相对运动参数 → 触发 X 轴相对运动（D4505=2）
                await SetManualMoveParamsAsync(distance, direction, ct);
                await WriteRegAsync(Addr.D_ManualMoveX, 2, $"抖动X轴 {i}/{count}", ct);

                // 等运动完成（200ms 足够短距离移动）
                await Task.Delay(200, ct);
            }

            Console.WriteLine($"[CraneService] [{_name}] ✔ 抖动松料完成");
        }

        // ─── 接液盘（接线器）───────────────────────────────────────

        /// <summary>
        /// 手动开接液盘：写 D4500=2（首次）→ D4512=2 触发 → D4512=0 复位。
        /// </summary>
        public async Task DrainOpenAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 开接液盘");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualDrainOpen, 2, "开接液盘(触发)", ct);
            await WriteRegAsync(Addr.D_ManualDrainOpen, 0, "开接液盘(复位)", ct);
        }

        /// <summary>
        /// 手动关接液盘：写 D4500=2（首次）→ D4513=2 触发 → D4513=0 复位。
        /// </summary>
        public async Task DrainCloseAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 关接液盘");
            await EnsureManualModeAsync(ct);
            await WriteRegAsync(Addr.D_ManualDrainClose, 2, "关接液盘(触发)", ct);
            await WriteRegAsync(Addr.D_ManualDrainClose, 0, "关接液盘(复位)", ct);
        }

        // ═══════════════════════════════════════════════════════════════
        //  天车任务启动前安全检查
        // ═══════════════════════════════════════════════════════════════

        /// <summary>天车安全检查结果。</summary>
        public sealed class SafetyCheckResult
        {
            /// <summary>是否全部通过</summary>
            public bool AllPassed { get; init; }
            /// <summary>失败原因（通过时为空）</summary>
            public string FailReason { get; init; } = string.Empty;

            public static SafetyCheckResult Passed() => new() { AllPassed = true };
            public static SafetyCheckResult Failed(string reason) => new() { AllPassed = false, FailReason = reason };
        }

        /// <summary>
        /// 天车任务启动前安全检查：
        ///   1. 天车是否正在运动（D5006 Busy ≠ 0）      → 等待（不可同时发两条运动指令）
        ///   2. 天车是否故障（D5009 Fault ≠ 0）          → 拒绝
        ///   3. 伺服是否报警（D5011 ServoAlarm ≠ 0）    → 拒绝
        ///   4. 天车是否已有版（D5029 HasRoller ≠ 0）   → 暂停（需人工确认）
        /// 全部通过返回 Passed，否则返回 Failed(reason)。
        /// </summary>
        public async Task<SafetyCheckResult> CheckSafetyAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 执行安全检查...");
            var status = await ReadStatusAsync(ct);
            if (status == null)
            {
                Console.WriteLine($"[CraneService] [{_name}] ✘ 安全检查失败：无法读取状态");
                return SafetyCheckResult.Failed("无法读取天车状态");
            }

            Console.WriteLine($"[CraneService] [{_name}]   忙闲={status.Busy} 故障={status.Fault} 伺服报警={status.ServoAlarm} 有版={status.HasRoller}");

            if (status.Busy != 0)
            {
                Console.WriteLine($"[CraneService] [{_name}] ⚠ 安全检查：天车正在运动中 D5006={status.Busy}，等待空闲");
                return SafetyCheckResult.Failed($"天车正在运动中（D5006={status.Busy}），请等待当前操作完成");
            }
            if (status.Fault != 0)
            {
                Console.WriteLine($"[CraneService] [{_name}] ✘ 安全检查：天车故障 D5009={status.Fault}");
                return SafetyCheckResult.Failed($"天车故障（D5009={status.Fault}）");
            }
            if (status.ServoAlarm != 0)
            {
                Console.WriteLine($"[CraneService] [{_name}] ✘ 安全检查：伺服报警 D5011={status.ServoAlarm}");
                return SafetyCheckResult.Failed($"伺服报警（D5011={status.ServoAlarm}）");
            }
            if (status.HasRoller != 0)
            {
                Console.WriteLine($"[CraneService] [{_name}] ⚠ 安全检查：天车已有版 D5029={status.HasRoller}，需人工确认");
                return SafetyCheckResult.Failed($"天车已有版（D5029={status.HasRoller}），需人工确认");
            }

            Console.WriteLine($"[CraneService] [{_name}] ✔ 安全检查通过");
            return SafetyCheckResult.Passed();
        }

        // ═══════════════════════════════════════════════════════════════
        //  自动任务握手信号（D5100~D5103）—— 2→0 脉冲模式
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 数据下发完成（D5100）：写2触发 → 写0复位。
        /// 上位机下发所有加工参数后调用，通知天车可以开始搬运。
        /// </summary>
        public async Task SetDataSentDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 数据下发完成 D5100");
            await WriteRegAsync(Addr.D_DataSentDone, 2, "数据下发完成(触发) D5100", ct);
            await WriteRegAsync(Addr.D_DataSentDone, 0, "数据下发完成(复位) D5100", ct);
        }

        /// <summary>
        /// 尾座已松开到位（D5101）：写2触发 → 写0复位。
        /// 上位机确认源设备尾座已松开后调用，通知天车可以取料。
        /// </summary>
        public async Task SetTailstockOpenedAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 尾座松开到位 D5101");
            await WriteRegAsync(Addr.D_TailstockOpened, 2, "尾座松开到位(触发) D5101", ct);
            await WriteRegAsync(Addr.D_TailstockOpened, 0, "尾座松开到位(复位) D5101", ct);
        }

        /// <summary>
        /// 目标设备尾座夹紧到位（D5102）：写2触发 → 写0复位。
        /// 上位机确认目标设备已夹紧工件后调用。
        /// </summary>
        public async Task SetTargetTailstockClampedAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 目标尾座夹紧到位 D5102");
            await WriteRegAsync(Addr.D_TargetTailstockClamped, 2, "目标尾座夹紧(触发) D5102", ct);
            await WriteRegAsync(Addr.D_TargetTailstockClamped, 0, "目标尾座夹紧(复位) D5102", ct);
        }

        /// <summary>
        /// 天车控制命令（D5103）：开始=1, 忙碌=2, 停止=3, 空闲=4。
        /// 自动流程中写 1 触发天车执行已下发的任务。
        /// </summary>
        /// <param name="command">1=开始, 2=忙碌, 3=停止, 4=空闲</param>
        public async Task SetStartStopCommandAsync(int command, CancellationToken ct = default)
        {
            string label = command switch { 1 => "开始", 2 => "忙碌", 3 => "停止", 4 => "空闲", _ => $"未知({command})" };
            Console.WriteLine($"[CraneService] [{_name}] ▶ 控制命令 D5103={command} ({label})");
            // D5103 是持续状态而非脉冲，只写一次不复位
            await WriteRegAsync(Addr.D_StartStop, command, $"控制命令({label}) D5103", ct);
        }

        // ─── 相对运动 速度/加减速设置 ───────────────────────────────

        /// <summary>
        /// 设置各轴独立的相对位移速度/加速度/减速度（手动点动用）。
        /// <para>寄存器：X=D2504~D2506, Y=D2513~D2515, Z=D2522~D2524。</para>
        /// </summary>
        public async Task SetRelSpeedAsync(int speedX, int accelX, int decelX,
                                             int speedY, int accelY, int decelY,
                                             int speedZ, int accelZ, int decelZ,
                                             CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 设置相对速度 X={speedX}/{accelX}/{decelX} Y={speedY}/{accelY}/{decelY} Z={speedZ}/{accelZ}/{decelZ}");

            await WriteRegAsync(Addr.D_XRelSpeed, speedX, "X相对速度 D2504", ct);
            await WriteRegAsync(Addr.D_XRelAccel, accelX, "X相对加速度 D2505", ct);
            await WriteRegAsync(Addr.D_XRelDecel, decelX, "X相对减速度 D2506", ct);

            await WriteRegAsync(Addr.D_YRelSpeed, speedY, "Y相对速度 D2513", ct);
            await WriteRegAsync(Addr.D_YRelAccel, accelY, "Y相对加速度 D2514", ct);
            await WriteRegAsync(Addr.D_YRelDecel, decelY, "Y相对减速度 D2515", ct);

            await WriteRegAsync(Addr.D_ZRelSpeed, speedZ, "Z相对速度 D2522", ct);
            await WriteRegAsync(Addr.D_ZRelAccel, accelZ, "Z相对加速度 D2523", ct);
            await WriteRegAsync(Addr.D_ZRelDecel, decelZ, "Z相对减速度 D2524", ct);

            Console.WriteLine($"[CraneService] [{_name}] ✔ 相对速度设置完成（9个寄存器）");
        }

        // ─── 绝对位移 速度/加减速设置 ───────────────────────────────

        /// <summary>
        /// 设置各轴独立的绝对位移速度/加速度/减速度。
        /// <para>寄存器：X=D2501~D2503, Y=D2510~D2512, Z=D2519~D2521。</para>
        /// </summary>
        public async Task SetAbsSpeedAsync(int speedX, int accelX, int decelX,
                                             int speedY, int accelY, int decelY,
                                             int speedZ, int accelZ, int decelZ,
                                             CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ 设置绝对速度 X={speedX}/{accelX}/{decelX} Y={speedY}/{accelY}/{decelY} Z={speedZ}/{accelZ}/{decelZ}");

            await WriteRegAsync(Addr.D_XAbsSpeed, speedX, "X绝对速度 D2501", ct);
            await WriteRegAsync(Addr.D_XAbsAccel, accelX, "X绝对加速度 D2502", ct);
            await WriteRegAsync(Addr.D_XAbsDecel, decelX, "X绝对减速度 D2503", ct);

            await WriteRegAsync(Addr.D_YAbsSpeed, speedY, "Y绝对速度 D2510", ct);
            await WriteRegAsync(Addr.D_YAbsAccel, accelY, "Y绝对加速度 D2511", ct);
            await WriteRegAsync(Addr.D_YAbsDecel, decelY, "Y绝对减速度 D2512", ct);

            await WriteRegAsync(Addr.D_ZAbsSpeed, speedZ, "Z绝对速度 D2519", ct);
            await WriteRegAsync(Addr.D_ZAbsAccel, accelZ, "Z绝对加速度 D2520", ct);
            await WriteRegAsync(Addr.D_ZAbsDecel, decelZ, "Z绝对减速度 D2521", ct);

            Console.WriteLine($"[CraneService] [{_name}] ✔ 绝对速度设置完成（9个寄存器）");
        }

        /// <summary>
        /// 确认Z轴位于零位允许范围。用于新取料任务首次XY横移前，避免上一次异常后低Z横移。
        /// 状态未知、回零失败或回零后复核失败均直接抛出，调用方不得继续XY动作。
        /// </summary>
        public async Task EnsureZAtZeroAsync(int tolerance = 5, CancellationToken ct = default)
        {
            if (tolerance < 0)
                throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Z零位容差不能小于0");

            var status = await ReadStatusAsync(ct);
            if (status == null)
                throw new InvalidOperationException($"[CraneService] [{_name}] 无法读取当前Z，禁止XY横移");

            if (Math.Abs(status.ZPos) <= tolerance)
            {
                Console.WriteLine($"[CraneService] [{_name}] Z零位检查通过: Z={status.ZPos}, 容差=±{tolerance}mm");
                return;
            }

            Console.WriteLine($"[CraneService] [{_name}] Z={status.ZPos}超出零位容差±{tolerance}mm，先回Z=0再允许XY");
            await MoveAbsoluteAsync(-1, -1, 0, tolerance: tolerance, ct: ct);

            var verified = await ReadStatusAsync(ct);
            if (verified == null)
                throw new InvalidOperationException($"[CraneService] [{_name}] Z回零后状态读取失败，禁止XY横移");
            if (Math.Abs(verified.ZPos) > tolerance)
                throw new InvalidOperationException(
                    $"[CraneService] [{_name}] Z回零复核失败: Z={verified.ZPos}, 容差=±{tolerance}mm，禁止XY横移");

            Console.WriteLine($"[CraneService] [{_name}] Z回零复核通过: Z={verified.ZPos}, 容差=±{tolerance}mm");
        }

        /// <summary>
        /// 绝对位移：写入目标坐标 → 按 Z 轴方向决定触发顺序 → 轮询到位 → 复位。
        /// <para>安全规范：Z 下降时先走 XY 再走 Z（防撞），Z 上升时先走 Z 再走 XY（防拖拽）。</para>
        /// <para>
        /// X 目标：D3102~D3103（DINT）→ 触发 D4522=2 → 到位后 D4522=0
        /// Y 目标：D3104~D3105（DINT）→ 触发 D4521=2 → 到位后 D4521=0
        /// Z 目标：D3106~D3107（DINT）→ 触发 D4520=2 → 到位后 D4520=0
        /// </para>
        /// </summary>
        public async Task MoveAbsoluteAsync(
            int xTarget, int yTarget, int zTarget,
            int tolerance = 5, int timeoutMs = 240_000, CancellationToken ct = default)
        {
            var activeAxes = new List<string>();
            if (xTarget != -1) activeAxes.Add("X");
            if (yTarget != -1) activeAxes.Add("Y");
            if (zTarget != -1) activeAxes.Add("Z");
            Console.WriteLine($"[CraneService] [{_name}] ▶ 绝对移动 目标 X={xTarget} Y={yTarget} Z={zTarget} 轴={string.Join(",", activeAxes)}");

            await EnsureManualModeAsync(ct);

            // ── 1. 写入所有目标坐标 ──────────────────────────────────────
            if (xTarget != -1)
                await WriteDintAsync(Addr.D_XAbsTarget, xTarget, "X绝对目标 D3102~D3103", ct);
            if (yTarget != -1)
                await WriteDintAsync(Addr.D_YAbsTarget, yTarget, "Y绝对目标 D3104~D3105", ct);
            if (zTarget != -1)
                await WriteDintAsync(Addr.D_ZAbsTarget, zTarget, "Z绝对目标 D3106~D3107", ct);

            // ── 2. 读取当前 Z，判断升降方向 ──────────────────────────────
            bool hasZMove = zTarget != -1;
            bool zGoingDown = false, zGoingUp = false;
            if (hasZMove)
            {
                var curStatus = await ReadStatusAsync(ct);
                // 包含Z目标时必须知道当前Z才能决定“先XY还是先Z”。状态未知时继续触发
                // 会落入三轴同时运动分支，破坏防碰撞顺序，因此这里保守拒绝本次动作。
                if (curStatus == null)
                    throw new InvalidOperationException($"[CraneService] [{_name}] 当前Z状态未知，禁止绝对移动");

                int dz = zTarget - curStatus.ZPos;
                // Z轴方向：Z从0起始，变大=向下走 ↓，变小=向上走 ↑
                zGoingDown = dz > tolerance;   // 目标 > 当前（Z变大）→ ↓下降
                zGoingUp   = dz < -tolerance;  // 目标 < 当前（Z变小）→ ↑上升
                Console.WriteLine($"[CraneService] [{_name}] 当前Z={curStatus.ZPos} 目标Z={zTarget} ΔZ={dz} " +
                    $"{(zGoingDown ? "↓下降" : zGoingUp ? "↑上升" : "→水平")}");
            }

            // ── 3. 接液盘互锁：Z 下降前必须打开，Z 上升后必须关闭 ──────────
            if (zGoingDown)
            {
                Console.WriteLine($"[CraneService] [{_name}] 接液盘互锁：Z下降前先打开接液盘");
                await DrainOpenAsync(ct);
                await Task.Delay(500, ct); // 等接液盘打开到位（无DI反馈用延时兜底）
            }

            // ── 4. 按 Z 方向决定触发顺序 ──────────────────────────────────
            try
            {
                // ── 绝对移动触发：2→500ms→0 脉冲模式 ──
                //    PLC 收到 2 脉冲后开始执行移动，写 0 是复位清理。
                //    Z下降：先脉冲XY → 等XY到位 → 脉冲Z → 等全部到位
                //    Z上升：先脉冲Z → 等Z到位 → 脉冲XY → 等全部到位
                if (zGoingDown)
                {
                    Console.WriteLine($"[CraneService] [{_name}] Z下降 → 先脉冲XY再脉冲Z");
                    if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 2, "X绝对移动触发 D4522", ct);
                    if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 2, "Y绝对移动触发 D4521", ct);
                    await Task.Delay(500, ct);
                    if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 0, "X绝对移动复位 D4522", ct);
                    if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 0, "Y绝对移动复位 D4521", ct);
                    await PollAxesAsync(xTarget, yTarget, -1, tolerance, timeoutMs / 2, ct);
                    if (zTarget != -1)
                    {
                        await WriteRegAsync(Addr.D_ManualZAbsMove, 2, "Z绝对移动触发 D4520", ct);
                        await Task.Delay(500, ct);
                        await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "Z绝对移动复位 D4520", ct);
                    }
                }
                else if (zGoingUp)
                {
                    Console.WriteLine($"[CraneService] [{_name}] Z上升 → 先脉冲Z再脉冲XY");
                    if (zTarget != -1)
                    {
                        await WriteRegAsync(Addr.D_ManualZAbsMove, 2, "Z绝对移动触发 D4520", ct);
                        await Task.Delay(500, ct);
                        await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "Z绝对移动复位 D4520", ct);
                    }
                    await PollAxesAsync(-1, -1, zTarget, tolerance, timeoutMs / 2, ct, monitorPressure: false);
                    if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 2, "X绝对移动触发 D4522", ct);
                    if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 2, "Y绝对移动触发 D4521", ct);
                    await Task.Delay(500, ct);
                    if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 0, "X绝对移动复位 D4522", ct);
                    if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 0, "Y绝对移动复位 D4521", ct);
                }
                else
                {
                    Console.WriteLine($"[CraneService] [{_name}] Z不变 → 三轴同时脉冲");
                    if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 2, "X绝对移动触发 D4522", ct);
                    if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 2, "Y绝对移动触发 D4521", ct);
                    if (zTarget != -1) await WriteRegAsync(Addr.D_ManualZAbsMove, 2, "Z绝对移动触发 D4520", ct);
                    await Task.Delay(500, ct);
                    if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 0, "X绝对移动复位 D4522", ct);
                    if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 0, "Y绝对移动复位 D4521", ct);
                    if (zTarget != -1) await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "Z绝对移动复位 D4520", ct);
                }

                // ── 4. 轮询等待所有轴到位 ──────────────────────────────────
                await PollAxesAsync(xTarget, yTarget, zTarget, tolerance, timeoutMs, ct, monitorPressure: zGoingDown);
            }
            finally
            {
                // ⚠️ finally 复位必须用 CancellationToken.None！
                // 上层可能传了带超时的 ct（如手动面板 5s），移动本身耗时远超 5s，
                // ct 取消后 finally 里再写寄存器会被拒绝，导致 D4520/D4521/D4522 卡在 2 不复位。
                var resetCt = CancellationToken.None;

                // ── 5. 复位所有触发信号 ──────────────────────────────────
                if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 0, "X绝对移动复位 D4522", resetCt);
                if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 0, "Y绝对移动复位 D4521", resetCt);
                if (zTarget != -1) await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "Z绝对移动复位 D4520", resetCt);

                // ── 接液盘互锁：Z 上升后关闭接液盘 ────────────────────
                if (zGoingUp)
                {
                    Console.WriteLine($"[CraneService] [{_name}] 接液盘互锁：Z上升后关闭接液盘");
                    await DrainCloseAsync(resetCt);
                }
            }

            Console.WriteLine($"[CraneService] [{_name}] ✔ 绝对移动完成，触发已复位");
        }

        /// <summary>
        /// Z 轴步进下探：取料时 Z 已到目标但磁铁没触发 → 再降 stepDistance mm → 再查。
        /// 重复直到检到版(D5029=1)或超过 maxSteps 次。
        /// </summary>
        /// <param name="stepDistance">每次下探步距 mm（默认5）</param>
        /// <param name="maxSteps">最大步数（默认10，即最多再降50mm）</param>
        /// <returns>true=检到版，false=步数用完仍未检到版</returns>
        /// <remarks>当前生产流程未调用此方法；接入前必须现场确认Z坐标方向。</remarks>
        public async Task<bool> StepDownUntilPickupAsync(int stepDistance = 5, int maxSteps = 10, CancellationToken ct = default)
        {
            Console.WriteLine($"[CraneService] [{_name}] ▶ Z轴步进下探 step={stepDistance}mm maxSteps={maxSteps}");

            for (int i = 1; i <= maxSteps; i++)
            {
                ct.ThrowIfCancellationRequested();

                var status = await ReadStatusAsync(ct);
                if (status == null)
                    throw new InvalidOperationException($"[CraneService] [{_name}] Z步进前当前位置未知，禁止计算下一个目标");

                if (status.HasRoller == 1)
                {
                    Console.WriteLine($"[CraneService] [{_name}] ✔ Z步进下探：检到版 D5029=1（第{i}步） Z={status.ZPos}");
                    return true;
                }

                // 在当前 Z 基础上再降 stepDistance
                int nextZ = status.ZPos - stepDistance;
                Console.WriteLine($"[CraneService] [{_name}]   Z步进 {i}/{maxSteps}：当前Z={status.ZPos} → 目标Z={nextZ}");

                await WriteDintAsync(Addr.D_ZAbsTarget, nextZ, "Z步进目标 D3106~D3107", ct);
                await WriteRegAsync(Addr.D_ManualZAbsMove, 2, "Z步进触发 D4520", ct);
                await Task.Delay(500, ct);
                await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "Z步进复位 D4520", ct);
            }

            Console.WriteLine($"[CraneService] [{_name}] ⚠ Z步进下探：{maxSteps}步后仍未检到版");
            return false;
        }

        /// <summary>
        /// 轮询等待指定轴到位（-1 表示跳过该轴）。
        /// 超时后抛异常，由上层按工件物理状态决定暂停或人工处理。
        /// <param name="monitorPressure">仅 Z 下降时检测 X2 下压信号；Z 上升时不检测，防止回升途中 X2 延迟释放导致误急停。</param>
        /// </summary>
        private async Task PollAxesAsync(int xTarget, int yTarget, int zTarget,
            int tolerance, int timeoutMs, CancellationToken ct, bool monitorPressure = false)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                // Z 轴下降时用 200ms 快轮询，更快响应下压信号；其他情况 500ms
                int pollDelay = zTarget != -1 ? 200 : 500;
                await Task.Delay(pollDelay, ct);

                // ── 磁铁下压限位检测：X2=63490 线圈=1 → 下压急停 ──
                //    仅 Z 下降（monitorPressure=true）时检测。Z 上升时不检测，
                //    防止吸住工件后回升途中 X2 延迟释放导致误急停。
                //    X2（FC01 Read Coil）是磁铁物理下压限位开关，PLC 无法直接停止伺服，
                //    上位机检测到 X2=1 后主动写 D4523=2→0（下压急停触发）、D4518=2→0（伺服急停）。
                if (monitorPressure)
                {
                    bool x2Pressed = await ReadXBitAsync(Addr.D_X2_MagnetLimit, ct);
                    if (x2Pressed)
                    {
                        Console.WriteLine($"══════════════════════════════════════");
                        Console.WriteLine($"  [CraneService] [{_name}] ⚠⚠⚠ 磁铁下压限位 X2=1 ⚠⚠⚠");
                        Console.WriteLine($"  磁铁已接触工件/障碍物！");
                        Console.WriteLine($"  ① D4523=2→0 下压急停（500ms脉冲）");
                        Console.WriteLine($"  ② D4518=2→0 伺服急停");
                        Console.WriteLine($"  ③ 复位绝对触发位 D4520/D4521/D4522=0");
                        Console.WriteLine($"  恢复步骤：①写D4523=0 ②清除报警D4514=2→0");
                        Console.WriteLine($"══════════════════════════════════════");

                        // 下压急停是安全关键操作，必须用 CancellationToken.None
                        var emergencyCt = CancellationToken.None;

                        // ① 下压急停 D4523=2 → 500ms → 0（通知 PLC 进入下压急停状态）
                        await WriteRegAsync(Addr.D_PressureEStop, 2, "下压急停 D4523(触发)", emergencyCt);
                        await Task.Delay(500, emergencyCt);
                        await WriteRegAsync(Addr.D_PressureEStop, 0, "下压急停 D4523(复位)", emergencyCt);

                        // ② 伺服急停 D4518=2→0（立即停止伺服电机）
                        await WriteRegAsync(Addr.D_ManualEStop, 2, "伺服急停 D4518(触发)", emergencyCt);
                        await WriteRegAsync(Addr.D_ManualEStop, 0, "伺服急停 D4518(复位)", emergencyCt);

                        // ③ 复位绝对移动触发位，防止急停恢复后继续运动
                        if (xTarget != -1) await WriteRegAsync(Addr.D_ManualXAbsMove, 0, "X下压停止复位 D4522", emergencyCt);
                        if (yTarget != -1) await WriteRegAsync(Addr.D_ManualYAbsMove, 0, "Y下压停止复位 D4521", emergencyCt);
                        if (zTarget != -1) await WriteRegAsync(Addr.D_ManualZAbsMove, 0, "Z下压停止复位 D4520", emergencyCt);

                        throw new PressureStopException(_name);
                    }
                }

                var status = await ReadStatusAsync(ct);
                if (status == null) continue;

                bool xOk = xTarget == -1 || Math.Abs(status.XPos - xTarget) <= tolerance;
                bool yOk = yTarget == -1 || Math.Abs(status.YPos - yTarget) <= tolerance;
                bool zOk = zTarget == -1 || Math.Abs(status.ZPos - zTarget) <= tolerance;

                if (xOk && yOk && zOk)
                {
                    Console.WriteLine($"[CraneService] [{_name}] ✔ 到位 X={status.XPos} Y={status.YPos} Z={status.ZPos}");
                    return;
                }
            }

            throw new TimeoutException($"[CraneService] [{_name}] 绝对移动超时，目标 X={xTarget} Y={yTarget} Z={zTarget}");
        }

        private bool ShouldLogStatus(CraneStatus status)
        {
            string key = $"{status.Mode}|{status.Busy}|{status.Fault}|{status.ServoAlarm}|" +
                         $"{status.PlcAlarm}|{status.HasRoller}|{status.CurrentTaskNo}|{status.StateMachineStep}";
            var now = DateTime.UtcNow;
            lock (_statusLogLock)
            {
                bool changed = !string.Equals(_lastStatusLogKey, key, StringComparison.Ordinal);
                bool heartbeat = now - _lastStatusLogAtUtc >= StatusLogHeartbeat;
                if (!changed && !heartbeat) return false;
                _lastStatusLogKey = key;
                _lastStatusLogAtUtc = now;
                return true;
            }
        }

        /// <summary>
        /// 原子写入 DINT（32bit）到两个连续 Modbus 寄存器（FC16 Write Multiple Registers）。
        /// 小端（低字在前 @ startAddr，高字在后 @ startAddr+1），与汇川 PLC 一致。
        /// </summary>
        private async Task WriteDintAsync(int startAddr, int value, string label, CancellationToken ct)
        {
            await SafeIoAsync(token => _client.WriteInt32Async(startAddr, value, token), $"{label} D{startAddr}~D{startAddr + 1}", ct);
            Console.WriteLine($"[CraneService] [{_name}] ✔ {label} 写入成功 D{startAddr}~D{startAddr + 1}={value}");
        }

        // ─── 辅助：统一写入并打印调试日志 ───────────────────────────

        /// <summary>写单个寄存器，并输出调试日志。</summary>
        private async Task WriteRegAsync(int address, int value, string label, CancellationToken ct)
        {
            try
            {
                // 使用公共 WriteAsync（FC06 单寄存器写入）
                await SafeIoAsync(token => _client.WriteAsync(address, value, token), $"{label} D{address}", ct);
                Console.WriteLine($"[CraneService] [{_name}] ✔ {label} 写入成功 D{address}={value}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CraneService] [{_name}] ✘ {label} 写入失败 D{address}：{ex.Message}");
                throw;
            }
        }

        private async Task<T> SafeIoAsync<T>(Func<CancellationToken, Task<T>> action, string desc, CancellationToken ct)
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= MaxIoAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!_client.IsConnected)
                        await ConnectAsync(ct);

                    return await action(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt >= MaxIoAttempts) break;
                    Console.WriteLine($"[CraneService] [{_name}] {desc} 通信异常 attempt={attempt}/{MaxIoAttempts}: {ex.Message} → 重连后重试");
                    try { await DisconnectAsync(); } catch { }
                    await Task.Delay(200, ct);
                }
            }

            throw new InvalidOperationException($"{desc} {MaxIoAttempts}次重连重试仍失败: {last?.Message}", last);
        }

        private Task SafeIoAsync(Func<CancellationToken, Task> action, string desc, CancellationToken ct)
            => SafeIoAsync(async token =>
            {
                await action(token);
                return true;
            }, desc, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // 天车状态快照（D5000~D5029）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>天车状态快照（对应一次批量读取 D5000~D5029 的结果）。</summary>
    public class CraneStatus
    {
        public short RequestData         { get; set; }  // D5000
        public short ArrivedMagnet       { get; set; }  // D5001
        public short PickDone            { get; set; }  // D5002
        public short ArrivedTarget       { get; set; }  // D5003
        public short LoadDone            { get; set; }  // D5004
        public short CurrentTaskNo       { get; set; }  // D5005
        /// <summary>忙碌(1) / 空闲(0)</summary>
        public short Busy                { get; set; }  // D5006
        public short StateMachineStep    { get; set; }  // D5007
        /// <summary>模式：自动(1) / 手动(2)</summary>
        public short Mode                { get; set; }  // D5008
        /// <summary>故障标志：正常(0) / 故障(1)</summary>
        public short Fault               { get; set; }  // D5009
        /// <summary>运行条件缺失位图（0=全满足）</summary>
        public short RunConditionMissing { get; set; }  // D5010
        /// <summary>伺服报警位图（0=无报警）</summary>
        public short ServoAlarm          { get; set; }  // D5011
        public short PlcServoAlarm       { get; set; }  // D5012
        public short PlcAlarm            { get; set; }  // D5013
        /// <summary>X轴绝对编码器值（DINT，D5014~D5015）</summary>
        public int   XEncoderAbs         { get; set; }
        /// <summary>X轴原点清零值（DINT，D5016~D5017）</summary>
        public int   XEncoderZero        { get; set; }
        /// <summary>X轴显示坐标（DINT，D5018~D5019）</summary>
        public int   XPos                { get; set; }
        public int   YEncoderAbs         { get; set; }  // D5020（ushort→int）
        public int   YEncoderZero        { get; set; }  // D5021（ushort→int）
        /// <summary>Y轴显示坐标（INT，0~65535）</summary>
        public int   YPos                { get; set; }  // D5022（ushort→int，无符号避免 32768+ 显示为负数）
        public int   ZEncoderAbs         { get; set; }  // D5023（ushort→int）
        public int   ZEncoderZero        { get; set; }  // D5024（ushort→int）
        /// <summary>Z轴显示坐标（INT，0~65535）</summary>
        public int   ZPos                { get; set; }  // D5025（ushort→int，无符号避免 32768+ 显示为负数）
        public short PlcSeqNo            { get; set; }  // D5026
        public short TaskSourceDevice    { get; set; }  // D5027
        public short TaskTargetDevice    { get; set; }  // D5028
        /// <summary>有版(1) / 无版(0)</summary>
        public short HasRoller           { get; set; }  // D5029
    }
}
