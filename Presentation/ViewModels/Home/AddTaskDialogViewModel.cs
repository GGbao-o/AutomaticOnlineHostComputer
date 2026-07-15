using System;
using System.Collections.Generic;
using System.Globalization;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 添加任务弹窗输入模型。
/// </summary>
public sealed class AddTaskDialogViewModel : ObservableObject
{
    private string _plateNo = string.Empty;
    private string _sequence = string.Empty;
    private double _length;
    private double _diameter;
    private double _plugHole = 70;
    private double _leftPlugThickness;
    private double _rightPlugThickness;
    private string _innerTaperText = "10";
    private string _cornerSizeText = "8";
    private double _validatedInnerTaper = 10;
    private double _validatedCornerSize = 8;
    private string _markingContent = string.Empty;
    private string _processType = "总工艺";
    private string _boringProcess = string.Empty;
    private string _skewBedProcess = "粗精一体不倒角";
    private bool _forceBalancing;
    private bool _isStartFromTransferRack;
    private TransferRackStartOption? _selectedTransferRack;

    public string PlateNo { get => _plateNo; set => SetField(ref _plateNo, value); }
    public string Sequence { get => _sequence; set => SetField(ref _sequence, value); }
    public double Length { get => _length; set => SetField(ref _length, value); }
    public double Diameter { get => _diameter; set => SetField(ref _diameter, value); }
    public double PlugHole { get => _plugHole; set => SetField(ref _plugHole, value); }
    public double LeftPlugThickness { get => _leftPlugThickness; set => SetField(ref _leftPlugThickness, value); }
    public double RightPlugThickness { get => _rightPlugThickness; set => SetField(ref _rightPlugThickness, value); }
    public string InnerTaperText { get => _innerTaperText; set => SetField(ref _innerTaperText, value); }
    public string CornerSizeText { get => _cornerSizeText; set => SetField(ref _cornerSizeText, value); }
    public string MarkingContent { get => _markingContent; set => SetField(ref _markingContent, value); }

    public string ProcessType { get => _processType; set => SetField(ref _processType, value); }
    public string BoringProcess { get => _boringProcess; set => SetField(ref _boringProcess, value); }
    public string SkewBedProcess { get => _skewBedProcess; set => SetField(ref _skewBedProcess, value); }
    public bool ForceBalancing { get => _forceBalancing; set => SetField(ref _forceBalancing, value); }
    public bool IsStartFromTransferRack { get => _isStartFromTransferRack; set => SetField(ref _isStartFromTransferRack, value); }
    public TransferRackStartOption? SelectedTransferRack { get => _selectedTransferRack; set => SetField(ref _selectedTransferRack, value); }

    /// <summary>跳过双头镗：工序选择"省去双头镗工艺"时为true</summary>
    public bool SkipBoring => ProcessType == "省去双头镗工艺";

    /// <summary>堵孔选项：70=小孔，100=大孔</summary>
    public List<double> PlugHoleOptions { get; } = new() { 70, 100 };

    public List<string> ProcessTypeOptions { get; } = new() { "总工艺", "省去双头镗工艺" };

    /// <summary>
    /// 斜床加工模式只允许这三种业务名称；实际写入数值由后端按斜床系统(FANUC/今洲)映射。
    /// </summary>
    public List<string> SkewBedProcessOptions { get; } = new() { "粗精一体不倒角", "粗精一体倒角", "精车" };

    /// <summary>
    /// 人工从中转架开始时的可选站位；只写上位机缓存, PLC有板信号仍由现场传感器读取。
    /// </summary>
    public List<TransferRackStartOption> TransferRackOptions { get; } = new()
    {
        new(1, "ST105", "1号线 ST105 中转架1"),
        new(1, "ST101", "1号线 ST101 中转架2"),
        new(1, "ST106", "1号线 ST106 中转架3"),
        new(2, "ST016", "2号线 ST016 中转架1"),
        new(2, "ST017", "2号线 ST017 中转架2"),
        new(2, "ST018", "2号线 ST018 中转架3"),
    };

    public AddTaskDialogViewModel()
    {
        SelectedTransferRack = TransferRackOptions[0];
    }

    /// <summary>
    /// 基础校验：人工入口与ERP入口保持相同的关键尺寸约束，
    /// 避免零值、负数和非有限数进入后续参数及坐标计算。
    /// </summary>
    public bool Validate(out string message)
    {
        if (string.IsNullOrWhiteSpace(PlateNo))
        {
            message = "版号不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Sequence))
        {
            message = "序号不能为空";
            return false;
        }

        if (!SkewBedProcessOptions.Contains(SkewBedProcess))
        {
            message = "请选择正确的斜床工艺";
            return false;
        }

        if (!TryParseBoringRegisterValue(InnerTaperText, out _validatedInnerTaper))
        {
            message = "内孔锥度必须>0且<=655.35";
            return false;
        }

        if (!double.IsFinite(Length) || Length <= 0)
        {
            message = "长度必须是大于0的有效数值";
            return false;
        }

        if (!double.IsFinite(Diameter) || Diameter <= 0)
        {
            message = "直径必须是大于0的有效数值";
            return false;
        }

        if (Math.Abs(PlugHole - 70) > 0.001 && Math.Abs(PlugHole - 100) > 0.001)
        {
            message = "堵孔只能选择70或100";
            return false;
        }

        if (!double.IsFinite(LeftPlugThickness) || LeftPlugThickness <= 0)
        {
            message = "左堵厚必须是大于0的有效数值";
            return false;
        }

        if (!double.IsFinite(RightPlugThickness) || RightPlugThickness <= 0)
        {
            message = "右堵厚必须是大于0的有效数值";
            return false;
        }

        if (!TryParseBoringRegisterValue(CornerSizeText, out _validatedCornerSize))
        {
            message = "圆角大小必须>0且<=655.35";
            return false;
        }

        if (IsStartFromTransferRack && SelectedTransferRack == null)
        {
            message = "请选择要写入缓存的中转架";
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
            Sequence = Sequence.Trim(),
            Length = Length,
            Diameter = Diameter,
            PlugHole = PlugHole,
            LeftPlugThickness = LeftPlugThickness,
            RightPlugThickness = RightPlugThickness,
            InnerTaper = _validatedInnerTaper,
            CornerSize = _validatedCornerSize,
            MarkingContent = MarkingContent.Trim(),
            ProcessType = ProcessType,
            BoringProcess = BoringProcess.Trim(),
            SkewBedProcess = SkewBedProcess.Trim(),
            ForceBalancing = ForceBalancing,
            StartFromTransferRack = IsStartFromTransferRack,
            TransferRackLine = IsStartFromTransferRack ? SelectedTransferRack?.Line ?? 0 : 0,
            TransferRackCode = IsStartFromTransferRack ? SelectedTransferRack?.Code ?? string.Empty : string.Empty,
            TransferRackDisplayName = IsStartFromTransferRack ? SelectedTransferRack?.DisplayName ?? string.Empty : string.Empty,
            Step = "待上料",
            State = "待执行"
        };
    }

    private static bool TryParseBoringRegisterValue(string text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value) && value > 0 && value <= 655.35;
}

public sealed class TransferRackStartOption
{
    public TransferRackStartOption(int line, string code, string displayName)
    {
        Line = line;
        Code = code;
        DisplayName = displayName;
    }

    public int Line { get; }
    public string Code { get; }
    public string DisplayName { get; }
    public override string ToString() => DisplayName;
}
