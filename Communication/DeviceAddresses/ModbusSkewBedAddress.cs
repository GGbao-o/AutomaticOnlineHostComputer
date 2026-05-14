namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses
{
    /// <summary>
    /// 锦州斜床 迅捷PLC（Modbus TCP）寄存器地址定义（共4台）。
    /// <para>通信方式：Modbus TCP，端口 502，单元ID=1。</para>
    /// <para>
    /// 数据格式：
    ///   INT16  → 单个保持寄存器，功能码 FC03/FC06。
    ///   FLOAT  → 2个连续保持寄存器，IEEE 754 单精度，高字在前（Big-Endian）。
    /// </para>
    /// <para>注意：Modbus 寄存器地址从 0 开始（即 10350 对应 0x286A）。</para>
    /// </summary>
    public static class ModbusSkewBedAddress
    {
        // ══════════════════════════════════════════════
        //  【读 FC03】机床状态（INT16，单寄存器）
        // ══════════════════════════════════════════════

        /// <summary>10350 准备就绪（读，1=就绪）</summary>
        public const int MachineReady = 10350;

        /// <summary>10351 操作模式（读，手动0，人工自动1，全自动2）</summary>
        public const int OperationMode = 10351;

        /// <summary>10352 请求数据（读，1=请求上位机下发数据）</summary>
        public const int RequestData = 10352;

        /// <summary>10354 请求上料（读，1=请求天车送料）</summary>
        public const int RequestLoad = 10354;

        /// <summary>10355 尾座顶紧信号（读，1=已顶紧）</summary>
        public const int TailstockClamped = 10355;

        /// <summary>10357 请求下料（读，1=请求天车取料）</summary>
        public const int RequestUnload = 10357;

        /// <summary>10358 尾座张开到位（读，1=已张开）</summary>
        public const int TailstockOpened = 10358;

        /// <summary>10359 门开到位（读，1=门已打开）</summary>
        public const int DoorOpen = 10359;

        /// <summary>10360 磨头上限（读，1=磨头在上限位）</summary>
        public const int GrindHeadUpperLimit = 10360;

        /// <summary>10361 X轴到位（读，1=X轴在安全位置）</summary>
        public const int XAxisInPlace = 10361;

        /// <summary>10362 Y轴到位（读，1=Y轴在安全位置）</summary>
        public const int YAxisInPlace = 10362;

        /// <summary>10363 刀具1寿命到（读，1=寿命到期）</summary>
        public const int Tool1LifeEnd = 10363;

        /// <summary>10364 刀具2寿命到（读，1=寿命到期）</summary>
        public const int Tool2LifeEnd = 10364;

        // ══════════════════════════════════════════════
        //  【读 FC03】机床参数（FLOAT，2个连续寄存器）
        // ══════════════════════════════════════════════

        /// <summary>10300~10301 版长（读，mm，FLOAT 2寄存器）</summary>
        public const int RollerLength      = 10300;
        public const int RollerLength_Len  = 2;

        /// <summary>10302~10303 堵孔尺寸（读，mm，FLOAT 2寄存器）</summary>
        public const int BorePlugSize      = 10302;
        public const int BorePlugSize_Len  = 2;

        /// <summary>10304~10305 成活直径（读，mm，FLOAT 2寄存器）</summary>
        public const int RollerDiameter    = 10304;
        public const int RollerDiameter_Len = 2;

        /// <summary>10394~10395 加工时间（读，分钟，FLOAT 2寄存器）</summary>
        public const int MachiningTime     = 10394;
        public const int MachiningTime_Len = 2;

        // ══════════════════════════════════════════════
        //  【写 FC06】上位机控制命令（INT16）
        // ══════════════════════════════════════════════

        /// <summary>10370 加工模式（写，粗车1，精车2，研磨3，粗精磨4，精磨5，粗精研磨6）</summary>
        public const int MachiningMode = 10370;

        /// <summary>10371 远程尾座顶紧（写，1=顶紧指令）</summary>
        public const int TailstockClampCmd = 10371;

        /// <summary>10372 远程启动（写，1=启动指令）</summary>
        public const int RemoteStart = 10372;

        /// <summary>10373 远程尾座张开（写，1=张开指令）</summary>
        public const int TailstockOpenCmd = 10373;

        /// <summary>10374 尾座停止（写，1=停止指令，天车异常时使用）</summary>
        public const int TailstockStopCmd = 10374;
    }
}
