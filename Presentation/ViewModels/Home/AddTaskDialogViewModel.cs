using System;
using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 添加任务弹窗输入模型。
/// </summary>
public sealed class AddTaskDialogViewModel : ObservableObject
{
    private string _plateNo = string.Empty;
    private int _sequence;
    private double _length;
    private double _diameter;
    private double _plugHole = 70;
    private double _leftPlugThickness;
    private double _rightPlugThickness;
    private string _markingContent = string.Empty;
    private string _processType = "总工艺";
    private string _boringProcess = string.Empty;
    private string _skewBedProcess = string.Empty;

    public string PlateNo { get => _plateNo; set => SetField(ref _plateNo, value); }
    public int Sequence { get => _sequence; set => SetField(ref _sequence, value); }
    public double Length { get => _length; set => SetField(ref _length, value); }
    public double Diameter { get => _diameter; set => SetField(ref _diameter, value); }
    public double PlugHole { get => _plugHole; set => SetField(ref _plugHole, value); }
    public double LeftPlugThickness { get => _leftPlugThickness; set => SetField(ref _leftPlugThickness, value); }
    public double RightPlugThickness { get => _rightPlugThickness; set => SetField(ref _rightPlugThickness, value); }
    public string MarkingContent { get => _markingContent; set => SetField(ref _markingContent, value); }

    public string ProcessType { get => _processType; set => SetField(ref _processType, value); }
    public string BoringProcess { get => _boringProcess; set => SetField(ref _boringProcess, value); }
    public string SkewBedProcess { get => _skewBedProcess; set => SetField(ref _skewBedProcess, value); }

    /// <summary>跳过双头镗：工序选择"省去双头镗工艺"时为true</summary>
    public bool SkipBoring => ProcessType == "省去双头镗工艺";

    /// <summary>堵孔选项：70=小孔，100=大孔</summary>
    public List<double> PlugHoleOptions { get; } = new() { 70, 100 };

    public List<string> ProcessTypeOptions { get; } = new() { "总工艺", "省去双头镗工艺" };

    /// <summary>
    /// 基础校验：版号不能为空，序号>0。
    /// </summary>
    public bool Validate(out string message)
    {
        if (string.IsNullOrWhiteSpace(PlateNo))
        {
            message = "版号不能为空";
            return false;
        }

        if (Sequence <= 0)
        {
            message = "序号必须大于0";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public TaskRowViewModel ToTaskRow()
    {
        return new TaskRowViewModel
        {
            PlateNo = PlateNo.Trim(),
            Sequence = Sequence,
            Length = Length,
            Diameter = Diameter,
            PlugHole = PlugHole,
            LeftPlugThickness = LeftPlugThickness,
            RightPlugThickness = RightPlugThickness,
            MarkingContent = MarkingContent.Trim(),
            ProcessType = ProcessType,
            BoringProcess = BoringProcess.Trim(),
            SkewBedProcess = SkewBedProcess.Trim(),
            Step = "待上料",
            State = "待执行"
        };
    }
}
