using System;
using System.Globalization;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// ERP任务文件解析器。
/// 文件每行固定为13列: 打印内容,版号,序号,长度,直径,堵孔,斜床工艺,左堵厚,右堵厚,是否做动平衡,是否跳过双头镗,内孔锥度,圆角大小。
/// 这里只创建主页面任务行, 不启动任务、不写产线缓存。
/// </summary>
internal static class ErpTaskImportParser
{
    public static bool TryParse(string line, out TaskRowViewModel? row, out string error)
    {
        row = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "ERP任务内容为空";
            return false;
        }

        string[] parts = line.Trim().Split(',');
        if (parts.Length != 13)
        {
            error = $"ERP任务字段数量错误: 当前{parts.Length}个, 必须13个";
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim();

        string markingContent = parts[0];
        string plateNo = parts[1];
        if (string.IsNullOrWhiteSpace(markingContent))
        {
            error = "打印内容不能为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(plateNo))
        {
            error = "版号不能为空";
            return false;
        }

        string sequence = parts[2];
        if (string.IsNullOrWhiteSpace(sequence))
        {
            error = "序号不能为空";
            return false;
        }
        if (!TryParseDouble(parts[3], out double length) || length <= 0)
        {
            error = $"长度[{parts[3]}]无效, 必须>0";
            return false;
        }
        if (!TryParseDouble(parts[4], out double diameter) || diameter <= 0)
        {
            error = $"直径[{parts[4]}]无效, 必须>0";
            return false;
        }
        if (!TryParseDouble(parts[5], out double plugHole) || (Math.Abs(plugHole - 70) > 0.001 && Math.Abs(plugHole - 100) > 0.001))
        {
            error = $"堵孔[{parts[5]}]无效, 只能是70或100";
            return false;
        }
        if (!TryParseInt(parts[6], out int skewMode) || skewMode < 1 || skewMode > 3)
        {
            error = $"斜床工艺[{parts[6]}]无效, 只能是1/2/3";
            return false;
        }
        if (!TryParseDouble(parts[7], out double leftPlugThickness) || leftPlugThickness <= 0)
        {
            error = $"左堵厚[{parts[7]}]无效, 必须>0";
            return false;
        }
        if (!TryParseDouble(parts[8], out double rightPlugThickness) || rightPlugThickness <= 0)
        {
            error = $"右堵厚[{parts[8]}]无效, 必须>0";
            return false;
        }
        if (!TryParseSwitch(parts[9], out bool forceBalancing))
        {
            error = $"是否做动平衡[{parts[9]}]无效, 只能是1或0";
            return false;
        }
        if (!TryParseSwitch(parts[10], out bool skipBoring))
        {
            error = $"是否跳过双头镗[{parts[10]}]无效, 只能是1或0";
            return false;
        }
        if (!TryParseBoringRegisterValue(parts[11], out double innerTaper))
        {
            error = $"内孔锥度[{parts[11]}]无效, 必须>0且<=655.35";
            return false;
        }
        if (!TryParseBoringRegisterValue(parts[12], out double cornerSize))
        {
            error = $"圆角大小[{parts[12]}]无效, 必须>0且<=655.35";
            return false;
        }

        row = new TaskRowViewModel
        {
            PlateNo = plateNo,
            Sequence = sequence,
            Length = length,
            Diameter = diameter,
            PlugHole = plugHole,
            LeftPlugThickness = leftPlugThickness,
            RightPlugThickness = rightPlugThickness,
            InnerTaper = innerTaper,
            CornerSize = cornerSize,
            MarkingContent = markingContent,
            ProcessType = skipBoring ? "省去双头镗工艺" : "总工艺",
            BoringProcess = string.Empty,
            SkewBedProcess = MapSkewMode(skewMode),
            ForceBalancing = forceBalancing,
            StartFromTransferRack = false,
            TransferRackLine = 0,
            TransferRackCode = string.Empty,
            TransferRackDisplayName = string.Empty,
            Step = "待上料",
            State = "待执行"
        };
        return true;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseBoringRegisterValue(string text, out double value) =>
        TryParseDouble(text, out value) && double.IsFinite(value) && value > 0 && value <= 655.35;

    private static bool TryParseSwitch(string text, out bool value)
    {
        value = false;
        if (text == "1") { value = true; return true; }
        if (text == "0") { value = false; return true; }
        return false;
    }

    private static string MapSkewMode(int mode) => mode switch
    {
        1 => "粗精一体不倒角",
        2 => "粗精一体倒角",
        3 => "精车",
        _ => "粗精一体不倒角"
    };
}
