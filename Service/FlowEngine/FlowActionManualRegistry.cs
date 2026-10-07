using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 进程内的“待人工确认动作”只读账本。
///
/// FlowActionContext 属于一次异步动作，catch 返回后对象会离开调用栈；若不把最终快照留下，
/// 页面只能看到异常文本，无法知道工件、步骤、最后坐标和当时登记的锁。本表只保存已经
/// PauseForManualResolution 的不可变快照，不发送命令、不释放锁、不替代各流程自己的应急结案。
/// </summary>
internal static class FlowActionManualRegistry
{
    private static readonly ConcurrentDictionary<string, FlowActionSnapshot> Pending = new(StringComparer.Ordinal);

    public static void Record(FlowActionSnapshot snapshot)
    {
        if (!snapshot.ManualConfirmationRequired)
            return;
        Pending[snapshot.OperationId] = snapshot;
    }

    public static bool TryGet(string operationId, out FlowActionSnapshot snapshot) =>
        Pending.TryGetValue(operationId, out snapshot!);

    /// <summary>人工结案后才移除；调用方应先完成自身缓存/Pending/任务牌的原子结案。</summary>
    public static bool Remove(string operationId) => Pending.TryRemove(operationId, out _);

    public static IReadOnlyList<FlowActionSnapshot> Snapshot() => Pending.Values
        .OrderBy(x => x.FlowScope, StringComparer.Ordinal)
        .ThenBy(x => x.DeviceName, StringComparer.Ordinal)
        .ThenBy(x => x.OperationId, StringComparer.Ordinal)
        .ToArray();

    /// <summary>正常完成的同一动作不应遗留旧人工暂停快照。</summary>
    public static void MarkCompleted(string operationId) => Pending.TryRemove(operationId, out _);
}
