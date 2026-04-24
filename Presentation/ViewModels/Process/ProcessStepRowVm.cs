namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Process;

/// <summary>
/// 工艺编辑弹窗右侧步骤表行模型。
/// </summary>
public sealed class ProcessStepRowVm
{
    /// <summary>机号。</summary>
    public int MachineNo { get; set; }

    /// <summary>工艺名称。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>工序执行时间。</summary>
    public string ExecuteTime { get; set; } = string.Empty;

    /// <summary>工序编号。</summary>
    public int StepNo { get; set; }

    /// <summary>工序名称。</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>使用设备。</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>回原位置。</summary>
    public string HomePosition { get; set; } = string.Empty;

    /// <summary>工序使能。</summary>
    public string Enabled { get; set; } = string.Empty;
}
