namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses
{
    /// <summary>
    /// 激光打标机/打号机 地址定义。
    /// <para>支持两种通信方式（根据设备型号选择）：</para>
    /// <para>
    ///   方式一：Modbus TCP（图6，寄存器地址从1起，端口502）
    ///     - 读输入寄存器（FC04，0x0001~0x0004）：设备状态
    ///     - 写保持寄存器（FC06=单个/FC16=批量，0x0005~0x00C8）：上位机控制+打印参数
    ///   方式二：文件共享协议（图5）
    ///     - 读文件夹3内容：0=有版（可放工件），1=没版
    ///     - 上位机生成文件A（刻印内容+工件直径）→ 打标机工作 → 生成文件B → 天车取料并删除B
    /// </para>
    /// </summary>
    public static class LaserMarkerAddress
    {
        // ══════════════════════════════════════════════════════
        //  方式一：Modbus TCP 寄存器地址
        //  注：寄存器编号从1开始（文档标注0001等）
        //      Modbus 实际地址 = 寄存器编号 - 1（零起始）
        // ══════════════════════════════════════════════════════

        // ─── 读输入寄存器 FC04（设备→上位机）────────────────────────

        /// <summary>0x0001 设备空闲寄存器（读，0=设备空闲，1=设备忙碌）</summary>
        public const int Reg_DeviceBusy       = 0x0001;

        /// <summary>0x0002 设备运行寄存器（读，0=设备停止，1=设备运行）</summary>
        public const int Reg_DeviceRunning    = 0x0002;

        /// <summary>0x0003 加工完成寄存器（读，0=加工未完，1=加工完成）</summary>
        public const int Reg_MachineDone      = 0x0003;

        /// <summary>0x0004 设备打印文字寄存器（读，0=无新内容，1=有新打印内容）</summary>
        public const int Reg_PrintReady       = 0x0004;

        // ─── 写保持寄存器 FC06 单个（上位机→设备）────────────────────

        /// <summary>0x0005 上料完成寄存器（写，0=上料未完，1=上料完成）</summary>
        public const int Reg_LoadDone         = 0x0005;

        /// <summary>0x0006 下料完成寄存器（写，0=下料未完，1=下料完成）</summary>
        public const int Reg_UnloadDone       = 0x0006;

        /// <summary>0x0007 版辊外径（写，mm，Word整数）</summary>
        public const int Reg_RollerOuterDiam  = 0x0007;

        /// <summary>0x0008 版辊内径（写，mm，Word整数）</summary>
        public const int Reg_RollerInnerDiam  = 0x0008;

        // ─── 写保持寄存器 FC16 批量（打印文字，UTF-8编码）────────────

        /// <summary>0x000A~0x00C8 打印文字寄存器（写，UTF-8编码字节流）</summary>
        public const int Reg_PrintText_Start  = 0x000A;   // 起始地址 10
        public const int Reg_PrintText_End    = 0x00C8;   // 结束地址 200
        public const int Reg_PrintText_Count  = 0x00C8 - 0x000A + 1;  // 191个寄存器

        // 打印内容格式：版号;序号; 版辊直径;周长;版长;内径;外径
        // 例：V2024001;001;220;691;1000;80;100
        // UTF-8 编码后按每寄存器2字节顺序填充

        // ══════════════════════════════════════════════════════
        //  方式二：文件共享协议 文件名/内容定义
        // ══════════════════════════════════════════════════════

        /// <summary>共享文件夹路径（根据实际网络路径配置）</summary>
        public const string SharedFolderPath  = @"\\192.168.1.xxx\LaserShare";

        /// <summary>
        /// 状态检测文件夹（文件夹3）：
        /// 读取该文件夹内容，0=有版可放，1=无版
        /// </summary>
        public const string StatusFolder      = "Folder3";

        /// <summary>
        /// 任务文件 A（上位机创建）：
        /// 文件名：A
        /// 内容行1：刻印文字内容（版号;序号;版辊直径;周长;版长;内径;外径）
        /// 内容行2：工件直径（mm）
        /// </summary>
        public const string TaskFileA         = "A";

        /// <summary>
        /// 完成文件 B（打标机创建，表示刻印完成）：
        /// 天车收到B文件后取料并删除B文件
        /// </summary>
        public const string DoneFileB         = "B";
    }
}
