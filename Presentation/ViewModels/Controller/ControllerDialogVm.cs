using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;

/// <summary>
/// 新增/编辑控制器弹窗模型。
/// </summary>
public sealed class ControllerDialogVm
{
    /// <summary>弹窗标题。</summary>
    public string DialogTitle { get; set; } = "添加控制器";

    /// <summary>标题栏文本。</summary>
    public string HeaderText { get; set; } = "添加控制器";

    /// <summary>关闭按钮文本。</summary>
    public string CloseButtonText { get; set; } = "✕";

    /// <summary>确认按钮文本。</summary>
    public string ConfirmButtonText { get; set; } = "确定";

    /// <summary>取消按钮文本。</summary>
    public string CancelButtonText { get; set; } = "取消";

    /// <summary>图标选择按钮文本。</summary>
    public string SelectIconButtonText { get; set; } = "选择";

    /// <summary>设备名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备类型。</summary>
    public string Type { get; set; } = "研磨机";

    /// <summary>IP 地址。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>端口。</summary>
    public int Port { get; set; }

    /// <summary>机号。</summary>
    public int No { get; set; }

    /// <summary>图标路径。</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>状态：1-启用，2-检修，0-停用。</summary>
    public int State { get; set; } = 1;

    /// <summary>启用文本。</summary>
    public string EnabledText { get; set; } = "启用";

    /// <summary>检修文本。</summary>
    public string MaintenanceText { get; set; } = "检修";

    /// <summary>停用文本。</summary>
    public string DisabledText { get; set; } = "停用";

    /// <summary>右侧寄存器配置表。</summary>
    public List<ControllerRegisterRowVm> RegisterRows { get; set; } = [];
}
