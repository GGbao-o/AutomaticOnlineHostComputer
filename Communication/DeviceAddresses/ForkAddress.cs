namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses;

/// <summary>
/// 货叉 PLC 信号地址（三菱 MC 协议，M 寄存器，端口 9000）。
/// <para>
/// 货叉由独立三菱 FX3G PLC 控制，4 个工位：待机位 / 1号位 / 2号位 / 3号位。
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
    public const int M_HasPlate        = 900;

    /// <summary>M901  货叉在待机位状态（读，1=在待机位）</summary>
    public const int M_AtStandbyPos    = 901;

    /// <summary>M902  货叉在1号位状态（读，1=在1号位）</summary>
    public const int M_AtPos1          = 902;

    /// <summary>M903  货叉在2号位状态（读，1=在2号位）</summary>
    public const int M_AtPos2          = 903;

    /// <summary>M904  货叉在3号位状态（读，1=在3号位）</summary>
    public const int M_AtPos3          = 904;

    // ═══════════════════════════════════════════════════════════════
    //  输出信号（中控→货叉，读-改-写）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>M911  货叉回待机位控制（写，1=回待机位）</summary>
    public const int M_GoStandby       = 911;

    /// <summary>M912  货叉去1号位置控制（写，1=去1号位）</summary>
    public const int M_GoPos1          = 912;

    /// <summary>M913  货叉去2号位置控制（写，1=去2号位）</summary>
    public const int M_GoPos2          = 913;

    /// <summary>M914  货叉去3号位置控制（写，1=去3号位）</summary>
    public const int M_GoPos3          = 914;

    // ═══════════════════════════════════════════════════════════════
    //  读取参数
    // ═══════════════════════════════════════════════════════════════

    /// <summary>批量读取起始地址（M900，读1个字=16bit覆盖M900~M915）</summary>
    public const int ReadStartAddr = 900;

    /// <summary>批量读取字数</summary>
    public const int ReadWordCount = 1;
}
