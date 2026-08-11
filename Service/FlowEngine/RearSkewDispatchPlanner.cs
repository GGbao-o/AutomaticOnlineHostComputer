using System;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 后天车斜床上/下料调度动作。
/// 这里只表达“本轮先做哪一类动作”，不碰任何PLC、天车、缓存或斜床状态。
/// </summary>
internal enum RearSkewDispatchAction
{
    None,
    Load,
    Unload
}

/// <summary>
/// 后天车调度决策结果，用于主循环打印原因，方便现场判断为什么本轮上料/下料。
/// </summary>
internal readonly record struct RearSkewDispatchDecision(
    RearSkewDispatchAction Action,
    string Reason,
    int HighUnloadThreshold);

/// <summary>
/// 后天车调度诊断日志的轻量门控。只限制重复文本输出，不参与调度或任何设备动作。
/// </summary>
internal sealed class RearSkewDispatchLogGate
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private RearSkewDispatchLogKey? _last;
    private DateTime _lastLoggedAtUtc;

    public bool ShouldLog(RearSkewDispatchDecision decision, int loadCandidates, int unloadCandidates,
        int physicalRackPlates, int matchableRackPlates, int usableBeds)
    {
        var current = new RearSkewDispatchLogKey(
            decision.Action, decision.Reason, decision.HighUnloadThreshold,
            loadCandidates, unloadCandidates, physicalRackPlates, matchableRackPlates, usableBeds);
        var now = DateTime.UtcNow;
        if (_last is { } last && last == current && now - _lastLoggedAtUtc < HeartbeatInterval)
            return false;

        _last = current;
        _lastLoggedAtUtc = now;
        return true;
    }

    private readonly record struct RearSkewDispatchLogKey(
        RearSkewDispatchAction Action,
        string Reason,
        int HighUnloadThreshold,
        int LoadCandidates,
        int UnloadCandidates,
        int PhysicalRackPlates,
        int MatchableRackPlates,
        int UsableBeds);
}

/// <summary>
/// 后天车斜床上/下料纯决策器。
/// <para>
/// 设计意图：把“是否先上料还是先下料”的策略从 1/2 号线大循环中抽出来，
/// 保持高内聚低耦合；本类只根据候选数量和压力做判断，不读取设备、不修改状态。
/// </para>
/// <para>
/// 中转架容量固定为3，因此中转架满板、可匹配板数阈值使用固定值；
/// 斜床可能坏掉或断线，因此下料高压力阈值根据有效斜床数量动态折算。
/// </para>
/// </summary>
internal static class RearSkewDispatchPlanner
{
    public static RearSkewDispatchDecision Decide(
        int loadCandidateCount,
        int unloadCandidateCount,
        int physicalRackPlateCount,
        int matchableRackPlateCount,
        int usableBedCount)
    {
        int highUnloadThreshold = GetHighUnloadThreshold(usableBedCount);

        if (loadCandidateCount <= 0 && unloadCandidateCount <= 0)
            return Decision(RearSkewDispatchAction.None, "无可上料候选且无可下料候选", highUnloadThreshold);

        if (unloadCandidateCount <= 0 && loadCandidateCount > 0)
            return Decision(RearSkewDispatchAction.Load, "无下料候选但存在上料候选", highUnloadThreshold);

        if (loadCandidateCount <= 0 && unloadCandidateCount > 0)
            return Decision(RearSkewDispatchAction.Unload, "无上料候选但存在下料候选", highUnloadThreshold);

        if (physicalRackPlateCount >= 3 && loadCandidateCount > 0)
            return Decision(RearSkewDispatchAction.Load, "中转架物理满3个且存在上料候选, 强制上料腾位置", highUnloadThreshold);

        if (matchableRackPlateCount >= 2 && unloadCandidateCount < highUnloadThreshold)
            return Decision(RearSkewDispatchAction.Load, $"中转架可匹配板数>=2且下料候选少于高压力阈值{highUnloadThreshold}, 优先上料", highUnloadThreshold);

        if (unloadCandidateCount >= highUnloadThreshold && matchableRackPlateCount <= 1)
            return Decision(RearSkewDispatchAction.Unload, $"下料候选达到高压力阈值{highUnloadThreshold}且中转架可匹配板数<=1, 优先下料", highUnloadThreshold);

        return Decision(RearSkewDispatchAction.Unload, "普通场景默认下料优先", highUnloadThreshold);
    }

    /// <summary>
    /// 在派发前根据中转架通道的即时可进入性，对原始压力决策作一次让位。
    /// 这是调度吞吐优化，不替代实际动作中的锁获取：DoLoad/DoUnload 仍按原有顺序获取锁。
    /// </summary>
    public static RearSkewDispatchDecision ApplyLoadPathAvailability(
        RearSkewDispatchDecision baseline,
        bool loadPathImmediatelyAvailable,
        int immediatelyDispatchableUnloadCount,
        string loadPathBlockReason)
    {
        if (baseline.Action != RearSkewDispatchAction.Load ||
            loadPathImmediatelyAvailable ||
            immediatelyDispatchableUnloadCount <= 0)
        {
            return baseline;
        }

        string reason = string.IsNullOrWhiteSpace(loadPathBlockReason)
            ? "中转架通道区域锁暂不可用"
            : loadPathBlockReason;

        return Decision(
            RearSkewDispatchAction.Unload,
            $"{baseline.Reason}；上料通道暂不可进入({reason})，存在{immediatelyDispatchableUnloadCount}个立即可下料候选，改派下料",
            baseline.HighUnloadThreshold);
    }

    private static RearSkewDispatchDecision Decision(RearSkewDispatchAction action, string reason, int threshold)
        => new(action, reason, threshold);

    /// <summary>
    /// 根据有效斜床数量动态折算“下料压力大”的判断门槛。
    /// 现场允许斜床坏1台或2台，因此不能固定要求3台同时等待下料。
    /// </summary>
    private static int GetHighUnloadThreshold(int usableBedCount)
    {
        if (usableBedCount <= 1) return 1;
        if (usableBedCount <= 3) return 2;
        return 3;
    }
}
