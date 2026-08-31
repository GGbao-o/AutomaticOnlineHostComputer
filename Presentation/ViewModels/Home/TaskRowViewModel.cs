using System;
using System.Windows.Input;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

public enum TaskDispatchState
{
    NotQueued,
    Queued,
    Dispatching,
    EnteredLine
}

/// <summary>
/// 主页面任务表格单行模型。
/// 点击「启动」按钮 → 触发 OnStartRequested 回调 → HomeViewModel 将工件分配到对应线路。
/// 点击「清除」按钮 → 触发 OnDeleteRequested 回调 → HomeViewModel 只隐藏页面行; 未派发任务会同时取消全局FIFO等待。
/// </summary>
public sealed class TaskRowViewModel : ObservableObject
{
    private string _state = "待执行";
    private string _step = "待上料";
    private TaskDispatchState _dispatchState;
    private int? _queuePosition;
    private int _assignedLine;
    private string _processType = "总工艺";

    public string Sequence { get; set; } = string.Empty;
    public string PlateNo { get; set; } = string.Empty;
    public double Length { get; set; }
    public double Diameter { get; set; }
    public double PlugHole { get; set; }       // 70=小孔, 100=大孔
    public double LeftPlugThickness { get; set; }
    public double RightPlugThickness { get; set; }
    public double InnerTaper { get; set; }
    public double CornerSize { get; set; }
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

    public TaskDispatchState DispatchState => _dispatchState;

    public int? QueuePosition => _queuePosition;

    public string QueueOrderText => QueuePosition?.ToString() ?? "—";

    public string ActionText => DispatchState switch
    {
        TaskDispatchState.NotQueued => "启动",
        TaskDispatchState.Queued => "暂停",
        TaskDispatchState.Dispatching => "派发中",
        TaskDispatchState.EnteredLine => "已入线",
        _ => "启动"
    };

    public bool CanToggleRun => DispatchState is TaskDispatchState.NotQueued or TaskDispatchState.Queued;

    /// <summary>当前任务是否已经启动；用于一键启动计数和清除确认，不作为FIFO派发依据。</summary>
    public bool IsRunning => DispatchState != TaskDispatchState.NotQueued;

    /// <summary>清除按钮始终可用。已进入现场流程的任务只隐藏页面行, 不清任何现场缓存。</summary>
    public bool CanDelete => true;

    /// <summary>用户点击「启动」时触发，参数为本行数据，HomeViewModel 订阅此回调</summary>
    public Action<TaskRowViewModel>? OnStartRequested { get; set; }

    /// <summary>用户点击「暂停」时触发；由HomeViewModel原子地从待派发FIFO移除。</summary>
    public Action<TaskRowViewModel>? OnPauseRequested { get; set; }

    /// <summary>用户点击「清除」时触发，HomeViewModel 做最终确认并移除页面行</summary>
    public Action<TaskRowViewModel>? OnDeleteRequested { get; set; }

    public ICommand ToggleRunCommand { get; }
    public ICommand DeleteCommand { get; }

    public TaskRowViewModel()
    {
        ToggleRunCommand = new RelayCommand(ToggleRun, () => CanToggleRun);
        DeleteCommand = new RelayCommand(Delete, () => CanDelete);
    }

    public void RequestStart()
    {
        if (DispatchState != TaskDispatchState.NotQueued) return;

        // 状态由HomeViewModel在真实入队/入线提交成功后更新，避免UI先行造成假启动。
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 启动 → 通知分配线路");
        OnStartRequested?.Invoke(this);
    }

    private void ToggleRun()
    {
        if (DispatchState == TaskDispatchState.NotQueued)
        {
            RequestStart();
            return;
        }

        if (DispatchState == TaskDispatchState.Queued)
        {
            Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 请求暂停并移出待派发FIFO");
            OnPauseRequested?.Invoke(this);
        }
    }

    private void Delete()
    {
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 请求清除显示");
        OnDeleteRequested?.Invoke(this);
    }

    public void RejectStart(string reason)
    {
        SetDispatchState(TaskDispatchState.NotQueued, null);
        State = "待执行";
        Step = reason;
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 启动被拒绝: {reason}");
    }

    public void MarkQueued(int? position = null)
    {
        SetDispatchState(TaskDispatchState.Queued, position);
        State = "运行中";
        Step = "等待总上料架派发";
    }

    public void MarkPaused()
    {
        SetDispatchState(TaskDispatchState.NotQueued, null);
        State = "已暂停";
        Step = "待上料";
    }

    public void MarkDispatching()
    {
        SetDispatchState(TaskDispatchState.Dispatching, null);
        State = "运行中";
        Step = "派发中";
    }

    public void MarkEnteredLine()
    {
        SetDispatchState(TaskDispatchState.EnteredLine, null);
        State = "运行中";
    }

    public void SetQueuePosition(int? position)
    {
        if (_queuePosition == position) return;
        _queuePosition = position;
        OnPropertyChanged(nameof(QueuePosition));
        OnPropertyChanged(nameof(QueueOrderText));
    }

    private void SetDispatchState(TaskDispatchState state, int? queuePosition)
    {
        bool stateChanged = _dispatchState != state;
        _dispatchState = state;
        SetQueuePosition(queuePosition);

        if (!stateChanged) return;

        OnPropertyChanged(nameof(DispatchState));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanToggleRun));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanDelete));
        if (ToggleRunCommand is RelayCommand toggleCommand)
            toggleCommand.RaiseCanExecuteChanged();
        RaiseDeleteCanExecuteChanged();
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
