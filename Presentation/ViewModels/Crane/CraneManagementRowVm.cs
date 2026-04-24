namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;

/// <summary>
/// 天车管理页列表行模型。
/// </summary>
public sealed class CraneManagementRowVm
{
    /// <summary>数据库主键（crane.id）。</summary>
    public int SourceId { get; set; }

    /// <summary>天车编号。</summary>
    public int Id { get; set; }

    /// <summary>线路号。</summary>
    public int LineNo { get; set; }

    /// <summary>天车机号。</summary>
    public int CraneNo { get; set; }

    /// <summary>天车名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>缩略图路径或名称。</summary>
    public string Thumb { get; set; } = string.Empty;

    /// <summary>X 坐标。</summary>
    public double X { get; set; }

    /// <summary>Y 坐标。</summary>
    public double Y { get; set; }

    /// <summary>Z 坐标。</summary>
    public double Z { get; set; }

    /// <summary>IP 地址。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>天车端口。</summary>
    public int Port { get; set; }

    /// <summary>编码器 IP。</summary>
    public string EncoderIp { get; set; } = string.Empty;

    /// <summary>编码器端口。</summary>
    public int EncoderPort { get; set; }

    /// <summary>编码器机号。</summary>
    public int EncoderNo { get; set; }

    /// <summary>天车宽度。</summary>
    public double Width { get; set; }

    /// <summary>绝对位置偏移。</summary>
    public int AbsXOffset { get; set; }

    /// <summary>X轴减速比。</summary>
    public double RatioX { get; set; }

    /// <summary>Y轴减速比。</summary>
    public double RatioY { get; set; }

    /// <summary>Z轴减速比。</summary>
    public double RatioZ { get; set; }

    /// <summary>X轴起始位置。</summary>
    public long StartX { get; set; }

    /// <summary>X轴结束位置。</summary>
    public long EndX { get; set; }

    /// <summary>Z轴正限位。</summary>
    public long LimitZP { get; set; }

    /// <summary>Z轴负限位。</summary>
    public long LimitZN { get; set; }

    /// <summary>X轴螺距。</summary>
    public double PulseX { get; set; }

    /// <summary>天车状态文本。</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>页面标题。</summary>
    public string TitleText { get; set; } = "天车管理";

    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get; set; } = "添加天车";

    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get; set; } = "编辑天车";

    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get; set; } = "删除天车";

    /// <summary>刷新图标按钮文本。</summary>
    public string RefreshIconButtonText { get; set; } = "刷新图标";
}
