namespace AutomaticOnlineHostComputer.Domain.Entities;

/// <summary>
/// 天车实体。
/// 对应天车管理页面的数据模型。
/// </summary>
public sealed class CraneEntity
{
    /// <summary>天车主键。</summary>
    public int Id { get; set; }

    /// <summary>天车名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>天车 IP。</summary>
    public string IP { get; set; } = string.Empty;

    /// <summary>天车端口。</summary>
    public int Port { get; set; }

    /// <summary>编码器 IP。</summary>
    public string EncodeIP { get; set; } = string.Empty;

    /// <summary>编码器端口。</summary>
    public int EncodePort { get; set; }

    /// <summary>编码器编号。</summary>
    public int EncodeNo { get; set; }

    /// <summary>X 坐标。</summary>
    public int PosX { get; set; }

    /// <summary>Y 坐标。</summary>
    public int PosY { get; set; }

    /// <summary>Z 坐标。</summary>
    public int PosZ { get; set; }

    /// <summary>宽度。</summary>
    public int Width { get; set; }

    /// <summary>图标路径。</summary>
    public string IcoPath { get; set; } = string.Empty;

    /// <summary>工作状态。</summary>
    public int WorkSta { get; set; }

    /// <summary>减速比 X。</summary>
    public double ReductionRatioX { get; set; }

    /// <summary>减速比 Y。</summary>
    public double ReductionRatioY { get; set; }

    /// <summary>减速比 Z。</summary>
    public double ReductionRatioZ { get; set; }

    /// <summary>绝对 X 偏移。</summary>
    public int AbsXOffset { get; set; }

    /// <summary>起点 X。</summary>
    public long StartX { get; set; }

    /// <summary>终点 X。</summary>
    public long EndX { get; set; }

    /// <summary>正向限位。</summary>
    public long LimitZP { get; set; }

    /// <summary>反向限位。</summary>
    public long LimitZN { get; set; }

    /// <summary>X 轴脉冲当量。</summary>
    public double PulsePerMMX { get; set; }
}
