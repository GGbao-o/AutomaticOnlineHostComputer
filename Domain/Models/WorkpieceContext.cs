using System;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Domain.Models;

/// <summary>
/// 工件上下文 —— 一个工件从"上料"到"成品完成"全流程携带的数据。
/// 引擎每走一个阶段就更新 CurrentStage，UI 绑定此对象实时显示进度。
/// </summary>
public sealed class WorkpieceContext
{
    // ═══════════════════════════════════════════════════════════════
    //  工件基本信息（从任务表输入）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>版辊编号（唯一标识）</summary>
    public string PlateNo { get; init; } = string.Empty;

    /// <summary>生产序号</summary>
    public string Sequence { get; init; } = string.Empty;

    /// <summary>版辊长度（mm）—— 决定线体分配、动平衡判断</summary>
    public double Length { get; init; }

    /// <summary>外圆成活直径（mm）</summary>
    public double Diameter { get; init; }

    /// <summary>堵孔尺寸（mm）</summary>
    public double PlugHole { get; init; }

    /// <summary>左侧堵头厚度（mm）</summary>
    public double LeftPlugThickness { get; init; }

    /// <summary>右侧堵头厚度（mm）</summary>
    public double RightPlugThickness { get; init; }

    /// <summary>打标/刻印内容</summary>
    public string MarkingContent { get; init; } = string.Empty;

    /// <summary>工艺类型："总工艺" / "省去双头镗工艺"（与 AddTaskDialog 下拉框一致）</summary>
    public string ProcessType { get; init; } = "总工艺";

    // ═══════════════════════════════════════════════════════════════
    //  流程运行状态（引擎实时更新）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>当前所处阶段</summary>
    public FlowStage CurrentStage { get; set; } = FlowStage.Idle;

    /// <summary>阶段开始时间（用于计算耗时）</summary>
    public DateTime StageStartTime { get; set; } = DateTime.Now;

    /// <summary>任务创建时间</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>任务完成时间</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>当前阶段的描述文本（绑定到 UI）</summary>
    public string CurrentStepText => CurrentStage switch
    {
        FlowStage.Idle          => "等待开始",
        FlowStage.Loading       => "上料分派中",
        FlowStage.Boring        => ProcessType == "省去双头镗工艺" ? "跳过双头镗" : "双头镗加工中",
        FlowStage.Marking       => "打标中",
        FlowStage.SkewBed       => "斜床加工中",
        FlowStage.BalanceCheck  => "动平衡判断中",
        FlowStage.Grinding      => "研磨加工中",
        FlowStage.Done          => "已完成",
        _                       => "未知"
    };

    /// <summary>分派结果：使用的线体（1 或 2）</summary>
    public int? AssignedLine { get; set; }

    /// <summary>分派结果：上料方式（货叉 / 天车）</summary>
    public string? LoadMethod { get; set; }

    /// <summary>是否需要动平衡（长度 > 800mm）</summary>
    public bool NeedsBalance => Length > 800;

    /// <summary>是否跳过双头镗工序（匹配 AddTaskDialog 下拉框文本）</summary>
    public bool SkipBoring => ProcessType == "省去双头镗工艺";

    /// <summary>绑定的 UI 行 ViewModel（引擎推进阶段时同步更新 DataGrid 显示）</summary>
    public TaskRowViewModel? UiRow { get; set; }

    // ═══════════════════════════════════════════════════════════════
    //  调试输出
    // ═══════════════════════════════════════════════════════════════

    public override string ToString()
        => $"[工件] 版号={PlateNo} 序号={Sequence} 长度={Length}mm 直径={Diameter}mm " +
           $"工艺={ProcessType} 阶段={CurrentStepText}";
}

/// <summary>
/// 流程阶段枚举。引擎依次推动工件经过这些阶段。
/// </summary>
public enum FlowStage
{
    /// <summary>空闲，等待引擎调度</summary>
    Idle,

    /// <summary>上料分派：小机械手取料 → 判断长度 → 分配线体/上料方式</summary>
    Loading,

    /// <summary>双头镗加工（或跳过）</summary>
    Boring,

    /// <summary>打标机打号</summary>
    Marking,

    /// <summary>斜床车削</summary>
    SkewBed,

    /// <summary>动平衡判断分流（长度 >800mm → 动平衡 → 研磨 | ≤800mm → 直接研磨）</summary>
    BalanceCheck,

    /// <summary>研磨加工</summary>
    Grinding,

    /// <summary>全部工序完成</summary>
    Done,
}
