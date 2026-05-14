using System;
using System.Windows.Input;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 主页面任务表格的单行数据模型（含启动/暂停切换命令）。
/// </summary>
public sealed class TaskRowViewModel : ObservableObject
{
    private string _state = "待执行";
    private string _step = "待上料";
    private bool _isRunning;

    public int Sequence { get; set; }
    public string PlateNo { get; set; } = string.Empty;
    public double Length { get; set; }
    public double Diameter { get; set; }
    public double PlugHole { get; set; }
    public double LeftPlugThickness { get; set; }
    public double RightPlugThickness { get; set; }
    public string MarkingContent { get; set; } = string.Empty;
    public string ProcessType { get; set; } = "总工艺";

    public string Step
    {
        get => _step;
        set => SetField(ref _step, value);
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string ActionText => _isRunning ? "暂停" : "启动";

    public ICommand ToggleRunCommand { get; }

    public TaskRowViewModel()
    {
        ToggleRunCommand = new RelayCommand(ToggleRun);
    }

    private void ToggleRun()
    {
        _isRunning = !_isRunning;
        State = _isRunning ? "运行中" : "已暂停";
        OnPropertyChanged(nameof(ActionText));
        Console.WriteLine($"[TaskRowVM] 版号={PlateNo} 序号={Sequence} 切换为：{ActionText}（下一次点击显示）");
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
