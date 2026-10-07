using System;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 总上料架全局分线前使用的只读准入结果。
/// 它只回答“这条线此刻适不适合接收一块新板”，不代表后续机械手、货叉、双头镗动作可以绕过原有实时复核。
/// </summary>
public sealed record FrontDispatchReadinessSnapshot(
    int Line,
    bool CanAccept,
    string RejectReason,
    int Pressure,
    DateTime CapturedAtUtc);

/// <summary>
/// 线路前端引擎采集到的准入输入。
/// 这里故意只放内存字段和既有快照，不允许在评估器里读PLC或等待动作锁，避免全局调度器和设备流程形成新耦合。
/// </summary>
internal readonly record struct FrontDispatchReadinessInput(
    int Line,
    bool IsRunning,
    bool IsPaused,
    bool HasCurrentWorkpiece,
    bool ForkPhaseIdle,
    int CachedCount,
    bool ForkSnapshotValid,
    DateTime ForkSnapshotAtUtc,
    bool ForkAtStandby,
    bool ForkHasPlate,
    bool BoringSnapshotValid,
    DateTime BoringSnapshotAtUtc,
    bool BoringRequestData,
    int CraneQueueCount,
    bool FrontCraneActive);

/// <summary>
/// 1/2号线共用的纯判断器。
/// 所有拒绝顺序和压力公式集中在这里，避免两条线各写一份判断后现场行为不一致。
/// </summary>
internal static class FrontDispatchReadinessEvaluator
{
    internal static FrontDispatchReadinessSnapshot Evaluate(
        FrontDispatchReadinessInput input,
        DateTime nowUtc,
        TimeSpan maxSnapshotAge)
    {
        int pressure = input.CachedCount
            + (input.HasCurrentWorkpiece ? 1 : 0)
            + input.CraneQueueCount
            + (input.FrontCraneActive ? 1 : 0);

        string? rejectReason = null;

        if (!input.IsRunning)
            rejectReason = "线路引擎未运行";
        else if (input.IsPaused)
            rejectReason = "线路已暂停";
        else if (input.CachedCount > 0)
            rejectReason = $"前端缓存未消化({input.CachedCount})";
        else if (input.HasCurrentWorkpiece)
            rejectReason = "前段_currentWp仍有在途工件";
        else if (!input.ForkPhaseIdle)
            rejectReason = "货叉/双头镗阶段未回Idle";
        else if (!IsFresh(input.ForkSnapshotValid, input.ForkSnapshotAtUtc, nowUtc, maxSnapshotAge))
            rejectReason = "货叉快照无效或过期";
        else if (!input.ForkAtStandby)
            rejectReason = "货叉不在待机位";
        else if (input.ForkHasPlate)
            rejectReason = "货叉仍有板";
        else if (!IsFresh(input.BoringSnapshotValid, input.BoringSnapshotAtUtc, nowUtc, maxSnapshotAge))
            rejectReason = "双头镗快照无效或过期";
        else if (!input.BoringRequestData)
            rejectReason = "双头镗R6101未请求数据";

        return rejectReason == null
            ? new FrontDispatchReadinessSnapshot(input.Line, true, "可接板", pressure, nowUtc)
            : new FrontDispatchReadinessSnapshot(input.Line, false, rejectReason, pressure, nowUtc);
    }

    private static bool IsFresh(bool valid, DateTime capturedAtUtc, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!valid || capturedAtUtc == default)
            return false;

        TimeSpan age = nowUtc - capturedAtUtc;
        return age >= TimeSpan.Zero && age <= maxAge;
    }
}
