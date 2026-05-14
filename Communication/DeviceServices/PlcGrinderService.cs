using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>
    /// 研磨机设备服务（共 4 台，两款系统各 2 台）。
    /// <para>研磨机1/2（ST701/ST702）→ TypeB 新代数控：R 区寄存器，Modbus 地址 = R编号 × 2 + 1。</para>
    /// <para>研磨机3/4（ST703/ST704）→ TypeA 西门子 PLC：点位表 bit 型，DI=40001 / DO=40011。</para>
    /// <para>通信：Modbus TCP，端口 502。IP：4#PLC 192.168.1.71 / 5#PLC 192.168.1.81。</para>
    /// <para>握手规范：所有触发信号采用 1→0 脉冲模式（写1触发→写0复位），与天车 2→0 不同。</para>
    /// </summary>
    public sealed class PlcGrinderService : IDisposable
    {
        /// <summary>研磨机型号：TypeA = 西门子 PLC（点位表 bit 型），TypeB = 新代数控（R 区字型）。</summary>
        public enum GrinderType { TypeA, TypeB }

        private readonly ModbusTcpClient _client;
        private readonly GrinderType _type;
        private readonly string _name; // 调试用名称
        private bool _disposed;

        // TypeA 寄存器零起始地址（Modbus 地址 = 40001段 - 40001）
        private static readonly int _doRegAddr  = PlcGrinderAddress.RegDO_Base - 40001;  // = 10
        private static readonly int _diRegAddr  = PlcGrinderAddress.RegDI_Base - 40001;  // = 0

        /// <param name="name">调试名称（如"研磨机1(新代)"）</param>
        /// <param name="ip">研磨机 PLC/CNC 的 IP 地址</param>
        /// <param name="type">研磨机型号（西门子=TypeA，新代=TypeB）</param>
        /// <param name="port">Modbus TCP 端口，默认 502</param>
        /// <param name="unitId">Modbus 从站地址，默认用 IP 末段（新代规则：192.168.2.90→unitId=90）</param>
        public PlcGrinderService(string name, string ip, GrinderType type, int port = 502, int? unitId = null)
        {
            _name = name;
            int uid = unitId ?? GetDefaultUnitId(ip);
            _client = new ModbusTcpClient(ip, port, (byte)uid);
            _type   = type;
            Console.WriteLine($"[GrinderSvc] [{_name}] 创建实例 Type={type} IP={ip}:{port} UnitId={uid}");
        }

        /// <summary>从 IP 末段提取默认从站地址（192.168.2.90 → 90）。</summary>
        private static int GetDefaultUnitId(string ip)
        {
            int lastDot = ip.LastIndexOf('.');
            if (lastDot >= 0 && int.TryParse(ip.Substring(lastDot + 1), out int id))
                return id;
            return 1; // 兜底
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] 连接中...");
            await _client.ConnectAsync(ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] 连接成功");
        }
        public Task DisconnectAsync() => _client.DisconnectAsync();
        public bool IsConnected => _client.IsConnected;

        /// <summary>读 TypeA 的 DI 寄存器原始值（Modbus地址=0，即40001的16bit整字）。</summary>
        public async Task<int> ReadDIRawAsync(CancellationToken ct = default)
        {
            int val = await _client.ReadIntAsync(_diRegAddr, ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] DI原始值=0x{val:X4} (b{Convert.ToString(val, 2).PadLeft(16, '0')})");
            return val;
        }

        // ═══════════════════════════════════════════════════════════════
        //  批量状态读取（测试用）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>一次性读取研磨机全部关键状态，输出到控制台日志。</summary>
        public async Task ReadAllStatusAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"══════════════════════════════════════");
            Console.WriteLine($"[GrinderSvc] [{_name}] === 状态读取 ===");
            Console.WriteLine($"[GrinderSvc]   Type={_type}");

            if (_type == GrinderType.TypeA)
            {
                int di = await _client.ReadIntAsync(_diRegAddr, ct);
                Console.WriteLine($"[GrinderSvc]   DI 40001=0x{di:X4} (b{Convert.ToString(di, 2).PadLeft(16, '0')})");
                Console.WriteLine($"[GrinderSvc]   心跳      bit8  = {(di & (1 << 8)) != 0}");
                Console.WriteLine($"[GrinderSvc]   请求数据  bit9  = {(di & (1 << 9)) != 0}");
                Console.WriteLine($"[GrinderSvc]   请求上料  bit10 = {(di & (1 << 10)) != 0}");
                Console.WriteLine($"[GrinderSvc]   锁紧完成  bit11 = {(di & (1 << 11)) != 0}");
                Console.WriteLine($"[GrinderSvc]   请求下料  bit12 = {(di & (1 << 12)) != 0}");
                Console.WriteLine($"[GrinderSvc]   松开完成  bit13 = {(di & (1 << 13)) != 0}");
                Console.WriteLine($"[GrinderSvc]   加工中    bit14 = {(di & (1 << 14)) != 0}");
                Console.WriteLine($"[GrinderSvc]   门开      bit15 = {(di & (1 << 15)) != 0}");
                Console.WriteLine($"[GrinderSvc]   报警          bit0  = {(di & (1 << 0)) != 0}");
                Console.WriteLine($"[GrinderSvc]   磨石1厚度报警 bit1  = {(di & (1 << 1)) != 0}");
                Console.WriteLine($"[GrinderSvc]   磨石2厚度报警 bit2  = {(di & (1 << 2)) != 0}");
            }
            else
            {
                int v7301 = await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_RequestData), ct);
                int v7306 = await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_Machining), ct);
                int v7307 = await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_DoorOpen), ct);
                int v7308 = await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_MachineStatus), ct);
                Console.WriteLine($"[GrinderSvc]   R7301 请求数据={v7301} R7306 加工中={v7306} R7307 门开={v7307} R7308 状态={v7308}");
            }
            Console.WriteLine($"══════════════════════════════════════");
        }

        // ─── 读取状态 ────────────────────────────────────────────────

        /// <summary>请求数据（读）。TypeA=40001 bit9，TypeB=R7301→Modbus 14603。</summary>
        public Task<bool> IsRequestDataAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_RequestData, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_RequestData, ct);

        /// <summary>请求上料（读）。TypeA=40001 bit10，TypeB=R7302→Modbus 14605。</summary>
        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_RequestLoad, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_RequestLoad, ct);

        /// <summary>锁紧完成上料退出（读）。TypeA=40001 bit11，TypeB=R7303→Modbus 14607。</summary>
        public Task<bool> IsClampDoneLoadOutAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_ClampDoneLoadOut, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_ClampDoneLoadOut, ct);

        /// <summary>请求下料（读）。TypeA=40001 bit12，TypeB=R7304→Modbus 14609。</summary>
        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_RequestUnload, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_RequestUnload, ct);

        /// <summary>松开完成（读）。TypeA=40001 bit13，TypeB=R7305→Modbus 14611。</summary>
        public Task<bool> IsUnclampDoneAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_UnclampDone, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_UnclampDoneUnloadOut, ct);

        /// <summary>加工中（读）。TypeA=40001 bit14，TypeB=R7306→Modbus 14613。</summary>
        public Task<bool> IsMachiningAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_Machining, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_Machining, ct);

        /// <summary>门开（读）。TypeA=40001 bit15，TypeB=R7307→Modbus 14615。</summary>
        public Task<bool> IsDoorOpenAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_DoorOpen, ct)
            : ReadBoolAsync(PlcGrinderAddress.R_DoorOpen, ct);

        /// <summary>磨石1厚度报警（读）。仅 TypeA=40001 bit1。</summary>
        public Task<bool> IsGrindStone1AlarmAsync(CancellationToken ct = default)
            => ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_GrindStone1Alarm, ct);

        /// <summary>磨石2厚度报警（读）。仅 TypeA=40001 bit2。</summary>
        public Task<bool> IsGrindStone2AlarmAsync(CancellationToken ct = default)
            => ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_GrindStone2Alarm, ct);

        /// <summary>报警（读）。TypeA=40001 bit0，TypeB：R7308值=2为报警。</summary>
        public async Task<bool> IsAlarmAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
                return await ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_Alarm, ct);
            else
                return (await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_MachineStatus), ct)) == 2;
        }

        /// <summary>设备状态（读）：TypeA=40001 bit14加工中判定，TypeB=R7308值 0=空闲 1=忙碌 2=报警。</summary>
        public async Task<int> GetMachineStatusAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
                return await ReadBitAsync(_diRegAddr, PlcGrinderAddress.Bit_Machining, ct) ? 1 : 0;
            else
                return await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_MachineStatus), ct);
        }

        // ─── 写入加工参数 ─────────────────────────────────────────────

        /// <summary>下发版辊参数（直径、版孔/模式、版长）。</summary>
        public async Task SendRollerParamsAsync(
            int rollerDiameter,
            int boreType,
            int rollerLength,
            CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下发加工参数 直径={rollerDiameter} 版孔={boreType} 长度={rollerLength}");
            if (_type == GrinderType.TypeA)
            {
                await _client.WriteAsync(PlcGrinderAddress.RegDO_RollerDiameter - 40001, rollerDiameter, ct);
                await _client.WriteAsync(PlcGrinderAddress.RegDO_BoreType        - 40001, boreType,       ct);
                await _client.WriteAsync(PlcGrinderAddress.RegDO_RollerLength    - 40001, rollerLength,   ct);
            }
            else
            {
                await _client.WriteAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_RollerDiameter), rollerDiameter, ct);
                await _client.WriteAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_MachiningMode),  boreType,       ct);
                await _client.WriteAsync(PlcGrinderAddress.RToModbus(PlcGrinderAddress.R_RollerLength),   rollerLength,   ct);
            }
            Console.WriteLine($"[GrinderSvc] [{_name}] ✔ 加工参数已下发");
        }

        // ─── 写入握手信号（1→0 脉冲，与天车 2→0 不同） ──────────────

        /// <summary>数据传输完成（1→0）。TypeA=40011 bit9，TypeB=R7311→Modbus 14623。</summary>
        public async Task SetDataSentDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 数据传输完成(1→0)");
            await PulseOutputAsync(PlcGrinderAddress.Bit_DataSentDone, PlcGrinderAddress.R_DataSentDone, ct);
        }

        /// <summary>上料到达锁紧位置（1→0）。TypeA=40011 bit10，TypeB=R7312→Modbus 14625。</summary>
        public async Task SetLoadInPlaceAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 上料到达锁紧位置(1→0)");
            await PulseOutputAsync(PlcGrinderAddress.Bit_LoadInPlace, PlcGrinderAddress.R_LoadInPlace, ct);
        }

        /// <summary>上料完成（1→0）。TypeA=40011 bit11，TypeB=R7313→Modbus 14627。</summary>
        public async Task SetLoadDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 上料完成(1→0)");
            await PulseOutputAsync(PlcGrinderAddress.Bit_LoadDone, PlcGrinderAddress.R_LoadDone, ct);
        }

        /// <summary>下料到达位置（1→0）。TypeA=40011 bit12，TypeB=R7314→Modbus 14629。</summary>
        public async Task SetUnloadInPlaceAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下料到达位置(1→0)");
            await PulseOutputAsync(PlcGrinderAddress.Bit_UnloadInPlace, PlcGrinderAddress.R_UnloadInPlace, ct);
        }

        /// <summary>下料完成（1→0）。TypeA=40011 bit13，TypeB=R7315→Modbus 14631。</summary>
        public async Task SetUnloadDoneAsync(CancellationToken ct = default)
        {
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下料完成(1→0)");
            await PulseOutputAsync(PlcGrinderAddress.Bit_UnloadDone, PlcGrinderAddress.R_UnloadDone, ct);
        }

        // ─── 私有辅助 ─────────────────────────────────────────────────

        /// <summary>1→0 脉冲：TypeA 操作 bit，TypeB 写 R 区寄存器。</summary>
        private async Task PulseOutputAsync(int typeABit, int typeBRAddr, CancellationToken ct)
        {
            if (_type == GrinderType.TypeA)
            {
                await SetBitAsync(_doRegAddr, typeABit, true, ct);
                await Task.Delay(100, ct);
                await SetBitAsync(_doRegAddr, typeABit, false, ct);
            }
            else
            {
                int addr = PlcGrinderAddress.RToModbus(typeBRAddr);
                await _client.WriteAsync(addr, 1, ct);
                await Task.Delay(100, ct);
                await _client.WriteAsync(addr, 0, ct);
            }
        }

        /// <summary>TypeB：读 R 区 BOOL 信号，Modbus 地址 = R × 2 + 1，非零即为 true。</summary>
        private async Task<bool> ReadBoolAsync(int rAddr, CancellationToken ct)
            => (await _client.ReadIntAsync(PlcGrinderAddress.RToModbus(rAddr), ct)) != 0;

        /// <summary>TypeA：读 DI 寄存器（地址=0）的指定位，1 即为 true。FC03 读整字后按位提取。</summary>
        private async Task<bool> ReadBitAsync(int regAddr, int bitIndex, CancellationToken ct)
        {
            int val = await _client.ReadIntAsync(regAddr, ct);
            return (val & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// TypeA：写 DO 寄存器（地址=10）的指定位。
        /// 先读当前值 → 修改目标 bit → 写回，避免覆盖其他 bit 的控制信号。
        /// </summary>
        private async Task SetBitAsync(int regAddr, int bitIndex, bool value, CancellationToken ct)
        {
            int cur = await _client.ReadIntAsync(regAddr, ct);
            int next = value
                ? cur | (1 << bitIndex)
                : cur & ~(1 << bitIndex);
            await _client.WriteAsync(regAddr, next, ct);
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
