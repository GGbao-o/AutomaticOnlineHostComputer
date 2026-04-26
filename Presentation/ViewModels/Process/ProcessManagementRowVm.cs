using AutomaticOnlineHostComputer.Presentation.ViewModels;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Process;

/// <summary>
/// 工艺管理页列表行模型。
/// 继承 <see cref="ObservableObject"/> 以支持 WPF 数据绑定自动刷新。
/// </summary>
public sealed class ProcessManagementRowVm : ObservableObject
{
    private int _sourceId;
    /// <summary>源数据主键（process_route.id），用于编辑定位。</summary>
    public int SourceId { get => _sourceId; set => SetField(ref _sourceId, value); }

    private int _id;
    /// <summary>机号/编号。</summary>
    public int Id { get => _id; set => SetField(ref _id, value); }

    private string _name = string.Empty;
    /// <summary>工艺名称。</summary>
    public string Name { get => _name; set => SetField(ref _name, value); }

    private int _stepNo;
    /// <summary>编辑使用：当前绑定工序号。</summary>
    public int StepNo { get => _stepNo; set => SetField(ref _stepNo, value); }

    private string _stepName = string.Empty;
    /// <summary>编辑使用：当前工序名称。</summary>
    public string StepName { get => _stepName; set => SetField(ref _stepName, value); }

    private int _executeTime;
    /// <summary>编辑使用：执行时间。</summary>
    public int ExecuteTime { get => _executeTime; set => SetField(ref _executeTime, value); }

    private string _device = string.Empty;
    /// <summary>编辑使用：设备。</summary>
    public string Device { get => _device; set => SetField(ref _device, value); }

    private int _machineNo;
    /// <summary>编辑使用：机号。</summary>
    public int MachineNo { get => _machineNo; set => SetField(ref _machineNo, value); }

    private string _safePosition = string.Empty;
    /// <summary>编辑使用：安全位置。</summary>
    public string SafePosition { get => _safePosition; set => SetField(ref _safePosition, value); }

    private bool _enabled;
    /// <summary>编辑使用：工序使能。</summary>
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    private bool _zAxisBackHome;
    /// <summary>编辑使用：Z轴回零。</summary>
    public bool ZAxisBackHome { get => _zAxisBackHome; set => SetField(ref _zAxisBackHome, value); }

    private string _step1 = string.Empty;
    /// <summary>工序1。</summary>
    public string Step1 { get => _step1; set => SetField(ref _step1, value); }

    private string _step2 = string.Empty;
    /// <summary>工序2。</summary>
    public string Step2 { get => _step2; set => SetField(ref _step2, value); }

    private string _step3 = string.Empty;
    /// <summary>工序3。</summary>
    public string Step3 { get => _step3; set => SetField(ref _step3, value); }

    private string _step4 = string.Empty;
    /// <summary>工序4。</summary>
    public string Step4 { get => _step4; set => SetField(ref _step4, value); }

    private string _step5 = string.Empty;
    /// <summary>工序5。</summary>
    public string Step5 { get => _step5; set => SetField(ref _step5, value); }

    private string _step6 = string.Empty;
    /// <summary>工序6。</summary>
    public string Step6 { get => _step6; set => SetField(ref _step6, value); }

    private string _step7 = string.Empty;
    /// <summary>工序7。</summary>
    public string Step7 { get => _step7; set => SetField(ref _step7, value); }

    private string _step8 = string.Empty;
    /// <summary>工序8。</summary>
    public string Step8 { get => _step8; set => SetField(ref _step8, value); }

    private string _step9 = string.Empty;
    /// <summary>工序9。</summary>
    public string Step9 { get => _step9; set => SetField(ref _step9, value); }

    private string _step10 = string.Empty;
    /// <summary>工序10。</summary>
    public string Step10 { get => _step10; set => SetField(ref _step10, value); }

    private string _titleText = "工艺管理";
    /// <summary>页面标题。</summary>
    public string TitleText { get => _titleText; set => SetField(ref _titleText, value); }

    private string _addButtonText = "添加工艺";
    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get => _addButtonText; set => SetField(ref _addButtonText, value); }

    private string _editButtonText = "编辑工艺";
    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get => _editButtonText; set => SetField(ref _editButtonText, value); }

    private string _deleteButtonText = "删除工艺";
    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get => _deleteButtonText; set => SetField(ref _deleteButtonText, value); }
}
