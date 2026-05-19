using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.PlcGrinderAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>
    /// 研磨机 Modbus TCP 通信服务。
    /// <para>TypeA（西门子 PLC）：1 台 PLC 管 1 台研磨机。DI=40001 bit 型（FC03 读整字），DO=40011 bit 型（FC06 读-改-写），Word 参数=40012~40014。</para>
    /// <para>TypeB（新代数控）：R 区寄存器，Modbus 地址 = R编号 × 2 + 1（FC03/FC06）。</para>
    /// <para>DO 信号规范：3 秒长信号（写 1 → 等 3s → 写 0），与天车 2→0 脉冲不同。</para>
    /// <para>UnitId：默认取 IP 末段（192.168.2.93 → 93），与数控 PLC 站号一致。</para>
    /// </summary>
    public sealed class PlcGrinderService : IDisposable
    {
        public enum GrinderType { TypeA, TypeB }

        private readonly ModbusTcpClient _client;
        private readonly GrinderType _type;
        private readonly string _name;
        private bool _disposed;

        // TypeA 寄存器零起始 Modbus 地址（通信地址 - 40001）
        private static readonly int _diRegAddr = Addr.RegDI_Base - 40001;  // = 0
        private static readonly int _doRegAddr = Addr.RegDO_Base - 40001;  // = 10

        /// <param name="name">调试名称（如"研磨机3(西门子)"）</param>
        /// <param name="ip">PLC/CNC 的 IP 地址</param>
        /// <param name="type">西门子=TypeA，新代=TypeB</param>
        /// <param name="port">Modbus TCP 端口，默认 502</param>
        /// <param name="unitId">
        /// Modbus 从站地址。null 时按类型自动取默认：
        /// TypeA（西门子）= 1，TypeB（新代）= IP 末段（192.168.2.90→90）。
        /// </param>
        public PlcGrinderService(string name, string ip, GrinderType type, int port = 502, int? unitId = null)
        {
            _name = name;
            int uid = unitId ?? GetDefaultUnitId(ip, type);
            _client = new ModbusTcpClient(ip, port, (byte)uid);
            _type = type;
            Console.WriteLine($"[GrinderSvc] [{_name}] 创建 Type={type} IP={ip}:{port} UnitId={uid}");
        }

        /// <summary>按类型取默认 UnitId：TypeA（西门子）= 1，TypeB（新代）= IP 末段。</summary>
        private static int GetDefaultUnitId(string ip, GrinderType type)
        {
            if (type == GrinderType.TypeA)
                return 1; // 西门子 PLC Modbus TCP Server 默认从站地址 = 1

            // TypeB 新代数控：站号 = IP 末段
            int lastDot = ip.LastIndexOf('.');
            if (lastDot >= 0 && int.TryParse(ip.Substring(lastDot + 1), out int id))
                return id;
            return 1;
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] 连接中...");
            await _client.ConnectAsync(ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] ✔ 连接成功");
        }
        public Task DisconnectAsync() => _client.DisconnectAsync();
        public bool IsConnected => _client.IsConnected;

        // ═══════════════════════════════════════════════════════════════
        //  DI 原始值读取
        // ═══════════════════════════════════════════════════════════════

        /// <summary>读 TypeA DI 寄存器原始整字（Modbus 地址=0，即 40001 的 16bit）。调试用。</summary>
        public async Task<int> ReadDIRawAsync(CancellationToken ct = default)
        {
            int val = await _client.ReadIntAsync(_diRegAddr, ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] DI=0x{val:X4} (b{Convert.ToString(val, 2).PadLeft(16, '0')})");
            return val;
        }

        // ═══════════════════════════════════════════════════════════════
        //  心跳检测（TypeA：40001 bit8，1Hz 方波）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检测 PLC 1Hz 心跳是否正常。
        /// 连续读两次（间隔 500ms），bit 有变化说明 PLC 存活。
        /// </summary>
        public async Task<bool> IsHeartbeatOkAsync(CancellationToken ct = default)
        {
            int di1 = await _client.ReadIntAsync(_diRegAddr, ct);
            bool bit1 = (di1 & (1 << Addr.Bit_Heartbeat)) != 0;

            await Task.Delay(500, ct);

            int di2 = await _client.ReadIntAsync(_diRegAddr, ct);
            bool bit2 = (di2 & (1 << Addr.Bit_Heartbeat)) != 0;

            bool ok = bit1 != bit2; // 1Hz 方波，500ms 内应翻转
            Console.WriteLine($"[GrinderSvc] [{_name}] 心跳检测 bit8: {bit1}→{bit2} {(ok ? "✔ 正常" : "✘ 无变化(PLC可能离线)")}");
            return ok;
        }

        // ═══════════════════════════════════════════════════════════════
        //  批量状态读取
        // ═══════════════════════════════════════════════════════════════

        /// <summary>一次性读取全部关键状态并输出到控制台。</summary>
        public async Task ReadAllStatusAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"═══ [GrinderSvc] [{_name}] 状态读取 ═══");
            Console.WriteLine($"[GrinderSvc]   Type={_type}");

            if (_type == GrinderType.TypeA)
            {
                int di = await _client.ReadIntAsync(_diRegAddr, ct);
                int doReg = await _client.ReadIntAsync(_doRegAddr, ct);
                Console.WriteLine($"[GrinderSvc]   DI 40001 =0x{di:X4} (b{Convert.ToString(di, 2).PadLeft(16, '0')})");
                Console.WriteLine($"[GrinderSvc]   DO 40011 =0x{doReg:X4} (b{Convert.ToString(doReg, 2).PadLeft(16, '0')})");
                Console.WriteLine($"[GrinderSvc]   bit8  心跳        = {(di & (1 << 8)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit9  请求数据    = {(di & (1 << 9)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit10 请求上料    = {(di & (1 << 10)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit11 锁紧完成    = {(di & (1 << 11)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit12 请求下料    = {(di & (1 << 12)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit13 松开完成    = {(di & (1 << 13)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit14 加工中      = {(di & (1 << 14)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit15 门开        = {(di & (1 << 15)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit0  报警        = {(di & (1 << 0)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit1  磨石1厚度报警 = {(di & (1 << 1)) != 0}");
                Console.WriteLine($"[GrinderSvc]   bit2  磨石2厚度报警 = {(di & (1 << 2)) != 0}");
            }
            else
            {
                int v7301 = await _client.ReadIntAsync(Addr.RToModbus(Addr.R_RequestData), ct);
                int v7306 = await _client.ReadIntAsync(Addr.RToModbus(Addr.R_Machining), ct);
                int v7307 = await _client.ReadIntAsync(Addr.RToModbus(Addr.R_DoorOpen), ct);
                int v7308 = await _client.ReadIntAsync(Addr.RToModbus(Addr.R_MachineStatus), ct);
                Console.WriteLine($"[GrinderSvc]   R7301 请求数据={v7301} R7306 加工中={v7306} R7307 门开={v7307} R7308 状态={v7308}");
            }
            Console.WriteLine($"══════════════════════════════════");
        }

        // ═══════════════════════════════════════════════════════════════
        //  状态读取（TypeA / TypeB 统一接口）
        // ═══════════════════════════════════════════════════════════════

        public Task<bool> IsRequestDataAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_RequestData, ct)
            : ReadBoolAsync(Addr.R_RequestData, ct);

        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_RequestLoad, ct)
            : ReadBoolAsync(Addr.R_RequestLoad, ct);

        public Task<bool> IsClampDoneLoadOutAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_ClampDoneLoadOut, ct)
            : ReadBoolAsync(Addr.R_ClampDoneLoadOut, ct);

        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_RequestUnload, ct)
            : ReadBoolAsync(Addr.R_RequestUnload, ct);

        public Task<bool> IsUnclampDoneAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_UnclampDone, ct)
            : ReadBoolAsync(Addr.R_UnclampDoneUnloadOut, ct);

        public Task<bool> IsMachiningAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_Machining, ct)
            : ReadBoolAsync(Addr.R_Machining, ct);

        /// <summary>门开状态（读）。天车上下移动前必须确认门开=0（门已关闭）。</summary>
        public Task<bool> IsDoorOpenAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_DoorOpen, ct)
            : ReadBoolAsync(Addr.R_DoorOpen, ct);

        /// <summary>确认安全门已关闭（门开=0）。false=门开着/禁止天车移动，true=门已关/安全。</summary>
        public async Task<bool> IsDoorClosedAsync(CancellationToken ct = default)
        {
            bool doorOpen = await IsDoorOpenAsync(ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] 安全门={(doorOpen ? "开(禁止天车升降)" : "关(安全)")}");
            return !doorOpen;
        }

        public Task<bool> IsGrindStone1AlarmAsync(CancellationToken ct = default)
            => ReadBitAsync(_diRegAddr, Addr.Bit_GrindStone1Alarm, ct);

        public Task<bool> IsGrindStone2AlarmAsync(CancellationToken ct = default)
            => ReadBitAsync(_diRegAddr, Addr.Bit_GrindStone2Alarm, ct);

        /// <summary>
        /// 磨石厚度报警检测（TypeA 专用）。
        /// 在「请求数据」阶段调用，任一磨石报警应暂停流程并提示更换磨石。
        /// </summary>
        /// <returns>true=有报警需停机，false=无报警可继续</returns>
        public async Task<bool> HasAnyGrindStoneAlarmAsync(CancellationToken ct = default)
        {
            bool a1 = await IsGrindStone1AlarmAsync(ct);
            bool a2 = await IsGrindStone2AlarmAsync(ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] 磨石厚度报警: 磨石1={(a1 ? "✘报警" : "✔正常")} 磨石2={(a2 ? "✘报警" : "✔正常")}");
            return a1 || a2;
        }

        public async Task<bool> IsAlarmAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
                return await ReadBitAsync(_diRegAddr, Addr.Bit_Alarm, ct);
            else
                return (await _client.ReadIntAsync(Addr.RToModbus(Addr.R_MachineStatus), ct)) == 2;
        }

        /// <summary>设备状态码：TypeA=加工中 bit 判定 0空闲/1忙碌，TypeB=R7308 0空闲/1忙碌/2报警。</summary>
        public async Task<int> GetMachineStatusAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
                return await ReadBitAsync(_diRegAddr, Addr.Bit_Machining, ct) ? 1 : 0;
            else
                return await _client.ReadIntAsync(Addr.RToModbus(Addr.R_MachineStatus), ct);
        }

        // ═══════════════════════════════════════════════════════════════
        //  写入加工参数
        // ═══════════════════════════════════════════════════════════════

        /// <summary>下发版辊参数（直径 mm、版孔 1大孔/2小孔、长度 mm）。</summary>
        public async Task SendRollerParamsAsync(int rollerDiameter, int boreType, int rollerLength, CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下发加工参数 直径={rollerDiameter}mm 版孔={(boreType == 1 ? "大孔" : "小孔")} 长度={rollerLength}mm");
            if (_type == GrinderType.TypeA)
            {
                await _client.WriteAsync(Addr.RegDO_RollerDiameter - 40001, rollerDiameter, ct);
                await _client.WriteAsync(Addr.RegDO_BoreType      - 40001, boreType,       ct);
                await _client.WriteAsync(Addr.RegDO_RollerLength  - 40001, rollerLength,   ct);
            }
            else
            {
                await _client.WriteAsync(Addr.RToModbus(Addr.R_RollerDiameter), rollerDiameter, ct);
                await _client.WriteAsync(Addr.RToModbus(Addr.R_MachiningMode),  boreType,       ct);
                await _client.WriteAsync(Addr.RToModbus(Addr.R_RollerLength),   rollerLength,   ct);
            }
            Console.WriteLine($"[GrinderSvc] [{_name}] ✔ 加工参数已下发");
        }

        // ═══════════════════════════════════════════════════════════════
        //  握手信号（3 秒长信号：写 1 → 等 3s → 写 0）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>数据传输完成（写 1→3s→0）。TypeA=40011 bit9，TypeB=R7311。</summary>
        public async Task SetDataSentDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 数据传输完成(3秒长信号)");
            await LongSignalAsync(Addr.Bit_DataSentDone, Addr.R_DataSentDone, ct);
        }

        /// <summary>上料到达锁紧位置（写 1→3s→0）。TypeA=40011 bit10，TypeB=R7312。</summary>
        public async Task SetLoadInPlaceAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 上料到达锁紧位置(3秒长信号)");
            await LongSignalAsync(Addr.Bit_LoadInPlace, Addr.R_LoadInPlace, ct);
        }

        /// <summary>上料完成（写 1→3s→0）。TypeA=40011 bit11，TypeB=R7313。</summary>
        public async Task SetLoadDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 上料完成(3秒长信号)");
            await LongSignalAsync(Addr.Bit_LoadDone, Addr.R_LoadDone, ct);
        }

        /// <summary>下料到达位置（写 1→3s→0）。TypeA=40011 bit12，TypeB=R7314。</summary>
        public async Task SetUnloadInPlaceAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下料到达位置(3秒长信号)");
            await LongSignalAsync(Addr.Bit_UnloadInPlace, Addr.R_UnloadInPlace, ct);
        }

        /// <summary>下料完成（写 1→3s→0）。TypeA=40011 bit13，TypeB=R7315。</summary>
        public async Task SetUnloadDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下料完成(3秒长信号)");
            await LongSignalAsync(Addr.Bit_UnloadDone, Addr.R_UnloadDone, ct);
        }

        // ═══════════════════════════════════════════════════════════════
        //  私有辅助
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 3 秒长信号：写 1 → 等 3000ms → 写 0。
        /// TypeA 操作 DO 的 bit（读-改-写），TypeB 写 R 区字寄存器。
        /// </summary>
        private async Task LongSignalAsync(int typeABit, int typeBRAddr, CancellationToken ct)
        {
            if (_type == GrinderType.TypeA)
            {
                await SetBitAsync(_doRegAddr, typeABit, true, ct);

                // ── 读回验证：确认寄存器确实被写入了 ────────────────
                int verify = await _client.ReadIntAsync(_doRegAddr, ct);
                bool confirmed = (verify & (1 << typeABit)) != 0;
                Console.WriteLine($"[GrinderSvc] [{_name}]   DO bit{typeABit}=1（开始3s长信号） 读回验证={(confirmed ? "✔ 已置位" : "✘ 写入失败！当前0x" + verify.ToString("X4"))}");
                if (!confirmed)
                    Console.WriteLine($"[GrinderSvc] [{_name}] ⚠⚠⚠ 严重：写入 DO bit{typeABit}=1 但读回为 0！寄存器 40011 当前值=0x{verify:X4}");

                await Task.Delay(3000, ct);
                await SetBitAsync(_doRegAddr, typeABit, false, ct);
                Console.WriteLine($"[GrinderSvc] [{_name}]   DO bit{typeABit}=0（3s长信号结束）");
            }
            else
            {
                int addr = Addr.RToModbus(typeBRAddr);
                await _client.WriteAsync(addr, 1, ct);

                int verify = await _client.ReadIntAsync(addr, ct);
                Console.WriteLine($"[GrinderSvc] [{_name}]   R{typeBRAddr}=1（开始3s长信号） 读回验证={(verify == 1 ? "✔" : "✘ 当前值=" + verify)}");

                await Task.Delay(3000, ct);
                await _client.WriteAsync(addr, 0, ct);
                Console.WriteLine($"[GrinderSvc] [{_name}]   R{typeBRAddr}=0（3s长信号结束）");
            }
        }

        /// <summary>TypeB：读 R 区 BOOL 信号。非零为 true。</summary>
        private async Task<bool> ReadBoolAsync(int rAddr, CancellationToken ct)
            => (await _client.ReadIntAsync(Addr.RToModbus(rAddr), ct)) != 0;

        /// <summary>TypeA：读 DI 寄存器的指定位。FC03 读整字后按位提取。</summary>
        private async Task<bool> ReadBitAsync(int regAddr, int bitIndex, CancellationToken ct)
        {
            int val = await _client.ReadIntAsync(regAddr, ct);
            return (val & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// TypeA：写 DO 寄存器的指定位（读-改-写）。
        /// 先读当前整字 → 修改目标 bit → 写回，保护其他 bit 不受影响。
        /// </summary>
        private async Task SetBitAsync(int regAddr, int bitIndex, bool value, CancellationToken ct)
        {
            int cur = await _client.ReadIntAsync(regAddr, ct);
            int next = value
                ? cur | (1 << bitIndex)
                : cur & ~(1 << bitIndex);
            await _client.WriteAsync(regAddr, next, ct);
            Console.WriteLine($"[GrinderSvc] [{_name}]   SetBit reg={regAddr} bit{bitIndex}={(value ? 1 : 0)} (0x{cur:X4}→0x{next:X4})");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Console.WriteLine($"[GrinderSvc] [{_name}] 释放资源");
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
        }
    }
}
