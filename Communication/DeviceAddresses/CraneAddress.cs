namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses
{
    /// <summary>
    /// 天车 / 机械手 共用寄存器地址定义（汇川 PLC，Modbus TCP）。
    /// <para>
    /// 三台小机械手与五台天车使用相同的寄存器布局，上位机统一走 Modbus TCP 读写：
    ///   - 状态读取：D5000~D5029（FC03，30 个连续保持寄存器）
    ///   - 自动任务：D4000~D4013 + D3101~D3137（FC16 写多寄存器）
    ///   - 手动操作：D4500~D4518（FC06 写单个寄存器）
    /// </para>
    /// <para>
    /// 数据类型：INT = 单寄存器 16bit / DINT = 双寄存器 32bit（低字在前 Little-Endian）。
    /// 端口：502。所有手动操作前需先写 D4001=2（手动模式），急停 D4518 除外。
    /// </para>
    /// <para>
    /// 5 台天车 IP（数据库未配置时使用）：
    ///   1号线天车前 192.168.2.81 / 1号线天车后 192.168.2.82
    ///   2号线天车前 192.168.2.83 / 2号线天车后 192.168.2.84
    ///   研磨机天车  192.168.2.80
    /// </para>
    /// <para>
    /// 3 台机械手 IP（数据库未配置时使用）：
    ///   机械手1 192.168.2.85 / 机械手2 192.168.2.86 / 机械手3 192.168.2.87
    /// </para>
    /// </summary>
    public static class CraneAddress
    {
        // ═══════════════════════════════════════════════════════════════
        // 从天车读取（FC03 读保持寄存器）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>D5000 - 天车空闲请求数据（INT，1=请求，0=不请求）</summary>
        public const int D_RequestData = 5000;

        /// <summary>D5001 - 天车到达充磁取到版辊（INT，1=到达）</summary>
        public const int D_ArrivedMagnet = 5001;

        /// <summary>D5002 - 天车取料完成（INT，1=完成）</summary>
        public const int D_PickDone = 5002;

        /// <summary>D5003 - 天车到达目标位置（INT，1=到达）</summary>
        public const int D_ArrivedTarget = 5003;

        /// <summary>D5004 - 天车上料完成（INT，1=完成）</summary>
        public const int D_LoadDone = 5004;

        /// <summary>D5005 - 天车当前任务编号（INT）</summary>
        public const int D_CurrentTaskNo = 5005;

        /// <summary>D5006 - 天车状态：忙碌(1) / 空闲(0)（INT）</summary>
        public const int D_Busy = 5006;

        /// <summary>D5007 - 天车状态机步骤（INT，调试用）</summary>
        public const int D_StateMachineStep = 5007;

        /// <summary>D5008 - 天车模式：自动(1) / 手动(2)（INT）</summary>
        public const int D_Mode = 5008;

        /// <summary>D5009 - 天车运行状态：正常(0) / 故障(1)（INT）</summary>
        public const int D_Fault = 5009;

        /// <summary>
        /// D5010 - 天车运行条件缺失（INT，位图）。
        /// <para>具体位定义请参考汇川 PLC 程序或厂商文档，暂按整字 != 0 表示有条件缺失。</para>
        /// </summary>
        public const int D_RunConditionMissing = 5010;

        /// <summary>
        /// D5011 - 天车伺服报警（INT，位图）。
        /// <para>各位含义请参考汇川 PLC 程序，暂按整字 != 0 表示有伺服报警。</para>
        /// </summary>
        public const int D_ServoAlarm = 5011;

        /// <summary>D5012 - PLC 伺服指令报警（INT，位图）</summary>
        public const int D_PlcServoAlarm = 5012;

        /// <summary>D5013 - PLC 报警（INT，位图）</summary>
        public const int D_PlcAlarm = 5013;

        // ─── X 轴编码器（DINT = 2 寄存器，低字在前 Little-Endian）──
        /// <summary>D5014~D5015 - X 轴绝对编码器值（DINT）</summary>
        public const int D_XEncoderAbs  = 5014;
        /// <summary>D5016~D5017 - X 轴原点清零值（DINT）</summary>
        public const int D_XEncoderZero = 5016;
        /// <summary>D5018~D5019 - X 轴当前显示坐标（DINT，单位：脉冲或 mm×100）</summary>
        public const int D_XPos         = 5018;

        // ─── Y 轴编码器（INT，单寄存器）─────────────────────────────
        /// <summary>D5020 - Y 轴绝对编码器值（INT）</summary>
        public const int D_YEncoderAbs  = 5020;
        /// <summary>D5021 - Y 轴原点清零值（INT）</summary>
        public const int D_YEncoderZero = 5021;
        /// <summary>D5022 - Y 轴当前显示坐标（INT）</summary>
        public const int D_YPos         = 5022;

        // ─── Z 轴编码器（INT，单寄存器）─────────────────────────────
        /// <summary>D5023 - Z 轴绝对编码器值（INT）</summary>
        public const int D_ZEncoderAbs  = 5023;
        /// <summary>D5024 - Z 轴原点清零值（INT）</summary>
        public const int D_ZEncoderZero = 5024;
        /// <summary>D5025 - Z 轴当前显示坐标（INT）</summary>
        public const int D_ZPos         = 5025;

        /// <summary>D5026 - PLC 产生的握手序号（INT）</summary>
        public const int D_PlcSeqNo          = 5026;
        /// <summary>D5027 - 当前任务源设备编号（INT）</summary>
        public const int D_TaskSourceDevice  = 5027;
        /// <summary>D5028 - 当前任务目标设备编号（INT）</summary>
        public const int D_TaskTargetDevice  = 5028;
        /// <summary>D5029 - 有版(1) / 无版(0)（INT）</summary>
        public const int D_HasRoller         = 5029;

        // ═══════════════════════════════════════════════════════════════
        // 写入天车（FC16 写多寄存器）—— 自动流程握手
        // ═══════════════════════════════════════════════════════════════

        /// <summary>D5100 - 数据下发完成（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_DataSentDone          = 5100;
        /// <summary>D5101 - 尾座已经松开到位（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_TailstockOpened       = 5101;
        /// <summary>D5102 - 目标设备尾座夹紧到位（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_TargetTailstockClamped = 5102;
        /// <summary>D5103 - 开始(1) / 忙碌(2) / 停止(3) / 空闲(4)（INT）</summary>
        public const int D_StartStop             = 5103;

        // ─── 自动任务下发（D4000 区）────────────────────────────────
        /// <summary>D4000 - 中控写入随机数（握手用，每次任务递增）</summary>
        public const int D_RandomNo         = 4000;
        /// <summary>D4001 - 中控写入模式：自动(1) / 手动(2)</summary>
        public const int D_ModeCmd          = 4001;
        /// <summary>D4003 - 中控写入继续(1) / 暂停(0)</summary>
        public const int D_PauseCmd         = 4003;
        /// <summary>D4004 - 任务编号（INT）</summary>
        public const int D_TaskNo           = 4004;
        /// <summary>D4005 - 版辊长度（INT，单位 mm）</summary>
        public const int D_RollerLength     = 4005;
        /// <summary>D4006 - 内径（INT，单位 μm）</summary>
        public const int D_InnerDiameter    = 4006;
        /// <summary>D4007 - 外径（INT，单位 μm）</summary>
        public const int D_OuterDiameter    = 4007;
        /// <summary>D4008 - 预留/待确认（按现场地址表补充）</summary>
        public const int D_Reserved4008     = 4008;
        /// <summary>D4009 - 预留/待确认（按现场地址表补充）</summary>
        public const int D_Reserved4009     = 4009;
        /// <summary>D4010 - 源设备编号（INT）</summary>
        public const int D_SourceDeviceNo   = 4010;
        /// <summary>D4011 - 源设备类型（INT）</summary>
        public const int D_SourceDeviceType = 4011;
        /// <summary>D4012 - 目标设备编号（INT）</summary>
        public const int D_TargetDeviceNo   = 4012;
        /// <summary>D4013 - 目标设备类型（INT）</summary>
        public const int D_TargetDeviceType = 4013;

        // ─── 手动操作（D4500 区），需先写 D4001=2 切换到手动模式 ────
        /// <summary>D4500 - 手动操作使能（写2=使能），配合 D4001=2 使用</summary>
        public const int D_ManualMode        = 4500;
        /// <summary>D4501 - 手动相对运动距离（INT，单位：mm）</summary>
        public const int D_ManualDistance    = 4501;
        /// <summary>D4502 - 手动运动方向（INT，正向=1，负向=2）</summary>
        public const int D_ManualDirection   = 4502;
        /// <summary>D4503 - 手动 Z 轴相对运动（INT，写2触发）</summary>
        public const int D_ManualMoveZ       = 4503;
        /// <summary>D4504 - 手动 Y 轴相对运动（INT，写2触发）</summary>
        public const int D_ManualMoveY       = 4504;
        /// <summary>D4505 - 手动 X 轴相对运动（INT，写2触发）</summary>
        public const int D_ManualMoveX       = 4505;
        /// <summary>D4506 - 手动 Z 轴回原点（INT，写2触发）</summary>
        public const int D_ManualHomeZ       = 4506;
        /// <summary>D4507 - 手动 Y 轴回原点（INT，写2触发）</summary>
        public const int D_ManualHomeY       = 4507;
        /// <summary>D4508 - 手动 X 轴回原点（INT，写2触发）</summary>
        public const int D_ManualHomeX       = 4508;
        /// <summary>D4510 - 手动充磁（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualMagnetOn    = 4510;
        /// <summary>D4511 - 手动退磁（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualMagnetOff   = 4511;
        /// <summary>D4512 - 手动开接液盘（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualDrainOpen   = 4512;
        /// <summary>D4513 - 手动关接液盘（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualDrainClose  = 4513;
        /// <summary>D4514 - 手动清除报警（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualClearAlarm  = 4514;
        /// <summary>D4515 - 手动伺服断电（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualServoPowerOff = 4515;
        /// <summary>D4516 - 手动伺服通电（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualServoPowerOn  = 4516;
        /// <summary>D4517 - 减速停止伺服运动（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualSlowStop    = 4517;
        /// <summary>D4518 - 急停伺服运动（INT，写2触发后写0复位，脉冲型）</summary>
        public const int D_ManualEStop       = 4518;
        /// <summary>D4523 - 下压急停（INT，PLC检测到压力写1，主机写0清除）。手动恢复流程：①D4523=0 ②清除报警D4514。</summary>
        public const int D_PressureEStop     = 4523;

        // ═══════════════════════════════════════════════════════════════
        //  X 区输入信号（PLC→上位机，只读，FC01 Read Coils）
        //  每个 X 点一个独立线圈，地址 = 63488 + X编号
        //  值：0=OFF, 1=ON
        // ═══════════════════════════════════════════════════════════════
        /// <summary>D63488 — X输入基地址（X0=63488, X6=63494, X7=63495...）</summary>
        public const int D_XInput_Base       = 63488;
        /// <summary>D63494 — X6 充磁反馈（读，1=充磁到位）</summary>
        public const int D_X6_MagnetizeOk    = 63494;
        /// <summary>D63495 — X7 退磁反馈（读，1=退磁到位）</summary>
        public const int D_X7_DemagnetizeOk  = 63495;
        /// <summary>D63490 — X2 磁铁下压限位（读，1=触发）</summary>
        public const int D_X2_MagnetLimit    = 63490;
        /// <summary>D63499 — X11 检测有版（读，1=有版）</summary>
        public const int D_X11_HasPlate      = 63499;
        /// <summary>D4519 - Z JOG+（INT，写2触发）</summary>
        public const int D_ManualZJogPlus    = 4519;
        /// <summary>D4520 - 手动Z绝对位移（INT，写2触发）</summary>
        public const int D_ManualZAbsMove    = 4520;
        /// <summary>D4521 - 手动Y绝对位移（INT，写2触发）</summary>
        public const int D_ManualYAbsMove    = 4521;
        /// <summary>D4522 - 手动X绝对位移（INT，写2触发）</summary>
        public const int D_ManualXAbsMove    = 4522;

        // ═══════════════════════════════════════════════════════════════
        //  JOG 速度/加减速设置（D2501~D2527，FC06 单寄存器写入）
        //  手动点动（X+/X-/Y+/Y-/Z+/Z-）走 JOG 模式，
        //  速度=点动时的运行速度，加减速=启动/停止的斜坡。
        // ═══════════════════════════════════════════════════════════════

        // ─── X 轴 ──────────────────────────────────────────────────────
        /// <summary>D2501 - X绝对位移速度</summary>
        public const int D_XAbsSpeed        = 2501;
        /// <summary>D2502 - X绝对位移加速度</summary>
        public const int D_XAbsAccel        = 2502;
        /// <summary>D2503 - X绝对位移减速度</summary>
        public const int D_XAbsDecel        = 2503;
        /// <summary>D2504 - X相对位移速度</summary>
        public const int D_XRelSpeed        = 2504;
        /// <summary>D2505 - X相对位移加速度</summary>
        public const int D_XRelAccel        = 2505;
        /// <summary>D2506 - X相对位移减速度</summary>
        public const int D_XRelDecel        = 2506;
        /// <summary>D2507 - X JOG速度</summary>
        public const int D_XJogSpeed        = 2507;
        /// <summary>D2508 - X JOG加速度</summary>
        public const int D_XJogAccel        = 2508;
        /// <summary>D2509 - X JOG减速度</summary>
        public const int D_XJogDecel        = 2509;

        // ─── Y 轴 ──────────────────────────────────────────────────────
        /// <summary>D2510 - Y绝对位移速度</summary>
        public const int D_YAbsSpeed        = 2510;
        /// <summary>D2511 - Y绝对位移加速度</summary>
        public const int D_YAbsAccel        = 2511;
        /// <summary>D2512 - Y绝对位移减速度</summary>
        public const int D_YAbsDecel        = 2512;
        /// <summary>D2513 - Y相对位移速度</summary>
        public const int D_YRelSpeed        = 2513;
        /// <summary>D2514 - Y相对位移加速度</summary>
        public const int D_YRelAccel        = 2514;
        /// <summary>D2515 - Y相对位移减速度</summary>
        public const int D_YRelDecel        = 2515;
        /// <summary>D2516 - Y JOG速度</summary>
        public const int D_YJogSpeed        = 2516;
        /// <summary>D2517 - Y JOG加速度</summary>
        public const int D_YJogAccel        = 2517;
        /// <summary>D2518 - Y JOG减速度</summary>
        public const int D_YJogDecel        = 2518;

        // ─── Z 轴 ──────────────────────────────────────────────────────
        /// <summary>D2519 - Z绝对位移速度</summary>
        public const int D_ZAbsSpeed        = 2519;
        /// <summary>D2520 - Z绝对位移加速度</summary>
        public const int D_ZAbsAccel        = 2520;
        /// <summary>D2521 - Z绝对位移减速度</summary>
        public const int D_ZAbsDecel        = 2521;
        /// <summary>D2522 - Z相对位移速度</summary>
        public const int D_ZRelSpeed        = 2522;
        /// <summary>D2523 - Z相对位移加速度</summary>
        public const int D_ZRelAccel        = 2523;
        /// <summary>D2524 - Z相对位移减速度</summary>
        public const int D_ZRelDecel        = 2524;
        /// <summary>D2525 - Z JOG速度</summary>
        public const int D_ZJogSpeed        = 2525;
        /// <summary>D2526 - Z JOG加速度</summary>
        public const int D_ZJogAccel        = 2526;
        /// <summary>D2527 - Z JOG减速度</summary>
        public const int D_ZJogDecel        = 2527;

        // ═══════════════════════════════════════════════════════════════
        //  绝对位移目标值（D3102~D3106，DINT，FC16 写多寄存器）
        // ═══════════════════════════════════════════════════════════════
        /// <summary>D3102~D3103 - X绝对位移目标值（DINT）</summary>
        public const int D_XAbsTarget       = 3102;
        /// <summary>D3104~D3105 - Y绝对位移目标值（DINT）</summary>
        public const int D_YAbsTarget       = 3104;
        /// <summary>D3106~D3107 - Z绝对位移目标值（DINT）</summary>
        public const int D_ZAbsTarget       = 3106;
    }
}
