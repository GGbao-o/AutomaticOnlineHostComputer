namespace AutomaticOnlineHostComputer.Communication.DeviceAddresses;

/// <summary>
/// 双头镗 Modbus TCP 地址定义。
/// <para>现场仍按 R 区编号描述，新代 Modbus TCP 实际保持寄存器地址 = R编号 * 2 + 1。</para>
/// </summary>
public static class BoringModbusAddress
{
    // ── 加工参数（上位机 → 双头镗）────────────────────────────────
    /// <summary>R2041 长度，16位无符号整数，ERP 下发多少写多少。</summary>
    public const int R_RollerLength = 2041;

    /// <summary>R2043 直径，整数，ERP 值 * 100。</summary>
    public const int R_OuterDiameter = 2043;

    /// <summary>R2044 左堵厚，整数，ERP 值 * 100。</summary>
    public const int R_LeftPlugThickness = 2044;

    /// <summary>R2045 右堵厚，整数，ERP 值 * 100。</summary>
    public const int R_RightPlugThickness = 2045;

    /// <summary>R2046 内孔锥度，任务值 * 100。</summary>
    public const int R_InnerTaper = 2046;

    /// <summary>R2047 内孔成活/版孔，整数，ERP 值 * 100。</summary>
    public const int R_BoreType = 2047;

    /// <summary>R2048 圆角大小，任务值 * 100。</summary>
    public const int R_CornerSize = 2048;

    // ── 握手信号 ─────────────────────────────────────────────────
    /// <summary>R6101 请求数据（读，双头镗 → 上位机）。</summary>
    public const int R_RequestData = 6101;

    /// <summary>R6102 数据下发完成（写1，上位机 → 双头镗）。</summary>
    public const int R_DataSentDone = 6102;

    /// <summary>R6103 请求上料（读，双头镗 → 上位机）。</summary>
    public const int R_RequestLoad = 6103;

    /// <summary>R6104 上料完成（写1，上位机 → 双头镗）。</summary>
    public const int R_LoadDone = 6104;

    /// <summary>R6107 请求下料（读，双头镗 → 上位机）。</summary>
    public const int R_RequestUnload = 6107;

    /// <summary>R6108 下料完成（写1，上位机 → 双头镗）。</summary>
    public const int R_UnloadDone = 6108;

    /// <summary>R 区编号转 Modbus TCP 保持寄存器地址。</summary>
    public static int RToModbus(int rNumber) => rNumber * 2 + 1;
}
