namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses
{
    /// <summary>
    /// 研磨机 Modbus TCP 寄存器地址定义。
    /// <para>共 4 台研磨机，两款不同系统各 2 台，均通过 Modbus TCP（端口 502）通信：</para>
    /// <para>  - 西门子 PLC（TypeA）→ 4#PLC 192.168.1.71 ×2台 / 5#PLC 192.168.1.81 ×2台</para>
    /// <para>  - 新代数控（TypeB）→ R 区寄存器，Modbus 地址 = R编号 × 2 + 1</para>
    /// <para>
    /// TypeA（西门子）地址说明：
    ///   - 通信地址格式 40001-8 表示保持寄存器 40001 的第 8 位（bit7）。
    ///   - DI（机床→上位机）：FC03 读保持寄存器，寄存器号 = 通信地址 - 40001（零起始）。
    ///   - DO（上位机→机床）：FC06 写单个寄存器，需先读-改-写以保持其他 bit 不变。
    ///   - Word 参数：FC06 直接写入整字值。
    /// </para>
    /// <para>
    /// TypeB（新代）地址说明：
    ///   - R 区寄存器通过 Modbus TCP 读写，寄存器地址 = R编号 × 2 + 1。
    ///   - 与锦州斜床地址段（R7301~）相同但 IP 不同，互不冲突。
    /// </para>
    /// </summary>
    public static class PlcGrinderAddress
    {
        // ══════════════════════════════════════════════════════
        //  TypeA — 西门子 PLC 研磨机 ×4台（点位表 bit 型）
        //  4#PLC 192.168.1.71（2台） + 5#PLC 192.168.1.81（2台）
        //  地址格式 40001-B（寄存器号-Bit位），Modbus地址 = 40001段号 - 40001（零起始）
        //  读 DI：FC03 读整个寄存器后按位解析
        //  写 DO：FC06 写单个寄存器（需先读-改-写保护其他 bit）
        // ══════════════════════════════════════════════════════

        // ─── DI 输入（机床→上位机读，FC03）────────────────────────────

        /// <summary>40001 bit8  1Hz心跳（读，机床心跳脉冲）</summary>
        public const int RegDI_Base        = 40001;  // Modbus地址 = 40001-40001 = 0
        public const int Bit_Heartbeat     = 8;

        /// <summary>40001 bit9  请求数据（读，1=请求上位机下发加工参数）</summary>
        public const int Bit_RequestData   = 9;

        /// <summary>40001 bit10 请求上料（读，1=请求天车上料）</summary>
        public const int Bit_RequestLoad   = 10;

        /// <summary>40001 bit11 锁紧完成上料退出（读，1=已锁紧且上料机构退出）</summary>
        public const int Bit_ClampDoneLoadOut = 11;

        /// <summary>40001 bit12 请求下料（读，1=请求天车取料）</summary>
        public const int Bit_RequestUnload = 12;

        /// <summary>40001 bit13 松开完成（读，1=已松开完成）</summary>
        public const int Bit_UnclampDone   = 13;

        /// <summary>40001 bit14 加工中（读，1=正在加工）</summary>
        public const int Bit_Machining     = 14;

        /// <summary>40001 bit15 门开（读，1=安全门已打开）</summary>
        public const int Bit_DoorOpen      = 15;

        /// <summary>40001 bit0  报警（读，1=设备报警）</summary>
        public const int Bit_Alarm              = 0;
        /// <summary>40001 bit1  磨石1厚度报警（读，1=磨石1厚度异常）</summary>
        public const int Bit_GrindStone1Alarm   = 1;
        /// <summary>40001 bit2  磨石2厚度报警（读，1=磨石2厚度异常）</summary>
        public const int Bit_GrindStone2Alarm   = 2;

        // ─── DO 输出（上位机→机床写，FC06）────────────────────────────

        /// <summary>40011 Modbus寄存器地址=10，天车1Hz心跳（写）</summary>
        public const int RegDO_Base           = 40011;  // Modbus地址 = 40011-40001 = 10

        /// <summary>40011 bit9  数据传输完成（写，3秒长信号）</summary>
        public const int Bit_DataSentDone     = 9;

        /// <summary>40011 bit10 上料到达锁紧位置（写，3秒长信号）</summary>
        public const int Bit_LoadInPlace      = 10;

        /// <summary>40011 bit11 上料完成（写，3秒长信号）</summary>
        public const int Bit_LoadDone         = 11;

        /// <summary>40011 bit12 下料到达位置（写，3秒长信号）</summary>
        public const int Bit_UnloadInPlace    = 12;

        /// <summary>40011 bit13 下料完成（写，3秒长信号）</summary>
        public const int Bit_UnloadDone       = 13;

        // ─── DO Word 输出（上位机→机床写版辊参数，FC06）────────────────

        /// <summary>40012 Modbus寄存器地址=11，版辊直径（写，Word整数，mm）</summary>
        public const int RegDO_RollerDiameter = 40012;  // Modbus地址 = 11

        /// <summary>40013 Modbus寄存器地址=12，版孔（写，Word整数，1大孔2小孔）</summary>
        public const int RegDO_BoreType       = 40013;  // Modbus地址 = 12

        /// <summary>40014 Modbus寄存器地址=13，版棍长度（写，Word整数，mm）</summary>
        public const int RegDO_RollerLength   = 40014;  // Modbus地址 = 13

        // ══════════════════════════════════════════════════════
        //  TypeB — 新代数控 研磨机 ×2台（R 区寄存器）
        //  Modbus TCP 端口 502，寄存器地址 = R编号 × 2 + 1
        //  该地址段与锦州斜床 R7301~ 相同但 IP 不同，各设备独立通信不冲突
        // ══════════════════════════════════════════════════════

        // ─── R区 输入（机床→上位机读，对应DI）────────────────────────

        /// <summary>R7301 关闭=0，请求数据=1</summary>
        public const int R_RequestData          = 7301;

        /// <summary>R7302 关闭=0，请求上料=1</summary>
        public const int R_RequestLoad          = 7302;

        /// <summary>R7303 关闭=0，锁紧完成上料退出=1</summary>
        public const int R_ClampDoneLoadOut     = 7303;

        /// <summary>R7304 关闭=0，请求下料=1</summary>
        public const int R_RequestUnload        = 7304;

        /// <summary>R7305 关闭=0，松开完成下料退出=1</summary>
        public const int R_UnclampDoneUnloadOut = 7305;

        /// <summary>R7306 加工完成=0，加工中=1</summary>
        public const int R_Machining            = 7306;

        /// <summary>R7307 安全门关闭=0，安全门打开=1</summary>
        public const int R_DoorOpen             = 7307;

        /// <summary>R7308 空闲=0，忙碌中=1，机床报警=2</summary>
        public const int R_MachineStatus        = 7308;

        // ─── R区 输出（上位机→机床写，对应DO）────────────────────────

        /// <summary>R7311 数据传输完成（写）</summary>
        public const int R_DataSentDone         = 7311;

        /// <summary>R7312 上料到达锁紧位置（写）</summary>
        public const int R_LoadInPlace          = 7312;

        /// <summary>R7313 上料完成（写）</summary>
        public const int R_LoadDone             = 7313;

        /// <summary>R7314 下料到达松开位置（写）</summary>
        public const int R_UnloadInPlace        = 7314;

        /// <summary>R7315 下料完成（写）</summary>
        public const int R_UnloadDone           = 7315;

        /// <summary>R7316 版辊直径数据（写，mm，Word）</summary>
        public const int R_RollerDiameter       = 7316;

        /// <summary>R7317 版辊加工模式（写，1=大孔，2=小孔）</summary>
        public const int R_MachiningMode        = 7317;

        /// <summary>R7318 版棍长度（写，mm，Word）</summary>
        public const int R_RollerLength         = 7318;

        // ─── TypeB Modbus地址转换（R*2+1）────────────────────────────

        /// <summary>将 R 区编号转换为 TypeB 研磨机的 Modbus 寄存器地址（R×2+1）。</summary>
        public static int RToModbus(int rNumber) => rNumber * 2 + 1;

        // ─── TypeA Modbus地址转换（地址-40001）────────────────────────

        /// <summary>将 TypeA 通信地址（如40001）转换为 Modbus 寄存器地址（零起始）。</summary>
        public static int TypeAToModbus(int address) => address - 40001;
    }
}
