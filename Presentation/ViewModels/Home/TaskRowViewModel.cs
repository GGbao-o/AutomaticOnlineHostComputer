using System;
using System.Windows.Input;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 主页面任务表格单行模型。
/// 点击「启动」按钮 → 触发 OnStartRequested 回调 → HomeViewModel 将工件分配到对应线路。
/// </summary>
public sealed class TaskRowViewModel : ObservableObject
{
    private string _state = "待执行";
    private string _step = "待上料";
    private bool _isRunning;
    private int _assignedLine;

    public int Sequence { get; set; }
    public string PlateNo { get; set; } = string.Empty;
    public double Length { get; set; }
    public double Diameter { get; set; }
    public double PlugHole { get; set; }       // 70=小孔, 100=大孔
    public double LeftPlugThickness { get; set; }
    public double RightPlugThickness { get; set; }
    public string MarkingContent { get; set; } = string.Empty;
    public string ProcessType { get; set; } = "总工艺";
    public string BoringProcess { get; set; } = string.Empty;
    public string SkewBedProcess { get; set; } = string.Empty;
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
                OnPropertyChanged(nameof(AssignedLineText));
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

    public string ActionText => _isRunning ? "暂停" : "启动";

    /// <summary>用户点击「启动」时触发，参数为本行数据，HomeViewModel 订阅此回调</summary>
    public Action<TaskRowViewModel>? OnStartRequested { get; set; }

    public ICommand ToggleRunCommand { get; }

    public TaskRowViewModel()
    {
        ToggleRunCommand = new RelayCommand(ToggleRun);
    }

    private void ToggleRun()
    {
        if (!_isRunning)
        {
            // 启动 → 通知 HomeViewModel 分配工件到线路
            _isRunning = true;
            State = "运行中";
            OnPropertyChanged(nameof(ActionText));
            Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 启动 → 通知分配线路");
            OnStartRequested?.Invoke(this);
        }
        else
        {
            // 暂停（暂不支持恢复，留作扩展）
            _isRunning = false;
            State = "已暂停";
            OnPropertyChanged(nameof(ActionText));
            Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 已暂停");
        }
    }

    public void RejectStart(string reason)
    {
        _isRunning = false;
        State = "待执行";
        Step = reason;
        OnPropertyChanged(nameof(ActionText));
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 启动被拒绝: {reason}");
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
}
