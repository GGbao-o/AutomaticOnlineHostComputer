using System;
using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 运行诊断项的用途分类。
/// 该分类只服务于页面分组与计数，不参与任何引擎准入或设备控制判断。
/// </summary>
public enum RuntimeDiagnosticCategory
{
    FrontReadiness,
    Communication,
    SignalFreshness,
    RackConsistency,
    CraneTask,
    ManualConfirmation
}

/// <summary>
/// 运行诊断项的展示等级。
/// “等待”表示正常业务条件暂未满足，不等同于故障或报警。
/// </summary>
public enum RuntimeDiagnosticLevel
{
    Normal,
    Waiting,
    Warning,
    ManualConfirmation
}

/// <summary>
/// 单条运行诊断的不可变内存副本。
/// 此类型不持有设备连接、引擎引用、锁、回调或控制命令，UI 只能读取和展示。
/// </summary>
public sealed record RuntimeDiagnosticItem(
    RuntimeDiagnosticCategory Category,
    string Section,
    string Scope,
    string Name,
    RuntimeDiagnosticLevel Level,
    string State,
    string Detail,
    DateTime CapturedAtUtc)
{
    public string LevelText => Level switch
    {
        RuntimeDiagnosticLevel.Normal => "正常",
        RuntimeDiagnosticLevel.Waiting => "等待",
        RuntimeDiagnosticLevel.Warning => "警告",
        RuntimeDiagnosticLevel.ManualConfirmation => "人工确认",
        _ => "未知"
    };
}

/// <summary>
/// 某一时刻运行诊断页面使用的完整只读快照。
/// Items 在返回后不会再由引擎修改；页面刷新失败也不能反向影响生产流程。
/// </summary>
public sealed record RuntimeDiagnosticsSnapshot(
    IReadOnlyList<RuntimeDiagnosticItem> Items,
    DateTime CapturedAtUtc);
