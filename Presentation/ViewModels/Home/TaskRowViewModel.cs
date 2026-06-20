using System;
using System.Windows.Input;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 主页面任务表格单行模型。
/// 点击「启动」按钮 → 触发 OnStartRequested 回调 → HomeViewModel 将工件分配到对应线路。
/// 点击「清除」按钮 → 触发 OnDeleteRequested 回调 → HomeViewModel 只隐藏页面行; 未派发任务会同时取消全局FIFO等待。
/// </summary>
public sealed class TaskRowViewModel : ObservableObject
{
    private string _state = "待执行";
    private string _step = "待上料";
    private bool _isRunning;
    private int _assignedLine;
    private string _processType = "总工艺";

    public string Sequence { get; set; } = string.Empty;
    public string PlateNo { get; set; } = string.Empty;
    public double Length { get; set; }
    public double Diameter { get; set; }
    public double PlugHole { get; set; }       // 70=小孔, 100=大孔
    public double LeftPlugThickness { get; set; }
    public double RightPlugThickness { get; set; }
    public string MarkingContent { get; set; } = string.Empty;
    public string ProcessType
    {
        get => _processType;
        set
        {
            if (SetField(ref _processType, value))
                OnPropertyChanged(nameof(SkipBoringText));
        }
    }
    public string BoringProcess { get; set; } = string.Empty;
    public string SkewBedProcess { get; set; } = string.Empty;
    public bool ForceBalancing { get; set; }
    public bool StartFromTransferRack { get; set; }
    public int TransferRackLine { get; set; }
    public string TransferRackCode { get; set; } = string.Empty;
    public string TransferRackDisplayName { get; set; } = string.Empty;

    /// <summary>分配的线路（1或2，由 HomeViewModel 在启动时自动判断）</summary>
    public int AssignedLine
    {
        get => _assignedLine;
        set
        {
            if (SetField(ref _assignedLine, value))
            {
                OnPropertyChanged(nameof(AssignedLineText));
                OnPropertyChanged(nameof(CanDelete));
                RaiseDeleteCanExecuteChanged();
            }
        }
    }

    public string Step { get => _step; set => SetField(ref _step, value); }
    public string State { get => _state; set => SetField(ref _state, value); }

    /// <summary>任务来源显示：正常任务从总上料架开始，人工补料任务显示具体中转架。</summary>
    public string StartSourceText => StartFromTransferRack
        ? (string.IsNullOrWhiteSpace(TransferRackDisplayName) ? TransferRackCode : TransferRackDisplayName)
        : "总上料架";

    /// <summary>分配线路显示：启动前尚未分配，启动后显示 1/2 号线。</summary>
    public string AssignedLineText => AssignedLine > 0 ? $"{AssignedLine}号线" : "未分配";

    /// <summary>动平衡来源显示：勾选时强制做, 未勾选仍按长度>=800的原规则自动判断。</summary>
    public string BalancingText => ForceBalancing ? "是" : "自动";

    /// <summary>ERP/手动任务统一显示是否跳过双头镗。这里只用于页面显示, 不改变原 ProcessType 业务判断。</summary>
    public string SkipBoringText => ProcessType == "省去双头镗工艺" ? "是" : "否";

    public string ActionText => _isRunning ? "暂停" : "启动";

    /// <summary>当前任务行是否处于启动态。全局派发队列用它判断队头是否允许继续派发。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>清除按钮始终可用。已进入现场流程的任务只隐藏页面行, 不清任何现场缓存。</summary>
    public bool CanDelete => true;

    /// <summary>用户点击「启动」时触发，参数为本行数据，HomeViewModel 订阅此回调</summary>
    public Action<TaskRowViewModel>? OnStartRequested { get; set; }

    /// <summary>用户点击「清除」时触发，HomeViewModel 做最终确认并移除页面行</summary>
    public Action<TaskRowViewModel>? OnDeleteRequested { get; set; }

    public ICommand ToggleRunCommand { get; }
    public ICommand DeleteCommand { get; }

    public TaskRowViewModel()
    {
        ToggleRunCommand = new RelayCommand(ToggleRun);
        DeleteCommand = new RelayCommand(Delete, () => CanDelete);
    }

    public void RequestStart()
    {
        if (_isRunning) return;

        // 启动 → 通知 HomeViewModel 分配工件到线路
        _isRunning = true;
        State = "运行中";
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanDelete));
        RaiseDeleteCanExecuteChanged();
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 启动 → 通知分配线路");
        OnStartRequested?.Invoke(this);
    }

    private void ToggleRun()
    {
        if (!_isRunning)
        {
            RequestStart();
            return;
        }

        // 暂停（暂不支持恢复，留作扩展）
        _isRunning = false;
        State = "已暂停";
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanDelete));
        RaiseDeleteCanExecuteChanged();
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 已暂停");
    }

    private void Delete()
    {
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 请求清除显示");
        OnDeleteRequested?.Invoke(this);
    }

    public void RejectStart(string reason)
    {
        _isRunning = false;
        State = "待执行";
        Step = reason;
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanDelete));
        RaiseDeleteCanExecuteChanged();
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 启动被拒绝: {reason}");
    }

    private void RaiseDeleteCanExecuteChanged()
    {
        if (DeleteCommand is RelayCommand cmd)
            cmd.RaiseCanExecuteChanged();
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
