namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;

/// <summary>
/// 新增/编辑天车弹窗模型。
/// 线路固定为 1/2。
/// </summary>
public sealed class CraneDialogVm
{
    /// <summary>弹窗标题。</summary>
    public string DialogTitle { get; set; } = "添加天车";

    /// <summary>标题栏文本。</summary>
    public string HeaderText { get; set; } = "添加天车";

    /// <summary>关闭按钮文本。</summary>
    public string CloseButtonText { get; set; } = "✕";

    /// <summary>确认按钮文本。</summary>
    public string ConfirmButtonText { get; set; } = "确定";

    /// <summary>取消按钮文本。</summary>
    public string CancelButtonText { get; set; } = "取消";

    /// <summary>线路选项（写死）。</summary>
    public int[] LineOptions { get; } = [1, 2];

    /// <summary>当前线路（仅 1/2）。</summary>
    public int Line { get; set; } = 1;

    /// <summary>天车编号。</summary>
    public int Id { get; set; }

    /// <summary>天车名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>IP 地址。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>天车端口。</summary>
    public int Port { get; set; } = 6000;

    /// <summary>编码器 IP。</summary>
    public string EncoderIp { get; set; } = string.Empty;

    /// <summary>编码器端口。</summary>
    public int EncoderPort { get; set; } = 502;

    /// <summary>编码器机号。</summary>
    public int EncoderNo { get; set; } = 1;

    /// <summary>绝对位置偏移。</summary>
    public double AbsXOffset { get; set; }

    /// <summary>Z 轴正限位。</summary>
    public double LimitZP { get; set; }

    /// <summary>Z 轴负限位。</summary>
    public double LimitZN { get; set; }

    /// <summary>X 轴螺距（脉冲当量）。</summary>
    public double PulsePerMMX { get; set; }

    /// <summary>天车原点 X 坐标。</summary>
    public double OriginX { get; set; }

    /// <summary>天车原点 Y 坐标。</summary>
    public double OriginY { get; set; }

    /// <summary>天车原点 Z 坐标。</summary>
    public double OriginZ { get; set; }

    /// <summary>天车宽度。</summary>
    public double Width { get; set; }

    /// <summary>X 轴减速比。</summary>
    public double ReductionRatioX { get; set; }

    /// <summary>Y 轴减速比。</summary>
    public double ReductionRatioY { get; set; }

    /// <summary>Z 轴减速比。</summary>
    public double ReductionRatioZ { get; set; }

    /// <summary>X 轴起始位置。</summary>
    public double StartX { get; set; }

    /// <summary>X 轴结束位置。</summary>
    public double EndX { get; set; }

    /// <summary>图标路径。</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>图标选择按钮文本。</summary>
    public string SelectIconButtonText { get; set; } = "选择";

    /// <summary>工作状态：1-启用，2-检修，0-停用。</summary>
    public int WorkSta { get; set; } = 1;

    /// <summary>启用状态文本。</summary>
    public string EnabledText { get; set; } = "启用";

    /// <summary>检修状态文本。</summary>
    public string MaintenanceText { get; set; } = "检修";

    /// <summary>停用状态文本。</summary>
    public string DisabledText { get; set; } = "停用";
}
