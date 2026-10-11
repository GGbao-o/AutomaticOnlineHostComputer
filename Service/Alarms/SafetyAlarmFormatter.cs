using System.Globalization;

namespace AutomaticOnlineHostComputer.Service.Alarms;

internal sealed record SafetyAlarmSections(string Title, string Failure, string Workpiece, string Historical,
    string Live, string Cleanup, string ExceptionDetail)
{
    public string FullText => string.Join("\n\n", Title, "【异常与步骤】\n" + Failure,
        "【工件与流向】\n" + Workpiece, "【异常时业务记录】\n" + Historical,
        "【异常后现场读取】\n" + Live, "【最终收尾结果】\n" + Cleanup,
        "【完整异常】\n" + ExceptionDetail);
}

internal static class SafetyAlarmFormatter
{
    public static string Format(SafetyAlarmDetails details, CraneAlarmSnapshot? sample) => Sections(details, sample).FullText;

    public static SafetyAlarmSections Sections(SafetyAlarmDetails d, CraneAlarmSnapshot? sample)
    {
        var a = d.FailureAction;
        var wp = d.Workpiece;
        string title = $"{d.Scope} · {d.CraneNo}号天车 · {d.Operation}异常";
        string failure = $"发生时间：{Time(d.OccurredAtUtc)}\n动作编号：{d.ActionId}\n" +
            $"异常原因：{d.Reason}\n失败步骤：{(a == null ? "未知" : Step(a.Step))}\n" +
            $"步骤说明：{Known(a?.Detail)}\n最后成功步骤：{d.LastSuccessfulStep}";
        string workpiece = $"所属线路：{(d.Scope == "研磨" ? "研磨共用（本工件来源线路未记录）" : d.Scope)}\n" +
            $"来源：{StationName(a?.Source)}\n目标：{StationName(a?.Target)}\n" +
            $"版号：{Known(wp?.PlateNo)}    序号：{Known(wp?.Sequence)}\n" +
            $"长度：{Measure(wp?.Length)}    直径：{Measure(wp?.Diameter)}\n" +
            $"孔型：{(wp == null ? "未知" : wp.BoreType.ToString(CultureInfo.InvariantCulture))}";
        string historical = $"本步骤运动目标：{Position(a?.TargetPosition, true)}\n" +
            $"最后业务确认坐标：{Position(a?.LastKnownPosition)}\n" +
            $"最后X11记录：{Bit(a?.X11)}    业务充磁记录：{Bit(a?.MagnetOn)}\n" +
            $"业务工件归属：{Ownership(a?.Ownership)}\n" +
            $"命令状态：{Command(a?.CommandState)}\n" +
            "以上为故障时保存的业务记录；命令返回、充磁记录均不能替代当前反馈或实物确认。";

        var p = sample?.Position;
        string live = $"采样时间：{(sample == null ? "尚未读取" : $"{Time(sample.StartedAtUtc)} → {Time(sample.FinishedAtUtc)}")}\n" +
            $"X6充磁反馈：{Input(sample?.X6)}\nX7退磁反馈：{Input(sample?.X7)}\n" +
            $"X11有板检测：{Input(sample?.X11)}\nX2下压保护：{Input(sample?.X2)}\n" +
            (p == null
                ? $"天车显示位置：X=未知 Y=未知 Z=未知\n绝对编码器：AbsX=未知 AbsY=未知 AbsZ=未知\n" +
                  $"D5029持板状态：未知\n设备故障／伺服报警／PLC伺服报警／PLC报警／运行条件缺失：未知\n坐标读取：{Known(sample?.PositionError, "等待后台只读采样")}"
                : $"天车显示位置：X={p.X} Y={p.Y} Z={p.Z}\n绝对编码器：AbsX={p.AbsX} AbsY={p.AbsY} AbsZ={p.AbsZ}\n" +
                  $"坐标采样时间：{Time(p.ReadAtUtc)}\nD5029持板状态：{p.HasRoller}\n" +
                  $"设备故障：{p.Fault}    伺服报警：{p.ServoAlarm}\nPLC伺服报警：{p.PlcServoAlarm}    PLC报警：{p.PlcAlarm}\n运行条件缺失：{p.RunConditionMissing}") +
            "\n每项为独立读取，非同一瞬间；刷新只更新本区域，不能证明现场已安全。";
        string cleanup = $"收尾进度：{(d.CleanupFinished ? "本动作收尾已结束" : "收尾进行中，结果将自动补充")}\n" +
            $"结果记录时间：{(d.CleanupAtUtc.HasValue ? Time(d.CleanupAtUtc.Value) : "尚未取得")}\n" +
            $"PLC/CNC参数及输出清零：{d.DeviceCleanup}\n设备内存：{d.MemoryCleanup}\n" +
            $"来源／下游缓存：{d.CacheCleanup}\n软件锁：{d.LockCleanup}\n" +
            "处置提示：按现场实物、读取结果及收尾结果核对；关闭、复制、刷新均不会恢复引擎或操作设备。";
        string originalReport = string.IsNullOrWhiteSpace(d.AlarmMessages) ? string.Empty
            : "\n\n原流程告警与收尾报告（其中固定清零文案以本窗实际收尾结果为准）：\n" + d.AlarmMessages;
        return new(title, failure, workpiece, historical, live, cleanup, d.ExceptionDetail + originalReport);
    }

    internal static string Step(FlowActionStep step) => step switch
    {
        FlowActionStep.PreCheck => "动作前检查",
        FlowActionStep.SourceReserved => "预约来源工件",
        FlowActionStep.MoveXYToSource => "XY移动到来源设备",
        FlowActionStep.FineTuneSource => "来源位置XY绝对编码器微调",
        FlowActionStep.MoveZDownToPick => "Z下降取料",
        FlowActionStep.MagnetOnSent => "充磁取料，确认充磁反馈",
        FlowActionStep.ConfirmPickup => "检查X11，确认取到板",
        FlowActionStep.HoldingWorkpiece => "已确认持板",
        FlowActionStep.MoveZSafeWithWorkpiece => "Z回安全高度",
        FlowActionStep.MoveXYToTarget => "XY移动到目标设备／对中位置",
        FlowActionStep.FineTuneTarget => "目标位置XY绝对编码器微调",
        FlowActionStep.MoveZDownToPlace => "Z下降放料",
        FlowActionStep.MagnetOffSent => "退磁放料，等待X7=1且X6=0",
        FlowActionStep.ConfirmPlaced => "确认已放料",
        FlowActionStep.TargetPendingHandoff => "目标设备交接未完成",
        FlowActionStep.NotifyDownstream => "等待或写入PLC/CNC及下游交接信号",
        FlowActionStep.ReturnSafe => "回安全位置／归位",
        FlowActionStep.Completed => "动作完成",
        _ => step.ToString()
    };

    private static string Command(FlowCommandState? command) => command switch
    {
        FlowCommandState.NotSent => "本步骤命令未发送",
        FlowCommandState.SentAwaitingEvidence => "命令已开始发送，反馈待确认",
        FlowCommandState.Confirmed => "调用成功返回（不是当前实物状态）",
        FlowCommandState.ResponseUnknown => "响应未知，设备可能已执行",
        _ => "未知"
    };

    private static string Ownership(FlowWorkpieceOwnership? ownership) => ownership switch
    {
        FlowWorkpieceOwnership.ReservedAtSource => "来源预约中，尚未确认取走",
        FlowWorkpieceOwnership.OnCarrier => "业务已确认天车持板",
        FlowWorkpieceOwnership.AtTargetPendingHandoff => "业务记录已放到目标，交接待确认",
        FlowWorkpieceOwnership.Completed => "业务动作完成",
        FlowWorkpieceOwnership.ManualConfirmation => "归属待人工确认",
        _ => "未知"
    };

    private static string Input(AlarmInputValue? input) => input?.Value.HasValue == true
        ? $"{Bit(input.Value)}（{Time(input.ReadAtUtc!.Value)}）"
        : $"未知／读取失败：{Known(input?.Error, "尚未读取")}";

    private static string Position(FlowActionPosition? p, bool target = false) =>
        $"X={Axis(p?.X, target)} Y={Axis(p?.Y, target)} Z={Axis(p?.Z, target)}";
    private static string Axis(int? n, bool target) => !n.HasValue ? "未知" : target && n == -1 ? "保持当前轴" : n.Value.ToString(CultureInfo.InvariantCulture);
    private static string Bit(bool? value) => value.HasValue ? value.Value ? "1" : "0" : "未知";
    private static string Measure(double? value) => value.HasValue && value > 0 ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm" : "未知";
    private static string Known(string? value, string fallback = "未知") => string.IsNullOrWhiteSpace(value) ? fallback : value;
    internal static string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ChinaTimeZone).ToString("yyyy-MM-dd HH:mm:ss.fff");
    private static readonly TimeZoneInfo ChinaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    internal static string StationName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "未知";
        if (code.Contains("分流目标", StringComparison.Ordinal)) return code + "（尚未确定）";
        return StationNames.TryGetValue(code, out string? name) ? $"{code}／{name}" : code;
    }

    private static readonly IReadOnlyDictionary<string, string> StationNames = new Dictionary<string, string>
    {
        ["ST101"] = "1号线中转架（M812）", ["ST105"] = "1号线中转架（M811）", ["ST106"] = "1号线中转架（M813）",
        ["ST016"] = "2号线中转架4", ["ST017"] = "2号线中转架5", ["ST018"] = "2号线中转架6",
        ["ST108"] = "1号斜床", ["ST109"] = "2号斜床", ["ST111"] = "3号斜床", ["ST110"] = "4号斜床", ["ST112"] = "5号斜床",
        ["ST606"] = "6号斜床", ["ST607"] = "7号斜床", ["ST608"] = "8号斜床", ["ST609"] = "9号斜床", ["ST610"] = "10号斜床",
        ["ST019"] = "1号线动平衡下料架", ["ST020"] = "2号线动平衡下料架", ["ST021"] = "2号线短板中转架",
        ["ST010"] = "研磨上料架入口", ["ST709"] = "研磨上料架取料位", ["ST710"] = "研磨下料架",
        ["ST701"] = "1号研磨机", ["ST702"] = "2号研磨机", ["ST703"] = "3号研磨机", ["ST704"] = "4号研磨机"
    };
}
