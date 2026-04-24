namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Process;

/// <summary>
/// 工艺管理页列表行模型。
/// </summary>
public sealed class ProcessManagementRowVm
{
    /// <summary>源数据主键（process_route.id），用于编辑定位。</summary>
    public int SourceId { get; set; }

    /// <summary>机号/编号。</summary>
    public int Id { get; set; }

    /// <summary>工艺名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>编辑使用：当前绑定工序号。</summary>
    public int StepNo { get; set; }

    /// <summary>编辑使用：当前工序名称。</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>编辑使用：执行时间。</summary>
    public int ExecuteTime { get; set; }

    /// <summary>编辑使用：设备。</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>编辑使用：机号。</summary>
    public int MachineNo { get; set; }

    /// <summary>编辑使用：安全位置。</summary>
    public string SafePosition { get; set; } = string.Empty;

    /// <summary>编辑使用：工序使能。</summary>
    public bool Enabled { get; set; }

    /// <summary>编辑使用：Z轴回零。</summary>
    public bool ZAxisBackHome { get; set; }

    /// <summary>工序1。</summary>
    public string Step1 { get; set; } = string.Empty;

    /// <summary>工序2。</summary>
    public string Step2 { get; set; } = string.Empty;

    /// <summary>工序3。</summary>
    public string Step3 { get; set; } = string.Empty;

    /// <summary>工序4。</summary>
    public string Step4 { get; set; } = string.Empty;

    /// <summary>工序5。</summary>
    public string Step5 { get; set; } = string.Empty;

    /// <summary>工序6。</summary>
    public string Step6 { get; set; } = string.Empty;

    /// <summary>工序7。</summary>
    public string Step7 { get; set; } = string.Empty;

    /// <summary>工序8。</summary>
    public string Step8 { get; set; } = string.Empty;

    /// <summary>工序9。</summary>
    public string Step9 { get; set; } = string.Empty;

    /// <summary>工序10。</summary>
    public string Step10 { get; set; } = string.Empty;

    /// <summary>页面标题。</summary>
    public string TitleText { get; set; } = "工艺管理";

    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get; set; } = "添加工艺";

    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get; set; } = "编辑工艺";

    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get; set; } = "删除工艺";
}
