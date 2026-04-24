using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

/// <summary>
/// 新增/编辑机器弹窗模型。
/// 线路固定为 1/2。
/// </summary>
public sealed class MachineDialogVm
{
    /// <summary>弹窗标题。</summary>
    public string DialogTitle { get; set; } = "添加机器";

    /// <summary>标题栏文本。</summary>
    public string HeaderText { get; set; } = "添加机器";

    /// <summary>确认按钮文本。</summary>
    public string ConfirmButtonText { get; set; } = "确定";

    /// <summary>取消按钮文本。</summary>
    public string CancelButtonText { get; set; } = "取消";

    /// <summary>图标选择按钮文本。</summary>
    public string SelectIconButtonText { get; set; } = "选择";

    /// <summary>参数配置标题。</summary>
    public string ParameterTitleText { get; set; } = "参数配置";

    /// <summary>线路选项（写死）。</summary>
    public int[] LineOptions { get; } = [1, 2];

    /// <summary>当前线路（仅 1/2）。</summary>
    public int Line { get; set; } = 1;

    /// <summary>机器名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>机器类型。</summary>
    public string MachineType { get; set; } = "斜床";

    /// <summary>X 坐标。</summary>
    public double X { get; set; }

    /// <summary>Y 坐标。</summary>
    public double Y { get; set; }

    /// <summary>Z 坐标。</summary>
    public double Z { get; set; }

    /// <summary>下降 Z 坐标。</summary>
    public double DescendZ { get; set; }

    /// <summary>上升 Z 坐标。</summary>
    public double AscendZ { get; set; }

    /// <summary>绝对坐标。</summary>
    public double AbsolutePosition { get; set; }

    /// <summary>X 位移。</summary>
    public double XDisplacement { get; set; }

    /// <summary>Y 位移。</summary>
    public double YDisplacement { get; set; }

    /// <summary>Z 位移。</summary>
    public double ZDisplacement { get; set; }

    /// <summary>抖动距离。</summary>
    public double ShakeDistance { get; set; }

    /// <summary>加工范围。</summary>
    public string ProcessingRange { get; set; } = string.Empty;

    /// <summary>图标路径。</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>参数配置表数据。</summary>
    public List<MachineParameterRowVm> ParameterRows { get; set; } = [];
}
