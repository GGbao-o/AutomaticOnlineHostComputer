namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses
{
    /// <summary>
    /// 双头镗 新代（Syntec）CNC 地址定义。
    /// <para>通信方式：Syntec OpenCNC SDK，以太网 TCP。</para>
    /// <para>
    /// 地址分两类：
    ///   - 宏变量（@编号）：使用 READ_macro_single / WRITE_macro_single 读写，传入编号即可。
    ///   - R 区寄存器（R编号）：使用 READ_plc_addr("R",...) / WRITE_plc_addr("R",...) 读写，
    ///     Modbus TCP 时寄存器地址 = R编号*2+1（见文档注：MODBUSTCP读取写入地址为 R*2+1）。
    /// </para>
    /// </summary>
    public static class SyntecBoringAddress
    {
        // ══════════════════════════════════════════════
        //  【宏变量】上位机 → 写入加工参数
        // ══════════════════════════════════════════════

        /// <summary>@740 版长（mm，浮点）</summary>
        public const int MacroRollerLength    = 740;

        /// <summary>@705 外圆直径（mm，浮点）</summary>
        public const int MacroOuterDiameter   = 705;

        /// <summary>@702 左堵厚（mm，浮点）</summary>
        public const int MacroLeftPlugThickness  = 702;

        /// <summary>@703 右堵厚（mm，浮点）</summary>
        public const int MacroRightPlugThickness = 703;

        /// <summary>@708 内孔锥度（mm/m，浮点）</summary>
        public const int MacroInnerConicity   = 708;

        /// <summary>@707 内孔成活（mm，浮点）</summary>
        public const int MacroInnerDiameter   = 707;

        // ══════════════════════════════════════════════
        //  【R区】交互握手信号（R6101~R6110 — 全部10个信号一个不能省）
        //
        //  上料: R6101=1(CNC请求数据)→写宏变量→R6102=1(上位机下发完成)
        //        →R6103=1(CNC请求上料)→R6104=1(上位机:货叉推入到位)→R6105=1(CNC夹紧完成)
        //        →货叉缩回→R6106=1(上位机:上料推出完成)
        //  加工: R6107=1(CNC加工完成请求下料)
        //  下料: 货叉伸进→R6108=1(上位机:货叉下料推入到位)→R6109=1(CNC松开完成)
        //        →货叉缩回→R6110=1(上位机:下料推出完成)
        // ══════════════════════════════════════════════

        /// <summary>R6101 请求数据（读，CNC→上位机, 1=请求下发加工参数）</summary>
        public const int R_RequestData        = 6101;

        /// <summary>R6102 数据下发完成（写，上位机→CNC, 1=参数已下发）</summary>
        public const int R_DataSentDone       = 6102;

        /// <summary>R6103 请求上料（读，CNC→上位机, 1=请求天车送料）</summary>
        public const int R_RequestLoad        = 6103;

        /// <summary>R6104 上料推入到位（写，上位机→CNC, 1=天车已到装料位）</summary>
        public const int R_ForkLoadInPlace    = 6104;

        /// <summary>R6105 卡钳夹紧完成（读，CNC→上位机, 1=已夹紧工件）</summary>
        public const int R_ClampDone          = 6105;

        /// <summary>R6106 上料完成（写，上位机→CNC, 1=天车已退出装料位）</summary>
        public const int R_ForkLoadOutDone    = 6106;

        /// <summary>R6107 请求下料（读，CNC→上位机, 1=加工完成请求取料）</summary>
        public const int R_RequestUnload      = 6107;

        /// <summary>R6108 下料推入到位（写，上位机→CNC, 1=天车已到取料位）</summary>
        public const int R_ForkUnloadInPlace  = 6108;

        /// <summary>R6109 卡钳松开完成（读，CNC→上位机, 1=已松开工件）</summary>
        public const int R_UnclampDone        = 6109;

        /// <summary>R6110 下料完成（写，上位机→CNC, 1=天车已取走工件）</summary>
        public const int R_ForkUnloadOutDone  = 6110;

        // ══════════════════════════════════════════════
        //  Modbus TCP 地址转换辅助（R*2+1）
        // ══════════════════════════════════════════════

        /// <summary>将 R 区编号转换为 Modbus TCP 保持寄存器地址（R*2+1）。</summary>
        public static int RToModbus(int rNumber) => rNumber * 2 + 1;
    }
}
