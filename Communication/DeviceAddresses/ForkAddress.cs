namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses;

/// <summary>
/// 货叉 PLC 信号地址（三菱 MC 协议，M 寄存器，端口 9000）。
/// <para>
/// MC 协议 M 寄存器为 bit 型，读 M900 起始 1 个字（16bit）可覆盖 M900~M915。
/// 写操作通过读-改-写实现（读整字→改目标bit→写回）。
/// </para>
/// </summary>
public static class ForkAddress
{
    // ═══════════════════════════════════════════════════════════════
    //  输入信号（货叉→中控，只读）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>M900  货叉有版信号（读，1=有版）</summary>
    public const int M_HasPlate           = 900;
    /// <summary>M901  气缸1退回限位信号（读，1=已退回）</summary>
    public const int M_Cyl1RetractLimit   = 901;
    /// <summary>M902  气缸1伸出限位信号（读，1=已伸出）</summary>
    public const int M_Cyl1ExtendLimit    = 902;
    /// <summary>M903  气缸2退回限位信号（读，1=已退回）</summary>
    public const int M_Cyl2RetractLimit   = 903;
    /// <summary>M904  气缸2伸出限位信号（读，1=已伸出）</summary>
    public const int M_Cyl2ExtendLimit    = 904;
    /// <summary>M905  气缸3退回限位信号（读，1=已退回）</summary>
    public const int M_Cyl3RetractLimit   = 905;
    /// <summary>M906  气缸3伸出在位信号（读，1=已伸出）</summary>
    public const int M_Cyl3ExtendInPlace  = 906;

    // ═══════════════════════════════════════════════════════════════
    //  输出信号（中控→货叉，读写）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>M911  气缸1退回控制（写，1=退回）</summary>
    public const int M_Cyl1RetractCtrl    = 911;
    /// <summary>M912  气缸1伸出控制（写，1=伸出）</summary>
    public const int M_Cyl1ExtendCtrl     = 912;
    /// <summary>M913  气缸2退回控制（写，1=退回）</summary>
    public const int M_Cyl2RetractCtrl    = 913;
    /// <summary>M914  气缸2伸出控制（写，1=伸出）</summary>
    public const int M_Cyl2ExtendCtrl     = 914;
    /// <summary>M915  气缸3退回控制（写，1=退回）</summary>
    public const int M_Cyl3RetractCtrl    = 915;
    /// <summary>M916  气缸3伸出控制（写，1=伸出）</summary>
    public const int M_Cyl3ExtendCtrl     = 916;

    // ═══════════════════════════════════════════════════════════════
    //  读取参数
    // ═══════════════════════════════════════════════════════════════

    /// <summary>批量读取起始地址（M900，读1个字=16bit覆盖M900~M915）</summary>
    public const int ReadStartAddr = 900;
    /// <summary>批量读取字数</summary>
    public const int ReadWordCount = 1;
}
