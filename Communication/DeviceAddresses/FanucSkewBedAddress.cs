namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses
{
    /// <summary>
    /// 沈阳斜床 FANUC 系统宏变量地址定义（共6台）。
    /// <para>通信方式：FANUC FOCAS2，TCP 端口 8193。</para>
    /// <para>地址类型：宏变量（#编号），通过 cnc_rdmacro / cnc_wrmacro 读写。</para>
    /// </summary>
    public static class FanucSkewBedAddress
    {
        // ══════════════════════════════════════════════
        //  【读】机床状态信号（上位机 → 读取）
        // ══════════════════════════════════════════════

        /// <summary>#1000 机床准备就绪（0=未就绪，1=就绪）</summary>
        public const int MachineReady = 1000;

        /// <summary>#1012 机床操作模式（0=手动，1=半自动，2=全自动）</summary>
        public const int OperationMode = 1012;

        /// <summary>#1001 请求数据（0=无请求，1=请求上位机下发加工参数）</summary>
        public const int RequestData = 1001;

        /// <summary>#1002 请求上料（0=无请求，1=请求天车送料）</summary>
        public const int RequestLoad = 1002;

        /// <summary>#1003 尾座夹紧到位（0=未到位，1=已夹紧）</summary>
        public const int TailstockClamped = 1003;

        /// <summary>#1004 请求下料（0=无请求，1=请求天车取料）</summary>
        public const int RequestUnload = 1004;

        /// <summary>#1005 尾座分开到位（0=未到位，1=已分开）</summary>
        public const int TailstockOpened = 1005;

        /// <summary>#1006 自动门开（0=关，1=开）</summary>
        public const int AutoDoorOpen = 1006;

        /// <summary>#1007 Y轴安全位置（0=未到位，1=在安全位置）</summary>
        public const int YAxisSafePos = 1007;

        /// <summary>#1009 Z轴安全位置（0=未到位，1=在安全位置）</summary>
        public const int ZAxisSafePos = 1009;

        /// <summary>#1010 刀架安全位置（0=未到位，1=在安全位置）</summary>
        public const int TurretSafePos = 1010;

        /// <summary>#1011 X轴安全位置（0=未到位，1=在安全位置）</summary>
        public const int XAxisSafePos = 1011;

        /// <summary>#1013 刀具1使用寿命到期（0=正常，1=寿命到期，新增）</summary>
        public const int Tool1LifeEnd = 1013;

        /// <summary>#1014 刀具2使用寿命到期（0=正常，1=寿命到期，新增）</summary>
        public const int Tool2LifeEnd = 1014;

        /// <summary>#651 尾座停止（新增，用于检测天车异常时停止尾座）</summary>
        public const int TailstockStop = 651;

        // ══════════════════════════════════════════════
        //  【写】上位机 → 下发加工参数
        // ══════════════════════════════════════════════

        /// <summary>#800 版辊长度（mm，浮点）</summary>
        public const int RollerLength = 800;

        /// <summary>#801 斜车成活直径（mm，浮点）</summary>
        public const int RollerDiameter = 801;

        /// <summary>#802 堵孔（mm，浮点）</summary>
        public const int BorePlugSize = 802;

        /// <summary>#909 加工模式（新增：粗车1，精车2，研磨3，粗精磨4，精磨5，粗精研磨6）</summary>
        public const int MachiningMode = 909;

        // ══════════════════════════════════════════════
        //  【写】上位机 → 天车交互信号
        // ══════════════════════════════════════════════

        /// <summary>#1101 数据下发完成（0=未完成，1=完成，上位机写）</summary>
        public const int DataSentDone = 1101;

        /// <summary>#1102 天车上料到位（0=未到位，1=到位，上位机/天车写）</summary>
        public const int CraneLoadInPlace = 1102;

        /// <summary>#1103 天车上料完成（0=未完成，1=完成，上位机写）</summary>
        public const int CraneLoadDone = 1103;

        /// <summary>#1104 天车下料到位（0=未到位，1=到位）</summary>
        public const int CraneUnloadInPlace = 1104;

        /// <summary>#1105 天车下料完成（0=未完成，1=完成）</summary>
        public const int CraneUnloadDone = 1105;
    }
}
