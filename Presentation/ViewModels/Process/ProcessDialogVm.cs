using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Process;

/// <summary>
/// 新增/编辑工艺弹窗模型。
/// </summary>
public sealed class ProcessDialogVm
{
    /// <summary>弹窗标题。</summary>
    public string DialogTitle { get; set; } = "添加工艺";

    /// <summary>标题栏文本。</summary>
    public string HeaderText { get; set; } = "添加工艺";

    /// <summary>关闭按钮文本。</summary>
    public string CloseButtonText { get; set; } = "✕";

    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get; set; } = "添加";

    /// <summary>修改按钮文本。</summary>
    public string ModifyButtonText { get; set; } = "修改";

    /// <summary>确认按钮文本。</summary>
    public string ConfirmButtonText { get; set; } = "确认";

    /// <summary>取消按钮文本。</summary>
    public string CancelButtonText { get; set; } = "取消";

    /// <summary>工艺名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>工序编号。</summary>
    public int StepNo { get; set; }

    /// <summary>工序名称。</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>工序时间。</summary>
    public string ExecuteTime { get; set; } = string.Empty;

    /// <summary>使用设备。</summary>
    public string Device { get; set; } = "斜床";

    /// <summary>工序使能。</summary>
    public bool EnableProcess { get; set; } = true;

    /// <summary>Z 轴回原点（false 表示回安全位置）。</summary>
    public bool ZAxisBackHome { get; set; } = true;

    /// <summary>安全位置。</summary>    这个输入1 就算回安全位置  现在主要是和和页面图不太一样
    public string SafePosition { get; set; } = string.Empty;

    /// <summary>右侧步骤表数据。</summary>
    public List<ProcessStepRowVm> StepRows { get; set; } = [];
}
