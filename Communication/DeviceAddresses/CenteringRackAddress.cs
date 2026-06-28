namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses;

/// <summary>
/// 机加居中上料架 PLC 信号地址（三菱 MC 协议，M/D 寄存器，端口 9000）。
/// <para>PLC 型号：三菱 FX3G + FX3U-ENET-L 扩展网口。</para>
/// <para>共 2 台：线体1和线体2各一台，独立 IP，IP 待确认。</para>
/// <para>
/// M 区 = 位信号（0x90），D 区 = 16bit 字寄存器（0xA8）。
/// 读 M800 起始 2 字（32bit）覆盖 M800~M831，M816=第2字 bit0。
/// D100 用 D 区单独读字。
/// </para>
/// </summary>
public static class CenteringRackAddress
{
    // ═══════════════════════════════════════════════════════════════
    //  输入信号（上料架→中控，只读）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>M800  上料架请求取料信号（读，1=请求天车来取料）</summary>
    public const int M_RequestPickup    = 800;

    /// <summary>M811  等待工位1有版信号（读，1=工位1有待加工版）</summary>
    public const int M_Station1HasPlate = 811;

    /// <summary>M812  等待工位2有版信号（读，1=工位2有待加工版）</summary>
    public const int M_Station2HasPlate = 812;

    /// <summary>M813  等待工位3有版信号（读，1=工位3有待加工版）</summary>
    public const int M_Station3HasPlate = 813;

    /// <summary>M814  等待工位4有版信号（读，1=工位4有待加工版）</summary>
    public const int M_Station4HasPlate = 814;

    /// <summary>M815  等待工位5有版信号（读，1=工位5有待加工版）</summary>
    public const int M_Station5HasPlate = 815;

    /// <summary>M816  等待工位6有版信号（读，1=工位6有待加工版）</summary>
    public const int M_Station6HasPlate = 816;

    // 2号线后端 ST020/ST021 握手。M818/M821不再表示单一“有板”状态，
    // 每一位的方向和业务语义必须独立使用。
    public const int M_RearHandshakeWordStart = 816;
    public const int M_ST019_HasPlate          = 817;
    public const int M_ST020_CanPlace          = 818;
    public const int M_ST020_PlaceDone         = 819;
    public const int M_ST021_CanPlace          = 820;
    public const int M_ST021_PlaceDone         = 821;
    public const int M_ST020_CanPick           = 823;
    public const int M_ST020_PickDone          = 824;
    public const int M_ST021_CanPick           = 825;
    public const int M_ST021_PickDone          = 826;

    public const int Bit_ST019_HasPlate  = M_ST019_HasPlate  - M_RearHandshakeWordStart;
    public const int Bit_ST020_CanPlace  = M_ST020_CanPlace  - M_RearHandshakeWordStart;
    public const int Bit_ST020_PlaceDone = M_ST020_PlaceDone - M_RearHandshakeWordStart;
    public const int Bit_ST021_CanPlace  = M_ST021_CanPlace  - M_RearHandshakeWordStart;
    public const int Bit_ST021_PlaceDone = M_ST021_PlaceDone - M_RearHandshakeWordStart;
    public const int Bit_ST020_CanPick   = M_ST020_CanPick   - M_RearHandshakeWordStart;
    public const int Bit_ST020_PickDone  = M_ST020_PickDone  - M_RearHandshakeWordStart;
    public const int Bit_ST021_CanPick   = M_ST021_CanPick   - M_RearHandshakeWordStart;
    public const int Bit_ST021_PickDone  = M_ST021_PickDone  - M_RearHandshakeWordStart;

    // ═══════════════════════════════════════════════════════════════
    //  输出信号（中控→上料架，读-改-写）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>M801  天车取料完成信号（写，1=天车已取走工件，上料架可复位）</summary>
    public const int M_PickupDone       = 801;

    // ═══════════════════════════════════════════════════════════════
    //  D 区字寄存器
    // ═══════════════════════════════════════════════════════════════

    /// <summary>D100  居中架测量板长数值（读，16位无符号，单位 mm）</summary>
    public const int D_PlateLength      = 100;

    // ═══════════════════════════════════════════════════════════════
    //  读取参数
    // ═══════════════════════════════════════════════════════════════

    /// <summary>M 区批量读取起始地址（M800）</summary>
    public const int M_ReadStartAddr    = 800;

    /// <summary>M 区批量读取字数（2字=32bit，覆盖 M800~M831，M816=第2字bit0）</summary>
    public const int M_ReadWordCount    = 2;
}
