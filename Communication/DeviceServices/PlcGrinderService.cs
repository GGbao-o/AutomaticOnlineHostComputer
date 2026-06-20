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
        private const int MaxIoAttempts = 3;

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
            int val = (await ReadTypeAStatusSnapshotAsync(ct)).RawDI;
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
            int di1 = await ReadIntSafeAsync(_diRegAddr, "读TypeA DI心跳(第一次)", ct);
            bool bit1 = (di1 & (1 << Addr.Bit_Heartbeat)) != 0;

            await Task.Delay(500, ct);

            int di2 = await ReadIntSafeAsync(_diRegAddr, "读TypeA DI心跳(第二次)", ct);
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
                var s = await ReadTypeAStatusSnapshotAsync(ct, includeOutputRegisters: true);
                int di = s.RawDI;
                int doReg = s.RawDO;
                Console.WriteLine($"[GrinderSvc]   DI 40001 =0x{di:X4} (b{Convert.ToString(di, 2).PadLeft(16, '0')})");
                Console.WriteLine($"[GrinderSvc]   DO 40011 =0x{doReg:X4} (b{Convert.ToString(doReg, 2).PadLeft(16, '0')})");
                Console.WriteLine($"[GrinderSvc]   {s.ToSignalText()}");
            }
            else
            {
                var s = await ReadTypeBStatusSnapshotAsync(ct);
                Console.WriteLine($"[GrinderSvc]   TypeB快照 {s.ToSignalText()}");
                Console.WriteLine($"[GrinderSvc]   R7304 请求下料={s.RequestUnload} (Modbus地址 {Addr.RToModbus(Addr.R_RequestUnload)})");
            }
            Console.WriteLine($"══════════════════════════════════");
        }

        // ═══════════════════════════════════════════════════════════════
        //  状态读取（TypeA / TypeB 统一接口）
        // ═══════════════════════════════════════════════════════════════

        public sealed record TypeAStatusSnapshot(
            int RawDI,
            int RawDO,
            int RollerDiameter,
            int BoreType,
            int RollerLength)
        {
            public bool Alarm => Bit(Addr.Bit_Alarm);
            public bool GrindStone1Alarm => Bit(Addr.Bit_GrindStone1Alarm);
            public bool GrindStone2Alarm => Bit(Addr.Bit_GrindStone2Alarm);
            public bool Heartbeat => Bit(Addr.Bit_Heartbeat);
            public bool ReqData => Bit(Addr.Bit_RequestData);
            public bool ReqLoad => Bit(Addr.Bit_RequestLoad);
            public bool Clamped => Bit(Addr.Bit_ClampDoneLoadOut);
            public bool ReqUnload => Bit(Addr.Bit_RequestUnload);
            public bool Unclamp => Bit(Addr.Bit_UnclampDone);
            public bool Busy => Bit(Addr.Bit_Machining);
            public bool Door => Bit(Addr.Bit_DoorOpen);

            private bool Bit(int bitIndex) => (RawDI & (1 << bitIndex)) != 0;

            public string ToSignalText()
                => $"DI=0x{RawDI:X4} b8心跳={To01(Heartbeat)} b9数据={To01(ReqData)} b10上料={To01(ReqLoad)} b11锁紧={To01(Clamped)} b12下料={To01(ReqUnload)} b13松开={To01(Unclamp)} b14加工={To01(Busy)} b15门开={To01(Door)} b0报警={To01(Alarm)} b1磨石1={To01(GrindStone1Alarm)} b2磨石2={To01(GrindStone2Alarm)}";

            private static int To01(bool value) => value ? 1 : 0;
        }

        /// <summary>
        /// TypeA(西门子)一次读取连续状态区。默认只读40001(DI)；手动诊断时可一并读到40014。
        /// </summary>
        public async Task<TypeAStatusSnapshot> ReadTypeAStatusSnapshotAsync(CancellationToken ct = default, bool includeOutputRegisters = false)
        {
            if (_type != GrinderType.TypeA)
                throw new InvalidOperationException("ReadTypeAStatusSnapshotAsync 仅适用于 TypeA 西门子研磨机。");

            int count = includeOutputRegisters
                ? Addr.TypeAToModbus(Addr.RegDO_RollerLength) - _diRegAddr + 1
                : 1;
            var result = await SafeIoAsync(token => _client.ReadAsync(_diRegAddr, count, token), $"读TypeA状态快照 addr={_diRegAddr} count={count}", ct);

            int At(int modbusAddress)
            {
                int index = modbusAddress - _diRegAddr;
                if ((uint)index >= (uint)result.IntValues.Length) return 0;
                return result.IntValues[index] & 0xFFFF;
            }

            return new TypeAStatusSnapshot(
                At(_diRegAddr),
                At(_doRegAddr),
                At(Addr.TypeAToModbus(Addr.RegDO_RollerDiameter)),
                At(Addr.TypeAToModbus(Addr.RegDO_BoreType)),
                At(Addr.TypeAToModbus(Addr.RegDO_RollerLength)));
        }

        public sealed record TypeBStatusSnapshot(
            int RequestData,
            int RequestLoad,
            int ClampDoneLoadOut,
            int RequestUnload,
            int UnclampDoneUnloadOut,
            int Machining,
            int DoorOpen,
            int MachineStatus)
        {
            public bool ReqData => RequestData != 0;
            public bool ReqLoad => RequestLoad != 0;
            public bool Clamped => ClampDoneLoadOut != 0;
            public bool ReqUnload => RequestUnload != 0;
            public bool Unclamp => UnclampDoneUnloadOut != 0;
            public bool Busy => Machining != 0;
            public bool Door => DoorOpen != 0;

            public string ToSignalText()
                => $"R7301={RequestData} R7302={RequestLoad} R7303={ClampDoneLoadOut} R7304={RequestUnload} R7305={UnclampDoneUnloadOut} R7306={Machining} R7307={DoorOpen} R7308={MachineStatus}";
        }

        /// <summary>
        /// TypeB(新代)一次性读取R7301~R7308所在的Modbus连续区间。
        /// R区有效地址为奇数位(R*2+1), 中间偶数寄存器一并读回但不使用。
        /// </summary>
        public async Task<TypeBStatusSnapshot> ReadTypeBStatusSnapshotAsync(CancellationToken ct = default)
        {
            if (_type != GrinderType.TypeB)
                throw new InvalidOperationException("ReadTypeBStatusSnapshotAsync 仅适用于 TypeB 新代研磨机。");

            int start = Addr.RToModbus(Addr.R_RequestData);      // R7301 -> 14603
            int end = Addr.RToModbus(Addr.R_MachineStatus);      // R7308 -> 14617
            var result = await SafeIoAsync(token => _client.ReadAsync(start, end - start + 1, token), $"读TypeB状态快照 addr={start} count={end - start + 1}", ct);

            int At(int rNumber)
            {
                int index = Addr.RToModbus(rNumber) - start;
                if ((uint)index >= (uint)result.IntValues.Length)
                    throw new InvalidOperationException($"TypeB快照缺少R{rNumber}数据。");
                return result.IntValues[index];
            }

            return new TypeBStatusSnapshot(
                At(Addr.R_RequestData),
                At(Addr.R_RequestLoad),
                At(Addr.R_ClampDoneLoadOut),
                At(Addr.R_RequestUnload),
                At(Addr.R_UnclampDoneUnloadOut),
                At(Addr.R_Machining),
                At(Addr.R_DoorOpen),
                At(Addr.R_MachineStatus));
        }

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

        /// <summary>门开状态（读）。协议规定天车接近研磨机取放工件前必须确认门开=1。</summary>
        public Task<bool> IsDoorOpenAsync(CancellationToken ct = default) => _type == GrinderType.TypeA
            ? ReadBitAsync(_diRegAddr, Addr.Bit_DoorOpen, ct)
            : ReadBoolAsync(Addr.R_DoorOpen, ct);

        /// <summary>仅用于显示/诊断门关闭状态；研磨上下料许可应使用 IsDoorOpenAsync() 等待门开=1。</summary>
        public async Task<bool> IsDoorClosedAsync(CancellationToken ct = default)
        {
            bool doorOpen = await IsDoorOpenAsync(ct);
            Console.WriteLine($"[GrinderSvc] [{_name}] 安全门={(doorOpen ? "开(允许天车接近)" : "关")}");
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
            bool a1;
            bool a2;
            if (_type == GrinderType.TypeA)
            {
                var s = await ReadTypeAStatusSnapshotAsync(ct);
                a1 = s.GrindStone1Alarm;
                a2 = s.GrindStone2Alarm;
            }
            else
            {
                a1 = await IsGrindStone1AlarmAsync(ct);
                a2 = await IsGrindStone2AlarmAsync(ct);
            }
            Console.WriteLine($"[GrinderSvc] [{_name}] 磨石厚度报警: 磨石1={(a1 ? "✘报警" : "✔正常")} 磨石2={(a2 ? "✘报警" : "✔正常")}");
            return a1 || a2;
        }

        public async Task<bool> IsAlarmAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
                return await ReadBitAsync(_diRegAddr, Addr.Bit_Alarm, ct);
            else
                return (await ReadIntSafeAsync(Addr.RToModbus(Addr.R_MachineStatus), "读TypeB设备状态", ct)) == 2;
        }

        /// <summary>设备状态码：TypeA=加工中 bit 判定 0空闲/1忙碌，TypeB=R7308 0空闲/1忙碌/2报警。</summary>
        public async Task<int> GetMachineStatusAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
                return await ReadBitAsync(_diRegAddr, Addr.Bit_Machining, ct) ? 1 : 0;
            else
                return await ReadIntSafeAsync(Addr.RToModbus(Addr.R_MachineStatus), "读TypeB设备状态", ct);
        }

        // ═══════════════════════════════════════════════════════════════
        //  写入加工参数
        // ═══════════════════════════════════════════════════════════════

        /// <summary>下发版辊参数（直径 mm、版孔 1大孔/2小孔、长度 mm）。</summary>
        public async Task SendRollerParamsAsync(double rollerDiameter, int boreType, double rollerLength, CancellationToken ct = default)
        {
            int d = (int)Math.Round(rollerDiameter);
            int l = (int)Math.Round(rollerLength);
            Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 下发加工参数 直径={d}mm(原{rollerDiameter}) 版孔={(boreType == 1 ? "大孔" : "小孔")} 长度={l}mm(原{rollerLength})");
            await SafeIoAsync(async token =>
            {
                if (_type == GrinderType.TypeA)
                {
                    await _client.WriteAsync(Addr.RegDO_RollerDiameter - 40001, d, token);
                    await _client.WriteAsync(Addr.RegDO_BoreType - 40001, boreType, token);
                    await _client.WriteAsync(Addr.RegDO_RollerLength - 40001, l, token);
                }
                else
                {
                    await _client.WriteAsync(Addr.RToModbus(Addr.R_RollerDiameter), d, token);
                    await _client.WriteAsync(Addr.RToModbus(Addr.R_MachiningMode), boreType, token);
                    await _client.WriteAsync(Addr.RToModbus(Addr.R_RollerLength), l, token);
                }
            }, "写研磨加工参数", ct);
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

        /// <summary>
        /// 清空所有输出信号（上位机→PLC），用于急停后恢复。
        /// TypeA: 40011 全部bit写0。TypeB: R7311~R7315 → 0。
        /// 注意：不经过3s长信号，直接写0。
        /// </summary>
        public async Task ClearAllOutputsAsync(CancellationToken ct = default)
        {
            if (_type == GrinderType.TypeA)
            {
                // 西门子: 40011 写0 清所有bit (读-改-写保护其他寄存器)
                await WriteIntSafeAsync(_doRegAddr, 0, "清空TypeA输出40011", ct);
                Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 清空输出 40011→0");
            }
            else
            {
                // 新代: R7311~R7315 写0
                var addrs = new[] {
                    Addr.RToModbus(Addr.R_DataSentDone),     // R7311 → 14623
                    Addr.RToModbus(Addr.R_LoadInPlace),      // R7312 → 14625
                    Addr.RToModbus(Addr.R_LoadDone),         // R7313 → 14627
                    Addr.RToModbus(Addr.R_UnloadInPlace),    // R7314 → 14629
                    Addr.RToModbus(Addr.R_UnloadDone),       // R7315 → 14631
                };
                foreach (var addr in addrs)
                    await WriteIntSafeAsync(addr, 0, $"清空TypeB输出addr={addr}", ct);
                Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 清空输出 R7311~R7315→0");
            }
        }

        /// <summary>
        /// 应急清零：清掉上位机写入的研磨参数和握手输出。
        /// TypeA: 40011~40014 → 0; TypeB: R7311~R7318 → 0。
        /// </summary>
        public async Task ClearEmergencyRegistersAsync(CancellationToken ct = default)
        {
            await ClearAllOutputsAsync(ct);
            if (_type == GrinderType.TypeA)
            {
                await WriteIntSafeAsync(Addr.RegDO_RollerDiameter - 40001, 0, "应急清零TypeA直径", ct);
                await WriteIntSafeAsync(Addr.RegDO_BoreType - 40001, 0, "应急清零TypeA孔型", ct);
                await WriteIntSafeAsync(Addr.RegDO_RollerLength - 40001, 0, "应急清零TypeA长度", ct);
                Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 应急清零参数 40012~40014→0");
            }
            else
            {
                await WriteIntSafeAsync(Addr.RToModbus(Addr.R_RollerDiameter), 0, "应急清零TypeB直径", ct);
                await WriteIntSafeAsync(Addr.RToModbus(Addr.R_MachiningMode), 0, "应急清零TypeB模式", ct);
                await WriteIntSafeAsync(Addr.RToModbus(Addr.R_RollerLength), 0, "应急清零TypeB长度", ct);
                Console.WriteLine($"[GrinderSvc] [{_name}] ▶ 应急清零参数 R7316~R7318→0");
            }
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
                int verify = await ReadIntSafeAsync(_doRegAddr, $"验证TypeA长信号bit{typeABit}", ct);
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
                await WriteIntSafeAsync(addr, 1, $"写TypeB长信号R{typeBRAddr}=1", ct);

                int verify = await ReadIntSafeAsync(addr, $"验证TypeB长信号R{typeBRAddr}", ct);
                Console.WriteLine($"[GrinderSvc] [{_name}]   R{typeBRAddr}=1（开始3s长信号） 读回验证={(verify == 1 ? "✔" : "✘ 当前值=" + verify)}");

                await Task.Delay(3000, ct);
                await WriteIntSafeAsync(addr, 0, $"写TypeB长信号R{typeBRAddr}=0", ct);
                Console.WriteLine($"[GrinderSvc] [{_name}]   R{typeBRAddr}=0（3s长信号结束）");
            }
        }

        /// <summary>TypeB：读 R 区 BOOL 信号。非零为 true。</summary>
        private async Task<bool> ReadBoolAsync(int rAddr, CancellationToken ct)
            => (await ReadIntSafeAsync(Addr.RToModbus(rAddr), $"读TypeB R{rAddr}", ct)) != 0;

        /// <summary>TypeA：读 DI 寄存器的指定位。FC03 读整字后按位提取。</summary>
        private async Task<bool> ReadBitAsync(int regAddr, int bitIndex, CancellationToken ct)
        {
            int val = await ReadIntSafeAsync(regAddr, $"读TypeA bit{bitIndex}", ct) & 0xFFFF;
            return (val & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// TypeA：写 DO 寄存器的指定位（读-改-写）。
        /// 先读当前整字 → 修改目标 bit → 写回，保护其他 bit 不受影响。
        /// </summary>
        private async Task SetBitAsync(int regAddr, int bitIndex, bool value, CancellationToken ct)
        {
            int cur = await ReadIntSafeAsync(regAddr, $"读TypeA输出字bit{bitIndex}", ct) & 0xFFFF;
            int next = value
                ? cur | (1 << bitIndex)
                : cur & ~(1 << bitIndex);
            await WriteIntSafeAsync(regAddr, next, $"写TypeA输出字bit{bitIndex}", ct);
            Console.WriteLine($"[GrinderSvc] [{_name}]   SetBit reg={regAddr} bit{bitIndex}={(value ? 1 : 0)} (0x{cur:X4}→0x{next:X4})");
        }

        private Task<int> ReadIntSafeAsync(int address, string desc, CancellationToken ct)
            => SafeIoAsync(token => _client.ReadIntAsync(address, token), $"{desc} addr={address}", ct);

        private Task WriteIntSafeAsync(int address, int value, string desc, CancellationToken ct)
            => SafeIoAsync(token => _client.WriteAsync(address, value, token), $"{desc} addr={address}", ct);

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
                    Console.WriteLine($"[GrinderSvc] [{_name}] {desc} 通信异常 attempt={attempt}/{MaxIoAttempts}: {ex.Message} → 重连后重试");
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Console.WriteLine($"[GrinderSvc] [{_name}] 释放资源");
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
        }
    }
}
